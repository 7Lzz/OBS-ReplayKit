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
using ReplayKitShared;

namespace ReplayKitSetup
{
    // cleanup for OBS ReplayKit installs, used by the uninstall window and the headless --cleanup mode
    public static class Cleanup
    {
        // the clip state that is kept across an uninstall; it is looked up with StateLayout.Resolve, so an install that has not been moved to the new layout yet is kept too
        internal static readonly string[] ReplaykitRuntimeStateFiles = { StateLayout.ClipsDbFile, StateLayout.ClipsIndexFile, StateLayout.CompressionHistoryFile };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

        // MoveFileEx with a null target queues the path for deletion by the session manager on the next boot; needs admin, which this setup exe already has
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        // required before recovery or removal can change the installation
        public static bool StopObsAndHelpers(Action<string> log = null)
        {
            Obs.CloseObs(log, forceCloseRemaining: true);
            StopEncoder(log);
            if (!ShareWindowInstall.Stop(log))
                throw new IOException(SetupLocale.Message("screenshare.theShareWindowDidNotClose"));
            return true;
        }

        // an encode the helper started keeps running through its ffmpeg link after the helper is gone, and holds the files the removal deletes
        private static void StopEncoder(Action<string> log)
        {
            string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Config.HELPER_EXE), VideoTools.EncoderAlias));
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        // hold the process handle from the image check through termination and the exit wait
                        _ = process.Handle;
                        string image = process.MainModule?.FileName;
                        if (image == null) throw new IOException("could not verify the encoder image path");
                        if (!string.Equals(Path.GetFullPath(image), target, StringComparison.OrdinalIgnoreCase)) continue;
                        process.Kill();
                        if (!process.WaitForExit(3000)) throw new IOException("the encoder did not exit in time");
                    }
                    catch (Exception ex) when ((ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) && process.HasExited) { }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException)
                    {
                        log?.Invoke("warn: encoder stop failed for pid " + process.Id + ": " + ex.Message);
                        throw new IOException(SetupLocale.Message("setup.obsOrItsHelperBrowserProcesses"), ex);
                    }
                }
            }
        }

        public static bool RemoveReplaykitPlugins(Action<string> log = null)
        {
            var targets = WinCapture.InstalledPaths
                .Concat(InputOverlay.InstalledPaths)
                .Concat(BongoCat.InstalledPaths)
                .Concat(ShaderFilter.InstalledPaths)
                // the obs updater comes back with replaykit gone
                .Concat(new[] { Config.REPLAYKIT_PLUGIN_DIR, ObsUpdateLock.MarkerPath });
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
            // a broken/phantom C:\Program Files\obs-studio (no obs64.exe, just our plugins + stale uninstaller stubs) is what seeds the "installer wrote plugins into a dead folder" bug on the next run. drop it here even on a keep-OBS uninstall -- RemoveBrokenObsInstallDir never touches a real install.
            RemoveBrokenObsInstallDir(Config.PROGRAMFILES_OBS_DIR, log);
            return ok;
        }

        // remove C:\Program Files\obs-studio ONLY when it holds no working OBS (no bin\64bit\obs64.exe). a real install is left for OBSs own uninstaller; a phantom folder is force-removed so the next install starts clean.
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

        // the items of deleted history live in a hidden folder of the clip folder, which the config wipe never reaches, so they would stay hidden on the disk for good; only the files the history recorded, inside a folder of that name, are removed
        public static bool EmptyDeletedHistory(Action<string> log = null)
        {
            string store = StateLayout.Resolve(Config.REPLAYKIT_CONFIG, StateLayout.DeletedHistoryFile);
            if (!File.Exists(store)) return true;
            JObject db;
            try { db = JObject.Parse(File.ReadAllText(store)); }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException || exc is JsonException)
            {
                log?.Invoke("warn: deleted history could not be read: " + exc.Message);
                return false;
            }
            bool ok = true;
            int removed = 0;
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in db.Properties())
            {
                try
                {
                    var value = (prop.Value as JObject)?["stored"];
                    if (value?.Type != JTokenType.String || !NetCompat.IsPathFullyQualified((string)value))
                        throw new InvalidDataException(SetupLocale.Message("setup.theDeletedHistoryPathIsInvalid"));
                    string stored = Path.GetFullPath((string)value);
                    string folder = Path.GetDirectoryName(stored);
                    if (!string.Equals(Path.GetFileName(folder), StateLayout.DeletedHistoryFolder, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(SetupLocale.Message("setup.theDeletedHistoryFolderIsInvalid"));
                    for (string parent = stored; parent != null; parent = Path.GetDirectoryName(parent))
                    {
                        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                            throw SetupLocale.Error("setup.theDeletedHistoryRunsThroughALink", m => new IOException(m), "path", parent);
                    }
                    File.Delete(stored);
                    folders.Add(folder);
                    removed++;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException || exc is InvalidDataException || exc is ArgumentException || exc is NotSupportedException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove a deleted history item: " + exc.Message);
                }
            }
            foreach (string folder in folders)
            {
                try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
                catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException) { log?.Invoke("warn: could not remove " + folder + ": " + exc.Message); }
            }
            log?.Invoke("deleted history emptied: " + removed + " item(s)");
            return ok;
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
                foreach (var file in ReplaykitRuntimeStateFiles)
                {
                    string source = StateLayout.Resolve(Config.REPLAYKIT_CONFIG, file);
                    if (!File.Exists(source)) continue;
                    string target = Path.Combine(Config.REPLAYKIT_USER_STATE_CACHE, file);
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
            bool ok = true;
            if (!keepUserSettings)
            {
                string credentials = Path.Combine(Config.LOCALAPPDATA, StateLayout.StreamableCredentialsFolder);
                try
                {
                    if ((File.GetAttributes(credentials) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(SetupLocale.Message("setup.theStreamableSignInFolderIsALink"));
                    File.Delete(Path.Combine(credentials, StateLayout.StreamableCredentialsFile));
                    File.Delete(Path.Combine(credentials, StateLayout.StreamableLegacyCookieFile));
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ok = false;
                    log?.Invoke("warn: could not remove Streamable credentials: " + ex.Message);
                }
            }
            string cacheDir = Config.REPLAYKIT_SETUP_CACHE;
            if (!Directory.Exists(cacheDir)) return ok;

            // the streamable sign-in webview2 profile -- nothing reads it after an uninstall and no other step touches it
            string streamable = Path.Combine(cacheDir, ReplayKitFolder.StreamableProfileFolder);
            if (Directory.Exists(streamable))
            {
                try { Directory.Delete(streamable, true); log?.Invoke("removed " + streamable); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { ok = false; log?.Invoke("warn: could not remove " + streamable + ": " + ex.Message); }
            }

            // the speech engine and model of auto subtitles, fetched by the helper the first time they were used
            string speech = Path.Combine(cacheDir, ReplayKitFolder.SpeechFolder);
            if (Directory.Exists(speech))
            {
                try { Directory.Delete(speech, true); log?.Invoke("removed " + speech); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { ok = false; log?.Invoke("warn: could not remove " + speech + ": " + ex.Message); }
            }

            // the cached uninstaller -- usually the exe this process is running from, so it is locked; fall back to a session-manager delete-on-reboot, queued before the folder below so the folder is empty by the time that entry runs
            string exe = Config.REPLAYKIT_SETUP_EXE;
            string exeMeta = SetupCache.MetaPath(exe);
            if (File.Exists(exeMeta))
            {
                try { File.Delete(exeMeta); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { log?.Invoke("warn: could not remove " + exeMeta + ": " + ex.Message); }
            }
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

            // remove the cache directory only when empty; the config archives survive uninstall
            if (!keepUserSettings && !Directory.EnumerateFileSystemEntries(cacheDir).Any(path => !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)))
            {
                try { Directory.Delete(cacheDir); log?.Invoke("removed " + cacheDir); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (MoveFileEx(cacheDir, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                        log?.Invoke("scheduled " + cacheDir + " for removal on the next reboot");
                    else { ok = false; log?.Invoke("warn: could not schedule " + cacheDir + " for removal (win32 " + Marshal.GetLastWin32Error() + ")"); }
                }
            }

            return ok;
        }

        // ReplayKits fingerprints inside OBSs OWN config -- WipeObsConfig only deletes the obs-replayKit/ folder, so without this the scene collection keeps listing replaykit.lua ("Error opening file: (null)" every launch), the All-In-One scene + its sources, the Custom Controls browser dock, and the generated obs theme all stay behind.
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

        // source/scene names ReplayKits bundled collection ships. the generic ones ("Display Capture" etc.) are only pulled when nothing the user kept still references them -- see the orphan check in ScrubSceneCollections.
        private static readonly HashSet<string> ReplaykitSceneNames = new HashSet<string>(StringComparer.Ordinal) { "All-In-One" };

        // the plugin adds the game out of focus card to the scenes of any collection with a capture source, the ones of the user too, and it has no settings to recognise it by; it goes even where a kept scene lists it, since its source type leaves with the plugin
        private static bool IsFocusCard(JObject src) =>
            string.Equals(src.Value<string>("id") ?? "", ReplayKitShared.OverlayScene.FocusCardSourceKind, StringComparison.Ordinal);

        private static bool LooksLikeReplaykitSource(JObject src)
        {
            string id = src.Value<string>("id") ?? "";
            string name = src.Value<string>("name") ?? "";
            if (string.Equals(id, OverlayScene.InputOverlaySourceKind, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsFocusCard(src)) return true;
            if (name == OverlayScene.DesktopAudioSourceName || name == OverlayScene.DiscordAudioSourceName) return true;
            var settings = src["settings"];
            return settings != null &&
                   settings.ToString(Formatting.None).IndexOf("obs-replaykit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ScrubSceneCollections(Action<string> log)
        {
            string scenesDir = Path.Combine(Config.OBS_CONFIG, "basic", "scenes");
            if (!Directory.Exists(scenesDir)) return true;
            bool ok = true;

            foreach (string path in Directory.GetFiles(scenesDir, "*.json"))
            {
                if (path.IndexOf(".bak", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                JObject root;
                try { root = JObject.Parse(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    log?.Invoke("warn: could not read " + Path.GetFileName(path) + ": " + ex.Message);
                    continue;
                }

                var sources = root["sources"] as JArray ?? new JArray();
                var groups = root["groups"] as JArray ?? new JArray();
                var removedScenes = new List<string>();
                var removedSources = new List<string>();
                bool changed = false;

                // drop the replaykit.lua entry from the scripts-tool module
                if (root["modules"]?["scripts-tool"] is JArray scriptsTool)
                {
                    var kept = new JArray(scriptsTool.Where(s =>
                        (s?["path"]?.Value<string>() ?? "").IndexOf("replaykit", StringComparison.OrdinalIgnoreCase) < 0));
                    if (kept.Count != scriptsTool.Count) { root["modules"]["scripts-tool"] = kept; changed = true; }
                }

                // remove the scenes of replaykit, keeping the first as a shell in case the collection ends up empty
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

                // names the surviving scenes still list (a doomed group must not shield its children, so groups are not counted yet)
                var sceneRefs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scene in sources.OfType<JObject>().Where(s => (s.Value<string>("id") ?? "") == "scene"))
                    foreach (var it in (scene["settings"]?["items"] as JArray ?? new JArray()))
                    { var n = it?["name"]?.Value<string>(); if (!string.IsNullOrEmpty(n)) sceneRefs.Add(n); }

                // drop the groups of replaykit first -- one no surviving scene lists and that is replaykits own or came from the removed scene
                foreach (var g in groups.OfType<JObject>().ToList())
                {
                    string name = g.Value<string>("name") ?? "";
                    if (sceneRefs.Contains(name)) continue;
                    if (!LooksLikeReplaykitSource(g) && !rkItemNames.Contains(name)) continue;
                    removedSources.Add(name);
                    g.Remove();
                    changed = true;
                }

                // now the reference set is the surviving scenes plus the groups that survived
                var referenced = new HashSet<string>(sceneRefs, StringComparer.Ordinal);
                foreach (var g in groups.OfType<JObject>())
                    foreach (var it in (g["settings"]?["items"] as JArray ?? new JArray()))
                    { var n = it?["name"]?.Value<string>(); if (!string.IsNullOrEmpty(n)) referenced.Add(n); }

                // remove the replaykit sources and whatever the removed scene held that nothing kept still uses
                foreach (var src in sources.OfType<JObject>().Where(s => (s.Value<string>("id") ?? "") != "scene").ToList())
                {
                    string name = src.Value<string>("name") ?? "";
                    if (referenced.Contains(name) && !IsFocusCard(src)) continue;
                    if (!LooksLikeReplaykitSource(src) && !rkItemNames.Contains(name)) continue;
                    removedSources.Add(name);
                    src.Remove();
                    changed = true;
                }

                // drop now-dangling items from the scenes and groups that survived
                var known = new HashSet<string>(sources.Concat(groups).OfType<JObject>()
                    .Select(s => s.Value<string>("name") ?? "").Where(n => n.Length > 0), StringComparer.Ordinal);
                foreach (var container in sources.Concat(groups).OfType<JObject>())
                    if (container["settings"]?["items"] is JArray items)
                    {
                        var pruned = new JArray(items.Where(it => known.Contains(it?["name"]?.Value<string>() ?? "")));
                        if (pruned.Count != items.Count) { container["settings"]["items"] = pruned; changed = true; }
                    }

                // never leave the collection with zero scenes
                if (!sources.OfType<JObject>().Any(s => (s.Value<string>("id") ?? "") == "scene") && shell != null)
                {
                    shell["name"] = "Scene";
                    shell["uuid"] = Guid.NewGuid().ToString();
                    shell["settings"] = new JObject { ["id_counter"] = 0, ["custom_size"] = false, ["items"] = new JArray() };
                    sources.Add(shell);
                    root["scene_order"] = new JArray { new JObject { ["name"] = "Scene" } };
                    log?.Invoke("scrubbed " + Path.GetFileName(path) + ": collection was entirely ReplayKit, left a blank \"Scene\"");
                }

                // repoint the active scene if it named something removed
                string firstScene = (root["scene_order"] as JArray)?.FirstOrDefault()?["name"]?.Value<string>()
                    ?? sources.OfType<JObject>().FirstOrDefault(s => (s.Value<string>("id") ?? "") == "scene")?.Value<string>("name");
                foreach (string key in new[] { "current_scene", "current_program_scene" })
                    if (firstScene != null && removedScenes.Contains(root[key]?.Value<string>() ?? "")) { root[key] = firstScene; changed = true; }

                if (!changed) continue;
                root["sources"] = sources;
                root["groups"] = groups;

                try
                {
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
                try { text = Config.ReadTextFileFlexible(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ok = false; log?.Invoke("warn: read " + name + ": " + ex.Message); continue; }
                string original = text;

                // [BasicWindow] ExtraBrowserDocks -- pull the Custom Controls entry, keep every other dock
                string docks = ObsIni.Get(text, "BasicWindow", "ExtraBrowserDocks");
                if (docks != null)
                {
                    string kept = StripReplaykitDock(docks);
                    if (kept != docks) text = ObsIni.Set(text, "BasicWindow", "ExtraBrowserDocks", kept);
                }

                // [BasicWindow] DockState -- the native plugin hid OBSs native "Controls" dock and OBS persisted that into this blob. it is an opaque QMainWindow::saveState() so there is no clean way to flip one dock; drop the whole line and OBS rebuilds its default layout (Controls dock visible) on next launch.
                if (ObsIni.Get(text, "BasicWindow", "DockState") != null) text = ObsIni.Remove(text, "BasicWindow", "DockState");

                // [Appearance] Theme= / [General] CurrentTheme= pointing at a generated ReplayKit theme -- drop the line, obs falls back to its default
                if ((ObsIni.Get(text, "Appearance", "Theme") ?? "").StartsWith(ThemeSheet.ObsThemeIdPrefix, StringComparison.OrdinalIgnoreCase))
                    text = ObsIni.Remove(text, "Appearance", "Theme");
                if ((ObsIni.Get(text, "General", "CurrentTheme") ?? "").StartsWith(ThemeSheet.ObsThemeIdPrefix, StringComparison.OrdinalIgnoreCase))
                    text = ObsIni.Remove(text, "General", "CurrentTheme");

                if (text == original) continue;
                try
                {
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    log?.Invoke("scrubbed " + name + " (Controls dock, ReplayKit theme; OBS dock layout resets to default)");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { ok = false; log?.Invoke("warn: write " + name + ": " + ex.Message); }
            }
            return ok;
        }

        // the ExtraBrowserDocks value with the ReplayKit "Custom Controls" entry removed, re-escaped for obss ini parser (\ -> \\).
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

        // the managed dock as Transform knows it, and any other dock page of the replaykit folder or the helper
        private static bool IsReplaykitDock(JToken item)
        {
            if (Transform.IsObsReplaykitDock(item)) return true;
            string url = ((item as JObject)?.Value<string>("url") ?? "").Replace("\\", "/").ToLowerInvariant();
            return url.Contains("obs-replaykit/obs-custom-dock/") || url.Contains("controls_app.html");
        }

        private static bool RemoveGeneratedObsThemes(Action<string> log)
        {
            bool ok = true;
            string themesDir = Path.Combine(Config.OBS_CONFIG, "themes");
            if (Directory.Exists(themesDir))
            {
                foreach (string f in Directory.GetFiles(themesDir, ThemeSheet.ObsThemeFilePrefix + "*.ovt"))
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

        // remove OBS Studio the way its own installer would: run the registered uninstaller silently (QuietUninstallString from the Uninstall key -- OBS writes it under WOW6432Node even for the 64-bit build -- else <obs>\uninstall.exe /S), wait for obs64.exe to go, then clear what it leaves behind (install dir, Start Menu shortcuts, a dangling Uninstall row). a broken/partial OBS has no working uninstaller, so the dir is force-removed when no obs64.exe remains. scenes + profiles under %APPDATA%\obs-studio are kept -- that matches a normal OBS uninstall.
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

        // undo RegisterClipNotificationShortcut / the helpers ToastNotify: delete our own Start Menu shortcuts and the HKCU header registration, and un-stamp our AppUserModelID from OBS Studios own shortcut (left pristine).
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

            // OBSs own all-users shortcut: never delete it here, only clear our property
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

        // the removal behind the uninstall window and the Uninstall button of the setup window, which starts obs again afterwards unless obs itself went too
        public static void RunUninstall(IInstallProgress progress, bool keepUserSettings, bool removeObs)
        {
            RunCleanup(progress, keepUserSettings, removeObs);
            if (removeObs) return;
            try { Obs.LaunchObs(progress.LogLine); }
            catch (Exception ex) { progress.LogLine("warn: could not relaunch OBS: " + ex.Message); }
        }

        public static List<string> RunCleanup(IInstallProgress progress, bool keepUserSettings = true, bool removeObs = false)
        {
            // a detached restart would start obs again in the middle of the removal
            using var operation = ObsExit.TryTakeOperation(ObsExit.SetupGateWaitMs);
            if (operation == null) throw new IOException(SetupLocale.Message("setup.anotherReplaykitUpdateIsAlready"));
            using var transaction = RuntimeTransaction.Open();
            var steps = new List<(SetupLocale.Text Title, SetupLocale.Text Detail, Func<object> Action)>
            {
                (SetupLocale.Pair("setup.closeObs"), SetupLocale.Pair("setup.stopsObsAndReplaykitHelpersBut"), () =>
                {
                    StopObsAndHelpers(progress.LogLine);
                    transaction.Recover(progress.LogLine);
                    return true;
                }),
                (SetupLocale.Pair("setup.restoreObsBrowser"), SetupLocale.Pair("setup.restoresTheOriginalObsBrowserProcess"), () => ProcessHost.Restore(progress.LogLine)),
                (SetupLocale.Pair("setup.removeLaunchPermission"), SetupLocale.Pair("setup.deletesTheReplaykitScheduledTask"), () => ScheduledTask.DeleteElevationTask(progress.LogLine)),
                (SetupLocale.Pair("setup.removeWindowsStartup"), SetupLocale.Pair("setup.stopsReplaykitFromLaunchingObsWhen"), () => Startup.ConfigureObsStartup(false, progress.LogLine)),
                (SetupLocale.Pair("setup.removeWindowsSleepOverride"), SetupLocale.Pair("setup.restoresDefaultWindowsSleepBehaviorFor"), () => SleepOverride.RemoveSleepOverride(progress.LogLine)),
                (SetupLocale.Pair("setup.removeObsPlugins"), SetupLocale.Pair("setup.deletesReplaykitObsPluginsFromThe"), () => RemoveReplaykitPlugins(progress.LogLine)),
                (SetupLocale.Pair("setup.removeReplaykitFromObs"), SetupLocale.Pair("setup.removesTheReplaykitSceneSourcesCustom"), () => RemoveReplaykitFromObsConfig(progress.LogLine)),
                (SetupLocale.Pair("setup.removeClipNotifications"), SetupLocale.Pair("setup.removesTheStartMenuShortcutAnd"), () => RemoveClipNotificationShortcut(progress.LogLine)),
                (SetupLocale.Pair("setup.removeObsStreamAudio"), SetupLocale.Pair("setup.uninstallsTheReplaykitVirtualAudioDevice"), () => VbCable.UninstallVbcable(progress.LogLine)),
                (SetupLocale.Pair("setup.removeVirtualDisplayDriver"), SetupLocale.Pair("setup.deletesTheOptionalVirtualDisplayDriver"), () => VirtualDisplayDriver.Remove(progress.LogLine)),
            };
            bool stateSaved = true;
            bool historyEmptied = false;
            if (keepUserSettings)
                steps.Add((SetupLocale.Pair("setup.keepReplaykitSettings"), SetupLocale.Pair("setup.savesCurrentReplaykitSettingsForThe"), () => stateSaved = SaveUserSettings(progress.LogLine)));
            else
                steps.Add((SetupLocale.Pair("setup.removeReplaykitSettings"), SetupLocale.Pair("setup.deletesSavedReplaykitPreferences"), () => RemoveUserSettings(progress.LogLine)));
            steps.Add((SetupLocale.Pair("setup.emptyDeletedHistory"), SetupLocale.Pair("setup.deletesTheClipsAndScreenshotsThat"), () => historyEmptied = EmptyDeletedHistory(progress.LogLine)));
            steps.Add((SetupLocale.Pair("setup.wipeObsReplaykitConfig"), SetupLocale.Pair("setup.deletesReplaykitSObsConfigFolder"), () => stateSaved && historyEmptied && WipeObsConfig(progress.LogLine)));
            steps.Add((SetupLocale.Pair("setup.clearTheSetupCache"), SetupLocale.Pair("setup.removesStreamableLoginDataAndSchedules"), () => ClearSetupCache(keepUserSettings, progress.LogLine)));
            if (removeObs)
                steps.Add((SetupLocale.Pair("setup.uninstallObsStudio"), SetupLocale.Pair("setup.runsObsStudioSOwnUninstaller"), () => UninstallObsStudio(progress.LogLine)));

            progress.TotalSteps = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                Apply.RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action);
            }
            progress.Render(progress.TotalSteps, SetupLocale.Pair("setup.cleanupComplete"), SetupLocale.Pair("setup.obsReplaykitChangesWereRemoved"), "done");
            return progress.Issues;
        }
    }
}
