using System;
using System.IO;

namespace ReplayKitHelper
{
    internal static class ClipFiles
    {
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
