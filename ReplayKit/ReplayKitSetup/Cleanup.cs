using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // in-menu cleanup for OBS ReplayKit installs. ported from obs_replaykit/cleanup.py.
    public static class Cleanup
    {
        private static readonly string[] ReplaykitRuntimeStateRels =
        {
            "obs-replayKit/scripts/helper/clips_db.json",
            "obs-replayKit/scripts/helper/clips_index.json",
        };

        private static (int ExitCode, string Stdout, string Stderr) RunHidden(string fileName, string arguments, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi))
            {
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException(fileName + " timed out");
                }
                return (proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

        // MoveFileEx with a null target queues the path for deletion by the session manager on the next boot; needs admin, which this setup exe already has
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        // own parent pid. was a Win32_Process wmi lookup; System.Management is not trim-safe and this project publishes trimmed, so it reads ProcessBasicInformation directly. null on any failure -- caller just skips adding it to the keep-set.
        private static int? CurrentParentPid()
        {
            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                using var process = Process.GetCurrentProcess();
                int rc = NtQueryInformationProcess(process.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
                if (rc != 0) return null;
                return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        // close obs and replaykit helper processes while keeping this setup process alive.
        public static bool StopObsAndHelpers(Action<string> log = null)
        {
            Obs.CloseObs(log);
            var keep = new HashSet<int> { Process.GetCurrentProcess().Id };
            var parent = CurrentParentPid();
            if (parent.HasValue) keep.Add(parent.Value);
            string keepCsv = string.Join(",", keep);
            string script =
                $"$keep = @({keepCsv})\r\n" +
                "Get-CimInstance Win32_Process |\r\n" +
                "  Where-Object { $_.Name -in @('OBSReplayKit.exe','OBSReplayKit-Encoder.exe') -and $keep -notcontains [int]$_.ProcessId } |\r\n" +
                "  ForEach-Object {\r\n" +
                "    try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch { }\r\n" +
                "  }\r\n";
            try
            {
                RunHidden("powershell.exe", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script));
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is TimeoutException)
            {
                log?.Invoke("warn: helper stop failed: " + exc.Message);
                return false;
            }
            return true;
        }

        public static bool RemoveReplaykitPlugins(Action<string> log = null)
        {
            string obsRoot = Config.PROGRAMFILES_OBS_DIR;
            var targets = new[]
            {
                Path.Combine(obsRoot, "obs-plugins", "64bit", "win-capture-audio.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "win-capture-audio.pdb"),
                Path.Combine(obsRoot, "data", "obs-plugins", "win-capture-audio"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "input-overlay.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "SDL2.dll"),
                Path.Combine(obsRoot, "data", "obs-plugins", "input-overlay"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "bongobs-cat.dll"),
                Path.Combine(obsRoot, "bin", "64bit", "Bango Cat"),
                Path.Combine(obsRoot, "data", "obs-plugins", "bongobs-cat"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-composite-blur.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-composite-blur.pdb"),
                Path.Combine(obsRoot, "data", "obs-plugins", "obs-composite-blur"),
                Path.Combine(Config.PROGRAMDATA, "obs-studio", "plugins", "obs-composite-blur"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-shaderfilter.dll"),
                Path.Combine(obsRoot, "obs-plugins", "64bit", "obs-shaderfilter.pdb"),
                Path.Combine(obsRoot, "data", "obs-plugins", "obs-shaderfilter"),
                Config.REPLAYKIT_TRAY_PLUGIN_DIR,
            };
            bool ok = true;
            foreach (var target in targets)
            {
                bool isDir = Directory.Exists(target);
                bool isFile = File.Exists(target);
                if (!isDir && !isFile) continue;
                try
                {
                    if (isDir) Directory.Delete(target, true);
                    else File.Delete(target);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove " + target + ": " + exc.Message);
                }
            }
            // a broken/phantom C:\Program Files\obs-studio (no obs64.exe, just our plugins + stale uninstaller stubs) is
            // what seeds the "installer wrote plugins into a dead folder" bug on the next run. drop it here even on a
            // keep-OBS uninstall -- RemoveBrokenObsInstallDir never touches a real install.
            RemoveBrokenObsInstallDir(Config.PROGRAMFILES_OBS_DIR, log);
            return ok;
        }

        // remove C:\Program Files\obs-studio ONLY when it holds no working OBS (no bin\64bit\obs64.exe). a real install is
        // left for OBS's own uninstaller; a phantom folder is force-removed so the next install starts clean.
        private static bool RemoveBrokenObsInstallDir(string dir, Action<string> log)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true;
            if (Obs.CoreInstalledAt(dir)) return true;
            if (!string.Equals(Path.GetFileName(dir.TrimEnd('\\', '/')), "obs-studio", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("warn: refusing to remove unexpected OBS path " + dir);
                return false;
            }
            try
            {
                Directory.Delete(dir, true);
                log?.Invoke("removed broken OBS folder " + dir + " (no obs64.exe present)");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not remove " + dir + ": " + ex.Message + " (a file may be locked; a reboot will clear it)");
                return false;
            }
        }

        public static bool RemoveVirtualDisplayDriver(Action<string> log = null)
        {
            const string script = @"
$ErrorActionPreference = 'Continue'
$vddDevice = Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' } |
    Select-Object -First 1
if ($vddDevice) {
    Disable-PnpDevice -InstanceId $vddDevice.InstanceId -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    pnputil.exe /remove-device $vddDevice.InstanceId | Out-Null
}
$drivers = pnputil.exe /enum-drivers | Out-String
$matches = [regex]::Matches($drivers, ""Published Name:\s+(oem\d+\.inf)\s+Original Name:\s+MttVDD\.inf"", ""IgnoreCase"")
foreach ($m in $matches) {
    pnputil.exe /delete-driver $m.Groups[1].Value /uninstall /force | Out-Null
}
if (Test-Path ""C:\IddSampleDriver"") {
    Remove-Item -Recurse -Force ""C:\IddSampleDriver"" -ErrorAction SilentlyContinue
}
";
            (int ExitCode, string Stdout, string Stderr) result;
            try
            {
                result = RunHidden("powershell.exe", Win32Args.Build("-NoProfile", "-NonInteractive", "-Command", script), 60000);
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is TimeoutException)
            {
                log?.Invoke("warn: virtual display cleanup failed: " + exc.Message);
                return false;
            }
            if (result.ExitCode != 0)
            {
                string message = !string.IsNullOrEmpty(result.Stderr) ? result.Stderr : result.Stdout;
                string firstLine = string.IsNullOrWhiteSpace(message) ? "" : message.Trim().Split('\n')[0];
                log?.Invoke("warn: virtual display cleanup returned " + result.ExitCode + ": " + firstLine);
            }
            return result.ExitCode == 0;
        }

        public static bool WipeObsConfig(Action<string> log = null)
        {
            if (!Directory.Exists(Config.REPLAYKIT_CONFIG)) return true;
            try
            {
                Directory.Delete(Config.REPLAYKIT_CONFIG, true);
                return true;
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not wipe " + Config.REPLAYKIT_CONFIG + ": " + exc.Message);
                return false;
            }
        }

        public static bool SaveUserSettings(Action<string> log = null)
        {
            bool ok = true;
            try
            {
                var prefs = Prefs.LoadPrefs();
                prefs.Save();
                log?.Invoke("ReplayKit user settings kept -> " + Prefs.PREFS_FILE);
            }
            catch (Exception exc)
            {
                ok = false;
                log?.Invoke("warn: could not keep ReplayKit user settings: " + exc.Message);
            }

            try
            {
                Directory.CreateDirectory(Config.REPLAYKIT_USER_STATE_CACHE);
                int kept = 0;
                foreach (var rel in ReplaykitRuntimeStateRels)
                {
                    string source = Path.Combine(Config.OBS_CONFIG, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(source)) continue;
                    string target = Path.Combine(Config.REPLAYKIT_USER_STATE_CACHE, Path.GetFileName(rel));
                    File.Copy(source, target, true);
                    kept++;
                }
                if (kept > 0) log?.Invoke("ReplayKit clip state kept -> " + Config.REPLAYKIT_USER_STATE_CACHE);
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                ok = false;
                log?.Invoke("warn: could not keep ReplayKit clip state: " + exc.Message);
            }
            return ok;
        }

        public static bool RemoveUserSettings(Action<string> log = null)
        {
            var targets = new List<string>
            {
                Prefs.PREFS_FILE,
                Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "prefs.json"),
                Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "clips_state.json"),
            };

            bool ok = true;
            var seen = new HashSet<string>();
            foreach (var target in targets)
            {
                string key;
                try { key = Path.GetFullPath(target).ToLowerInvariant(); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException) { continue; }
                if (!seen.Add(key)) continue;
                if (!File.Exists(target)) continue;
                try
                {
                    File.Delete(target);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove " + target + ": " + exc.Message);
                }
            }
            if (Directory.Exists(Config.REPLAYKIT_USER_STATE_CACHE))
            {
                try
                {
                    Directory.Delete(Config.REPLAYKIT_USER_STATE_CACHE, true);
                }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove " + Config.REPLAYKIT_USER_STATE_CACHE + ": " + exc.Message);
                }
            }
            return ok;
        }

        // clears %localappdata%\OBS ReplayKit after an uninstall -- deletes what is unlocked, and hands the still-running exe (and the folder itself on a full uninstall) to the session manager for delete-on-reboot rather than a self-deleting cmd stub that av would flag.
        public static bool ClearSetupCache(bool keepUserSettings, Action<string> log = null)
        {
            string cacheDir = Config.REPLAYKIT_SETUP_CACHE;
            if (!Directory.Exists(cacheDir)) return true;
            bool ok = true;

            // the streamable sign-in webview2 profile -- nothing reads it after an uninstall and no other step touches it
            string streamable = Path.Combine(cacheDir, "StreamableProfile");
            if (Directory.Exists(streamable))
            {
                try { Directory.Delete(streamable, true); log?.Invoke("removed " + streamable); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { ok = false; log?.Invoke("warn: could not remove " + streamable + ": " + ex.Message); }
            }

            // the cached uninstaller -- usually the exe this process is running from, so it is locked; fall back to a session-manager delete-on-reboot, queued before the folder below so the folder is empty by the time that entry runs
            string exe = Config.REPLAYKIT_SETUP_EXE;
            if (File.Exists(exe))
            {
                try { File.Delete(exe); log?.Invoke("removed " + Path.GetFileName(exe)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (MoveFileEx(exe, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                        log?.Invoke("scheduled " + Path.GetFileName(exe) + " for removal on the next reboot (in use now)");
                    else { ok = false; log?.Invoke("warn: could not schedule " + exe + " for removal (win32 " + Marshal.GetLastWin32Error() + ")"); }
                }
            }

            // keep-settings uninstall leaves the folder on purpose (state\ + prefs.json feed the next install); a full uninstall takes it too, now if it is already empty otherwise on reboot right after the exe entry
            if (!keepUserSettings)
            {
                try { Directory.Delete(cacheDir, true); log?.Invoke("removed " + cacheDir); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (MoveFileEx(cacheDir, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                        log?.Invoke("scheduled " + cacheDir + " for removal on the next reboot");
                    else { ok = false; log?.Invoke("warn: could not schedule " + cacheDir + " for removal (win32 " + Marshal.GetLastWin32Error() + ")"); }
                }
            }

            return ok;
        }

        // ReplayKit's fingerprints inside OBS's OWN config -- WipeObsConfig only deletes the obs-replayKit/ folder, so
        // without this the scene collection keeps listing replaykit.lua ("Error opening file: (null)" every launch), the
        // All-In-One scene + its sources, the Custom Controls browser dock, and the generated obs theme all stay behind.
        public static bool RemoveReplaykitFromObsConfig(Action<string> log = null)
        {
            bool ok = true;
            try { ok &= ScrubSceneCollections(log); }
            catch (Exception ex) { ok = false; log?.Invoke("warn: scene scrub failed: " + ex.Message); }
            try { ok &= ScrubObsInis(log); }
            catch (Exception ex) { ok = false; log?.Invoke("warn: ini scrub failed: " + ex.Message); }
            try { ok &= RemoveGeneratedObsThemes(log); }
            catch (Exception ex) { ok = false; log?.Invoke("warn: theme cleanup failed: " + ex.Message); }
            return ok;
        }

        // source/scene names ReplayKit's bundled collection ships. the generic ones ("Display Capture" etc.) are only
        // pulled when nothing the user kept still references them -- see the orphan check in ScrubSceneCollections.
        private static readonly HashSet<string> ReplaykitSceneNames = new HashSet<string>(StringComparer.Ordinal) { "All-In-One" };

        private static bool LooksLikeReplaykitSource(JObject src)
        {
            string id = src.Value<string>("id") ?? "";
            string name = src.Value<string>("name") ?? "";
            if (string.Equals(id, "input-overlay", StringComparison.OrdinalIgnoreCase)) return true;
            if (name == "Desktop Audio (excl. Discord)" || name == "Discord Audio (record only)") return true;
            var settings = src["settings"];
            return settings != null &&
                   settings.ToString(Formatting.None).IndexOf("obs-replaykit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ScrubSceneCollections(Action<string> log)
        {
            string scenesDir = Path.Combine(Config.OBS_CONFIG, "basic", "scenes");
            if (!Directory.Exists(scenesDir)) return true;
            bool ok = true;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            foreach (string path in Directory.GetFiles(scenesDir, "*.json"))
            {
                if (path.IndexOf(".bak", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                JObject root;
                try { root = JObject.Parse(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                {
                    log?.Invoke("warn: could not read " + Path.GetFileName(path) + ": " + ex.Message);
                    continue;
                }

                var sources = root["sources"] as JArray ?? new JArray();
                var groups = root["groups"] as JArray ?? new JArray();
                var removedScenes = new List<string>();
                var removedSources = new List<string>();
                bool changed = false;

                // 1. drop the replaykit.lua entry from the scripts-tool module
                if (root["modules"]?["scripts-tool"] is JArray scriptsTool)
                {
                    var kept = new JArray(scriptsTool.Where(s =>
                        (s?["path"]?.Value<string>() ?? "").IndexOf("replaykit", StringComparison.OrdinalIgnoreCase) < 0));
                    if (kept.Count != scriptsTool.Count) { root["modules"]["scripts-tool"] = kept; changed = true; }
                }

                // 2. remove ReplayKit's scene(s); keep the first shell in case the collection ends up empty
                JObject shell = null;
                var rkItemNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scene in sources.OfType<JObject>()
                             .Where(s => (s.Value<string>("id") ?? "") == "scene"
                                         && ReplaykitSceneNames.Contains(s.Value<string>("name") ?? "")).ToList())
                {
                    foreach (var it in (scene["settings"]?["items"] as JArray ?? new JArray()))
                    { var n = it?["name"]?.Value<string>(); if (!string.IsNullOrEmpty(n)) rkItemNames.Add(n); }
                    if (shell == null) shell = (JObject)scene.DeepClone();
                    removedScenes.Add(scene.Value<string>("name"));
                    scene.Remove();
                    changed = true;
                }
                if (root["scene_order"] is JArray order && removedScenes.Count > 0)
                    root["scene_order"] = new JArray(order.Where(o => !removedScenes.Contains(o?["name"]?.Value<string>() ?? "")));

                // 3. names the surviving SCENES still list (a doomed group must not shield its children, so groups are not counted yet)
                var sceneRefs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scene in sources.OfType<JObject>().Where(s => (s.Value<string>("id") ?? "") == "scene"))
                    foreach (var it in (scene["settings"]?["items"] as JArray ?? new JArray()))
                    { var n = it?["name"]?.Value<string>(); if (!string.IsNullOrEmpty(n)) sceneRefs.Add(n); }

                // 3b. drop ReplayKit groups first -- one no surviving scene lists and that is ReplayKit's or came from the removed scene
                foreach (var g in groups.OfType<JObject>().ToList())
                {
                    string name = g.Value<string>("name") ?? "";
                    if (sceneRefs.Contains(name)) continue;
                    if (!LooksLikeReplaykitSource(g) && !rkItemNames.Contains(name)) continue;
                    removedSources.Add(name);
                    g.Remove();
                    changed = true;
                }

                // 3c. now the reference set = surviving scenes + the groups that survived
                var referenced = new HashSet<string>(sceneRefs, StringComparer.Ordinal);
                foreach (var g in groups.OfType<JObject>())
                    foreach (var it in (g["settings"]?["items"] as JArray ?? new JArray()))
                    { var n = it?["name"]?.Value<string>(); if (!string.IsNullOrEmpty(n)) referenced.Add(n); }

                // 4. remove ReplayKit sources + whatever the removed scene held that nothing kept still uses
                foreach (var src in sources.OfType<JObject>().Where(s => (s.Value<string>("id") ?? "") != "scene").ToList())
                {
                    string name = src.Value<string>("name") ?? "";
                    if (referenced.Contains(name)) continue;
                    if (!LooksLikeReplaykitSource(src) && !rkItemNames.Contains(name)) continue;
                    removedSources.Add(name);
                    src.Remove();
                    changed = true;
                }

                // 5. drop now-dangling items from the scenes/groups that survived
                var known = new HashSet<string>(sources.Concat(groups).OfType<JObject>()
                    .Select(s => s.Value<string>("name") ?? "").Where(n => n.Length > 0), StringComparer.Ordinal);
                foreach (var container in sources.Concat(groups).OfType<JObject>())
                    if (container["settings"]?["items"] is JArray items)
                    {
                        var pruned = new JArray(items.Where(it => known.Contains(it?["name"]?.Value<string>() ?? "")));
                        if (pruned.Count != items.Count) { container["settings"]["items"] = pruned; changed = true; }
                    }

                // 6. never leave the collection with zero scenes
                if (!sources.OfType<JObject>().Any(s => (s.Value<string>("id") ?? "") == "scene") && shell != null)
                {
                    shell["name"] = "Scene";
                    shell["uuid"] = Guid.NewGuid().ToString();
                    shell["settings"] = new JObject { ["id_counter"] = 0, ["custom_size"] = false, ["items"] = new JArray() };
                    sources.Add(shell);
                    root["scene_order"] = new JArray { new JObject { ["name"] = "Scene" } };
                    log?.Invoke("scrubbed " + Path.GetFileName(path) + ": collection was entirely ReplayKit, left a blank \"Scene\"");
                }

                // 7. repoint the active scene if it named something removed
                string firstScene = (root["scene_order"] as JArray)?.FirstOrDefault()?["name"]?.Value<string>()
                    ?? sources.OfType<JObject>().FirstOrDefault(s => (s.Value<string>("id") ?? "") == "scene")?.Value<string>("name");
                foreach (string key in new[] { "current_scene", "current_program_scene" })
                    if (firstScene != null && removedScenes.Contains(root[key]?.Value<string>() ?? "")) { root[key] = firstScene; changed = true; }

                if (!changed) continue;
                root["sources"] = sources;
                root["groups"] = groups;

                try
                {
                    File.Copy(path, path + ".replaykit-uninstall-" + stamp + ".bak", true);
                    File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                    log?.Invoke("scrubbed " + Path.GetFileName(path)
                        + (removedScenes.Count > 0 ? "; scene(s): " + string.Join(", ", removedScenes) : "")
                        + (removedSources.Count > 0 ? "; source(s): " + string.Join(", ", removedSources.Distinct()) : ""));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not write " + Path.GetFileName(path) + ": " + ex.Message);
                }
            }
            return ok;
        }

        private static bool ScrubObsInis(Action<string> log)
        {
            bool ok = true;
            foreach (string name in new[] { "user.ini", "global.ini" })
            {
                string path = Path.Combine(Config.OBS_CONFIG, name);
                if (!File.Exists(path)) continue;

                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException ex) { ok = false; log?.Invoke("warn: read " + name + ": " + ex.Message); continue; }
                string original = text;

                // [BasicWindow] ExtraBrowserDocks -- pull the Custom Controls entry, keep every other dock
                text = Regex.Replace(text, @"(\[BasicWindow\][^\[]*?\r?\nExtraBrowserDocks=)([^\r\n]*)",
                    m => m.Groups[1].Value + StripReplaykitDock(m.Groups[2].Value), RegexOptions.Singleline);

                // [BasicWindow] DockState -- the tray plugin hid OBS's native "Controls" dock and OBS persisted that
                // into this blob. it is an opaque QMainWindow::saveState() so there is no clean way to flip one dock;
                // drop the whole line and OBS rebuilds its default layout (Controls dock visible) on next launch. the
                // .bak beside this file keeps the old layout if the user wants it.
                text = Regex.Replace(text, @"(\[BasicWindow\][^\[]*?\r?\n)DockState=[^\r\n]*\r?\n",
                    "$1", RegexOptions.Singleline);

                // [Appearance] Theme= / [General] CurrentTheme= pointing at a generated ReplayKit theme -- drop the line, obs falls back to its default
                text = Regex.Replace(text, @"(\[Appearance\][^\[]*?\r?\n)Theme=com\.replaykit\.theme\.[^\r\n]*\r?\n",
                    "$1", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\[General\][^\[]*?\r?\n)CurrentTheme=com\.replaykit[^\r\n]*\r?\n",
                    "$1", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (text == original) continue;
                try
                {
                    File.Copy(path, path + ".replaykit-uninstall.bak", true);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    log?.Invoke("scrubbed " + name + " (Controls dock, ReplayKit theme; OBS dock layout resets to default)");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { ok = false; log?.Invoke("warn: write " + name + ": " + ex.Message); }
            }
            return ok;
        }

        // the ExtraBrowserDocks value with the ReplayKit "Custom Controls" entry removed, re-escaped for obs's ini parser (\ -> \\).
        private static string StripReplaykitDock(string iniValue)
        {
            JArray docks;
            try { docks = JsonConvert.DeserializeObject<JToken>(iniValue.Replace("\\\\", "\\")) as JArray; }
            catch (JsonException) { return iniValue; }
            if (docks == null) return iniValue;
            var kept = new JArray(docks.Where(d => !IsReplaykitDock(d)));
            if (kept.Count == docks.Count) return iniValue;
            return JsonConvert.SerializeObject(kept).Replace("\\", "\\\\");
        }

        private static bool IsReplaykitDock(JToken item)
        {
            if (!(item is JObject o)) return false;
            string title = (o.Value<string>("title") ?? "").Trim().ToLowerInvariant();
            string url = (o.Value<string>("url") ?? "").Replace("\\", "/").ToLowerInvariant();
            string uuid = (o.Value<string>("uuid") ?? "").Replace("-", "").ToLowerInvariant();
            return uuid == "a59ce0ef5d6f4a4f91d9c7c3c1d4e2b0" || title == "custom controls"
                || url.Contains("obs-replaykit/obs-custom-dock/") || url.Contains("controls_app.html");
        }

        private static bool RemoveGeneratedObsThemes(Action<string> log)
        {
            bool ok = true;
            string themesDir = Path.Combine(Config.OBS_CONFIG, "themes");
            if (Directory.Exists(themesDir))
            {
                foreach (string f in Directory.GetFiles(themesDir, "rk_*.ovt"))
                {
                    try { File.Delete(f); log?.Invoke("removed generated theme " + Path.GetFileName(f)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { ok = false; log?.Invoke("warn: delete " + Path.GetFileName(f) + ": " + ex.Message); }
                }
            }
            foreach (string marker in Directory.Exists(Config.OBS_CONFIG)
                ? Directory.GetFiles(Config.OBS_CONFIG, ".replaykit-*") : Array.Empty<string>())
                try { File.Delete(marker); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return ok;
        }

        // remove OBS Studio the way its own installer would: run the registered uninstaller silently (QuietUninstallString
        // from the Uninstall key -- OBS writes it under WOW6432Node even for the 64-bit build -- else <obs>\uninstall.exe
        // /S), wait for obs64.exe to go, then clear what it leaves behind (install dir, Start Menu shortcuts, a dangling
        // Uninstall row). a broken/partial OBS has no working uninstaller, so the dir is force-removed when no obs64.exe
        // remains. scenes + profiles under %APPDATA%\obs-studio are kept -- that matches a normal OBS uninstall.
        public static bool UninstallObsStudio(Action<string> log = null)
        {
            string obsDir = Config.PROGRAMFILES_OBS_DIR;
            bool hadCore = Obs.CoreInstalledAt(obsDir) || Obs.FindObsExe() != null;

            string command = ReadObsQuietUninstallString();
            string exe = null, arguments = "/S";
            if (!string.IsNullOrEmpty(command))
            {
                (exe, arguments) = SplitCommandLine(command);
                if (arguments.IndexOf("/S", StringComparison.OrdinalIgnoreCase) < 0) arguments = ("/S " + arguments).Trim();
            }
            else
            {
                string fallback = Path.Combine(obsDir, "uninstall.exe");
                if (File.Exists(fallback)) exe = fallback;
            }

            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                log?.Invoke("running OBS Studio uninstaller: " + exe + " " + arguments);
                try
                {
                    var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true };
                    using (var proc = Process.Start(psi))
                        proc?.WaitForExit(2 * 60 * 1000);
                }
                catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
                {
                    log?.Invoke("warn: OBS Studio uninstaller could not start: " + exc.Message);
                }
                // nsis /S keeps running after the launcher returns -- wait for obs64.exe to actually be gone
                for (int i = 0; i < 120 && Obs.FindObsExe() != null; i++)
                    Thread.Sleep(1000);
            }
            else if (hadCore)
            {
                log?.Invoke("warn: OBS Studio is installed but its uninstaller was not found; removing its files directly.");
            }
            else
            {
                log?.Invoke("OBS Studio is not installed; clearing any leftover files.");
            }

            // a real obs64.exe still there means the uninstaller genuinely failed -- never force-delete a working install
            if (Obs.CoreInstalledAt(obsDir) || Obs.FindObsExe() != null)
            {
                log?.Invoke("warn: OBS Studio is still present after the uninstaller ran; it may need a manual uninstall.");
                return false;
            }

            bool ok = RemoveBrokenObsInstallDir(obsDir, log);
            RemoveObsStartMenuShortcuts(log);
            RemoveDanglingObsUninstallKeys(log);
            log?.Invoke(hadCore ? "OBS Studio removed." : "OBS Studio leftovers cleared.");
            return ok;
        }

        // undo RegisterClipNotificationShortcut / the helper's ToastNotify: delete our own Start Menu shortcuts and the
        // HKCU header registration, and un-stamp our AppUserModelID from OBS Studio's own shortcut (left pristine).
        public static bool RemoveClipNotificationShortcut(Action<string> log = null)
        {
            bool ok = true;
            string aumid = Shortcuts.ClipNotifyAumid;

            void DeleteIfPresent(string p)
            {
                try { if (File.Exists(p)) { File.Delete(p); log?.Invoke("removed " + p); } }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { ok = false; log?.Invoke("warn: could not remove " + p + ": " + ex.Message); }
            }

            DeleteIfPresent(Path.Combine(Shortcuts.UserPrograms, "OBS ReplayKit.lnk"));
            DeleteIfPresent(Path.Combine(Shortcuts.AllUsersPrograms, "OBS ReplayKit.lnk"));

            // a user-scope "OBS Studio.lnk" only exists if we wrote it as a fallback -- drop it when it carries our id
            string userObs = Path.Combine(Shortcuts.UserPrograms, "OBS Studio.lnk");
            if (Shortcuts.HasAumid(userObs, aumid)) DeleteIfPresent(userObs);

            // OBS's own all-users shortcut: never delete it here, only clear our property
            string allObs = Path.Combine(Shortcuts.AllUsersPrograms, "OBS Studio.lnk");
            if (Shortcuts.HasAumid(allObs, aumid)) Shortcuts.ClearAumid(allObs, log);

            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId", writable: true);
                k?.DeleteSubKeyTree(aumid, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            return ok;
        }

        private static void RemoveObsStartMenuShortcuts(Action<string> log)
        {
            var roots = new[]
            {
                Path.Combine(Config.PROGRAMDATA, "Microsoft", "Windows", "Start Menu", "Programs"),
                Path.Combine(Config.APPDATA, "Microsoft", "Windows", "Start Menu", "Programs"),
            };
            foreach (var root in roots)
            {
                try
                {
                    string folder = Path.Combine(root, "OBS Studio");
                    if (Directory.Exists(folder)) { Directory.Delete(folder, true); log?.Invoke("removed Start Menu folder " + folder); }
                    string lnk = Path.Combine(root, "OBS Studio.lnk");
                    if (File.Exists(lnk)) { File.Delete(lnk); log?.Invoke("removed " + lnk); }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // drop the "OBS Studio" Add/Remove Programs row, but only when what it points at is gone -- never orphan a live install.
        private static void RemoveDanglingObsUninstallKeys(Action<string> log)
        {
            var hives = new (Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View)[]
            {
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
                (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64),
            };
            foreach (var (hive, view) in hives)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    if (uninstall == null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        string display, loc;
                        using (var app = uninstall.OpenSubKey(name))
                        {
                            display = app?.GetValue("DisplayName") as string;
                            loc = app?.GetValue("InstallLocation") as string;
                        }
                        if (display == null || !display.StartsWith("OBS Studio", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrEmpty(loc) && Obs.CoreInstalledAt(loc)) continue;
                        uninstall.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                        log?.Invoke("removed stale registry entry Uninstall\\" + name);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            }
        }

        private static string ReadObsQuietUninstallString()
        {
            var hives = new (Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View)[]
            {
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
                (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64),
            };
            foreach (var (hive, view) in hives)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        string display = app?.GetValue("DisplayName") as string;
                        if (display == null || !display.StartsWith("OBS Studio", StringComparison.OrdinalIgnoreCase)) continue;
                        return (app.GetValue("QuietUninstallString") as string)
                               ?? (app.GetValue("UninstallString") as string);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
                {
                }
            }
            return null;
        }

        // split "\"C:\\path\\uninstall.exe\" /S" into the exe and the rest.
        private static (string Exe, string Args) SplitCommandLine(string command)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                if (end > 0) return (command.Substring(1, end - 1), command.Substring(end + 1).Trim());
            }
            int space = command.IndexOf(' ');
            return space < 0 ? (command, "") : (command.Substring(0, space), command.Substring(space + 1).Trim());
        }

        public static List<string> RunCleanup(IInstallProgress progress, bool keepUserSettings = true, bool removeObs = false)
        {
            var steps = new List<(string Title, string Detail, Func<object> Action)>
            {
                ("Close OBS", "Stops OBS and ReplayKit helpers, but keeps this setup window alive.", () => StopObsAndHelpers(progress.LogLine)),
                ("Remove launch permission", "Deletes the ReplayKit scheduled task.", () => ScheduledTask.DeleteElevationTask(progress.LogLine)),
                ("Remove Windows startup", "Stops ReplayKit from launching OBS when Windows signs in.", () => Startup.ConfigureObsStartup(false, progress.LogLine)),
                ("Remove Windows sleep override", "Restores default Windows sleep behavior for OBS.", () => SleepOverride.RemoveSleepOverride(progress.LogLine)),
                ("Remove OBS plugins", "Deletes ReplayKit OBS plugins from the OBS install folder.", () => RemoveReplaykitPlugins(progress.LogLine)),
                ("Remove ReplayKit from OBS", "Removes the ReplayKit scene, sources, Custom Controls dock, script entry and generated theme from OBS's own config.", () => RemoveReplaykitFromObsConfig(progress.LogLine)),
                ("Remove clip notifications", "Removes the Start Menu shortcut and header registration used for clip-ready notifications.", () => RemoveClipNotificationShortcut(progress.LogLine)),
                ("Remove OBS Stream Audio", "Uninstalls the ReplayKit virtual audio device.", () => VbCable.UninstallVbcable(progress.LogLine)),
                ("Remove virtual display driver", "Deletes the optional virtual display driver if it exists.", () => RemoveVirtualDisplayDriver(progress.LogLine)),
            };
            if (keepUserSettings)
                steps.Add(("Keep ReplayKit settings", "Saves current ReplayKit settings for the next install.", () => SaveUserSettings(progress.LogLine)));
            else
                steps.Add(("Remove ReplayKit settings", "Deletes saved ReplayKit preferences.", () => RemoveUserSettings(progress.LogLine)));
            steps.Add(("Wipe OBS ReplayKit config", "Deletes ReplayKit's OBS config folder while preserving OBS scenes and profiles.", () => WipeObsConfig(progress.LogLine)));
            steps.Add(("Clear the setup cache", "Removes Streamable login data and schedules the cached uninstaller in LOCALAPPDATA for deletion on the next reboot.", () => ClearSetupCache(keepUserSettings, progress.LogLine)));
            if (removeObs)
                steps.Add(("Uninstall OBS Studio", "Runs OBS Studio's own uninstaller and clears what it leaves behind. Your OBS scenes and profiles in AppData are kept.", () => UninstallObsStudio(progress.LogLine)));

            progress.TotalSteps = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                Apply.RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action);
            }
            progress.Render(progress.TotalSteps, "Cleanup complete", "OBS ReplayKit changes were removed.", "done");
            return progress.Issues;
        }
    }
}
