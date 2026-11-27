using System;
using System.IO;
using System.Text;

namespace ReplayKitSetup
{
    // append-only file log for the windowed installer / uninstaller, so a step that fails quietly still leaves something on disk to diagnose; the headless --update/--cleanup paths keep their own OBSReplayKitUpdate.log
    internal static class SetupLog
    {
        private static readonly object Gate = new object();
        private static string _path;

        // %TEMP%\ReplayKit\logs\setup_<timestamp>.log -- resolved once on first use so every line in one run shares a file.
        public static string Path
        {
            get
            {
                if (_path != null) return _path;
                lock (Gate)
                {
                    if (_path == null)
                    {
                        string dir = ReplayKitShared.TempLayout.Logs;
                        try { Directory.CreateDirectory(dir); } catch (Exception) { }
                        _path = System.IO.Path.Combine(dir, "setup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    }
                }
                return _path;
            }
        }

        public static void Line(string message)
        {
            lock (Gate)
            {
                try { File.AppendAllText(Path, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + (message ?? "") + "\n", new UTF8Encoding(false)); }
                catch (Exception) { }
            }
        }
    }
}
