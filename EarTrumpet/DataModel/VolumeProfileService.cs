using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using EarTrumpet.DataModel.WindowsAudio;
using EarTrumpet.Interop.Helpers;
using EarTrumpet.Interop.MMDeviceAPI;
using EarTrumpet.UI.ViewModels;

namespace EarTrumpet.DataModel
{
    /// <summary>
    /// Stores and restores QuickTrumpet presets. A preset can carry any mix of:
    /// default-device assignments (playback / communications / microphone),
    /// device volumes for a chosen set of output devices, and app volumes.
    /// Presets are persisted as JSON in AppSettings.
    /// </summary>
    public class VolumeProfileService
    {
        public const int CurrentSchemaVersion = 3;

        public enum CaptureScope
        {
            CurrentDevice = 0,
            AllDevices = 1,
        }

        /// <summary>The Windows default-device slots a preset can switch.</summary>
        public enum DefaultDeviceRole
        {
            /// <summary>Default output for apps (Console + Multimedia roles).</summary>
            Playback = 0,
            /// <summary>Default communications output (calls, Discord, Teams).</summary>
            PlaybackCommunications = 1,
            /// <summary>Default microphone (Console + Multimedia roles).</summary>
            Recording = 2,
            /// <summary>Default communications microphone.</summary>
            RecordingCommunications = 3,
        }

        public static readonly DefaultDeviceRole[] AllRoles =
        {
            DefaultDeviceRole.Playback,
            DefaultDeviceRole.PlaybackCommunications,
            DefaultDeviceRole.Recording,
            DefaultDeviceRole.RecordingCommunications,
        };

        public class ApplyProfileResult
        {
            public string Name { get; set; }
            public int DevicesApplied { get; set; }
            public int AppsApplied { get; set; }
            public int AppsMissing { get; set; }
            public int AppsRouted { get; set; }
            /// <summary>Default-device switches that succeeded, in role order.</summary>
            public List<AppliedDefault> DefaultsApplied { get; set; } = new List<AppliedDefault>();
            /// <summary>Saved devices (default slots or volume entries) that were not connected.</summary>
            public List<string> DevicesMissing { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
        }

        public class AppliedDefault
        {
            public DefaultDeviceRole Role { get; set; }
            public string DeviceId { get; set; }
            public string DisplayName { get; set; }
        }

        public class AppVolumeEntry
        {
            public string ExeName { get; set; }
            public string AppId { get; set; }
            public string DisplayName { get; set; }
            public string DeviceId { get; set; }
            public string DeviceDisplayName { get; set; }
            public int Volume { get; set; }
            public bool IsMuted { get; set; }
        }

        public class DeviceVolumeEntry
        {
            public string DeviceId { get; set; }
            public string DisplayName { get; set; }
            public int Volume { get; set; }
            public bool IsMuted { get; set; }
            /// <summary>
            /// False when the entry only exists to hold apps captured on this device;
            /// its own volume is then neither applied nor shown. Defaults to true so
            /// presets saved before schema 3 keep applying every device they hold.
            /// </summary>
            public bool IncludeVolume { get; set; } = true;
            public List<AppVolumeEntry> Apps { get; set; } = new List<AppVolumeEntry>();
        }

        public class DefaultDeviceEntry
        {
            public string DeviceId { get; set; }
            public string DisplayName { get; set; }
        }

        public class DefaultDeviceSet
        {
            public DefaultDeviceEntry Playback { get; set; }
            public DefaultDeviceEntry PlaybackCommunications { get; set; }
            public DefaultDeviceEntry Recording { get; set; }
            public DefaultDeviceEntry RecordingCommunications { get; set; }

            [Newtonsoft.Json.JsonIgnore]
            public bool IsEmpty => AllRoles.All(role => Get(role) == null);

            public DefaultDeviceEntry Get(DefaultDeviceRole role)
            {
                switch (role)
                {
                    case DefaultDeviceRole.Playback: return Valid(Playback);
                    case DefaultDeviceRole.PlaybackCommunications: return Valid(PlaybackCommunications);
                    case DefaultDeviceRole.Recording: return Valid(Recording);
                    case DefaultDeviceRole.RecordingCommunications: return Valid(RecordingCommunications);
                    default: return null;
                }
            }

            public void Set(DefaultDeviceRole role, DefaultDeviceEntry entry)
            {
                entry = Valid(entry);
                switch (role)
                {
                    case DefaultDeviceRole.Playback: Playback = entry; break;
                    case DefaultDeviceRole.PlaybackCommunications: PlaybackCommunications = entry; break;
                    case DefaultDeviceRole.Recording: Recording = entry; break;
                    case DefaultDeviceRole.RecordingCommunications: RecordingCommunications = entry; break;
                }
            }

            private static DefaultDeviceEntry Valid(DefaultDeviceEntry entry) =>
                entry == null || string.IsNullOrWhiteSpace(entry.DeviceId) ? null : entry;
        }

        public class VolumeProfile
        {
            public int SchemaVersion { get; set; } = CurrentSchemaVersion;
            public string Id { get; set; }
            public string Name { get; set; }
            public string Slug { get; set; }
            public string CreatedAt { get; set; }
            public string UpdatedAt { get; set; }
            public CaptureScope CaptureScope { get; set; }

            /// <summary>Apply the volume/mute of the device entries that have IncludeVolume.</summary>
            public bool IncludeDeviceVolumes { get; set; } = true;

            /// <summary>Apply the saved app volumes/mute states.</summary>
            public bool IncludeAppVolumes { get; set; } = true;

            /// <summary>
            /// Pin each saved app back to the device it was captured on. Presets saved
            /// before schema 3 always did this, so the default stays true for them;
            /// new presets opt in explicitly.
            /// </summary>
            public bool RouteApps { get; set; } = true;

            /// <summary>Optional default-device switching. Empty = leave defaults alone.</summary>
            public DefaultDeviceSet DefaultDevices { get; set; } = new DefaultDeviceSet();

            /// <summary>
            /// Schema 2 flag, kept as a view of IncludeDeviceVolumes so older presets,
            /// exports and older app versions keep reading the same meaning.
            /// </summary>
            public bool ApplyAppsOnly
            {
                get => !IncludeDeviceVolumes;
                set => IncludeDeviceVolumes = !value;
            }

            public HotkeyData Hotkey { get; set; } = new HotkeyData();
            public List<DeviceVolumeEntry> Devices { get; set; } = new List<DeviceVolumeEntry>();

            [Newtonsoft.Json.JsonIgnore]
            public IEnumerable<DeviceVolumeEntry> VolumeDevices => (Devices ?? new List<DeviceVolumeEntry>()).Where(d => d.IncludeVolume);

            [Newtonsoft.Json.JsonIgnore]
            public IEnumerable<AppVolumeEntry> AllApps => (Devices ?? new List<DeviceVolumeEntry>()).SelectMany(d => d.Apps ?? new List<AppVolumeEntry>());
        }

        /// <summary>What a new capture should contain.</summary>
        public class CaptureOptions
        {
            public bool IncludeDeviceVolumes { get; set; } = true;
            /// <summary>Output devices whose volume is saved. Null = the current default device.</summary>
            public ICollection<string> DeviceIds { get; set; }
            public bool AllDevices { get; set; }
            public bool IncludeAppVolumes { get; set; } = true;
            /// <summary>When false, only apps playing on the volume devices are saved (legacy CLI behavior).</summary>
            public bool AppsFromAllDevices { get; set; } = true;
            public bool RouteApps { get; set; }
            /// <summary>Default-device slots to capture. Null or empty = none.</summary>
            public ICollection<DefaultDeviceRole> DefaultRoles { get; set; }
        }

        private readonly AppSettings _settings;
        private string _loadedJson;
        public ObservableCollection<VolumeProfile> Profiles { get; } = new ObservableCollection<VolumeProfile>();

        public VolumeProfileService(AppSettings settings)
        {
            _settings = settings;
            LoadProfiles();
        }

        /// <summary>
        /// Several services can be alive at once (settings page, hotkeys, CLI) and
        /// settings import writes the JSON directly. Reloads when the stored JSON no
        /// longer matches what this instance loaded or last wrote.
        /// </summary>
        public bool RefreshIfStale()
        {
            var json = _settings.VolumeProfilesJson;
            if (string.Equals(json, _loadedJson, StringComparison.Ordinal)) return false;
            Profiles.Clear();
            LoadProfiles();
            return true;
        }

        // ── Capture ──────────────────────────────────────────────────────────

        /// <summary>
        /// Legacy capture used by the CLI and the classic settings window: the default
        /// (or every) device with the apps playing on it, routed back on apply.
        /// </summary>
        public VolumeProfile CaptureCurrentState(string profileName, DeviceCollectionViewModel collection, CaptureScope captureScope = CaptureScope.CurrentDevice)
        {
            var profile = Capture(profileName, collection, new CaptureOptions
            {
                AllDevices = captureScope == CaptureScope.AllDevices,
                AppsFromAllDevices = captureScope == CaptureScope.AllDevices,
                RouteApps = true,
            });
            profile.CaptureScope = captureScope;
            return profile;
        }

        public VolumeProfile Capture(string profileName, DeviceCollectionViewModel collection, CaptureOptions options)
        {
            options = options ?? new CaptureOptions();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var profile = new VolumeProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = profileName,
                Slug = ToSlug(profileName),
                CreatedAt = now,
                UpdatedAt = now,
                CaptureScope = options.AllDevices ? CaptureScope.AllDevices : CaptureScope.CurrentDevice,
                IncludeDeviceVolumes = options.IncludeDeviceVolumes,
                IncludeAppVolumes = options.IncludeAppVolumes,
                RouteApps = options.RouteApps,
            };

            var volumeIds = ResolveVolumeDeviceIds(collection, options);
            FillDevices(profile, collection, volumeIds, options.IncludeAppVolumes, options.AppsFromAllDevices);

            if (options.DefaultRoles != null)
            {
                foreach (var role in options.DefaultRoles.Distinct())
                {
                    profile.DefaultDevices.Set(role, ReadCurrentDefault(role));
                }
            }

            return profile;
        }

        /// <summary>
        /// Overwrites the captured values with the current state while keeping what the
        /// preset is made of: the same volume devices, every open app, and the same
        /// default-device slots.
        /// </summary>
        public void UpdateFromCurrentState(VolumeProfile profile, DeviceCollectionViewModel collection)
        {
            if (profile == null || collection == null) return;

            var volumeIds = new HashSet<string>(profile.VolumeDevices.Select(d => d.DeviceId).Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            var stale = profile.VolumeDevices.Where(d => FindDevice(collection, d) == null).ToList();
            var captureApps = profile.IncludeAppVolumes || profile.AllApps.Any();

            profile.Devices = new List<DeviceVolumeEntry>();
            FillDevices(profile, collection, volumeIds, captureApps, appsFromAllDevices: true);

            // A disconnected device keeps its saved values instead of silently vanishing.
            foreach (var missing in stale)
            {
                missing.Apps = new List<AppVolumeEntry>();
                profile.Devices.Add(missing);
            }

            foreach (var role in AllRoles)
            {
                if (profile.DefaultDevices.Get(role) != null)
                {
                    profile.DefaultDevices.Set(role, ReadCurrentDefault(role) ?? profile.DefaultDevices.Get(role));
                }
            }

            profile.SchemaVersion = CurrentSchemaVersion;
            Touch(profile);
        }

        private static HashSet<string> ResolveVolumeDeviceIds(DeviceCollectionViewModel collection, CaptureOptions options)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (collection == null) return ids;

            if (options.AllDevices)
            {
                foreach (var device in collection.AllDevices) ids.Add(device.Id);
            }
            else if (options.DeviceIds != null)
            {
                foreach (var id in options.DeviceIds.Where(id => !string.IsNullOrWhiteSpace(id))) ids.Add(id);
            }
            else
            {
                var current = collection.Default ?? collection.AllDevices.FirstOrDefault();
                if (current != null) ids.Add(current.Id);
            }
            return ids;
        }

        private static void FillDevices(VolumeProfile profile, DeviceCollectionViewModel collection, HashSet<string> volumeIds, bool includeApps, bool appsFromAllDevices)
        {
            if (collection == null) return;

            foreach (var device in collection.AllDevices)
            {
                var hasVolume = volumeIds.Contains(device.Id);
                var takeApps = includeApps && (appsFromAllDevices || hasVolume);
                var apps = takeApps
                    ? device.Apps.Select(app => new AppVolumeEntry
                    {
                        ExeName = app.ExeName,
                        AppId = app.AppId,
                        DisplayName = app.DisplayName,
                        DeviceId = device.Id,
                        DeviceDisplayName = device.DisplayName,
                        Volume = app.Volume,
                        IsMuted = app.IsMuted
                    }).ToList()
                    : new List<AppVolumeEntry>();

                if (!hasVolume && apps.Count == 0) continue;

                profile.Devices.Add(new DeviceVolumeEntry
                {
                    DeviceId = device.Id,
                    DisplayName = device.DisplayName,
                    Volume = device.Volume,
                    IsMuted = device.IsMuted,
                    IncludeVolume = hasVolume,
                    Apps = apps
                });
            }
        }

        public static DefaultDeviceEntry ReadCurrentDefault(DefaultDeviceRole role)
        {
            var endpoint = AudioEndpointHelper.GetDefaultEndpoint(IsCaptureRole(role), PrimaryRole(role));
            return endpoint == null ? null : new DefaultDeviceEntry { DeviceId = endpoint.Id, DisplayName = endpoint.Name };
        }

        public static bool IsCaptureRole(DefaultDeviceRole role) =>
            role == DefaultDeviceRole.Recording || role == DefaultDeviceRole.RecordingCommunications;

        private static ERole PrimaryRole(DefaultDeviceRole role) =>
            role == DefaultDeviceRole.PlaybackCommunications || role == DefaultDeviceRole.RecordingCommunications
                ? ERole.eCommunications
                : ERole.eMultimedia;

        // ── Apply ────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply a saved profile to current devices
        /// </summary>
        public ApplyProfileResult ApplyProfile(VolumeProfile profile, DeviceCollectionViewModel collection, IAudioDeviceManagerWindowsAudio deviceManager = null)
        {
            var result = new ApplyProfileResult { Name = profile?.Name };
            if (profile == null || collection == null) return result;

            // Default devices first, so the volumes below land on the new setup.
            ApplyDefaultDevices(profile, collection, result);

            // Suppress undo recording for bulk profile restore
            App.UndoService.BeginUndoRedo();
            try
            {
                if (profile.IncludeDeviceVolumes)
                {
                    foreach (var savedDevice in profile.VolumeDevices)
                    {
                        var device = FindDevice(collection, savedDevice);
                        if (device == null)
                        {
                            AddMissing(result, savedDevice.DisplayName);
                            continue;
                        }

                        device.Volume = savedDevice.Volume;
                        device.IsMuted = savedDevice.IsMuted;
                        result.DevicesApplied++;
                    }
                }

                if (profile.IncludeAppVolumes)
                {
                    foreach (var savedDevice in profile.Devices)
                    {
                        var device = FindDevice(collection, savedDevice);
                        foreach (var savedApp in savedDevice.Apps)
                        {
                            var routed = 0;
                            if (profile.RouteApps && deviceManager != null && device != null)
                            {
                                routed = TryRouteApp(savedApp, device, collection, deviceManager);
                                result.AppsRouted += routed;
                            }

                            if (ApplyAppState(savedApp, collection))
                            {
                                result.AppsApplied++;
                                if (routed > 0)
                                {
                                    ApplyAppStateAfterRoutingSettles(savedApp, collection);
                                }
                            }
                            else
                            {
                                result.AppsMissing++;
                                var warning = $"App not found: {savedApp.DisplayName} ({savedApp.ExeName})";
                                result.Warnings.Add(warning);
                                Trace.WriteLine($"VolumeProfileService: {warning}");
                            }
                        }
                    }
                }
            }
            finally
            {
                App.UndoService.EndUndoRedo();
            }

            if (!string.IsNullOrWhiteSpace(profile.Id))
            {
                _settings.QuickTrumpetLastAppliedId = profile.Id;
            }

            Trace.WriteLine($"VolumeProfileService: Applied profile '{profile.Name}' (defaults {result.DefaultsApplied.Count}, devices {result.DevicesApplied}, apps {result.AppsApplied}, missing {result.DevicesMissing.Count})");
            return result;
        }

        private static void ApplyDefaultDevices(VolumeProfile profile, DeviceCollectionViewModel collection, ApplyProfileResult result)
        {
            if (profile.DefaultDevices == null || profile.DefaultDevices.IsEmpty) return;

            foreach (var role in AllRoles)
            {
                var entry = profile.DefaultDevices.Get(role);
                if (entry == null) continue;

                var capture = IsCaptureRole(role);
                if (!AudioEndpointHelper.IsActive(entry.DeviceId))
                {
                    AddMissing(result, entry.DisplayName ?? entry.DeviceId);
                    continue;
                }

                var ok = true;
                if (role == DefaultDeviceRole.Playback)
                {
                    // Prefer the view model path for the multimedia role: it commits the
                    // new default immediately so the flyout updates even when Windows
                    // never confirms (virtual/RDP endpoints).
                    var device = collection.AllDevices.FirstOrDefault(d => string.Equals(d.Id, entry.DeviceId, StringComparison.OrdinalIgnoreCase));
                    if (device != null)
                    {
                        device.MakeDefaultDevice();
                        ok &= device.TryMakeDefaultDevice(ERole.eConsole);
                    }
                    else
                    {
                        ok &= AudioEndpointHelper.SetDefaultEndpoint(entry.DeviceId, false, ERole.eMultimedia);
                        ok &= AudioEndpointHelper.SetDefaultEndpoint(entry.DeviceId, false, ERole.eConsole);
                    }
                }
                else if (role == DefaultDeviceRole.Recording)
                {
                    ok &= AudioEndpointHelper.SetDefaultEndpoint(entry.DeviceId, true, ERole.eMultimedia);
                    ok &= AudioEndpointHelper.SetDefaultEndpoint(entry.DeviceId, true, ERole.eConsole);
                }
                else
                {
                    ok &= AudioEndpointHelper.SetDefaultEndpoint(entry.DeviceId, capture, ERole.eCommunications);
                }

                if (ok)
                {
                    result.DefaultsApplied.Add(new AppliedDefault { Role = role, DeviceId = entry.DeviceId, DisplayName = entry.DisplayName });
                }
                else
                {
                    var warning = $"Windows did not switch the {role} default to {entry.DisplayName}";
                    result.Warnings.Add(warning);
                    Trace.WriteLine($"VolumeProfileService: {warning}");
                }
            }
        }

        private static void AddMissing(ApplyProfileResult result, string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "?" : name;
            if (!result.DevicesMissing.Contains(name)) result.DevicesMissing.Add(name);
            var warning = $"Device not found: {name}";
            result.Warnings.Add(warning);
            Trace.WriteLine($"VolumeProfileService: {warning}");
        }

        private static DeviceViewModel FindDevice(DeviceCollectionViewModel collection, DeviceVolumeEntry savedDevice)
        {
            // Match device by ID first, then by display name
            return collection.AllDevices.FirstOrDefault(d => d.Id == savedDevice.DeviceId)
                ?? collection.AllDevices.FirstOrDefault(d => d.DisplayName == savedDevice.DisplayName);
        }

        private static bool ApplyAppState(AppVolumeEntry savedApp, DeviceCollectionViewModel collection)
        {
            var matches = collection.AllDevices
                .SelectMany(d => d.Apps)
                .Where(a =>
                    (!string.IsNullOrEmpty(savedApp.ExeName) && string.Equals(a.ExeName, savedApp.ExeName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(savedApp.AppId) && string.Equals(a.AppId, savedApp.AppId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var app in matches)
            {
                app.Volume = savedApp.Volume;
                app.IsMuted = savedApp.IsMuted;
            }

            return matches.Count > 0;
        }

        private static void ApplyAppStateAfterRoutingSettles(AppVolumeEntry savedApp, DeviceCollectionViewModel collection)
        {
            var remainingTicks = 3;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) =>
            {
                ApplyAppState(savedApp, collection);
                remainingTicks--;
                if (remainingTicks <= 0)
                {
                    timer.Stop();
                }
            };
            timer.Start();
        }

        // ── Lookup / cycling ─────────────────────────────────────────────────

        public VolumeProfile FindProfile(string nameOrSlug)
        {
            if (string.IsNullOrWhiteSpace(nameOrSlug)) return null;
            var slug = ToSlug(nameOrSlug);
            return Profiles.FirstOrDefault(p => string.Equals(GetSlug(p), slug, StringComparison.OrdinalIgnoreCase))
                ?? Profiles.FirstOrDefault(p => string.Equals(p.Name, nameOrSlug, StringComparison.OrdinalIgnoreCase));
        }

        public VolumeProfile FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The preset a "next" (+1) or "previous" (-1) shortcut should apply: list order,
        /// wrapping around, starting from the last applied preset.
        /// </summary>
        public VolumeProfile GetCycleTarget(int direction)
        {
            if (Profiles.Count == 0) return null;
            var current = FindById(_settings.QuickTrumpetLastAppliedId);
            var index = current == null ? -1 : Profiles.IndexOf(current);
            int next;
            if (index < 0)
            {
                next = direction >= 0 ? 0 : Profiles.Count - 1;
            }
            else
            {
                next = ((index + (direction >= 0 ? 1 : -1)) % Profiles.Count + Profiles.Count) % Profiles.Count;
            }
            return Profiles[next];
        }

        // ── Persistence / editing ────────────────────────────────────────────

        public void SaveProfile(VolumeProfile profile)
        {
            // Replace existing profile with same name, or add new
            var existing = Profiles.FirstOrDefault(p => p.Name == profile.Name);
            if (existing != null)
            {
                var idx = Profiles.IndexOf(existing);
                if (!ReferenceEquals(existing, profile))
                {
                    profile.Id = string.IsNullOrWhiteSpace(existing.Id) ? profile.Id : existing.Id;
                    profile.CreatedAt = string.IsNullOrWhiteSpace(existing.CreatedAt) ? profile.CreatedAt : existing.CreatedAt;
                    // Overwriting a preset by name must not drop the shortcut bound to it.
                    if ((profile.Hotkey == null || profile.Hotkey.IsEmpty) && existing.Hotkey != null)
                    {
                        profile.Hotkey = existing.Hotkey;
                    }
                }
                profile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                Profiles[idx] = profile;
            }
            else
            {
                Profiles.Add(profile);
            }
            PersistProfiles();
        }

        /// <summary>Persist an in-place edit of a preset already in the list.</summary>
        public void CommitEdit(VolumeProfile profile)
        {
            if (profile == null) return;
            Touch(profile);
            PersistProfiles();
        }

        public void DeleteProfile(VolumeProfile profile)
        {
            Profiles.Remove(profile);
            PersistProfiles();
        }

        public void RenameProfile(VolumeProfile profile, string newName)
        {
            profile.Name = newName;
            PersistProfiles();
        }

        public bool SetDeviceEntry(VolumeProfile profile, string deviceId, int? volume, bool? muted)
        {
            var entry = profile?.Devices?.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            if (volume.HasValue) entry.Volume = Math.Max(0, Math.Min(100, volume.Value));
            if (muted.HasValue) entry.IsMuted = muted.Value;
            CommitEdit(profile);
            return true;
        }

        public bool RemoveDeviceEntry(VolumeProfile profile, string deviceId)
        {
            var entry = profile?.Devices?.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            // The entry may still carry the apps captured on that device.
            if (entry.Apps != null && entry.Apps.Count > 0) entry.IncludeVolume = false;
            else profile.Devices.Remove(entry);
            CommitEdit(profile);
            return true;
        }

        /// <summary>Add (or re-include) an output device with its current volume.</summary>
        public bool AddDeviceEntry(VolumeProfile profile, DeviceCollectionViewModel collection, string deviceId)
        {
            if (profile == null || collection == null) return false;
            var device = collection.AllDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device == null) return false;

            var entry = profile.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new DeviceVolumeEntry { DeviceId = device.Id };
                profile.Devices.Add(entry);
            }
            entry.DisplayName = device.DisplayName;
            entry.Volume = device.Volume;
            entry.IsMuted = device.IsMuted;
            entry.IncludeVolume = true;
            profile.IncludeDeviceVolumes = true;
            CommitEdit(profile);
            return true;
        }

        public static string GetAppKey(AppVolumeEntry app) =>
            $"{app.DeviceId}|{(string.IsNullOrWhiteSpace(app.ExeName) ? app.AppId : app.ExeName)}";

        public bool SetAppEntry(VolumeProfile profile, string appKey, int? volume, bool? muted)
        {
            var apps = profile?.AllApps.Where(a => string.Equals(GetAppKey(a), appKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (apps == null || apps.Count == 0) return false;
            foreach (var app in apps)
            {
                if (volume.HasValue) app.Volume = Math.Max(0, Math.Min(100, volume.Value));
                if (muted.HasValue) app.IsMuted = muted.Value;
            }
            CommitEdit(profile);
            return true;
        }

        public bool RemoveAppEntry(VolumeProfile profile, string appKey)
        {
            if (profile?.Devices == null) return false;
            var removed = false;
            foreach (var device in profile.Devices.ToList())
            {
                removed |= device.Apps.RemoveAll(a => string.Equals(GetAppKey(a), appKey, StringComparison.OrdinalIgnoreCase)) > 0;
                if (!device.IncludeVolume && device.Apps.Count == 0) profile.Devices.Remove(device);
            }
            if (removed) CommitEdit(profile);
            return removed;
        }

        /// <summary>Set a default-device slot. A null/empty id clears the slot ("don't change").</summary>
        public void SetDefaultDevice(VolumeProfile profile, DefaultDeviceRole role, string deviceId)
        {
            if (profile == null) return;
            if (profile.DefaultDevices == null) profile.DefaultDevices = new DefaultDeviceSet();

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                profile.DefaultDevices.Set(role, null);
            }
            else
            {
                var endpoint = AudioEndpointHelper.GetActiveEndpoints(IsCaptureRole(role))
                    .FirstOrDefault(e => string.Equals(e.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                var name = endpoint?.Name ?? profile.DefaultDevices.Get(role)?.DisplayName ?? deviceId;
                profile.DefaultDevices.Set(role, new DefaultDeviceEntry { DeviceId = deviceId, DisplayName = name });
            }
            CommitEdit(profile);
        }

        private static void Touch(VolumeProfile profile)
        {
            profile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        private void PersistProfiles()
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(Profiles.ToList(), Newtonsoft.Json.Formatting.Indented);
                _settings.VolumeProfilesJson = json;
                _loadedJson = json;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfileService: PersistProfiles failed - {ex.Message}");
            }
        }

        private void LoadProfiles()
        {
            var json = _settings.VolumeProfilesJson;
            _loadedJson = json;
            try
            {
                var list = ParseProfiles(json, out var assignedIds);
                foreach (var profile in list)
                {
                    Profiles.Add(profile);
                }

                // Pre-Id presets get a stable id once, so "last applied" and the web
                // editor can address them across service instances.
                if (assignedIds)
                {
                    PersistProfiles();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfileService: LoadProfiles failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Parses and migrates stored presets without touching settings. Used by the
        /// CLI listing and the hotkey registration, which only need a read.
        /// </summary>
        public static List<VolumeProfile> ParseProfiles(string json)
        {
            try
            {
                return ParseProfiles(json, out _);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfileService: ParseProfiles failed - {ex.Message}");
                return new List<VolumeProfile>();
            }
        }

        private static List<VolumeProfile> ParseProfiles(string json, out bool assignedIds)
        {
            assignedIds = false;
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return new List<VolumeProfile>();
            // A null once written through the registry bag is stored as an XML-serialized
            // nil string ("<?xml ...nil=true />"); it means "no presets", not corrupt JSON.
            if (json.TrimStart().StartsWith("<")) return new List<VolumeProfile>();
            var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<VolumeProfile>>(json) ?? new List<VolumeProfile>();
            list.RemoveAll(p => p == null);
            foreach (var profile in list)
            {
                assignedIds |= NormalizeProfile(profile);
            }
            return list;
        }

        /// <summary>
        /// Export a profile to JSON string
        /// </summary>
        public string ExportProfile(VolumeProfile profile)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(profile, Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// Import a profile from JSON string
        /// </summary>
        public VolumeProfile ImportProfile(string json)
        {
            try
            {
                var profile = Newtonsoft.Json.JsonConvert.DeserializeObject<VolumeProfile>(json);
                if (profile != null)
                {
                    NormalizeProfile(profile);
                    // A re-imported export must not collide with the original's id.
                    if (Profiles.Any(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        profile.Id = Guid.NewGuid().ToString("N");
                    }
                    // Ensure unique name
                    if (Profiles.Any(p => p.Name == profile.Name))
                    {
                        profile.Name = profile.Name + " (Imported)";
                    }
                    Profiles.Add(profile);
                    PersistProfiles();
                }
                return profile;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"VolumeProfileService: ImportProfile failed - {ex.Message}");
                return null;
            }
        }

        public static string ToSlug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = value.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var slug = new string(chars);
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Trim('-');
        }

        public static string GetSlug(VolumeProfile profile)
        {
            if (profile == null) return string.Empty;
            return string.IsNullOrWhiteSpace(profile.Slug) ? ToSlug(profile.Name) : profile.Slug;
        }

        /// <summary>
        /// Fills fields missing from older presets. Schema 1/2 presets deserialize with
        /// IncludeDeviceVolumes derived from ApplyAppsOnly, IncludeAppVolumes = true,
        /// RouteApps = true, every device entry included and no default devices —
        /// exactly how they applied before. Returns true when an id had to be created.
        /// </summary>
        private static bool NormalizeProfile(VolumeProfile profile)
        {
            if (profile == null) return false;
            var assignedId = false;
            if (profile.SchemaVersion <= 0) profile.SchemaVersion = 1;
            if (string.IsNullOrWhiteSpace(profile.Id)) { profile.Id = Guid.NewGuid().ToString("N"); assignedId = true; }
            if (string.IsNullOrWhiteSpace(profile.Slug)) profile.Slug = ToSlug(profile.Name);
            if (string.IsNullOrWhiteSpace(profile.UpdatedAt)) profile.UpdatedAt = profile.CreatedAt;
            if (profile.Hotkey == null) profile.Hotkey = new HotkeyData();
            if (profile.DefaultDevices == null) profile.DefaultDevices = new DefaultDeviceSet();
            if (profile.Devices == null) profile.Devices = new List<DeviceVolumeEntry>();
            profile.Devices.RemoveAll(d => d == null);
            foreach (var device in profile.Devices)
            {
                if (device.Apps == null) device.Apps = new List<AppVolumeEntry>();
                device.Apps.RemoveAll(a => a == null);
                foreach (var app in device.Apps)
                {
                    if (string.IsNullOrWhiteSpace(app.DeviceId)) app.DeviceId = device.DeviceId;
                    if (string.IsNullOrWhiteSpace(app.DeviceDisplayName)) app.DeviceDisplayName = device.DisplayName;
                }
            }
            return assignedId;
        }

        private static int TryRouteApp(AppVolumeEntry savedApp, DeviceViewModel targetDevice, DeviceCollectionViewModel collection, IAudioDeviceManagerWindowsAudio deviceManager)
        {
            if (savedApp == null || targetDevice == null || collection == null || deviceManager == null)
            {
                return 0;
            }

            var routed = 0;
            foreach (var device in collection.AllDevices)
            {
                foreach (var app in device.Apps)
                {
                    if ((!string.IsNullOrWhiteSpace(savedApp.ExeName) && string.Equals(app.ExeName, savedApp.ExeName, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(savedApp.AppId) && string.Equals(app.AppId, savedApp.AppId, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            deviceManager.SetDefaultEndPoint(targetDevice.Id, app.ProcessId);
                            routed++;
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"VolumeProfileService: Failed to route {savedApp.DisplayName} - {ex.Message}");
                        }
                    }
                }
            }
            return routed;
        }
    }
}
