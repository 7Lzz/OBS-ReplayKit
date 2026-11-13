using System;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReplayKitHelper
{
    internal static class ClipFiles
    {
        // windows will not rename or delete a file that is still open somewhere, and right after a clip appeared or was previewed the stream that sent it, a thumbnail or a probe can hold it for a moment; the change is tried again for a short while before it fails, and then says so in words
        internal static void MoveWhenFree(string from, string to)
        {
            bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            WhenFree(() => File.Move(from, to), () => File.Exists(from) && (caseOnly || !File.Exists(to)));
        }

        internal static void DeleteWhenFree(string path) => WhenFree(() => File.Delete(path), () => File.Exists(path));

        // puts a remuxed copy in place of the clip it was made from and gives it back the dates of the clip, so it keeps its place in the list; false when the clip changed or went away while the copy was made, which leaves the clip as it is. readers of the clip finish first (JobCoordinator.ChangeSource)
        internal static bool ReplaceKeepingDates(string path, string replacement, FileInfo original, Func<bool> stillWanted = null)
        {
            bool replaced = false;
            JobCoordinator.ChangeSource(path, () =>
            {
                var current = new FileInfo(path);
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    current.Length != original.Length || current.LastWriteTimeUtc != original.LastWriteTimeUtc ||
                    current.CreationTimeUtc != original.CreationTimeUtc || (stillWanted != null && !stillWanted())) return;
                File.Replace(replacement, path, null);
                replaced = true;
            });
            if (!replaced) return false;
            try
            {
                File.SetCreationTimeUtc(path, original.CreationTimeUtc);
                File.SetLastWriteTimeUtc(path, original.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("Clip replace could not restore the file dates: " + ex.GetType().Name);
            }
            return true;
        }

        // the failure keeps the in-use code, so a caller tells it apart with InUse like the first one
        private static void WhenFree(Action change, Func<bool> worthWaiting)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { change(); return; }
                catch (IOException ex) when (InUse(ex) && worthWaiting())
                {
                    if (attempt >= 6) throw new IOException(InUseMessage, ex.HResult);
                    System.Threading.Thread.Sleep(120 + attempt * 60);
                }
            }
        }

        // a sharing or lock violation, and what the user is told about one
        internal static bool InUse(IOException ex) { int code = ex.HResult & 0xFFFF; return code == 32 || code == 33; }
        internal const string InUseMessage = "The file is in use by another program. Close anything that is playing it and try again.";

        internal static string AvailableName(string requested, Func<string, bool> taken)
        {
            if (!taken(requested)) return requested;
            string extension = Path.GetExtension(requested);
            string name = Path.GetFileNameWithoutExtension(requested);
            long number = 2;
            var suffix = Regex.Match(name, @"^(.+) \(([0-9]+)\)$");
            if (suffix.Success && long.TryParse(suffix.Groups[2].Value, out long previous) && previous >= 2)
            {
                if (previous > long.MaxValue - 10000) throw new ArgumentException("The numbered name is too large.");
                name = suffix.Groups[1].Value;
                number = previous + 1;
            }
            for (int attempt = 0; attempt < 10000; attempt++, number++)
            {
                string candidate = name + " (" + number.ToString(CultureInfo.InvariantCulture) + ")" + extension;
                if (!taken(candidate)) return candidate;
            }
            throw new ArgumentException("No available numbered name was found.");
        }

        public static void Replace(string requestId, string temporary, string source, long expectedSize, DateTime expectedWriteTime)
        {
            string staged = temporary;
            try
            {
                var token = JobCoordinator.Token(requestId);
                token.ThrowIfCancellationRequested();
                if (new FileInfo(temporary).Length == 0) throw new IOException("Encoder output is empty.");
                if (!string.Equals(Path.GetPathRoot(temporary), Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase))
                {
                    staged = Path.Combine(Path.GetDirectoryName(source), "_replaykit_finalize_" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
                    using (var input = File.OpenRead(temporary))
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[65536];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            token.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, count);
                        }
                        output.Flush(true);
                    }
                }
                JobCoordinator.Commit(requestId, () =>
                {
                    var current = new FileInfo(source);
                    if (!current.Exists || current.Length != expectedSize || current.LastWriteTimeUtc != expectedWriteTime)
                        throw new IOException("The source clip changed during processing; the original was left untouched.");
                    Native.MoveFileReplace(staged, source);
                });
            }
            finally
            {
                if (staged != temporary)
                {
                    try { if (File.Exists(staged)) File.Delete(staged); }
                    catch (IOException ex) { Log.Write("Finalize cleanup: " + ex.Message); }
                    catch (UnauthorizedAccessException ex) { Log.Write("Finalize cleanup: " + ex.Message); }
                }
            }
        }
    }
}
