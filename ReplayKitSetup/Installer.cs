using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // install bundled obs config into %appdata%/obs-studio/. walks assets/obs-studio/ as a 1:1 mirror, pipes text files thru RewriteUserPaths + ApplyPreferences before writing. ported from obs_replaykit/installer.py.
    public static class Installer
    {
        // top-level entries backed up before overwriting. obs-replayKit/ covers the whole replaykit-managed tree (lua scripts, dock html, presets); global.ini is merged not overwritten but cheap to back up anyway.
        private static readonly string[] BackupTargets = { "basic", "obs-replayKit", "plugin_manager", "plugin_config", "user.ini", "global.ini" };

        // scene path inside %appdata%\obs-studio\ transformed on fresh installs and patched on runtime updates -- when it already exists, the full installer uses that live file as the transform input so user-added filters and source state survive reinstall/repair installs.
        private static readonly string UserSceneRel = Path.Combine("basic", "scenes", "Untitled.json");

        private static string ReadTextFile(string path) => Config.ReadTextFileFlexible(path);

        // true if this scene json has replaykits own monitor_capture (display capture) source in it -- a bare scene obs auto-creates on its own first launch is valid json too, but ApplyScenesJson only patches sources that already exist (it creates window_capture and the overlays, nothing else), so treating a non-replaykit scene as the reinstall base silently drops display capture, game capture, mic, and desktop audio. monitor_capture id must stay in sync with transform.py.
        private static bool LooksLikeReplaykitScene(JToken data)
        {
            if (!(data is JObject obj)) return false;
            if (!(obj["sources"] is JArray sources)) return false;
            return sources.Any(s => s is JObject so && so.Value<string>("id") == "monitor_capture");
        }

        private static string InstallTextSource(string src, string rel, string dst)
        {
            if (rel == UserSceneRel && File.Exists(dst))
            {
                try
                {
                    string content = ReadTextFile(dst);
                    if (LooksLikeReplaykitScene(JToken.Parse(content))) return content;
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                {
                    // fall through to the bundled default.
                }
            }
            return ReadTextFile(src);
        }

        // retry a write for a while on a locked file -- during an update, the outgoing helper process can still hold its own exe file open for a moment after obs exits, since it notices obs is gone and self-exits asynchronously rather than at that same instant. an update-initiating caller may also be racing this on its own, so this retry is the backstop that holds regardless of what triggered the update. normally the old process is gone in well under a second; 20s is padding for a loaded system, not the expected case.
        private static void WriteWithRetry(Action writeFn)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20.0);
            while (true)
            {
                try
                {
                    writeFn();
                    return;
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline) throw;
                    System.Threading.Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException)
                {
                    if (DateTime.UtcNow >= deadline) throw;
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        // copy src to dst; text files get RewriteUserPaths + ApplyPreferences first.
        private static void InstallFile(string src, string rel, string dst, Preferences prefs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (Config.TEXT_EXTS.Contains(Path.GetExtension(src)))
            {
                string content = InstallTextSource(src, rel, dst);
                content = PathRewrite.RewriteUserPaths(content, Config.USERNAME);
                content = Transform.ApplyPreferences(rel, content, prefs);
                WriteWithRetry(() => File.WriteAllText(dst, content, new System.Text.UTF8Encoding(false)));
            }
            else
            {
                WriteWithRetry(() => File.Copy(src, dst, true));
            }
        }

        // write the installed ReplayKit runtime version used by the updater.
        public static string WriteReplaykitVersion(Action<string> log = null)
        {
            string path = Path.Combine(Config.REPLAYKIT_CONFIG, "version.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(new JObject { ["version"] = VersionInfo.Version }, Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false));
            log?.Invoke($"ReplayKit version -> {VersionInfo.Version}");
            return path;
        }

        public static bool CacheSetupExecutable(Action<string> log = null)
        {
            string source = Path.Combine(Config.BUNDLE_ROOT, "OBSReplayKit.exe");
            if (!File.Exists(source))
            {
                log?.Invoke("warn: setup executable was not cached for in-app cleanup");
                return false;
            }
            string target = Config.REPLAYKIT_SETUP_EXE;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, target, true);
                }
                // self-contained single-file exe (see ReplayKitSetup.csproj) -- the cached copy runs standalone, no sibling assemblies to carry across.
                log?.Invoke($"cached setup executable -> {target}");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke($"warn: setup executable cache failed: {ex.Message}");
                return false;
            }
        }

        // mirror assets/obs-studio/ into %appdata%/obs-studio/. returns the file count.
        public static int InstallObsConfig(Preferences prefs, Action<string> log = null)
        {
            if (!Directory.Exists(Config.OBS_ASSETS_DIR))
            {
                log?.Invoke($"warn: {Config.OBS_ASSETS_DIR} not found - nothing to install");
                return 0;
            }

            MigrateLegacyStreamableState(log);

            int count = 0;
            foreach (var src in Directory.EnumerateFiles(Config.OBS_ASSETS_DIR, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(Config.OBS_ASSETS_DIR.Length).TrimStart('\\', '/');
                string dst = Path.Combine(Config.OBS_CONFIG, rel);
                InstallFile(src, rel, dst, prefs);
                log?.Invoke("-> " + rel.Replace('\\', '/'));
                count++;
            }
            WriteReplaykitVersion(log);
            CacheSetupExecutable(log);
            CleanupReplaykitLegacyFiles(log);
            RestoreReplaykitUserState(log);
            return count;
        }

        private static readonly HashSet<string> RuntimePreserveRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("obs-replayKit", "scripts", "replaykit_settings.json"),
            Path.Combine("obs-replayKit", "scripts", "helper", "clips_db.json"),
            Path.Combine("obs-replayKit", "scripts", "helper", "clips_index.json"),
        };

        private static readonly string[] RuntimeDeleteRels =
        {
            Path.Combine("obs-replayKit", "scripts", "replay_buffer", "replay_buffer_saved.mp3"),
            Path.Combine("obs-replayKit", "scripts", "audio", "auto_pick_monitor_device.lua"),
            Path.Combine("obs-replayKit", "scripts", "helper", "replaykit_update_bootstrap.ps1"),
        };

        private static readonly string[] RuntimeStateRels =
        {
            Path.Combine("obs-replayKit", "scripts", "helper", "clips_db.json"),
            Path.Combine("obs-replayKit", "scripts", "helper", "clips_index.json"),
        };

        // installs made before the streamable/ -> helper/ rename: source name (in the old directory) -> new relative path.
        private static readonly string LegacyStreamableDirRel = Path.Combine("obs-replayKit", "scripts", "streamable");
        private static readonly Dictionary<string, string> LegacyStreamableStateRels = new Dictionary<string, string>
        {
            ["clips_db.json"] = Path.Combine("obs-replayKit", "scripts", "helper", "clips_db.json"),
            ["clips_index.json"] = Path.Combine("obs-replayKit", "scripts", "helper", "clips_index.json"),
            ["helper_config.json"] = Path.Combine("obs-replayKit", "scripts", "helper", "helper_config.json"),
        };

        // one-time migration for installs made before the streamable/ directory was renamed to helper/: carries the live clip db/index/helper-config forward to their new path, then removes the old directory tree. no-ops once the old directory is gone, so its safe to call on every install.
        private static void MigrateLegacyStreamableState(Action<string> log = null)
        {
            string oldDir = Path.Combine(Config.OBS_CONFIG, LegacyStreamableDirRel);
            if (!Directory.Exists(oldDir)) return;

            foreach (var kv in LegacyStreamableStateRels)
            {
                string oldFile = Path.Combine(oldDir, kv.Key);
                string newFile = Path.Combine(Config.OBS_CONFIG, kv.Value);
                if (File.Exists(oldFile) && !File.Exists(newFile))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newFile));
                        File.Copy(oldFile, newFile, true);
                        log?.Invoke($"migrated legacy state: {kv.Key} -> {kv.Value.Replace('\\', '/')}");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        log?.Invoke($"warn: could not migrate legacy {kv.Key}: {ex.Message}");
                    }
                }
            }
            try
            {
                Directory.Delete(oldDir, true);
                log?.Invoke($"removed legacy directory: {LegacyStreamableDirRel.Replace('\\', '/')}");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke($"warn: could not remove legacy directory {LegacyStreamableDirRel.Replace('\\', '/')}: {ex.Message}");
            }
        }

        // scene-source patches applied during auto-update so property fixes (eg. flipping capture_audio on Game Capture) land on existing users without a fresh setup run -- each key is a source name in Untitled.json, each value a flat dotted-path -> value map, and only the listed fields are touched, everything else on that source is left alone. add entries here when a release needs to change a source property on existing installs.
        private static readonly Dictionary<string, Dictionary<string, object>> SceneSourcePatches = new Dictionary<string, Dictionary<string, object>>
        {
            ["Game Capture"] = new Dictionary<string, object>
            {
                ["settings.capture_audio"] = false,
                ["settings.hook_rate"] = 2,
                ["settings.limit_framerate"] = true,
            },
            ["Audio Input Capture"] = new Dictionary<string, object> { ["monitoring_type"] = 0 },
            ["Desktop Audio (excl. Discord)"] = new Dictionary<string, object> { ["monitoring_type"] = 0 },
            ["Discord Audio (record only)"] = new Dictionary<string, object> { ["monitoring_type"] = 0 },
        };

        // remove ReplayKit-managed files that were renamed or retired.
        public static int CleanupReplaykitLegacyFiles(Action<string> log = null)
        {
            int removed = 0;
            foreach (var rel in RuntimeDeleteRels)
            {
                string target = Path.Combine(Config.OBS_CONFIG, rel);
                try
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                        removed++;
                        log?.Invoke("removed legacy: " + rel.Replace('\\', '/'));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log?.Invoke($"warn: could not remove legacy {rel.Replace('\\', '/')}: {ex.Message}");
                }
            }
            return removed;
        }

        // restore user-owned clip state after an uninstall/reinstall that kept settings.
        public static int RestoreReplaykitUserState(Action<string> log = null)
        {
            int restored = 0;
            foreach (var rel in RuntimeStateRels)
            {
                string source = Path.Combine(Config.REPLAYKIT_USER_STATE_CACHE, Path.GetFileName(rel));
                string target = Path.Combine(Config.OBS_CONFIG, rel);
                if (!File.Exists(source) || File.Exists(target)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, true);
                    restored++;
                    log?.Invoke("restored state: " + rel.Replace('\\', '/'));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log?.Invoke($"warn: could not restore state {rel.Replace('\\', '/')}: {ex.Message}");
                }
            }
            return restored;
        }

        // write value into target at a dotted path, creating intermediate objects as needed.
        private static void SetNested(JObject target, string dotted, object value)
        {
            var parts = dotted.Split('.');
            JObject cursor = target;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!(cursor[parts[i]] is JObject next))
                {
                    next = new JObject();
                    cursor[parts[i]] = next;
                }
                cursor = next;
            }
            cursor[parts[parts.Length - 1]] = JToken.FromObject(value);
        }

        // patch known ReplayKit-managed sources in the users scene file in place, walking SceneSourcePatches and overwriting only the listed fields; safe when the file is missing or has no matches (returns 0), and skips writing if nothing changed so the mtime doesnt bump for no reason.
        public static int ApplyScenePatches(Action<string> log = null)
        {
            if (SceneSourcePatches.Count == 0) return 0;
            string scenePath = Path.Combine(Config.OBS_CONFIG, UserSceneRel);
            if (!File.Exists(scenePath)) return 0;

            JObject data;
            try
            {
                data = JObject.Parse(File.ReadAllText(scenePath, System.Text.Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                log?.Invoke($"warn: cannot parse scene file for patches: {ex.Message}");
                return 0;
            }

            if (!(data["sources"] is JArray sources)) return 0;

            int patched = 0;
            foreach (var sourceToken in sources)
            {
                if (!(sourceToken is JObject source)) continue;
                string name = source.Value<string>("name") ?? "";
                if (!SceneSourcePatches.TryGetValue(name, out var spec)) continue;

                string before = source.ToString(Formatting.None);
                foreach (var kv in spec) SetNested(source, kv.Key, kv.Value);
                if (source.ToString(Formatting.None) != before)
                {
                    patched++;
                    log?.Invoke($"scene patch -> {name}: {string.Join(", ", spec.Keys)}");
                }
            }

            if (patched > 0)
            {
                try
                {
                    File.WriteAllText(scenePath, data.ToString(Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log?.Invoke($"warn: scene patch save failed: {ex.Message}");
                    return 0;
                }
            }
            return patched;
        }

        private sealed class RuntimeChange
        {
            public string Target;
            public string Backup;
            public bool Existed;
        }

        private static void StageRuntimeFile(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (Config.TEXT_EXTS.Contains(Path.GetExtension(src)))
            {
                string content = ReadTextFile(src);
                string rewritten = PathRewrite.RewriteUserPaths(content, Config.USERNAME);
                File.WriteAllText(dst, rewritten, new System.Text.UTF8Encoding(false));
            }
            else
            {
                File.Copy(src, dst, true);
            }
        }

        private static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static void VerifyFile(string expected, string actual)
        {
            if (!File.Exists(actual) || new FileInfo(expected).Length != new FileInfo(actual).Length || FileSha256(expected) != FileSha256(actual))
                throw new IOException("File verification failed: " + actual);
        }

        private static void ReplaceFile(string source, string target, string transactionId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string incoming = target + ".replaykit-" + transactionId + ".tmp";
            try
            {
                WriteWithRetry(() =>
                {
                    if (File.Exists(incoming)) File.Delete(incoming);
                    File.Copy(source, incoming, false);
                });
                VerifyFile(source, incoming);
                WriteWithRetry(() =>
                {
                    if (File.Exists(target)) File.Replace(incoming, target, null, true);
                    else File.Move(incoming, target);
                });
                VerifyFile(source, target);
            }
            finally
            {
                try { if (File.Exists(incoming)) File.Delete(incoming); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static void RollbackRuntimeChanges(List<RuntimeChange> changes, string transactionId, Action<string> log)
        {
            var failures = new List<string>();
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                RuntimeChange change = changes[i];
                try
                {
                    if (change.Existed) ReplaceFile(change.Backup, change.Target, transactionId + "-rollback");
                    else WriteWithRetry(() => { if (File.Exists(change.Target)) File.Delete(change.Target); });
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(change.Target) + ": " + ex.Message);
                }
            }
            if (failures.Count > 0)
                throw new IOException("Update rollback was incomplete: " + string.Join("; ", failures));
            log?.Invoke("runtime file transaction rolled back cleanly");
        }

        // the runtime tree an update installs from. exposed so the updater can preflight it before it closes obs, against the same path InstallReplaykitRuntimeUpdate walks.
        public static string GetRuntimeAssetsDir() => Path.Combine(Config.OBS_ASSETS_DIR, "obs-replayKit");

        // refresh only ReplayKit-managed runtime files while preserving user obs config/state: copies everything under obs-replayKit/, preserves replaykit_settings.json, removes retired files, applies SceneSourcePatches to the scene file, and re-runs idempotent tool installs (ffmpeg, elevation task) so existing installs pick up new dependencies.
        public static int InstallReplaykitRuntimeUpdate(Action<string> log = null)
        {
            string runtimeSrc = GetRuntimeAssetsDir();
            if (!Directory.Exists(runtimeSrc)) throw new DirectoryNotFoundException($"ReplayKit runtime assets not found: {runtimeSrc}");
            string helperSource = Path.Combine(runtimeSrc, "scripts", "helper", "OBSReplayKit.exe");
            if (!File.Exists(helperSource) || new FileInfo(helperSource).Length < 256 * 1024)
                throw new InvalidDataException("The bundled ReplayKit helper is missing or incomplete.");

            string transactionId = Guid.NewGuid().ToString("N");
            string transactionRoot = Path.Combine(Path.GetTempPath(), "ReplayKit", "runtime-update-" + transactionId);
            string stageRoot = Path.Combine(transactionRoot, "stage");
            string backupRoot = Path.Combine(transactionRoot, "backup");
            var files = Directory.EnumerateFiles(runtimeSrc, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) throw new InvalidDataException("The bundled ReplayKit runtime is empty.");

            Directory.CreateDirectory(stageRoot);
            foreach (string src in files)
            {
                string fileRel = src.Substring(runtimeSrc.Length).TrimStart('\\', '/');
                string staged = Path.Combine(stageRoot, fileRel);
                StageRuntimeFile(src, staged);
                if (Config.TEXT_EXTS.Contains(Path.GetExtension(src)))
                {
                    if (!File.Exists(staged)) throw new IOException("Staging failed: " + fileRel);
                }
                else VerifyFile(src, staged);
            }
            log?.Invoke($"staged and verified {files.Count} runtime file(s)");

            int count = 0;
            var changes = new List<RuntimeChange>();
            try
            {
                foreach (string src in files)
                {
                    string fileRel = src.Substring(runtimeSrc.Length).TrimStart('\\', '/');
                    string rel = Path.Combine("obs-replayKit", fileRel);
                    string staged = Path.Combine(stageRoot, fileRel);
                    string dst = Path.Combine(Config.OBS_CONFIG, rel);
                    if (RuntimePreserveRels.Contains(rel) && File.Exists(dst))
                    {
                        log?.Invoke("preserve: " + rel.Replace('\\', '/'));
                        continue;
                    }

                    var change = new RuntimeChange { Target = dst, Existed = File.Exists(dst) };
                    if (change.Existed)
                    {
                        change.Backup = Path.Combine(backupRoot, fileRel);
                        Directory.CreateDirectory(Path.GetDirectoryName(change.Backup));
                        WriteWithRetry(() => File.Copy(dst, change.Backup, true));
                        VerifyFile(dst, change.Backup);
                    }
                    changes.Add(change);
                    ReplaceFile(staged, dst, transactionId);
                    log?.Invoke("-> " + rel.Replace('\\', '/'));
                    count++;
                }

                WriteReplaykitVersion(log);
            }
            catch (Exception installError)
            {
                try { RollbackRuntimeChanges(changes, transactionId, log); }
                catch (Exception rollbackError)
                {
                    throw new IOException("Runtime update failed: " + installError.Message + " " + rollbackError.Message, installError);
                }
                throw new IOException("Runtime update failed and was rolled back: " + installError.Message, installError);
            }
            finally
            {
                try { if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { log?.Invoke("warn: update staging cleanup failed: " + ex.Message); }
            }

            CacheSetupExecutable(log);
            CleanupReplaykitLegacyFiles(log);
            RestoreReplaykitUserState(log);

            // scene patches and tool refreshes are best-effort -- an exception here must not abort the update, since the runtime files are already on disk and obs will relaunch with them.
            try
            {
                int patched = ApplyScenePatches(log);
                if (patched > 0) log?.Invoke($"scene patches applied: {patched}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"warn: scene patch pass failed: {ex.Message}");
            }

            try
            {
                RunUpdateDriverRefresh(log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"warn: tool refresh pass failed: {ex.Message}");
            }

            return count;
        }

        // re-run idempotent installer functions during an auto-update; each one short-circuits when its target is already present (ffmpeg checks file hashes, elevation task checks schtasks), so new releases can plug in additional plugin installs here and existing users pick them up next auto-update.
        public static void RunUpdateDriverRefresh(Action<string> log = null)
        {
            try { InstallObsFfmpeg(log); }
            catch (Exception ex) { log?.Invoke($"warn: ffmpeg refresh skipped: {ex.Message}"); }

            try { ShaderFilter.InstallReplaykitMotionBlurPlugin(log); }
            catch (Exception ex) { log?.Invoke($"warn: OBS Shaderfilter refresh skipped: {ex.Message}"); }

            // the tray plugin was missing from this refresh entirely, so an updating install kept whatever replaykit.dll it already had and never picked up plugin-side changes -- and never dropped the pre-unification replaykit-tray.dll, which obs then loaded alongside the new one and injected every tray menu item twice. safe to copy here because the update flow has already closed obs.
            try { TrayPlugin.InstallReplaykitTrayPlugin(log); }
            catch (Exception ex) { log?.Invoke($"warn: tray plugin refresh skipped: {ex.Message}"); }

            try { RemoveObsElevationTask(log); }
            catch (Exception ex) { log?.Invoke($"warn: elevation task cleanup skipped: {ex.Message}"); }

            try
            {
                var prefs = Prefs.LoadPrefs();
                InstallObsSleepOverride(prefs.AllowSleepWhileActive, log);
            }
            catch (Exception ex) { log?.Invoke($"warn: sleep override refresh skipped: {ex.Message}"); }

            try
            {
                var prefs = Prefs.LoadPrefs();
                if (prefs.PinObsTrayIcon) TrayPin.PinObsTrayIcon(log);
                else TrayPin.UnpinObsTrayIcon(log);
            }
            catch (Exception ex) { log?.Invoke($"warn: tray icon pin refresh skipped: {ex.Message}"); }
        }

        // copy the users current obs config to obs-studio.bak.<timestamp>. returns the backup path, or null if there was nothing to back up.
        public static string BackupExistingConfig(Action<string> log = null)
        {
            if (!Directory.Exists(Config.OBS_CONFIG)) return null;

            var present = BackupTargets.Select(name => Path.Combine(Config.OBS_CONFIG, name)).Where(p => Directory.Exists(p) || File.Exists(p)).ToList();
            if (present.Count == 0) return null;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupDir = Path.Combine(Path.GetDirectoryName(Config.OBS_CONFIG), $"obs-studio.bak.{stamp}");
            Directory.CreateDirectory(backupDir);

            foreach (var src in present)
            {
                string dst = Path.Combine(backupDir, Path.GetFileName(src));
                try
                {
                    if (Directory.Exists(src)) CopyDirectory(src, dst);
                    else File.Copy(src, dst, true);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"warn: backup of {Path.GetFileName(src)} failed: {ex.Message}");
                }
            }

            log?.Invoke($"backed up existing config -> {backupDir}");
            return backupDir;
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(src.Length).TrimStart('\\', '/');
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        // make sure the recording output folder exists. ~/videos is also created as the simple-output fallback obs reverts to if a profile is reset.
        public static void EnsureRecordingDirs(Preferences prefs, Action<string> log = null)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { prefs.RecordingPath, Path.Combine(Config.USERPROFILE, "Videos") };
            foreach (var target in targets)
            {
                try
                {
                    Directory.CreateDirectory(target);
                    log?.Invoke("ok: " + target);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log?.Invoke($"warn: could not create {target}: {ex.Message}");
                }
            }
        }

        // sanity-check the dock html survived the main walker. the walker already mirror-copies it; this just flags missing files loudly.
        public static int InstallObsCustomDock(Action<string> log = null) => Dock.VerifyDockInstall(log);

        // the hidden, highest-privilege OBSReplayKit-Elevate task used to get installed on every apply regardless of whether run-as-admin was even turned on -- exactly the shape av heuristics read as a persistence mechanism, and it was flagging on every build. run-as-admin now always goes thru the normal UAC prompt instead, so this just cleans up the task on installs that already have one from an older release.
        public static bool RemoveObsElevationTask(Action<string> log = null) => ScheduledTask.DeleteElevationTask(log);

        // download + drop ffmpeg.exe and ffprobe.exe next to the helper. obs ships only obs-ffmpeg-mux.exe; compress/trim need the full pair. idempotent.
        public static bool InstallObsFfmpeg(Action<string> log = null) => FfmpegInstall.InstallFfmpeg(log);

        // Configure authenticated OBS WebSocket access for the helper.
        public static bool ConfigureObsWebsocket(Action<string> log = null) => WebSocketConfig.InstallWebsocketConfig(log);

        // let windows monitor/system sleep timers run while obs is active, or restore obs defaults.
        public static bool InstallObsSleepOverride(bool allowSleep = true, Action<string> log = null) =>
            allowSleep ? SleepOverride.InstallSleepOverride(log) : SleepOverride.RemoveSleepOverride(log);

        // the helper (OBSReplayKit.exe under scripts/helper/) is a full compiled C# app (ReplayKitHelper), built by build.bat straight into assets/ the same way replaykit.dll is a prebuilt native plugin -- there is no lightweight on-the-fly compile step for it the way the old thin PowerShell-hosting launcher had (this one needs the .NET SDK, NuGet restore, and an ILRepack merge, all done at repo build time); this just confirms the bundle actually shipped it, since helper_bootstrap.lua has no fallback if it's missing.
        public static bool EnsureLauncherBuilt(Action<string> log = null)
        {
            string launcherPath = Path.Combine(Config.OBS_ASSETS_DIR, "obs-replayKit", "scripts", "helper", "OBSReplayKit.exe");
            if (File.Exists(launcherPath)) return true;
            // "re-run build.bat" used to be here, which only makes sense to a dev with the source checked out -- an end user hitting this has no build.bat. the file was unpacked from the bundle successfully (AssetBundle.ExtractTo would have failed the whole install otherwise); it going missing right after almost always means antivirus quarantined it the moment it landed on disk as a fresh, unsigned exe. used to read Defender's wmi threat log to name Defender specifically -- dropped with System.Management for the trimmed build; the generic pointer covers Defender's Protection History too.
            log?.Invoke($"warn: helper launcher missing at {launcherPath} -- your antivirus likely quarantined it right after install. check its quarantine or history (Windows Security > Protection History for Defender), restore or exclude the ReplayKit temp folder, then reinstall.");
            return false;
        }
    }
}
