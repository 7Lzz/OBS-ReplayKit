using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReplayKitHelper
{
    internal static class JobCoordinator
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Reservation> Jobs = new Dictionary<string, Reservation>();

        public static Reservation TryReserve(string requestId, string name, string path, string kind, out string message, string destination = null)
        {
            var paths = new[] { path, destination }.Where(p => p != null).Select(Path.GetFullPath).ToArray();
            lock (Gate)
            {
                if (Jobs.Values.Any(j => j.Paths.Any(p => paths.Contains(p, StringComparer.OrdinalIgnoreCase))))
                {
                    message = "That clip already has an operation running";
                    return null;
                }
                if (Jobs.Count >= Constants.MAX_CONCURRENT_VIDEO_JOBS)
                {
                    message = "Already running " + Jobs.Count + " clip operations";
                    return null;
                }
                var job = new Reservation(requestId, paths);
                Jobs.Add(requestId, job);
                UploadState.SetUploadState(requestId: requestId, state: "preparing", active: true,
                    clipName: name, kind: kind, cts: job.Source);
                message = "";
                return job;
            }
        }

        private static Reservation Find(string id)
        {
            lock (Gate)
            {
                if (!Jobs.TryGetValue(id, out var job)) throw new InvalidOperationException("Clip operation is no longer reserved.");
                return job;
            }
        }

        public static CancellationToken Token(string id) => Find(id).Source.Token;
        public static void Commit(string id, Action action) => Find(id).Commit(action);

        public static bool Cancel(string id)
        {
            Reservation job;
            lock (Gate) { if (!Jobs.TryGetValue(id, out job)) return false; }
            return job.Cancel();
        }

        internal sealed class Reservation : IDisposable
        {
            private readonly object gate = new object();
            private readonly string id;
            internal readonly string[] Paths;
            internal readonly CancellationTokenSource Source = new CancellationTokenSource();
            private bool started, finished, committed;

            internal Reservation(string id, string[] paths) { this.id = id; Paths = paths; }

            public void Start<T>(Func<T> work, Action<Task<T>> complete)
            {
                lock (gate)
                {
                    Source.Token.ThrowIfCancellationRequested();
                    if (started || finished) throw new InvalidOperationException("Operation already started.");
                    started = true;
                    Task.Run(work).ContinueWith(task =>
                    {
                        try
                        {
                            if (task.IsFaulted && !(task.Exception.GetBaseException() is OperationCanceledException))
                                Program.WriteCrashReport("clip_worker", task.Exception, false);
                            complete(task);
                        }
                        catch (OperationCanceledException) when (Source.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            Program.WriteCrashReport("clip_completion", ex, false);
                            UploadState.SetUploadState(requestId: id, state: "error", error: ex.Message, phase: "error");
                        }
                        finally { Finish(); }
                    }, TaskScheduler.Default);
                }
            }

            internal void Commit(Action action)
            {
                lock (gate)
                {
                    if (finished || committed) throw new InvalidOperationException("Operation already completed.");
                    Source.Token.ThrowIfCancellationRequested();
                    action();
                    committed = true;
                }
            }

            internal bool Cancel()
            {
                lock (gate)
                {
                    if (finished || committed) return false;
                    UploadState.SetUploadState(requestId: id, state: "cancelling", phase: "cancelling", cancelRequested: true);
                    Source.Cancel();
                    return true;
                }
            }

            private void Finish()
            {
                lock (gate)
                {
                    if (finished) return;
                    finished = true;
                    if (Source.IsCancellationRequested)
                        UploadState.SetUploadState(requestId: id, state: "error", error: "Cancelled", phase: "cancelled", percent: 0);
                    lock (Server.State.UploadLock)
                    {
                        if (Server.State.Jobs.TryGetValue(id, out var record))
                        {
                            record.Active = false;
                            if (record.State == "preparing") record.State = "idle";
                            record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            record.Cts = null;
                            record.EncoderProcess = null;
                        }
                    }
                    lock (Gate) { Jobs.Remove(id); }
                    Source.Dispose();
                }
            }

            public void Dispose()
            {
                lock (gate) { if (!started) Finish(); }
            }
        }
    }
}
