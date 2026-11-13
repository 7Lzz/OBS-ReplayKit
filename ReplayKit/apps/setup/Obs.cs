using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // obs process management: locate obs64.exe, close running instances, clear crash flags, relaunch
    public static class Obs
    {
        // best detected obs executable path, or null.
        public static string FindObsExe() => Config.FindObsExeCandidate();

        // a real OBS core sits under obsRoot (bin\64bit\obs64.exe). tells a working install apart from a broken/phantom C:\Program Files\obs-studio that only holds our plugins + stale uninstaller stubs.
        public static bool CoreInstalledAt(string obsRoot) =>
            !string.IsNullOrEmpty(obsRoot) && File.Exists(Path.Combine(obsRoot, "bin", "64bit", "obs64.exe"));

        private static Process[] ObsProcesses()
        {
            string root = Path.GetFullPath(Config.PROGRAMFILES_OBS_DIR).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string helper = Path.GetFullPath(Path.Combine(Config.REPLAYKIT_CONFIG, "scripts", "helper", "OBSReplayKit.exe"));
            using var current = Process.GetCurrentProcess();
            var result = new List<Process>();
            try
            {
                foreach (string name in Config.OBS_PROCESSES.Concat(new[] { "obs-browser-page.exe", "OBSReplayKit.exe" }))
                    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
                    {
                        try
                        {
                            if (process.Id == current.Id || process.HasExited) { process.Dispose(); continue; }
                            string path = process.MainModule?.FileName;
                            bool owned = string.Equals(name, "OBSReplayKit.exe", StringComparison.OrdinalIgnoreCase)
                                ? string.Equals(path, helper, StringComparison.OrdinalIgnoreCase)
                                : path != null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
                            if (owned) result.Add(process);
                            else process.Dispose();
                        }
                        catch (InvalidOperationException) { process.Dispose(); }
                        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { process.Dispose(); }
                        catch { process.Dispose(); throw; }
                    }
                return result.ToArray();
            }
            catch
            {
                foreach (var process in result) process.Dispose();
                throw;
            }
        }

        private static bool AnyObsRunning()
        {
            var processes = ObsProcesses();
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }

        // asks for a normal exit; an unavailable helper or a stalled shutdown leaves obs running
        internal static void RequestGracefulExit(Func<bool> obsRunning, TimeSpan wait, Action<string> log)
        {
            if (!obsRunning()) return;
            try
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    using (var check = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, HelperEndpoint.Origin + "/exit-obs/safety"))
                    {
                        check.Headers.Add("Origin", HelperEndpoint.Origin);
                        using (var safety = http.SendAsync(check).GetAwaiter().GetResult())
                        {
                            if (!safety.IsSuccessStatusCode)
                                throw new IOException("The running ReplayKit helper does not support graceful-only shutdown.");
                            var supported = JObject.Parse(safety.Content.ReadAsStringAsync().GetAwaiter().GetResult())["gracefulOnly"];
                            if (supported?.Type != JTokenType.Boolean || !supported.Value<bool>())
                                throw new IOException("The running ReplayKit helper does not support graceful-only shutdown.");
                        }
                    }
                    // the reason lets the helper stop obs itself if a normal close stalls, instead of leaving it running with a notice in the bell
                    using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, HelperEndpoint.Origin + "/exit-obs?reason=update"))
                    {
                        // the helper only takes calls that come from its own page
                        request.Headers.Add("Origin", HelperEndpoint.Origin);
                        using (var response = http.SendAsync(request).GetAwaiter().GetResult())
                            if (!response.IsSuccessStatusCode) return;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is System.Threading.Tasks.TaskCanceledException || ex is InvalidOperationException)
            {
                return;
            }

            log?.Invoke("asked obs to exit through the helper");
            DateTime deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline && obsRunning()) Thread.Sleep(250);
            log?.Invoke(obsRunning() ? "obs did not exit in time" : "obs exited by itself");
        }

        // installation takes priority after the normal close has had time to drain outputs
        public static int CloseObs(Action<string> log = null)
        {
            if (!AnyObsRunning()) return 0;
            try { RequestGracefulExit(AnyObsRunning, TimeSpan.FromSeconds(30), log); }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                log?.Invoke("normal OBS shutdown unavailable: " + ex.Message);
            }
            if (!AnyObsRunning()) return 0;
            log?.Invoke("normal shutdown did not finish; force-closing OBS and its remaining processes to continue installation");
            var processes = ObsProcesses();
            int killed = 0;
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        process.Kill();
                        killed++;
                        log?.Invoke("terminated OBS process pid " + process.Id);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex) { log?.Invoke("OBS termination failed: " + ex.Message); }
                }
                var timer = Stopwatch.StartNew();
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited) process.WaitForExit((int)Math.Max(0, 10000 - timer.ElapsedMilliseconds));
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex) { log?.Invoke("OBS exit could not be confirmed: " + ex.Message); }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            if (AnyObsRunning()) throw new IOException("OBS or its helper/browser processes are still running after termination. No update files were replaced; restart Windows before trying again.");
            return killed;
        }

        // remove safe_mode + .sentinel/* so obs doesnt show its crash-recovery prompt on the next launch.
        public static void CleanupCrashFlags(Action<string> log = null)
        {
            string safeMode = Path.Combine(Config.OBS_CONFIG, "safe_mode");
            if (File.Exists(safeMode) || Directory.Exists(safeMode))
            {
                try
                {
                    if (File.Exists(safeMode)) File.Delete(safeMode);
                    else Directory.Delete(safeMode, true);
                    log?.Invoke("removed safe_mode flag");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            string sentinel = Path.Combine(Config.OBS_CONFIG, ".sentinel");
            if (Directory.Exists(sentinel))
            {
                int cleared = 0;
                foreach (var item in Directory.EnumerateFileSystemEntries(sentinel))
                {
                    try
                    {
                        if (File.Exists(item)) File.Delete(item);
                        else Directory.Delete(item, true);
                        cleared++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }
                if (cleared > 0) log?.Invoke($"cleared .sentinel ({cleared} item(s))");
            }
        }

        // obs started straight from this process keeps its administrator rights; detached, so native plugins cannot write to the console of this exe, and outside its job, so obs outlives it
        private static bool LaunchObsKeepingRights(string obsExe, Action<string> log)
        {
            if (DetachedSpawn.Start(obsExe, ObsLaunch.Arguments) > 0) return true;
            log?.Invoke("failed to launch OBS: " + obsExe);
            return false;
        }

        // a child of this exe inherits its administrator token and hands it on through every relaunch after it, so obs started here would stay elevated; a missing or unreadable settings file reads as off, the same rule the lua script that elevates obs follows
        private static bool RunObsAsAdminEnabled()
        {
            try
            {
                if (!File.Exists(Prefs.RUNTIME_SETTINGS_FILE)) return false;
                var flag = JObject.Parse(File.ReadAllText(Prefs.RUNTIME_SETTINGS_FILE))["runObsAsAdmin"];
                return flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return false;
            }
        }

        // the running shell starts a shortcut at the level of the signed in user and leaves no console to attach to, so obs does not inherit the administrator token of this exe. wscript.shell is late-bound com, so the trimmer warnings are moot
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "com idispatch dispatch, no trimmable managed members")]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "com idispatch dispatch, no trimmable managed members")]
        private static bool LaunchObsViaShell(string obsExe, Action<string> log)
        {
            string link = Path.Combine(TempLayout.Scratch, "obs-launch.lnk");
            object shell = null;
            object shortcut = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { obsExe });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { ObsLaunch.Arguments });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(obsExe) });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception exc) when (exc is COMException || exc is TargetInvocationException || exc is MissingMemberException || exc is ArgumentNullException || exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("failed to prepare the OBS launch shortcut: " + exc.Message);
                return false;
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }

            try
            {
                var psi = new ProcessStartInfo("explorer.exe", Win32Args.Build(link)) { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("failed to start OBS through the shell: " + exc.Message);
                return false;
            }
        }

        // the setup, uninstall and update flows all run elevated; obs only keeps those rights when the run as administrator setting asks for them
        public static bool LaunchObsAt(string obsExe, Action<string> log)
        {
            if (RunObsAsAdminEnabled())
            {
                log?.Invoke("the run as administrator setting is on, obs keeps the administrator rights of this process");
                return LaunchObsKeepingRights(obsExe, log);
            }
            return LaunchObsViaShell(obsExe, log);
        }

        // launch obs without attaching it to the setup console.
        public static bool LaunchObs(Action<string> log = null)
        {
            string obsExe = FindObsExe();
            if (obsExe == null)
            {
                log?.Invoke("OBS install was not found - install OBS, set OBS_REPLAYKIT_OBS_EXE, or re-run from an active OBS install.");
                return false;
            }

            log?.Invoke("starting " + obsExe);
            return LaunchObsAt(obsExe, log);
        }
    }
}
