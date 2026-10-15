using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReplayKitHelper
{
    internal sealed class PreviewJobs
    {
        private sealed class Job
        {
            internal bool Running = true;
            internal string Error;
            internal DateTime FinishedAt;
        }

        private static readonly SemaphoreSlim Slots = new SemaphoreSlim(2);
        private readonly object gate = new object();
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();

        internal string Ensure(string key, Action work)
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                foreach (var old in jobs.Where(p => !p.Value.Running && (now - p.Value.FinishedAt).TotalSeconds >= 10).Select(p => p.Key).ToList())
                    jobs.Remove(old);
                if (jobs.TryGetValue(key, out var existing)) return existing.Running ? null : existing.Error;
                // Describe is polled by the editor. Saturated requests stay pending without allocating queued workers.
                if (!Slots.Wait(0)) return null;
                foreach (var old in jobs.Where(p => !p.Value.Running).OrderBy(p => p.Value.FinishedAt).Take(Math.Max(0, jobs.Count - 63)).Select(p => p.Key).ToList())
                    jobs.Remove(old);
                var job = new Job();
                jobs.Add(key, job);
                try
                {
                    Task.Run(() =>
                    {
                        string error = null;
                        try { work(); }
                        catch (Exception ex) { error = ex.Message; Log.Write("Preview job: " + ex.Message); }
                        finally
                        {
                            lock (gate)
                            {
                                job.Running = false;
                                job.Error = error;
                                job.FinishedAt = DateTime.UtcNow;
                                if (error == null) jobs.Remove(key);
                            }
                            Slots.Release();
                        }
                    });
                }
                catch
                {
                    jobs.Remove(key);
                    Slots.Release();
                    throw;
                }
                return null;
            }
        }
    }
}
