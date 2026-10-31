using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReplayKitHelper
{
    // windows keeps a mute per app and output device and starts every new stream of that app muted while it is set, so a mute of obs on the share cable (a click in the volume mixer, or one carried over from an older setup) silences what discord hears from the projector while every meter in obs still moves; share preview owns that cable, so it takes the mute off whenever it turns the share on or checks it
    internal static class ShareCableMute
    {
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, IntPtr context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, IntPtr context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, IntPtr context);
            [PreserveSig] int GetMute(out bool mute);
        }

        // how many streams of obs on the cable were muted and are not now, or -1 when the cable could not be read; obs only has a stream there while it monitors, and windows saves the unmute for the streams it opens later
        public static int UnmuteObs(string deviceId)
        {
            string obs = Constants.ResolveObsExe();
            if (string.IsNullOrEmpty(obs) || string.IsNullOrEmpty(deviceId)) return -1;
            try
            {
                var enumerator = (WasapiInterop.IMMDeviceEnumerator)new WasapiInterop.MMDeviceEnumeratorObject();
                if (enumerator.GetDevice(deviceId, out var device) != 0 || device == null) return -1;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out object manager) != 0) return -1;
                if (((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions) != 0) return -1;
                sessions.GetCount(out int count);
                int unmuted = 0;
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0 || session.GetProcessId(out uint pid) != 0) continue;
                    if (!string.Equals(ProcessPath(pid), obs, StringComparison.OrdinalIgnoreCase)) continue;
                    var volume = (ISimpleAudioVolume)session;
                    if (volume.GetMute(out bool muted) != 0 || !muted) continue;
                    if (volume.SetMute(false, IntPtr.Zero) == 0) unmuted++;
                }
                if (unmuted > 0) Log.Write("share cable: took a Windows mixer mute off " + unmuted + " OBS stream(s)");
                return unmuted;
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
            {
                Log.Write("share cable mute check failed: " + ex.Message);
                return -1;
            }
        }

        private static string ProcessPath(uint pid)
        {
            try { using (var process = Process.GetProcessById((int)pid)) return process.MainModule?.FileName; }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return null; }
        }
    }
}
