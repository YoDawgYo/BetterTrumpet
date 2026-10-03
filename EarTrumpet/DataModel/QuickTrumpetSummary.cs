using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static EarTrumpet.DataModel.VolumeProfileService;

namespace EarTrumpet.DataModel
{
    /// <summary>
    /// Localized one-line descriptions of what a QuickTrumpet preset holds and of
    /// what applying it changed. Shared by the confirmation toast, the web settings
    /// list and the classic settings window.
    /// </summary>
    public static class QuickTrumpetSummary
    {
        private const string Separator = " · ";

        /// <summary>"Speakers + Desk mic · 2 devices · 3 apps"</summary>
        public static string DescribeProfile(VolumeProfile profile)
        {
            if (profile == null) return string.Empty;
            var parts = new List<string>();

            var defaults = profile.DefaultDevices == null
                ? new List<string>()
                : AllRoles.Select(role => profile.DefaultDevices.Get(role)?.DisplayName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            if (defaults.Count > 0)
            {
                parts.Add(defaults.Count <= 2 ? string.Join(" + ", defaults) : $"{defaults[0]} + {defaults[1]} +{defaults.Count - 2}");
            }

            var devices = profile.IncludeDeviceVolumes ? profile.VolumeDevices.Count() : 0;
            if (devices > 0) parts.Add(CountDevices(devices));

            var apps = profile.IncludeAppVolumes ? CountDistinctApps(profile.AllApps) : 0;
            if (apps > 0) parts.Add(CountApps(apps));

            return parts.Count == 0 ? R("QuickTrumpetSummaryEmpty") : string.Join(Separator, parts);
        }

        /// <summary>"Output → Speakers · Mic → Desk mic · 2 devices · 3 apps" (+ a "Not found" line).</summary>
        public static string DescribeResult(ApplyProfileResult result)
        {
            if (result == null) return string.Empty;
            var parts = new List<string>();

            var applied = result.DefaultsApplied ?? new List<AppliedDefault>();
            string MainId(DefaultDeviceRole role) => applied.FirstOrDefault(a => a.Role == role)?.DeviceId;
            foreach (var item in applied)
            {
                // A communications slot that matches its main slot adds nothing to read.
                if (item.Role == DefaultDeviceRole.PlaybackCommunications && string.Equals(item.DeviceId, MainId(DefaultDeviceRole.Playback), StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Role == DefaultDeviceRole.RecordingCommunications && string.Equals(item.DeviceId, MainId(DefaultDeviceRole.Recording), StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add(string.Format(CultureInfo.CurrentCulture, R(RoleFormatKey(item.Role)), item.DisplayName));
            }

            if (result.DevicesApplied > 0) parts.Add(CountDevices(result.DevicesApplied));
            if (result.AppsApplied > 0) parts.Add(CountApps(result.AppsApplied));

            var text = parts.Count == 0 ? R("QuickTrumpetSummaryNothing") : string.Join(Separator, parts);
            if (result.DevicesMissing != null && result.DevicesMissing.Count > 0)
            {
                text += "\n" + string.Format(CultureInfo.CurrentCulture, R("QuickTrumpetSummaryMissingFormat"), string.Join(", ", result.DevicesMissing));
            }
            return text;
        }

        public static string RoleLabel(DefaultDeviceRole role)
        {
            switch (role)
            {
                case DefaultDeviceRole.Playback: return R("QuickTrumpetRolePlayback");
                case DefaultDeviceRole.PlaybackCommunications: return R("QuickTrumpetRolePlaybackComms");
                case DefaultDeviceRole.Recording: return R("QuickTrumpetRoleRecording");
                default: return R("QuickTrumpetRoleRecordingComms");
            }
        }

        public static int CountDistinctApps(IEnumerable<AppVolumeEntry> apps) =>
            apps.Select(a => string.IsNullOrWhiteSpace(a.ExeName) ? a.AppId : a.ExeName)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

        public static string CountDevices(int count) =>
            count == 1 ? R("QuickTrumpetSummaryDeviceOne") : string.Format(CultureInfo.CurrentCulture, R("QuickTrumpetSummaryDevicesFormat"), count);

        public static string CountApps(int count) =>
            count == 1 ? R("QuickTrumpetSummaryAppOne") : string.Format(CultureInfo.CurrentCulture, R("QuickTrumpetSummaryAppsFormat"), count);

        private static string RoleFormatKey(DefaultDeviceRole role)
        {
            switch (role)
            {
                case DefaultDeviceRole.Playback: return "QuickTrumpetSummaryPlaybackFormat";
                case DefaultDeviceRole.PlaybackCommunications: return "QuickTrumpetSummaryPlaybackCommsFormat";
                case DefaultDeviceRole.Recording: return "QuickTrumpetSummaryRecordingFormat";
                default: return "QuickTrumpetSummaryRecordingCommsFormat";
            }
        }

        private static string R(string key) =>
            EarTrumpet.Properties.Resources.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }
}
