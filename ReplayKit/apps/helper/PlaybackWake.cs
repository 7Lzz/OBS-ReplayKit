using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayKitHelper
{
    // holds a windows display request while a clip plays. the clips page reports playback in leases so a page that dies mid-play cannot pin the display on; the request belongs to this process, so the sleep override set for obs64.exe does not swallow it
    internal static class PlaybackWake
    {
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
        private static IntPtr _request = IntPtr.Zero;
        // one small string for the life of the process: the request keeps pointing at it while it is held
        private static IntPtr _reason = IntPtr.Zero;
        private static Timer _sweep;
        private static bool _failureLogged;

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
                return _request != IntPtr.Zero;
            }
        }

        private static void Prune()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string key in Leases.Where(l => l.Value <= now).Select(l => l.Key).ToList()) Leases.Remove(key);
        }

        // runs under the lock: drops lapsed leases, then holds or lets go of the request to match what is left
        private static void Update()
        {
            Prune();
            if (Leases.Count > 0)
            {
                if (_request == IntPtr.Zero) Hold();
                if (_sweep == null) _sweep = new Timer(_ => { lock (Gate) Update(); }, null, 5000, 5000);
                return;
            }
            if (_request != IntPtr.Zero) Release();
            if (_sweep != null)
            {
                _sweep.Dispose();
                _sweep = null;
            }
        }

        private static void Hold()
        {
            if (_reason == IntPtr.Zero) _reason = Marshal.StringToHGlobalUni("ReplayKit clip playback");
            var context = new ReasonContext { Version = ContextVersion, Flags = ContextSimpleString, SimpleReasonString = _reason };
            IntPtr handle = PowerCreateRequest(ref context);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                LogFailureOnce("PowerCreateRequest failed (" + Marshal.GetLastWin32Error() + ")");
                return;
            }
            bool display = PowerSetRequest(handle, PowerRequestDisplayRequired);
            bool system = PowerSetRequest(handle, PowerRequestSystemRequired);
            if (!display && !system)
            {
                LogFailureOnce("PowerSetRequest failed (" + Marshal.GetLastWin32Error() + ")");
                CloseHandle(handle);
                return;
            }
            _request = handle;
        }

        private static void Release()
        {
            PowerClearRequest(_request, PowerRequestDisplayRequired);
            PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }

        private static void LogFailureOnce(string what)
        {
            if (_failureLogged) return;
            _failureLogged = true;
            Log.Write("PlaybackWake: " + what + "; the display may sleep while a clip plays.");
        }
    }
}
