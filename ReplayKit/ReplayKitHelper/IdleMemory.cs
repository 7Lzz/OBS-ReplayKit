using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // gives memory back to windows after a burst of work. the collector frees what nothing reaches on its own schedule, but keeps the freed room of its heap committed for later, so the process stays as big as its busiest moment; one compacting full collection once the helper has gone quiet and grown since the last one hands that room back
    internal static class IdleMemory
    {
        private const int SweepMs = 30000;
        private const long GrowthBytes = 16L * 1024 * 1024;

        private static Timer _sweep;
        private static long _baseline;
        private static int _running;

        public static void Start()
        {
            _baseline = PrivateBytes();
            _sweep = new Timer(_ => Sweep(), null, SweepMs, SweepMs);
        }

        public static void Stop() => _sweep?.Dispose();

        private static long PrivateBytes()
        {
            using (var self = Process.GetCurrentProcess()) return self.PrivateMemorySize64;
        }

        private static void Sweep()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            try
            {
                // a job in flight is still using what it allocated, and a collection now would only pause it
                if ((UploadState.GetUploadStatusSnapshot()["activeJobs"]?.Value<int>() ?? 0) > 0) return;
                long before = PrivateBytes();
                if (before - _baseline < GrowthBytes) return;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                _baseline = PrivateBytes();
                Log.Write("idle memory: " + (before >> 20) + " MB -> " + (_baseline >> 20) + " MB");
            }
            catch (Exception ex) { Log.Write("idle memory sweep: " + ex.Message); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }
    }
}
