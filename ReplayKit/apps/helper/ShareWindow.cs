using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // the share window (ReplayKitShare.exe), the process that owns the window discord streams for share preview and outlives obs: started here while share preview is on, driven over its command pipe, and ended when share preview goes off
    internal static class ShareWindow
    {
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        public static readonly string ExePath = Path.GetFullPath(Path.Combine(Constants.HelperRoot, "..", "..", ShareWindowLayout.Folder, ShareWindowLayout.ExeName));
        private static readonly object Gate = new object();
        private static long knownHwnd;
        private static int knownPid;

        public sealed class Info
        {
            public bool Ok;
            public int Protocol;
            public string Version;
            public int Pid;
            public long Hwnd;
            public bool Started;
            public string Message;
        }

        // the seconds the window keeps its card after a real exit or crash, from the share_window_keep setting; below zero keeps it until obs comes back
        public static int KeepSeconds(JObject settings)
        {
            switch (settings?["share_window_keep"]?.Value<string>() ?? SettingDefaults.ShareWindowKeep)
            {
                case "off": return 0;
                case "15m": return 900;
                case "1h": return 3600;
                case "always": return -1;
                default: return 300;
            }
        }

        // one command, one line of json back; ok false with a message when no window answers
        public static JObject Send(string command, int connectMs = 500)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", ShareWindowLayout.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(connectMs);
                    byte[] bytes = new UTF8Encoding(false).GetBytes(command + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    var reader = new StreamReader(pipe, new UTF8Encoding(false));
                    Task<string> line = reader.ReadLineAsync();
                    if (!line.Wait(6000) || line.Result == null) return Fail("the share window did not answer");
                    return JObject.Parse(line.Result);
                }
            }
            catch (TimeoutException) { return Fail("no share window is running"); }
            catch (IOException ex) { return Fail("the share window pipe failed: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { return Fail("the share window pipe refused the helper: " + ex.Message); }
            catch (JsonReaderException) { return Fail("the share window answered something that is not json"); }
            catch (AggregateException ex) { return Fail("the share window pipe failed: " + ex.GetBaseException().Message); }
        }

        private static JObject Fail(string message) => new JObject { ["ok"] = false, ["message"] = message };

        public static Info Hello(int connectMs = 500)
        {
            var reply = Send("HELLO", connectMs);
            if (reply["ok"]?.Value<bool>() != true) return new Info { Ok = false, Message = reply["message"]?.Value<string>() };
            var info = new Info
            {
                Ok = true,
                Protocol = reply["protocol"]?.Value<int>() ?? 0,
                Version = reply["version"]?.Value<string>() ?? "",
                Pid = reply["pid"]?.Value<int>() ?? 0,
                Hwnd = reply["hwnd"]?.Value<long>() ?? 0,
            };
            Interlocked.Exchange(ref knownHwnd, info.Hwnd);
            Interlocked.Exchange(ref knownPid, info.Pid);
            return info;
        }

        // the window the running share window reported, still owned by its process and still of its class; every window move of share preview is only ever done to this window
        public static bool IsOwnWindow(long hwndValue)
        {
            long hwnd = Interlocked.Read(ref knownHwnd);
            int pid = Volatile.Read(ref knownPid);
            if (hwndValue == 0 || hwndValue != hwnd || pid == 0) return false;
            var handle = new IntPtr(hwndValue);
            if (!IsWindow(handle)) return false;
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner != (uint)pid) return false;
            var name = new StringBuilder(128);
            GetClassName(handle, name, name.Capacity);
            return string.Equals(name.ToString(), ShareWindowLayout.WindowClass, StringComparison.Ordinal);
        }

        // whether the last window this helper heard from still exists, without asking it over the pipe
        public static bool KnownWindowAlive() => IsOwnWindow(Interlocked.Read(ref knownHwnd));

        // the running window, started first when none answers; a window of another protocol (an older release still running after an update) is ended and started again
        public static Info EnsureRunning(string title, int keepSeconds)
        {
            lock (Gate)
            {
                var info = Hello();
                if (info.Ok && info.Protocol != ShareWindowLayout.ProtocolVersion)
                {
                    Log.Write("Share window " + info.Version + " speaks protocol " + info.Protocol + ", not " + ShareWindowLayout.ProtocolVersion + "; starting the current one instead");
                    Quit(3000);
                    info = new Info { Ok = false };
                }
                if (!info.Ok)
                {
                    if (!File.Exists(ExePath)) return new Info { Ok = false, Message = Locales.Message("screenshare.theShareWindowIsMissing", "path", ExePath) };
                    // the window plays the share audio, which desktop audio must never record back, so a scene from before the window existed gets the exclusion first
                    SettingsLiveApply.EnsureDesktopAudioExclusions();
                    if (!Launch(title, keepSeconds)) return new Info { Ok = false, Message = Locales.Message("screenshare.theShareWindowCouldNotStart") };
                    for (int i = 0; i < 24 && !info.Ok; i++)
                    {
                        Thread.Sleep(250);
                        info = Hello(250);
                    }
                    if (!info.Ok) return new Info { Ok = false, Message = Locales.Message("screenshare.theShareWindowCouldNotStart") };
                    info.Started = true;
                    Log.Write("Share window " + info.Version + " started: pid " + info.Pid + ", hwnd " + info.Hwnd);
                }
                else
                {
                    Send("TITLE " + title);
                    Send("KEEP " + keepSeconds);
                }
                return info;
            }
        }

        private static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // the quoting CommandLineToArgvW undoes: backslashes are doubled only in front of a quote
        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        private static bool Launch(string title, int keepSeconds)
        {
            string arguments = "--title " + Quote(title) + " --keep " + keepSeconds;
            // an admin obs makes an admin helper, and discord cannot capture a window of an admin process, so the window starts with the normal rights of the user
            if (IsElevated())
            {
                Log.Write("Starting the share window thru the shell, since the helper runs as admin");
                return ShellLaunch.Start(ExePath, arguments, Path.Combine(TempLayout.Scratch, "share-window-launch.lnk"), message => Log.Write(message));
            }
            int pid = Native.SpawnDetached(Quote(ExePath) + " " + arguments, Path.GetDirectoryName(ExePath));
            if (pid == 0) Log.Write("warn: the share window could not be started: " + Marshal.GetLastWin32Error());
            return pid != 0;
        }

        // ends the window: asked over its pipe first, then ended outright, since nothing of the user lives in it; true once no share window runs
        public static bool Quit(int waitMs)
        {
            lock (Gate)
            {
                var reply = Send("QUIT");
                var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                bool asked = reply["ok"]?.Value<bool>() == true;
                foreach (var process in Running())
                {
                    using (process)
                    {
                        try
                        {
                            int left = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                            if (!asked || !process.WaitForExit(left))
                            {
                                Log.Write("Ending share window pid " + process.Id + (asked ? " (it did not close in time)" : " (it did not answer)"));
                                process.Kill();
                                process.WaitForExit(2000);
                            }
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                    }
                }
                Interlocked.Exchange(ref knownHwnd, 0);
                Interlocked.Exchange(ref knownPid, 0);
                foreach (var process in Running())
                {
                    process.Dispose();
                    return false;
                }
                return true;
            }
        }

        // every share window process started from the share window folder
        private static System.Collections.Generic.List<Process> Running()
        {
            var list = new System.Collections.Generic.List<Process>();
            string folder = Path.GetDirectoryName(ExePath).TrimEnd('\\') + "\\";
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ShareWindowLayout.ExeName)))
            {
                string path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                if (path != null && Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase)) list.Add(process);
                else process.Dispose();
            }
            return list;
        }

        public static bool AnyRunning()
        {
            var running = Running();
            foreach (var process in running) process.Dispose();
            return running.Count > 0;
        }

        // tells the window obs is about to close for a restart or an update and comes back, so it keeps its card whatever the keep time says
        public static void HoldForObsReturn(int seconds, string reason)
        {
            if (SettingsLiveApply.GetSharePreviewFlags()["enabled"]?.Value<bool>() != true) return;
            var reply = Send("HOLD " + seconds);
            Log.Write("Share window hold for " + reason + " (" + seconds + " s): " + (reply["ok"]?.Value<bool>() == true ? "ok" : reply["message"]?.Value<string>()));
        }

        public static JObject State()
        {
            var reply = Send("STATE");
            reply["exe"] = ExePath;
            reply["running"] = AnyRunning();
            return reply;
        }
    }
}
