using EarTrumpet.Extensions;
using EarTrumpet.Interop;
using EarTrumpet.Interop.MMDeviceAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EarTrumpet.DataModel
{
    /// <summary>
    /// Lightweight endpoint queries for QuickTrumpet default-device presets.
    /// The app only keeps a full device manager for playback; recording endpoints
    /// are read on demand here instead of spinning up a second session-tracking
    /// manager (and its peak meters) just to list microphones.
    /// </summary>
    public static class AudioEndpointHelper
    {
        public class EndpointInfo
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        private static AutoPolicyConfigClientWin7 s_policyConfig;

        public static List<EndpointInfo> GetActiveEndpoints(bool capture)
        {
            var result = new List<EndpointInfo>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                var devices = enumerator.EnumAudioEndpoints(capture ? EDataFlow.eCapture : EDataFlow.eRender, DeviceState.ACTIVE);
                var count = devices.GetCount();
                for (uint i = 0; i < count; i++)
                {
                    var device = devices.Item(i);
                    result.Add(new EndpointInfo { Id = device.GetId(), Name = ReadName(device) });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioEndpointHelper: GetActiveEndpoints({(capture ? "capture" : "render")}) failed - {ex.Message}");
            }
            return result;
        }

        public static EndpointInfo GetDefaultEndpoint(bool capture, ERole role)
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(capture ? EDataFlow.eCapture : EDataFlow.eRender, role);
                return device == null ? null : new EndpointInfo { Id = device.GetId(), Name = ReadName(device) };
            }
            catch (Exception ex) when (ex.Is(HRESULT.ERROR_NOT_FOUND))
            {
                // No endpoint of that kind (e.g. no microphone plugged in).
                return null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioEndpointHelper: GetDefaultEndpoint failed - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true when the endpoint exists and is active right now.
        /// </summary>
        public static bool IsActive(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                return enumerator.GetDevice(deviceId)?.GetState() == DeviceState.ACTIVE;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// PolicyConfig accepts render and capture endpoint ids alike. Verifies the
        /// change so a silently ignored COM call is not reported as applied.
        /// </summary>
        public static bool SetDefaultEndpoint(string deviceId, bool capture, ERole role)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            try
            {
                if (s_policyConfig == null)
                {
                    s_policyConfig = new AutoPolicyConfigClientWin7();
                }
                s_policyConfig.SetDefaultEndpoint(deviceId, role);
                var current = GetDefaultEndpoint(capture, role);
                return string.Equals(current?.Id, deviceId, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioEndpointHelper: SetDefaultEndpoint {role} failed - {ex.Message}");
                return false;
            }
        }

        private static string ReadName(IMMDevice device)
        {
            try
            {
                var store = device.OpenPropertyStore(STGM.STGM_READ);
                return store.GetValue<string>(PropertyKeys.PKEY_Device_FriendlyName) ?? device.GetId();
            }
            catch
            {
                return device.GetId();
            }
        }
    }
}
