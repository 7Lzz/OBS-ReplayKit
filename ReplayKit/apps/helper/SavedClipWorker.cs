using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ReplayKitHelper
{
    // what happens to a clip or recording obs has just finished writing (the plugin posts its path to /clips/saved): the silent discord track of an mp4 goes and an hev1 tag becomes hvc1. one thread does the files in turn, and once it is idle after a start it looks through the library for clips the fix has not seen, which covers the ones saved while the helper was down
    internal static class SavedClipWorker
    {
        private static readonly object Gate = new object();
        // a saved file and whether discord was closed when the plugin reported it, since its turn can come much later, after the files before it and a library pass
        private struct SavedFile
        {
            public string Path;
            public bool DiscordClosed;
        }

        private static readonly Queue<SavedFile> Saved = new Queue<SavedFile>();
        private static Thread thread;
        // the library pass waits until obs and its pages have started, like the probe of the encoders
        private static readonly TimeSpan SweepDelay = TimeSpan.FromSeconds(20);
        private static DateTime sweepAt;
        private static bool sweepDue;
        // obs can still hold a file it reported as saved while it finishes the last write
        private const int UnlockWaitMs = 30000;
        private const int MaxQueued = 64;

        public static void Start()
        {
            lock (Gate)
            {
                if (thread != null) return;
                sweepAt = DateTime.UtcNow + SweepDelay;
                sweepDue = true;
                thread = new Thread(Loop) { Name = "saved-clips", IsBackground = true };
                thread.Start();
            }
        }

        // false when the path is not a clip of the clip folder or the queue is full
        public static bool Enqueue(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
            if (!InClipFolder(full) || !Constants.ALLOWED_EXTS.Contains(Path.GetExtension(full))) return false;
            var saved = new SavedFile { Path = full, DiscordClosed = SavedClipAudio.DiscordClosed() };
            lock (Gate)
            {
                if (Saved.Count >= MaxQueued) return false;
                Saved.Enqueue(saved);
                Monitor.PulseAll(Gate);
            }
            return true;
        }

        // the clip folder the way Path.GetDirectoryName writes a folder: no separator at the end, except for the root of a drive
        private static string ClipRoot()
        {
            string full = Path.GetFullPath(AppConfig.GetClipDir());
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }

        // obs writes into the clip folder or the game folder of the clip, which is a folder straight inside it
        private static bool InClipFolder(string full)
        {
            string root = ClipRoot();
            string dir = Path.GetDirectoryName(full);
            return string.Equals(dir, root, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetDirectoryName(dir), root, StringComparison.OrdinalIgnoreCase);
        }

        private static void Loop()
        {
            while (!Server.State.Shutdown)
            {
                SavedFile? next = null;
                bool sweep = false;
                lock (Gate)
                {
                    if (Saved.Count > 0) next = Saved.Dequeue();
                    else if (sweepDue && DateTime.UtcNow >= sweepAt) { sweepDue = false; sweep = true; }
                    else Monitor.Wait(Gate, 1000);
                }
                try
                {
                    if (next != null) Finish(next.Value);
                    else if (sweep) Sweep();
                }
                catch (Exception ex) { Log.Write("saved clip worker: " + ex.Message); }
            }
        }

        private static void Finish(SavedFile saved)
        {
            string path = saved.Path;
            if (!WaitUntilUnlocked(path, UnlockWaitMs)) { Log.Write("saved clip: still locked after 30 s, left as it was: " + Path.GetFileName(path)); return; }
            SavedClipAudio.FinishObsFile(path, saved.DiscordClosed);
            if (HevcTag.Enabled() && HevcTag.NeedsFix(path)) HevcTag.Fix(path);
        }

        private static bool WaitUntilUnlocked(string path, int waitMs)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (!File.Exists(path)) return false;
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { return false; }
                if (waited.ElapsedMilliseconds >= waitMs || Server.State.Shutdown) return false;
                Thread.Sleep(250);
            }
        }

        // the clips of the library written since the last pass of this folder; the pass time is kept per clip folder, so a folder picked later gets a full first pass
        private static void Sweep()
        {
            if (!HevcTag.Enabled()) return;
            if (!File.Exists(Path.Combine(Constants.HelperRoot, "ffmpeg.exe"))) { Log.Write("HEVC sweep skipped: the ReplayKit FFmpeg tools are missing"); return; }
            string root = ClipRoot();
            if (!Directory.Exists(root)) return;
            string stateDir = Path.Combine(Constants.SCRATCH_DIR, "hevc-sweeps");
            Directory.CreateDirectory(stateDir);
            // the folder is only swept by one pass at a time
            using (var progress = new FileStream(Path.Combine(stateDir, SweepKey(root) + ".sweep"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                long started = DateTime.UtcNow.Ticks;
                long watermark = 0;
                long earliestLeft = long.MaxValue;
                var saved = new byte[8];
                if (progress.Length == saved.Length && progress.Read(saved, 0, saved.Length) == saved.Length)
                {
                    long ticks = BitConverter.ToInt64(saved, 0);
                    if (ticks > 0 && ticks <= started) watermark = ticks;
                }
                foreach (var file in Clips.LibraryFiles(root).Where(Clips.IsLibraryClip).Where(f => f.LastWriteTimeUtc.Ticks > watermark).ToList())
                {
                    if (Server.State.Shutdown) return;
                    // a clip saved while the pass runs is done first
                    while (true)
                    {
                        SavedFile? next;
                        lock (Gate) next = Saved.Count > 0 ? Saved.Dequeue() : (SavedFile?)null;
                        if (next == null) break;
                        Finish(next.Value);
                    }
                    bool left;
                    // a file something still holds open, such as a recording in progress, is not copied for nothing
                    try { left = HevcTag.NeedsFix(file.FullName) && !(WaitUntilUnlocked(file.FullName, 0) && HevcTag.Fix(file.FullName)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("HEVC sweep: " + file.Name + ": " + ex.Message); left = true; }
                    if (left) earliestLeft = Math.Min(earliestLeft, file.LastWriteTimeUtc.Ticks);
                }
                // a clip that still needs the fix keeps its date ahead of the saved pass time, so the next pass comes back to it
                saved = BitConverter.GetBytes(Math.Min(started, earliestLeft - 1));
                progress.Position = 0;
                progress.Write(saved, 0, saved.Length);
                progress.SetLength(saved.Length);
            }
        }

        // the name the pass time of a folder is kept under, the same one earlier releases used, so the passes they made still count
        private static string SweepKey(string root)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).Replace("-", "");
        }
    }
}
