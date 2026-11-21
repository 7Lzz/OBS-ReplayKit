using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayKitHelper
{
    // holds a windows display request while a clip plays. the clips page reports playback in leases so a page that dies mid-play cannot pin the display on. the request is held by a child process run as OBSReplayKit.exe (the --playback-wake mode), since this helper runs as the obs64.exe of the process host and windows matches the sleep override set for obs64.exe by image name, which swallowed a request made here
    internal static class PlaybackWake
    {
        internal const string Mode = "--playback-wake";
        private const int LeaseSeconds = 45;
        private const int MaxSources = 16;
        private const int PowerRequestDisplayRequired = 0;
        private const int PowerRequestSystemRequired = 1;
        private const uint ContextVersion = 0;
        private const uint ContextSimpleString = 0x1;

        // REASON_CONTEXT is a union on the native side; only the simple string member is used, and the size is the full x64 size of the union
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct ReasonContext
        {
            [FieldOffset(0)] public uint Version;
            [FieldOffset(4)] public uint Flags;
            [FieldOffset(8)] public IntPtr SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr request, int requestType);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr request, int requestType);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DateTime> Leases = new Dictionary<string, DateTime>();
        private static Process _holder;
        private static Timer _sweep;
        private static bool _failureLogged;
        // a holder that ended on its own is not started again until playback stops, so a holder that cannot start does not respawn every sweep
        private static bool _gaveUp;

        // a page says it is playing (which renews its lease) or that it stopped (which drops it); returns whether the display is held afterwards
        public static bool Report(string source, bool playing)
        {
            lock (Gate)
            {
                if (playing)
                {
                    if (!Leases.ContainsKey(source) && Leases.Count >= MaxSources) Prune();
                    if (Leases.ContainsKey(source) || Leases.Count < MaxSources) Leases[source] = DateTime.UtcNow.AddSeconds(LeaseSeconds);
                }
                else
                {
                    Leases.Remove(source);
                }
                Update();
                return HolderAlive();
            }
        }

        private static void Prune()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string key in Leases.Where(l => l.Value <= now).Select(l => l.Key).ToList()) Leases.Remove(key);
        }

        private static bool HolderAlive()
        {
            try { return _holder != null && !_holder.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        // runs under the lock: drops lapsed leases, then holds or lets go of the request to match what is left
        private static void Update()
        {
            Prune();
            if (Leases.Count > 0)
            {
                if (_holder != null && !HolderAlive())
                {
                    LogFailureOnce("the holder ended with code " + SafeExitCode(_holder));
                    _holder.Dispose();
                    _holder = null;
                    _gaveUp = true;
                }
                if (_holder == null && !_gaveUp) Hold();
                if (_sweep == null) _sweep = new Timer(_ => { lock (Gate) Update(); }, null, 5000, 5000);
                return;
            }
            Release();
            _gaveUp = false;
            if (_sweep != null)
            {
                _sweep.Dispose();
                _sweep = null;
            }
        }

        private static string SafeExitCode(Process process)
        {
            try { return process.ExitCode.ToString(); }
            catch (InvalidOperationException) { return "unknown"; }
        }

        private static void Hold()
        {
            try
            {
                var psi = new ProcessStartInfo(ProcessHost.HelperExecutable, Mode)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };
                _holder = Process.Start(psi);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                LogFailureOnce("the holder could not start: " + ex.Message);
                _holder = null;
                _gaveUp = true;
            }
        }

        // closing its input ends the holder, which lets go of the request as it exits
        private static void Release()
        {
            if (_holder == null) return;
            try
            {
                _holder.StandardInput.Close();
                if (!_holder.WaitForExit(2000)) _holder.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is System.IO.IOException)
            {
                Log.Write("PlaybackWake: releasing the holder: " + ex.Message);
            }
            _holder.Dispose();
            _holder = null;
        }

        private static void LogFailureOnce(string what)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            Log.Write("PlaybackWake: " + what + "; the display may sleep while a clip plays.");
        }

        // the --playback-wake mode: holds the request until its input closes, which happens when the helper lets go or the helper itself is gone
        internal static int RunHolder()
        {
            IntPtr reason = Marshal.StringToHGlobalUni("ReplayKit clip playback");
            var context = new ReasonContext { Version = ContextVersion, Flags = ContextSimpleString, SimpleReasonString = reason };
            IntPtr handle = PowerCreateRequest(ref context);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return 2;
            bool display = PowerSetRequest(handle, PowerRequestDisplayRequired);
            bool system = PowerSetRequest(handle, PowerRequestSystemRequired);
            if (!display && !system)
            {
                CloseHandle(handle);
                return 3;
            }
            try
            {
                using (var input = Console.OpenStandardInput())
                {
                    var buffer = new byte[64];
                    while (input.Read(buffer, 0, buffer.Length) > 0) { }
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is ObjectDisposedException) { }
            PowerClearRequest(handle, PowerRequestDisplayRequired);
            PowerClearRequest(handle, PowerRequestSystemRequired);
            CloseHandle(handle);
            Marshal.FreeHGlobal(reason);
            return 0;
        }
    }
}
