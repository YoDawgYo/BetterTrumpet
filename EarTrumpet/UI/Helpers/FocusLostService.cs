using EarTrumpet.DataModel.Storage;
using EarTrumpet.Logic;
using EarTrumpet.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using EarTrumpet.Interop;

namespace EarTrumpet.UI.Helpers
{
    /// <summary>
    /// Polls the foreground window and applies focus-lost volume rules. Volume changes
    /// can be interpolated without adding undo entries; mute transitions fade to silence
    /// before muting and unmute before fading back to the saved level.
    /// The poll timer only runs while the feature is enabled (or while saved volumes still
    /// need restoring) and the fade timer only while a fade is pending, so a disabled
    /// feature costs no wakeups at all (GitHub #74).
    /// </summary>
    public sealed class FocusLostService
    {
        private readonly DeviceCollectionViewModel _collection;
        private readonly AppSettings _settings;
        private readonly FocusLostSupervisor _supervisor = new FocusLostSupervisor();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _fadeTimer;
        private readonly Dictionary<string, FadeOperation> _fades = new Dictionary<string, FadeOperation>(StringComparer.Ordinal);
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly ISettingsBag _settingsBag = StorageFactory.GetSettings();
        private bool _isStarted;

        // Settings reads go through the registry and an XmlSerializer, so they are cached here
        // and refreshed from SettingChanged instead of being re-read on every 250 ms poll.
        private bool _isEnabled;
        private int _attenuatePercent;
        private int _fadeDurationMs;
        private bool _selectedAppsOnly;

        private int _lastForegroundPid;
        private string _lastForegroundExecutableName;

        public FocusLostService(DeviceCollectionViewModel collection, AppSettings settings)
        {
            _collection = collection;
            _settings = settings;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, __) => Poll();
            _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _fadeTimer.Tick += (_, __) => AdvanceFades();
        }

        public void Start()
        {
            if (_isStarted)
            {
                return;
            }

            _isStarted = true;
            ReadSettings();
            _settingsBag.SettingChanged += OnSettingChanged;
            UpdatePollTimer();
        }

        public void Stop()
        {
            _isStarted = false;
            _settingsBag.SettingChanged -= OnSettingChanged;
            _timer.Stop();
            // Shutdown must never leave an app muted because a transition was pending.
            Apply(FocusLostMode.Off, 0, 0, 0);
            _fadeTimer.Stop();
            _fades.Clear();
        }

        private void OnSettingChanged(object sender, string key)
        {
            if (key == null ||
                !(key == "UseFocusLostVolume" || key.StartsWith("FocusLost", StringComparison.Ordinal)))
            {
                return;
            }

            _dispatcher.BeginInvoke((Action)(() =>
            {
                if (!_isStarted)
                {
                    return;
                }

                ReadSettings();
                // Run at least one poll after any change: enabling starts tracking, disabling
                // restores the saved volumes, after which the timer stops itself.
                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                }
            }));
        }

        private void ReadSettings()
        {
            _isEnabled = _settings != null && _settings.UseFocusLostVolume;
            _attenuatePercent = _settings?.FocusLostAttenuatePercent ?? 0;
            _fadeDurationMs = _settings?.FocusLostFadeDurationMs ?? FocusLostFadePolicy.DefaultDurationMs;
            _selectedAppsOnly = _settings?.FocusLostSelectedAppsOnly ?? false;
        }

        private void UpdatePollTimer()
        {
            var needsPolling = _isStarted && (_isEnabled || _supervisor.HasSavedState);
            if (needsPolling && !_timer.IsEnabled)
            {
                _timer.Start();
            }
            else if (!needsPolling && _timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        private void Poll()
        {
            var hwnd = User32.GetForegroundWindow();
            uint pid = 0;
            if (hwnd != IntPtr.Zero)
            {
                User32.GetWindowThreadProcessId(hwnd, out pid);
            }

            var mode = FocusLostVolumePolicy.ResolveMode(_isEnabled, _attenuatePercent);
            Apply(
                mode,
                (int)pid,
                _attenuatePercent,
                _fadeDurationMs);
            UpdatePollTimer();
        }

        private void Apply(FocusLostMode mode, int foregroundPid, int attenuatePercent, int durationMs)
        {
            try
            {
                durationMs = FocusLostFadePolicy.ClampDurationMs(durationMs);
                var foregroundExecutableName = GetForegroundExecutableName(foregroundPid);
                var sessions = new List<FocusLostSession>();
                var appsByKey = new Dictionary<string, IAppItemViewModel>(StringComparer.Ordinal);
                var selectedOnly = _selectedAppsOnly;

                if (_collection?.AllDevices != null)
                {
                    foreach (var device in _collection.AllDevices)
                    {
                        if (device?.Apps == null)
                        {
                            continue;
                        }

                        foreach (var app in device.Apps)
                        {
                            if (app == null || string.IsNullOrEmpty(app.Id))
                            {
                                continue;
                            }

                            var key = BuildSessionKey(device.Id, app.Id);
                            appsByKey[key] = app;
                            var rule = _settings?.GetAppRule(app.ExeName);
                            var blockedByRule = rule != null &&
                                                (rule.HardMuted || rule.VolumeMode == AppSettings.VolumeRuleMode.Lock);
                            var selected = rule?.FocusLostEnabled ?? false;
                            var canAdjust = !blockedByRule && (!selectedOnly || selected);
                            sessions.Add(new FocusLostSession(
                                key,
                                app.ProcessId,
                                app.Volume,
                                app.IsMuted,
                                canAdjust,
                                app.ExeName));
                        }
                    }
                }

                var adjustments = _supervisor.OnForegroundChanged(
                    foregroundPid,
                    sessions,
                    mode,
                    attenuatePercent,
                    Environment.ProcessId,
                    foregroundExecutableName);

                foreach (var adjustment in adjustments)
                {
                    IAppItemViewModel app;
                    if (appsByKey.TryGetValue(adjustment.Key, out app) && app != null)
                    {
                        QueueAdjustment(adjustment, app, durationMs);
                    }
                }

                // Audio sessions can disappear while a transition is running.
                // Forgetting the operation prevents a late tick from touching a new
                // session that happens to reuse a provider id.
                foreach (var staleKey in _fades.Keys.Where(key => !appsByKey.ContainsKey(key)).ToList())
                {
                    _fades.Remove(staleKey);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"FocusLostService Apply failed: {ex.Message}");
            }
        }

        private void QueueAdjustment(FocusLostAdjustment adjustment, IAppItemViewModel app, int durationMs)
        {
            FadeOperation existing;
            if (_fades.TryGetValue(adjustment.Key, out existing) &&
                existing.Target.Volume == adjustment.Volume &&
                existing.Target.IsMuted == adjustment.IsMuted &&
                existing.DurationMs == durationMs)
            {
                return;
            }

            _fades.Remove(adjustment.Key);
            if (durationMs <= 0)
            {
                Write(app, adjustment.Volume, adjustment.IsMuted);
                return;
            }

            var currentVolume = app.Volume;
            var currentMuted = app.IsMuted;
            var startVolume = currentVolume;
            var endVolume = adjustment.Volume;

            if (adjustment.IsMuted && !currentMuted)
            {
                // Keep the normal slider value after the fade, but reach silence first.
                endVolume = 0;
            }
            else if (!adjustment.IsMuted && currentMuted)
            {
                // Unmute at zero so restoring focus never exposes the saved level in one jump.
                Write(app, 0, false);
                startVolume = 0;
            }

            if (startVolume == endVolume && currentMuted == adjustment.IsMuted)
            {
                Write(app, adjustment.Volume, adjustment.IsMuted);
                return;
            }

            _fades[adjustment.Key] = new FadeOperation
            {
                Key = adjustment.Key,
                App = app,
                Target = adjustment,
                StartVolume = startVolume,
                EndVolume = endVolume,
                DurationMs = durationMs,
                StartedUtc = DateTime.UtcNow,
            };

            if (!_fadeTimer.IsEnabled)
            {
                _fadeTimer.Start();
            }
        }

        private void AdvanceFades()
        {
            if (_fades.Count == 0)
            {
                _fadeTimer.Stop();
                return;
            }

            foreach (var operation in _fades.Values.ToList())
            {
                try
                {
                    var elapsedMs = (DateTime.UtcNow - operation.StartedUtc).TotalMilliseconds;
                    var progress = operation.DurationMs <= 0 ? 1.0 : elapsedMs / operation.DurationMs;
                    var volume = FocusLostFadePolicy.InterpolateVolume(operation.StartVolume, operation.EndVolume, progress);
                    if (progress >= 1.0)
                    {
                        // For mute, restore the original slider position only after the
                        // volume is silent; for unmute this is the final audible level.
                        Write(operation.App, operation.Target.Volume, operation.Target.IsMuted);
                        _fades.Remove(operation.Key);
                    }
                    else
                    {
                        Write(operation.App, volume, operation.Target.IsMuted && operation.EndVolume != operation.Target.Volume
                            ? false
                            : operation.Target.IsMuted);
                    }
                }
                catch (Exception ex)
                {
                    _fades.Remove(operation.Key);
                    Trace.WriteLine($"FocusLostService fade failed: {ex.Message}");
                }
            }
        }

        private static void Write(IAppItemViewModel app, int volume, bool isMuted)
        {
            if (app is AudioSessionViewModel session)
            {
                session.SetVolumeWithoutUndo(volume);
                session.SetMuteWithoutUndo(isMuted);
                return;
            }

            if (app.Volume != volume)
            {
                app.Volume = volume;
            }

            if (app.IsMuted != isMuted)
            {
                app.IsMuted = isMuted;
            }
        }

        private static string BuildSessionKey(string deviceId, string sessionId)
        {
            return (deviceId ?? "") + "\u001f" + sessionId;
        }

        // Process.GetProcessById(...).ProcessName snapshots every process on the system,
        // so only resolve the name when the foreground process actually changes.
        private string GetForegroundExecutableName(int processId)
        {
            if (processId != _lastForegroundPid)
            {
                _lastForegroundPid = processId;
                _lastForegroundExecutableName = TryGetProcessName(processId);
            }

            return _lastForegroundExecutableName;
        }

        private static string TryGetProcessName(int processId)
        {
            if (processId <= 0)
            {
                return null;
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return null;
            }
        }

        private sealed class FadeOperation
        {
            public string Key { get; set; }
            public IAppItemViewModel App { get; set; }
            public FocusLostAdjustment Target { get; set; }
            public int StartVolume { get; set; }
            public int EndVolume { get; set; }
            public int DurationMs { get; set; }
            public DateTime StartedUtc { get; set; }
        }
    }
}
