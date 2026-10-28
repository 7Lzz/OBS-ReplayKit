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
        private sealed class Editor
        {
            public string Id;
            public long Revision;
            public readonly HashSet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        private static readonly Dictionary<string, Editor> Editors = new Dictionary<string, Editor>();
        private static readonly HashSet<string> CompressionQueue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void RegisterEditor(string view, string id)
        {
            lock (Gate) Editors[view] = new Editor { Id = id };
        }

        public static bool UpdateEditor(string view, string id, long revision, string[] paths, out string message)
        {
            lock (Gate)
            {
                if (!Editors.TryGetValue(view, out var editor) || editor.Id != id || revision <= editor.Revision)
                {
                    message = "The editor session changed. Reopen the editor.";
                    return false;
                }
                // A cancelled load or page unload must win over an older request arriving late.
                editor.Revision = revision;
                if (paths == null) editor.Paths.Clear();
                else
                {
                    if (editor.Paths.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 128)
                    {
                        message = "Too many editor sources. Save your edits and reopen the editor.";
                        return false;
                    }
                    if (paths.Any(p => CompressionQueue.Contains(p) || Jobs.Values.Any(j => j.Paths.Contains(p, StringComparer.OrdinalIgnoreCase))))
                    {
                        message = "A clip is processing or queued for compression. Open it after that finishes.";
                        return false;
                    }
                    foreach (string path in paths) editor.Paths.Add(path);
                }
                message = "";
                return true;
            }
        }

        public static bool IsEditorSource(string path)
        {
            lock (Gate) return Editors.Values.Any(e => e.Paths.Contains(path));
        }

        public static bool QueueCompression(IEnumerable<string> paths, out string message)
        {
            lock (Gate)
            {
                var selected = paths.ToArray();
                if (selected.Any(p => Editors.Values.Any(e => e.Paths.Contains(p))))
                {
                    message = "A selected clip is open in the editor. Close the editor and review the clips again.";
                    return false;
                }
                if (selected.Any(p => Jobs.Values.Any(j => j.Paths.Contains(p, StringComparer.OrdinalIgnoreCase))))
                {
                    message = "A selected clip is processing. Review the clips again after it finishes.";
                    return false;
                }
                CompressionQueue.UnionWith(selected);
                message = "";
                return true;
            }
        }

        public static void ClearCompressionQueue()
        {
            lock (Gate) CompressionQueue.Clear();
        }

        public static Reservation TryReserve(string requestId, string name, string path, string kind, out string message, string destination = null)
        {
            var paths = new[] { path, destination }.Where(p => p != null).Select(Path.GetFullPath).ToArray();
            lock (Gate)
            {
                if (kind.StartsWith("compress", StringComparison.Ordinal) && paths.Any(p => Editors.Values.Any(e => e.Paths.Contains(p))))
                {
                    message = "That clip is open in the editor. Close the editor before compressing it.";
                    return null;
                }
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
                    clipName: name, kind: kind);
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
