using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // the share window (ReplayKitShare.exe) keeps running while obs and replaykit update, so discord keeps streaming its window, unless the update changes the window itself: then it is ended for the copy and the helper opens the new one once obs is back. uninstall and the share preview audio removal end it
    internal static class ShareWindowInstall
    {
        public static string FolderRel => Path.Combine(ReplayKitFolder.Name, ShareWindowLayout.Folder);
        private static string Folder => Path.Combine(Config.REPLAYKIT_CONFIG, ShareWindowLayout.Folder);
        private static string Target => Path.Combine(Folder, ShareWindowLayout.ExeName);

        // the files of the share window folder in the bundle, which the plain file copy of setup and update leave to Install
        public static bool IsShareWindowFile(string rel) =>
            rel.StartsWith(FolderRel + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return NetCompat.ToHex(sha.ComputeHash(stream));
        }

        // true once the bundled exe is the installed one
        public static bool Install(Action<string> log)
        {
            string source = Path.Combine(Config.OBS_ASSETS_DIR, FolderRel, ShareWindowLayout.ExeName);
            if (!File.Exists(source))
            {
                log?.Invoke("warn: the bundle has no share window at " + source);
                return false;
            }
            Directory.CreateDirectory(Folder);
            string hash = Sha256(source);
            if (File.Exists(Target) && Sha256(Target) == hash)
            {
                log?.Invoke("share window unchanged");
                return true;
            }
            // a running window holds its exe, so a changed one is ended first and the old exe stays when it does not close
            if (!Stop(log))
            {
                log?.Invoke("warn: the share window was not updated becuase the running one did not close");
                return false;
            }
            string incoming = Target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, incoming, false);
                if (Sha256(incoming) != hash) throw new IOException("the copied share window does not match the bundle");
                Installer.WriteWithRetry(() => NetCompat.MoveFile(incoming, Target, true));
                if (Sha256(Target) != hash) throw new IOException("the installed share window does not match the bundle");
                log?.Invoke("-> " + Path.Combine(FolderRel, ShareWindowLayout.ExeName).Replace('\\', '/'));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: the share window was not installed: " + ex.Message);
                return false;
            }
            finally
            {
                try { if (File.Exists(incoming)) File.Delete(incoming); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // asks the window to quit over its pipe, then ends whatever share window still runs from its folder; true once none runs
        public static bool Stop(Action<string> log)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", ShareWindowLayout.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(500);
                    byte[] bytes = new UTF8Encoding(false).GetBytes("QUIT\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    new StreamReader(pipe, new UTF8Encoding(false)).ReadLineAsync().Wait(3000);
                }
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException || ex is AggregateException) { }

            bool stopped = true;
            string folder = Folder.TrimEnd('\\') + "\\";
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ShareWindowLayout.ExeName)))
            {
                using (process)
                {
                    try
                    {
                        string path = process.MainModule?.FileName;
                        if (path == null || !Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                        if (process.WaitForExit(3000)) continue;
                        log?.Invoke("ending share window pid " + process.Id);
                        process.Kill();
                        if (!process.WaitForExit(3000)) stopped = false;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception exc)
                    {
                        log?.Invoke("warn: share window stop failed for pid " + process.Id + ": " + exc.Message);
                        stopped = false;
                    }
                }
            }
            return stopped;
        }
    }
}
