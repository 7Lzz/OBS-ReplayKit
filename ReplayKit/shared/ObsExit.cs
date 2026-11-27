using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ReplayKitShared
{
    internal static class ObsExit
    {
        internal const int GracefulWaitMs = 15000;
        internal const int ForceWaitMs = 10000;
        internal const int SetupGateWaitMs = 5000;

        // the names obs runs under; the role host shares obs64, so a caller that needs obs itself checks for its folder too
        internal static readonly string[] ProcessNames = { "obs64", "obs32", "obs" };
        // the folder beside obs64.exe that holds the role host, the obs64.exe copy the helper and the browser pages run under so they show as obs
        internal const string RoleHostFolder = "replaykit-host";

        internal static string GateName(int pid) => @"Local\OBSReplayKit-close-" + pid.ToString(CultureInfo.InvariantCulture);

        // setup keeps this through file replacement, and a detached restart keeps it through relaunch
        internal static IDisposable TryTakeOperation(int waitMs)
        {
            var gate = new Mutex(false, @"Local\OBSReplayKit-runtime-update");
            bool held = false;
            try
            {
                try { held = gate.WaitOne(waitMs); }
                catch (AbandonedMutexException) { held = true; }
                return held ? new Operation(gate) : null;
            }
            finally { if (!held) gate.Dispose(); }
        }

        private sealed class Operation : IDisposable
        {
            private readonly Mutex gate;

            internal Operation(Mutex gate) { this.gate = gate; }

            public void Dispose()
            {
                gate.ReleaseMutex();
                gate.Dispose();
            }
        }

        // callers pin and verify the original process before using this fallback
        internal static bool ForceAndWait(Process process)
        {
            if (process.HasExited) return true;
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { return true; }
            catch (Win32Exception) when (process.HasExited) { return true; }
            return process.WaitForExit(ForceWaitMs);
        }
    }
}
