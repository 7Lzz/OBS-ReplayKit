using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // install bundled obs config into %appdata%/obs-studio/. walks assets/obs-studio/ as a 1:1 mirror, pipes text files thru RewriteUserPaths + ApplyPreferences before writing
    public static class Installer
    {
        // scene path inside %appdata%\obs-studio\ transformed on fresh installs and patched on runtime updates -- when it already exists, the full installer uses that live file as the transform input so user-added filters and source state survive reinstall/repair installs.
        private static readonly string UserSceneRel = Path.Combine("basic", "scenes", "Untitled.json");
        private const string RuntimeVersionFile = "version.json";

        private static string ReadTextFile(string path) => Config.ReadTextFileFlexible(path);

        private static bool IsEmptyDefaultScene(JObject data)
        {
            return data["sources"] is JArray sources && sources.Count == 1 &&
                sources[0] is JObject scene && scene.Value<string>("id") == "scene" && scene.Value<string>("name") == "Scene" &&
                scene["settings"] is JObject settings && (settings["items"] == null || settings["items"] is JArray items && items.Count == 0) &&
                (scene["filters"] == null || scene["filters"] is JArray filters && filters.Count == 0) &&
                (data["groups"] == null || data["groups"] is JArray groups && groups.Count == 0) &&
                (data["transitions"] == null || data["transitions"] is JArray transitions && transitions.Count == 0) &&
                (data["modules"] is not JObject modules || modules["scripts-tool"] == null || modules["scripts-tool"] is JArray scripts && scripts.Count == 0);
        }

        private static string InstallTextSource(string src, string rel, string dst, out bool userScene)
        {
            userScene = false;
            if (rel == UserSceneRel && !IsMissingOrEmpty(dst))
            {
                string content = ReadTextFile(dst);
                userScene = !IsEmptyDefaultScene(JObject.Parse(content));
                if (userScene) return content;
            }
            return ReadTextFile(src);
        }

        // retry a write on a file another process still holds for a moment; obs and its helpers have exited before any caller writes, so what holds it is short lived, such as a scanner reading a file that was just written
        internal static void WriteWithRetry(Action writeFn)
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
                string content = InstallTextSource(src, rel, dst, out bool userScene);
                content = PathRewrite.RewriteUserPaths(content, Config.USERNAME);
                content = Transform.ApplyPreferences(rel, content, prefs, userScene);
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
            string path = Path.Combine(Config.REPLAYKIT_CONFIG, RuntimeVersionFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteVersion(temporary);
                NetCompat.MoveFile(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            log?.Invoke($"ReplayKit version -> {VersionInfo.Version}");
            return path;
        }

        private static void WriteVersion(string path) =>
            File.WriteAllText(path, JsonConvert.SerializeObject(new JObject { ["version"] = VersionInfo.Version }, Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false));

        // arm the in-obs first-run wizard by flipping setupWizardPending in the runtime settings file. the installer calls this only on a genuine first install; an update or a re-run leaves whatever the user already has, so a completed wizard never comes back.
        public static void MarkSetupWizardPending(Action<string> log = null)
        {
            string path = Prefs.RUNTIME_SETTINGS_FILE;
            try
            {
                JObject obj = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
                obj["setupWizardPending"] = true;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, obj.ToString(Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false));
                log?.Invoke("first-run setup wizard armed");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                log?.Invoke("warn: could not arm the first-run setup wizard: " + ex.Message);
            }
        }

        // the verified copy of this exe the in-app uninstall and the discord screenshare buttons run; a failure is a warning line the setup window lists as needing attention
        public static bool CacheSetupExecutable(Action<string> log = null) => SetupCache.Provision(log);

        public static void PrepareObsTheme(Action<string> log = null)
        {
            string helper = Config.HELPER_EXE;
            VerifyFile(Path.Combine(GetRuntimeAssetsDir(), Config.HELPER_EXE_REL), helper);
            var start = new System.Diagnostics.ProcessStartInfo(helper, "--prepare-theme")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helper),
            };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                if (process == null) throw new IOException(SetupLocale.Message("setup.couldNotStartObsThemePreparation"));
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    throw new IOException(SetupLocale.Message("setup.obsThemePreparationTimedOut"));
                }
                if (process.ExitCode != 0) throw new IOException(error.GetAwaiter().GetResult().Trim());
            }
            log?.Invoke("OBS theme prepared before launch");
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
            // before anything is written: the settings of the setup are merged into the existing settings file, which has to be the one in the new place by then
            MigrateStateLayout(log);

            int count = 0;
            foreach (var src in Directory.EnumerateFiles(Config.OBS_ASSETS_DIR, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(Config.OBS_ASSETS_DIR.Length).TrimStart('\\', '/');
                if (ShareWindowInstall.IsShareWindowFile(rel)) continue;
                string dst = InstallTarget(rel);
                InstallFile(src, rel, dst, prefs);
                log?.Invoke("-> " + rel.Replace('\\', '/'));
                count++;
            }
            if (ShareWindowInstall.Install(log)) count++;
            WriteReplaykitVersion(log);
            CacheSetupExecutable(log);
            CleanupReplaykitLegacyFiles(log);
            RestoreReplaykitUserState(log);
            return count;
        }

        private static readonly HashSet<string> RuntimePreserveRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("obs-replayKit", StateLayout.SettingsFile),
            Path.Combine("obs-replayKit", StateLayout.ClipsDbFile),
            Path.Combine("obs-replayKit", StateLayout.ClipsIndexFile),
            Path.Combine("obs-replayKit", StateLayout.CompressionHistoryFile),
        };

        private static readonly string[] RuntimeDeleteRels =
        {
            Path.Combine("obs-replayKit", "scripts", "replay_buffer", "replay_buffer_saved.mp3"),
            Path.Combine("obs-replayKit", "scripts", "replay_buffer", "obs-replaykit.ico"),
            Path.Combine("obs-replayKit", "scripts", "audio", "auto_pick_monitor_device.lua"),
            Path.Combine("obs-replayKit", "scripts", "helper", "replaykit_update_bootstrap.ps1"),
            Path.Combine("obs-replayKit", "scripts", "helper", "helper_endpoint.lua"),
            // the script jobs that moved into the helper, the plugin and lua itself
            Path.Combine("obs-replayKit", "scripts", "helper", "restart_obs.ps1"),
            Path.Combine("obs-replayKit", "scripts", "hevc_compat", "fix_hevc_tag.ps1"),
            Path.Combine("obs-replayKit", "scripts", "hevc_compat", "auto_fix_hevc_tag.lua"),
            Path.Combine("obs-replayKit", "scripts", "replay_buffer", "clip_saved_notification.ps1"),
            Path.Combine("obs-replayKit", "scripts", "replay_buffer", "clip_saved_notification.lua"),
            Path.Combine("obs-replayKit", "scripts", "obs_elevation", "hidden_relauncher.vbs"),
            Path.Combine("obs-replayKit", "icons", "nav", "tab-general.svg"),
            // shipped as opus now, and the helper keeps its own wav of it in user-data
            Path.Combine("obs-replayKit", "sounds", "share-card-default.wav"),
            // copies of preset files the overlay preview reads from input-overlay-presets
            Path.Combine("obs-replayKit", "obs-custom-dock", "overlay-preview", "wasd", "wasd.png"),
            Path.Combine("obs-replayKit", "obs-custom-dock", "overlay-preview", "wasd", "wasd-minimal.json"),
            Path.Combine("obs-replayKit", "obs-custom-dock", "overlay-preview", "mouse", "mouse.png"),
            Path.Combine("obs-replayKit", "obs-custom-dock", "overlay-preview", "mouse", "mouse-no-movement.json"),
        };

        // installs made before the streamable/ -> helper/ rename: source name (in the old directory) -> new relative path.
        private static readonly string LegacyStreamableDirRel = Path.Combine("obs-replayKit", "scripts", "streamable");
        private static readonly Dictionary<string, string> LegacyStreamableStateRels = new Dictionary<string, string>
        {
            [StateLayout.ClipsDbFile] = Path.Combine("obs-replayKit", StateLayout.ClipsDbFile),
            [StateLayout.ClipsIndexFile] = Path.Combine("obs-replayKit", StateLayout.ClipsIndexFile),
            [StateLayout.HelperConfigFile] = Path.Combine("obs-replayKit", StateLayout.HelperConfigFile),
        };

        internal static string LegacyStreamableStatePath(string root, string file) =>
            LegacyStreamableStateRels.ContainsKey(file) ? Path.Combine(root, LegacyStreamableDirRel, file) : null;

        // the starter settings ship at their old place inside the bundle, the path the transform of the settings is keyed on; on the disk they belong in the user-data folder with the rest of the state
        private static string InstallTarget(string rel)
        {
            if (RuntimePreserveRels.Contains(rel))
                return StateLayout.PathFor(Config.REPLAYKIT_CONFIG, Path.GetFileName(rel));
            return Path.Combine(Config.OBS_CONFIG, rel);
        }
        // the settings and the saved state moved out of scripts\ and scripts\helper\, then out of the replaykit folder root, into its user-data folder; this moves what an older install still has in the old places, and a file that cannot be moved yet stays where it is and keeps being used
        private static void MigrateStateLayout(Action<string> log)
        {
            try
            {
                var result = StateLayout.Run(Config.REPLAYKIT_CONFIG, log);
                if (result.Moved > 0) log?.Invoke($"state files moved into {StateLayout.Folder}: {result.Moved}");
            }
            catch (Exception ex)
            {
                log?.Invoke("warn: could not move the settings into the replaykit folder: " + ex.Message);
            }
        }

        // keep older state until each destination contains a verified copy.
        private static void MigrateLegacyStreamableState(Action<string> log = null)
        {
            string oldDir = Path.Combine(Config.OBS_CONFIG, LegacyStreamableDirRel);
            if (!Directory.Exists(oldDir)) return;

            bool canRemove = true;
            foreach (var kv in LegacyStreamableStateRels)
            {
                string oldFile = Path.Combine(oldDir, kv.Key);
                string newFile = Path.Combine(Config.OBS_CONFIG, kv.Value);
                if (!File.Exists(oldFile)) continue;
                try
                {
                    string existing = StateLayout.Resolve(Config.REPLAYKIT_CONFIG, kv.Key);
                    if (File.Exists(existing))
                    {
                        if (!FileMatches(existing, new FileInfo(oldFile).Length, FileSha256(oldFile)))
                        {
                            log?.Invoke($"retained distinct legacy state: {oldFile}");
                            canRemove = false;
                        }
                        continue;
                    }
                    ReplaceFile(oldFile, newFile, Guid.NewGuid().ToString("N"));
                    log?.Invoke($"migrated legacy state: {kv.Key} -> {kv.Value.Replace('\\', '/')}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    var reason = SetupLocale.Pair("setup.olderStateCouldNotBeMoved", "file", kv.Key);
                    throw new IOException(reason.Shown, ex).WithEnglish(reason);
                }
            }
            if (!canRemove) return;
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
            [OverlayScene.GameCaptureSourceName] = new Dictionary<string, object>
            {
                ["settings.capture_audio"] = false,
                ["settings.hook_rate"] = 2,
                ["settings.limit_framerate"] = true,
            },
            ["Audio Input Capture"] = new Dictionary<string, object> { ["monitoring_type"] = 0, ["monitoring_enabled"] = false },
            // listed for the volume repair only, with no fields: it is monitored while Share Preview is on, and the virtual camera script sets its monitoring for the saved Share Preview state each time obs starts, so turning it off here only made that script switch it back on after every update, which also cleared a mute
            ["Desktop Audio (excl. Discord)"] = new Dictionary<string, object>(),
            ["Discord Audio (record only)"] = new Dictionary<string, object> { ["monitoring_type"] = 0, ["monitoring_enabled"] = false },
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
            string localeDir = Path.Combine(Config.REPLAYKIT_CONFIG, "locale");
            try
            {
                if (Directory.Exists(localeDir) && (File.GetAttributes(localeDir) & FileAttributes.ReparsePoint) == 0)
                {
                    // the english-keyed tables of earlier releases sat here, one per language; the messages that replaced them live in the messages folder below, which this leaves alone
                    foreach (string file in Directory.EnumerateFiles(localeDir, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        string code = Path.GetFileNameWithoutExtension(file);
                        if (!LanguageMatch.IsCode(code)) continue;
                        File.Delete(file);
                        removed++;
                        log?.Invoke("removed legacy locale: " + code);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not remove legacy locale files: " + ex.Message);
            }
            return removed;
        }

        // restore user-owned clip state after an uninstall/reinstall that kept settings.
        public static int RestoreReplaykitUserState(Action<string> log = null)
        {
            int restored = 0;
            foreach (var file in Cleanup.ReplaykitRuntimeStateFiles)
            {
                string source = Path.Combine(Config.REPLAYKIT_USER_STATE_CACHE, file);
                // a file that is still in the old place counts as present: it is the real one and gets moved
                if (!File.Exists(source) || File.Exists(StateLayout.Resolve(Config.REPLAYKIT_CONFIG, file))) continue;
                string target = StateLayout.PathFor(Config.REPLAYKIT_CONFIG, file);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, true);
                    restored++;
                    log?.Invoke("restored state: " + file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    log?.Invoke($"warn: could not restore state {file}: {ex.Message}");
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

        // patch known ReplayKit-managed sources in the users scene file in place, walking SceneSourcePatches and overwriting only the listed fields; safe when the file is missing or has no matches (returns 0), and skips writing if nothing changed so the mtime doesnt bump for no reason. a save that fails throws, so the update lists the scene settings as not repaired instead of reporting them done.
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
                AudioVolume.RepairSource(source);
                foreach (var kv in spec) SetNested(source, kv.Key, kv.Value);
                if (source.ToString(Formatting.None) != before)
                {
                    patched++;
                    log?.Invoke($"scene patch -> {name}: {(spec.Count > 0 ? string.Join(", ", spec.Keys) : "volume")}");
                }
            }

            if (patched > 0)
                WriteWithRetry(() => File.WriteAllText(scenePath, data.ToString(Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false)));
            return patched;
        }

        // an update leaves user.ini as obs saved it apart from what ApplyUserIni owns there: the controls dock row, which moved from a file:// url to the helper, and the exit confirmation the settings pick; every other dock and window key stays. obs is closed while this runs
        private static bool UpdateControlsDockUrl(Action<string> log)
        {
            string path = Path.Combine(Config.OBS_CONFIG, "user.ini");
            if (!File.Exists(path)) return true;
            try
            {
                string current = Config.ReadTextFileFlexible(path);
                string updated = Transform.ApplyUserIni(current, Prefs.LoadPrefs());
                if (string.Equals(current, updated, StringComparison.Ordinal)) return true;
                WriteWithRetry(() => File.WriteAllText(path, updated, new System.Text.UTF8Encoding(false)));
                log?.Invoke("updated Controls dock URL");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                log?.Invoke("warn: Controls dock URL update skipped: " + ex.Message);
                return false;
            }
        }

        private static bool StageRuntimeFile(string src, string staged, string target)
        {
            byte[] text = null;
            if (Config.TEXT_EXTS.Contains(Path.GetExtension(src)))
            {
                string content = ReadTextFile(src);
                string rewritten = PathRewrite.RewriteUserPaths(content, Config.USERNAME);
                text = System.Text.Encoding.UTF8.GetBytes(rewritten);
            }
            long length = text?.LongLength ?? new FileInfo(src).Length;
            string hash = text == null ? FileSha256(src) : NetCompat.ToHex(NetCompat.Sha256(text));
            if (CanSkipRuntimeFile(target, length, hash)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(staged));
            if (text == null) File.Copy(src, staged, true);
            else File.WriteAllBytes(staged, text);
            if (!FileMatches(staged, length, hash))
            {
                var reason = SetupLocale.Pair("setup.aStagedFileDoesNotMatchTheBundle", "path", staged);
                throw new IOException(reason.Shown).WithEnglish(reason);
            }
            return true;
        }

        private static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static bool FileMatches(string path, long length, string hash) =>
            File.Exists(path) && new FileInfo(path).Length == length && FileSha256(path) == hash;

        private static bool CanSkipRuntimeFile(string target, long length, string hash)
        {
            try
            {
                return (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0 && FileMatches(target, length, hash);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool FilesMatch(string expected, string actual) =>
            FileMatches(actual, new FileInfo(expected).Length, FileSha256(expected));

        internal static void VerifyFile(string expected, string actual)
        {
            if (!FilesMatch(expected, actual))
            {
                var reason = SetupLocale.Pair("setup.aCopiedFileDoesNotMatchItsSource", "path", actual);
                throw new IOException(reason.Shown).WithEnglish(reason);
            }
        }

        internal static void FlushFile(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            file.Flush(true);
        }

        // a copy in an old location still counts as present, so the bundle never seeds a second state file
        private static bool PreservesRuntimeFile(string relative) =>
            RuntimePreserveRels.Contains(relative) && File.Exists(StateLayout.Resolve(Config.REPLAYKIT_CONFIG, Path.GetFileName(relative)));

        internal static void ReplaceFile(string source, string target, string transactionId)
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
                FlushFile(incoming);
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

        // the runtime tree an update installs from. exposed so the updater can preflight it before it closes obs, against the same path InstallReplaykitRuntimeUpdate walks.
        public static string GetRuntimeAssetsDir() => Path.Combine(Config.OBS_ASSETS_DIR, "obs-replayKit");

        public static string ValidateRuntimeAssets()
        {
            string runtimeSrc = GetRuntimeAssetsDir();
            if (!Directory.Exists(runtimeSrc))
                throw new DirectoryNotFoundException(SetupLocale.Message("setup.thisSetupHasNoReplaykitFiles"));
            string helperSource = Path.Combine(runtimeSrc, Config.HELPER_EXE_REL);
            if (!File.Exists(helperSource) || new FileInfo(helperSource).Length < 256 * 1024)
                throw new InvalidDataException(SetupLocale.Message("setup.theBundledReplaykitHelperIsMissing"));
            return runtimeSrc;
        }

        // stage and apply the runtime and config that can be restored together
        internal static int InstallReplaykitRuntimeUpdate(RuntimeTransaction transaction, Action<string> log = null, List<string> problems = null)
        {
            ValidateRuntimeAssets();
            // obs is brought to the one supported version before any file is copied: the plugins and the process host of this release are made for it, so an obs that cannot get there leaves the old replaykit as it was
            ObsDownloader.EnsureObsVersion(log);
            if (!ObsVersions.IsInstalledSupported())
            {
                var reason = SetupLocale.Pair("setup.updateNeedsObsVersion", "version", VersionInfo.Version, "obs", ObsVersions.Install.Tested);
                throw new IOException(reason.Shown).WithEnglish(reason);
            }

            int count = CopyRuntimeFiles(transaction, log);
            RepairAfterRuntimeCopy(log, problems);

            // the defaults this build ships, once per install -- see DefaultsMigration
            try
            {
                if (!DefaultsMigration.Run(log)) problems?.Add(SetupLocale.Message("setup.defaultSettingsPart"));
            }
            catch (Exception ex)
            {
                log?.Invoke($"warn: default settings pass failed: {ex.Message}");
                problems?.Add(SetupLocale.Message("setup.defaultSettingsPart"));
            }

            return count;
        }

        // the Update button of the setup window: the same file refresh the in-app updater does, plus the parts only the setup window sets. the obs config stays as obs saved it (profile, hotkeys, scenes, plugin and websocket config), since rebuilding it from the bundle lost whatever the rebuild did not carry back; a file of it that is gone is written from the bundle the way a first install writes it. the setup flow runs the obs version, plugin, tool and default steps around this itself
        internal static bool UpdateInstalledConfig(RuntimeTransaction transaction, Preferences prefs, bool writeShareChoice, Action<string> log)
        {
            MigrateLegacyStreamableState(log);
            CopyRuntimeFiles(transaction, log);
            SeedMissingObsConfig(prefs, log);
            var problems = new List<string>();
            RepairAfterRuntimeCopy(log, problems);
            bool ok = problems.Count == 0;
            if (writeShareChoice && !WriteShareChoice(prefs, log)) ok = false;
            if (prefs.DiscordScreenshareEnabled && !PointMonitoringAtShareCable(log)) ok = false;
            foreach (string problem in problems) log?.Invoke("warn: not repaired: " + problem);
            return ok;
        }

        // the runtime files of the bundle, staged and verified first and then swapped in as one transaction, so a failure puts the old files back; returns how many changed
        private static int CopyRuntimeFiles(RuntimeTransaction transaction, Action<string> log)
        {
            string runtimeSrc = ValidateRuntimeAssets();
            string transactionId = Guid.NewGuid().ToString("N");
            var files = Directory.EnumerateFiles(runtimeSrc, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) throw new InvalidDataException(SetupLocale.Message("setup.theBundledReplaykitRuntimeIsEmpty"));

            int unchanged = 0;
            var stagedFiles = new List<string>();
            var stagedState = new List<string>();
            foreach (string src in files)
            {
                string fileRel = NetCompat.GetRelativePath(runtimeSrc, src);
                string rel = Path.Combine("obs-replayKit", fileRel);
                // the share window has its own update path because it can outlive obs
                if (ShareWindowInstall.IsShareWindowFile(rel) || string.Equals(fileRel, RuntimeVersionFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (PreservesRuntimeFile(rel))
                {
                    log?.Invoke("preserve: " + rel.Replace('\\', '/'));
                    continue;
                }
                string targetRel = NetCompat.GetRelativePath(Config.REPLAYKIT_CONFIG, InstallTarget(rel));
                string staged = transaction.Stage(targetRel);
                string target = transaction.Target(targetRel);
                if (!StageRuntimeFile(src, staged, target))
                {
                    unchanged++;
                    continue;
                }
                // setup can edit seeded state before commit, so the config snapshot owns its rollback
                if (RuntimePreserveRels.Contains(rel)) stagedState.Add(targetRel);
                else stagedFiles.Add(targetRel);
            }
            log?.Invoke($"staged and verified {stagedFiles.Count} runtime file(s)");

            string stagedVersion = transaction.Stage(RuntimeVersionFile);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedVersion));
            WriteVersion(stagedVersion);
            stagedFiles.Add(RuntimeVersionFile);
            transaction.Prepare(stagedFiles);
            foreach (string relative in stagedFiles.Concat(stagedState))
            {
                ReplaceFile(transaction.Stage(relative), transaction.Target(relative), transactionId);
                log?.Invoke("-> obs-replayKit/" + relative.Replace('\\', '/'));
            }

            log?.Invoke($"{unchanged} runtime file(s) were already current");
            return stagedFiles.Count - 1 + stagedState.Count;
        }

        // repair the config while the runtime journal is still available
        private static void RepairAfterRuntimeCopy(Action<string> log, List<string> problems)
        {
            MigrateStateLayout(log);
            CacheSetupExecutable(log);
            RestoreReplaykitUserState(log);
            if (!UpdateControlsDockUrl(log)) problems?.Add(SetupLocale.Message("setup.updateReplaykitFiles"));

            try
            {
                int patched = ApplyScenePatches(log);
                if (patched > 0) log?.Invoke($"scene patches applied: {patched}");
                if (EnsureReplaykitScript(log)) log?.Invoke("replaykit script entry repaired");
            }
            catch (Exception ex)
            {
                log?.Invoke($"warn: scene patch pass failed: {ex.Message}");
                problems?.Add(SetupLocale.Message("setup.sceneSourceSettings"));
            }
        }

        // the scene collection replaykit installs loads replaykit.lua through its script entry; one that lost the entry, or kept the path of a renamed user folder, loads none of replaykit. the full setup used to rebuild the whole scene file to put it back; this touches only that entry
        private static bool EnsureReplaykitScript(Action<string> log)
        {
            string scenePath = Path.Combine(Config.OBS_CONFIG, UserSceneRel);
            if (!File.Exists(scenePath)) return false;
            JObject data;
            try { data = JObject.Parse(File.ReadAllText(scenePath, System.Text.Encoding.UTF8)); }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                log?.Invoke($"warn: cannot parse scene file for the script entry: {ex.Message}");
                return false;
            }
            if (!Transform.EnsureReplaykitScriptEntry(data, Prefs.LoadPrefs())) return false;
            WriteWithRetry(() => File.WriteAllText(scenePath, data.ToString(Formatting.Indented) + "\n", new System.Text.UTF8Encoding(false)));
            return true;
        }

        // an empty file counts as gone: obs leaves one behind when a write is cut off, and it cannot read it either. a file that cannot be read right now is never counted, so it is never written over
        private static bool IsMissingOrEmpty(string path)
        {
            try { return !File.Exists(path) || string.IsNullOrWhiteSpace(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }

        // the obs config files of the bundle that are gone from the live config, written the way a first install writes them. the files of a profile or scene collection obs does not open are left out, so an update never adds an Untitled profile or collection nobody uses; with no user.ini at all obs has no config to keep and everything that is gone is written
        private static int SeedMissingObsConfig(Preferences prefs, Action<string> log)
        {
            string userIni = Path.Combine(Config.OBS_CONFIG, "user.ini");
            string userText = IsMissingOrEmpty(userIni) ? null : ReadTextFile(userIni);
            string activeProfile = userText == null ? null : Transform.GetIniValue(userText, "Basic", "ProfileDir");
            string activeCollection = userText == null ? null : Transform.GetIniValue(userText, "Basic", "SceneCollectionFile");
            int seeded = 0;
            foreach (var src in Directory.EnumerateFiles(Config.OBS_ASSETS_DIR, "*", SearchOption.AllDirectories))
            {
                string rel = NetCompat.GetRelativePath(Config.OBS_ASSETS_DIR, src);
                var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                // the runtime copy owns the replaykit folder
                if (parts.Length > 1 && string.Equals(parts[0], "obs-replayKit", StringComparison.OrdinalIgnoreCase)) continue;
                bool profileFile = parts.Length >= 4 && string.Equals(parts[0], "basic", StringComparison.OrdinalIgnoreCase) && string.Equals(parts[1], "profiles", StringComparison.OrdinalIgnoreCase);
                bool sceneFile = parts.Length == 3 && string.Equals(parts[0], "basic", StringComparison.OrdinalIgnoreCase) && string.Equals(parts[1], "scenes", StringComparison.OrdinalIgnoreCase);
                if (userText != null && profileFile && !string.Equals(parts[2], activeProfile, StringComparison.OrdinalIgnoreCase)) continue;
                if (userText != null && sceneFile && !string.Equals(Path.GetFileNameWithoutExtension(parts[2]), Path.GetFileNameWithoutExtension(activeCollection ?? ""), StringComparison.OrdinalIgnoreCase)) continue;
                string dst = InstallTarget(rel);
                if (!IsMissingOrEmpty(dst)) continue;
                // an empty one is moved out of the way first: the scene transform reads a live scene file it finds, and the backup step has kept a copy
                if (File.Exists(dst)) WriteWithRetry(() => File.Delete(dst));
                InstallFile(src, rel, dst, prefs);
                log?.Invoke("-> " + rel.Replace('\\', '/') + " (it was gone, written from the bundle)");
                seeded++;
            }
            return seeded;
        }

        // the discord screenshare box is the one setting the setup window changes on an update; it goes into the settings file as a merge of its two keys, and every other setting stays as it is. a settings file that cannot be read is never written over
        private static bool WriteShareChoice(Preferences prefs, Action<string> log)
        {
            string path = Prefs.RUNTIME_SETTINGS_FILE;
            try
            {
                var settings = JObject.Parse(ReadTextFile(path));
                bool changed = false;
                if (settings["discord_screenshare_enabled"]?.Type != JTokenType.Boolean || settings.Value<bool>("discord_screenshare_enabled") != prefs.DiscordScreenshareEnabled)
                {
                    settings["discord_screenshare_enabled"] = prefs.DiscordScreenshareEnabled;
                    changed = true;
                }
                if (settings["discord_projector_enabled"]?.Type != JTokenType.Boolean || settings.Value<bool>("discord_projector_enabled") != prefs.DiscordProjectorEnabled)
                {
                    settings["discord_projector_enabled"] = prefs.DiscordProjectorEnabled;
                    changed = true;
                }
                if (!changed) return true;
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, settings.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
                    FlushFile(temporary);
                    WriteWithRetry(() => NetCompat.MoveFile(temporary, path, true));
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                log?.Invoke($"discord screenshare {(prefs.DiscordScreenshareEnabled ? "on" : "off")}, share preview {(prefs.DiscordProjectorEnabled ? "on" : "off")}");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                log?.Invoke("warn: the discord screenshare choice was not saved: " + ex.Message);
                return false;
            }
        }

        // with screenshare on, obs monitors to the share cable the cable step just set up, the one edit an update makes to the profile obs opens
        private static bool PointMonitoringAtShareCable(Action<string> log)
        {
            try
            {
                string userIni = Path.Combine(Config.OBS_CONFIG, "user.ini");
                if (IsMissingOrEmpty(userIni)) return true;
                string profile = Transform.GetIniValue(ReadTextFile(userIni), "Basic", "ProfileDir");
                if (string.IsNullOrWhiteSpace(profile) || profile == "." || profile == ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
                string basicIni = Path.Combine(Config.OBS_CONFIG, "basic", "profiles", profile, "basic.ini");
                if (IsMissingOrEmpty(basicIni)) return true;
                string current = ReadTextFile(basicIni);
                string updated = Transform.ApplyShareCableMonitoring(current);
                if (string.Equals(current, updated, StringComparison.Ordinal)) return true;
                WriteWithRetry(() => File.WriteAllText(basicIni, updated, new System.Text.UTF8Encoding(false)));
                log?.Invoke("OBS monitoring device set to OBS Stream Audio");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: the OBS monitoring device was not set to the share cable: " + ex.Message);
                return false;
            }
        }

        // verify the video tools and refresh managed plugins on update; report any repairs that did not finish.
        public static List<string> RunUpdateDriverRefresh(Action<string> log = null)
        {
            var problems = new List<string>();
            if (!ObsUpdateLock.Lock(log)) problems.Add(SetupLocale.Message("setup.theObsUpdateLock"));
            // an install that only ever updated never got the process host, and one whose obs was just updated needs the one built for the new version; obs and the helper are closed here
            try { ProcessHost.Install(log); }
            catch (Exception ex)
            {
                log?.Invoke($"warn: OBS process roles not installed: {ex.Message}");
                problems.Add(SetupLocale.Message("setup.theObsProcessRoles"));
            }
            try
            {
                if (!InstallObsFfmpeg(log)) problems.Add(SetupLocale.Message("setup.ffmpegAndFfprobe"));
            }
            catch (Exception ex)
            {
                log?.Invoke($"warn: ffmpeg refresh failed: {ex.Message}");
                problems.Add(SetupLocale.Message("setup.ffmpegAndFfprobe"));
            }

            try
            {
                if (!ShaderFilter.InstallReplaykitMotionBlurPlugin(log)) problems.Add(SetupLocale.Message("setup.theMotionBlurPlugin"));
            }
            catch (Exception ex) { log?.Invoke($"warn: OBS Shaderfilter refresh failed: {ex.Message}"); problems.Add(SetupLocale.Message("setup.theMotionBlurPlugin")); }

            try
            {
                if (!InputOverlay.InstallInputOverlayPlugin(log) || !InputOverlay.InstallInputOverlayPresets(log))
                    problems.Add(SetupLocale.Message("setup.theWasdMouseOverlay"));
            }
            catch (Exception ex) { log?.Invoke($"warn: input overlay refresh failed: {ex.Message}"); problems.Add(SetupLocale.Message("setup.theWasdMouseOverlay")); }

            // the native plugin was missing from this refresh entirely, so an updating install kept whatever replaykit.dll it already had and never picked up plugin-side changes -- and never dropped the pre-unification replaykit-tray.dll, which obs then loaded alongside the new one and injected every tray menu item twice. safe to copy here because the update flow has already closed obs.
            try { NativePlugin.InstallReplaykitPlugin(log); }
            catch (Exception ex) { log?.Invoke($"warn: plugin refresh skipped: {ex.Message}"); }
            if (NativePlugin.IsBundled() && !NativePlugin.IsCurrent()) problems.Add(SetupLocale.Message("setup.theReplaykitPluginPart"));

            try
            {
                if (!BongoCat.InstallBongoCat(log)) problems.Add(SetupLocale.Message("setup.bongoCatFilesAndSavedSources"));
            }
            catch (Exception ex)
            {
                log?.Invoke($"warn: Bongo Cat refresh failed: {ex.Message}");
                problems.Add(SetupLocale.Message("setup.bongoCatFilesAndSavedSources"));
            }

            // the desktop audio plugin was only ever put in by a full setup, so an updating install kept the 2022 build with its known crash and never got a fix to the build replaykit ships. it compares the installed dll with the bundled one and does nothing when they match; obs is closed here
            try { WinCapture.InstallWinCaptureAudio(log); }
            catch (Exception ex) { log?.Invoke($"warn: desktop audio plugin refresh skipped: {ex.Message}"); }
            if (WinCapture.IsExpected() && !WinCapture.IsCurrent()) problems.Add(SetupLocale.Message("setup.theDesktopAudioPlugin"));

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
            return problems;
        }

        // make sure the recording output folder exists. the videos known folder is also created as the simple-output fallback obs reverts to if a profile is reset.
        public static void EnsureRecordingDirs(Preferences prefs, Action<string> log = null)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { prefs.RecordingPath, ClipFolder.Default() };
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

        // configure authenticated OBS WebSocket access for the helper.
        public static bool ConfigureObsWebsocket(Action<string> log = null) => WebSocketConfig.InstallWebsocketConfig(log);

        // clip-ready toasts (from the helper) need some Start Menu shortcut carrying the apps AppUserModelID -- the windows rule for an unpackaged app. instead of a separate "OBS ReplayKit" entry, stamp that id onto OBS Studios own shortcut (this runs elevated so the all-users .lnk is writable); fall back to a same-named user-scope copy only if OBSs is missing. also removes the old distinct "OBS ReplayKit.lnk" from earlier versions.
        public static bool RegisterClipNotificationShortcut(Action<string> log = null)
        {
            string aumid = Shortcuts.ClipNotifyAumid;

            foreach (var root in new[] { Shortcuts.UserPrograms, Shortcuts.AllUsersPrograms })
            {
                string legacy = Path.Combine(root, "OBS ReplayKit.lnk");
                try { if (File.Exists(legacy)) { File.Delete(legacy); log?.Invoke("removed the old \"OBS ReplayKit\" Start Menu shortcut"); } }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            string allUsers = Path.Combine(Shortcuts.AllUsersPrograms, "OBS Studio.lnk");
            string user = Path.Combine(Shortcuts.UserPrograms, "OBS Studio.lnk");

            if (File.Exists(allUsers) && Shortcuts.StampAumid(allUsers, aumid, log))
            {
                log?.Invoke("clip notifications registered on OBS Studio's Start Menu shortcut");
                try { if (File.Exists(user) && Shortcuts.HasAumid(user, aumid)) File.Delete(user); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                return true;
            }

            string obs = Obs.FindObsExe();
            if (obs != null && Shortcuts.Write(user, obs, aumid, log))
            {
                log?.Invoke("clip notifications registered (user-scope \"OBS Studio\" Start Menu shortcut)");
                return true;
            }

            log?.Invoke("note: could not register the clip-notification shortcut now; the helper retries it on first launch");
            return false;
        }

        // let windows monitor/system sleep timers run while obs is active, or restore obs defaults.
        public static bool InstallObsSleepOverride(bool allowSleep = true, Action<string> log = null) =>
            allowSleep ? SleepOverride.InstallSleepOverride(log) : SleepOverride.RemoveSleepOverride(log);

        // the bundled helper was there when the bundle was unpacked (ValidateRuntimeAssets checked it), so one that is gone a few steps later was most likely quarantined by antivirus as a fresh unsigned exe; helper_bootstrap.lua has nothing to fall back on without it
        public static bool EnsureLauncherBuilt(Action<string> log = null)
        {
            string launcherPath = Path.Combine(GetRuntimeAssetsDir(), Config.HELPER_EXE_REL);
            if (File.Exists(launcherPath)) return true;
            log?.Invoke($"warn: helper launcher missing at {launcherPath} -- your antivirus likely quarantined it right after install. check its quarantine or history (Windows Security > Protection History for Defender), restore or exclude the ReplayKit temp folder, then reinstall.");
            return false;
        }
    }
}
