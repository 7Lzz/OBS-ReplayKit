using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;
using Encoder = ReplayKitShared.Encoder;
using static ReplayKitHelper.ReplaykitSettings;
using static ReplayKitHelper.SettingsSceneJson;
using static ReplayKitHelper.SettingsOverlayLive;
using static ReplayKitHelper.SettingsObsConfig;
using static ReplayKitHelper.SettingsRecording;
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHotkeys;

namespace ReplayKitHelper
{
    // the http side of the settings: the payload the pages load, change detection and the save handler
    internal static class SettingsHttp
    {
        // -- http-facing settings payload + change-detection + save-request handler: the docks initial page load fetches GetSettingsPayload; every settings-form submit goes through SaveSettingsFromRequest, which picks the cheapest live-apply path that actually covers what changed instead of always running the full ApplyLiveSettings orchestrator.

        // resolved theme colours for the tray menus custom keybind rows -- text is the normal (at-rest) label colour (a widget stylesheet on the row otherwise breaks the labels inherited menu colour), accentLight + onAccent are the active/hovered fill + ink so the rows match a native QMenu::item:selected
        internal static JObject ThemeMenuColors(JObject settings)
        {
            try
            {
                var t = Themes.Resolve(settings);
                return new JObject
                {
                    ["text"] = t.Text,
                    ["muted"] = t.Muted,
                    // the window ground the clean-layout title bar and rail paint with, and the green of a live status light
                    ["bg"] = t.Bg,
                    // one colour for the title bar and the rail, so a theme can set it apart from the stage
                    ["bar"] = t.BarColor,
                    ["success"] = t.Success,
                    ["warning"] = t.Warning,
                    ["accent"] = t.Accent,
                    ["accentLight"] = t.Accent2,
                    ["onAccent"] = Themes.OnColor(t.Accent),
                    // the dock --grey5 / --grey3 pair, so the title-bar bell can paint the same hover surface and open border the clips sort trigger does under whatever theme is active. same mix Themes uses for --grey5.
                    ["surface"] = Themes.Mix(t.Panel, t.Field, 0.45),
                    ["border"] = t.Border,
                    // dock button parity for the plugin confirm dialog: panel = card ground, field = --button_bg, borderStrong = --button_border_hover. fieldHover is a lightness bump on field (the docks grey4->grey3 step) rather than t.Border, which on some themes equals t.Field and would give no visible hover.
                    ["panel"] = t.Panel,
                    ["field"] = t.Field,
                    ["fieldHover"] = Themes.Shift(t.Field, 0.09),
                    ["borderStrong"] = t.BorderStrong,
                    ["danger"] = t.Danger,
                    // --grey8, the ground the dock tooltips sit on, so the bells tip matches the sort triggers
                    ["tip"] = Themes.Shift(t.Bg, t.Dark ? -0.28 : -0.06),
                };
            }
            catch { return new JObject(); }
        }

        // the theme the Appearance tab is editing right now, as menu colours for the native title bar, rail and menus to show before Apply. every edit sets it again, and it lapses after ten minutes so a tab left behind never holds the window in a colour that was never saved
        internal static JObject themePreviewColors;
        internal static DateTime themePreviewUntil = DateTime.MinValue;
        internal static readonly object ThemePreviewGate = new object();

        public static void SetThemePreview(JObject candidate)
        {
            var colors = ThemeMenuColors(candidate);
            lock (ThemePreviewGate) { themePreviewColors = colors; themePreviewUntil = DateTime.UtcNow.AddMinutes(10); }
            // the plugin reads the colours on a one second poll, this makes the change land at once
            PipeClient.SendPollSettings();
        }

        internal static JObject ActiveMenuColors(JObject settings)
        {
            lock (ThemePreviewGate)
            {
                if (themePreviewColors != null && DateTime.UtcNow < themePreviewUntil) return (JObject)themePreviewColors.DeepClone();
            }
            return ThemeMenuColors(settings);
        }

        // what the plugin reads on its one second poll: the saved settings and the menu colours, without the reads from obs that GetSettingsPayload makes (hotkeys, clip length, mic, volumes, overlay opacity; about ten websocket requests each time). the plugin asks for the full payload once the obs settings dialog closes or the profile changes, which is when those can differ from the file
        public static JObject GetNativeSettingsPayload()
        {
            lock (SettingsStore.Gate)
            {
                var settings = ReadSettings();
                return new JObject { ["ok"] = true, ["settings"] = settings, ["menuColors"] = ActiveMenuColors(settings), ["clipBufferMode"] = ClipBuffer.EffectiveMode(settings), ["updateReady"] = Update.ReadyVersion, ["obsUpdateReady"] = ObsUpdate.ReadyVersion };
            }
        }

        public static JObject GetSettingsPayload()
        {
            lock (SettingsStore.Gate)
            {
                AppConfig.LoadConfig();
                var settings = ReadSettings();
                settings = SyncHotkeysFromObs(settings);
                settings = SyncReplayBufferSecondsFromObs(settings);
                settings = SyncMicFromObs(settings);
                settings = SyncAudioVolumesFromObs(settings);
                settings = SyncCaptureDisplayFromObs(settings);
                if (string.IsNullOrWhiteSpace(settings["clipDir"]?.Value<string>()) && !string.IsNullOrWhiteSpace(Server.State.Config?["clipDir"]?.Value<string>()))
                {
                    settings["clipDir"] = ResolveClipDirSetting(Server.State.Config["clipDir"]?.Value<string>());
                }
                var overlayOpacity = GetLiveOverlayOpacityPercent(settings);
                if (overlayOpacity["ok"]?.Value<bool>() != true) overlayOpacity = GetSceneFileOverlayOpacityPercent(settings);
                if (overlayOpacity["ok"]?.Value<bool>() == true && settings["overlayOpacity"]?.Value<int>() != overlayOpacity["opacity"]?.Value<int>())
                {
                    settings["overlayOpacity"] = overlayOpacity["opacity"];
                    WriteSettings(settings);
                }
                return new JObject
                {
                    ["ok"] = true,
                    ["settings"] = settings,
                    ["menuColors"] = ActiveMenuColors(settings),
                    ["clipBufferMode"] = ClipBuffer.EffectiveMode(settings),
                    ["systemMemoryGb"] = ClipBuffer.SystemMemoryGb,
                    // drives Install vs Uninstall on the Share Preview audio row in the Uninstall box
                    ["shareAudioInstalled"] = IsShareAudioDriverInstalled(),
                    ["options"] = new JObject
                    {
                        ["recordingPresets"] = new JArray(
                            new JObject { ["value"] = "performance", ["label"] = "Performance", ["blurb"] = "720p30. Lowest load and smaller files." },
                            new JObject { ["value"] = "balanced", ["label"] = "Balanced", ["blurb"] = "1080p60. Recommended for most PCs." },
                            new JObject { ["value"] = "quality", ["label"] = "Quality", ["blurb"] = "Higher-quality target for high-end PCs." }
                        ),
                        ["compressionModes"] = new JArray(
                            new JObject { ["value"] = "lower_gpu", ["label"] = "Lowest GPU use", ["blurb"] = "Least encoder work. Larger clips." },
                            new JObject { ["value"] = "balanced", ["label"] = "Balanced", ["blurb"] = "Good file size with modest encoder load." },
                            new JObject { ["value"] = "smaller_files", ["label"] = "Smallest clips", ["blurb"] = "More encoder work for tighter files." },
                            new JObject { ["value"] = "tiniest", ["label"] = "Microscopic GPU Cooker", ["blurb"] = "Preset P7 with two full resolution passes. The tiniest clips, and the most work your GPU can do for them." }
                        ),
                        ["codecs"] = new JArray(
                            new JObject { ["value"] = "h264", ["label"] = "H.264", ["blurb"] = "Recommended. Plays everywhere: phones, browsers, Discord and editors." },
                            new JObject { ["value"] = "h265", ["label"] = "HEVC / H.265", ["blurb"] = "Smaller files, but HEVC / H.265 clips may not play for some people, on some phones, browsers and apps." }
                        ),
                        ["recordingScaleModes"] = new JArray(
                            new JObject { ["value"] = "native", ["label"] = "Native", ["blurb"] = "Record at your monitor's full resolution. Sharpest image, but more GPU load and bigger files." },
                            new JObject { ["value"] = "downscale", ["label"] = "Downscale", ["blurb"] = "Shrink the recording to the size below. Lighter on the GPU and smaller files, slightly softer." }
                        ),
                        ["downscaleResolutions"] = BuildDownscaleResolutionList(),
                        ["recordingBuffers"] = ClipBuffer.Options(),
                        ["downscaleFilters"] = new JArray(
                            new JObject { ["value"] = "lanczos", ["label"] = "Lanczos", ["blurb"] = "Sharpest, 36-sample. Default." },
                            new JObject { ["value"] = "bicubic", ["label"] = "Bicubic", ["blurb"] = "16-sample, slightly softer than Lanczos." },
                            new JObject { ["value"] = "area", ["label"] = "Area", ["blurb"] = "Clean average, no sharpening pass." },
                            new JObject { ["value"] = "bilinear", ["label"] = "Bilinear", ["blurb"] = "Fastest, softest." }
                        ),
                        ["overlays"] = new JArray(
                            new JObject { ["value"] = "input_overlay", ["label"] = "WASD / mouse", ["blurb"] = "Simple keyboard and mouse overlay." },
                            new JObject { ["value"] = "bongo_cat", ["label"] = "Bongo Cat", ["blurb"] = "Animated keyboard and mouse overlay." },
                            new JObject { ["value"] = "off", ["label"] = "Off", ["blurb"] = "No input overlay in the OBS scene." }
                        ),
                        ["inputOverlay"] = InputOverlayLayouts.Options(),
                        ["bongoModes"] = new JArray(
                            new JObject { ["value"] = "standard", ["label"] = "Standard", ["blurb"] = "Mouse paw, plus 1 to 7, Q, E, R, A, S, D, W and Space." },
                            new JObject { ["value"] = "keyboard", ["label"] = "Keyboard", ["blurb"] = "Two paws on the arrow keys, R, Left Ctrl and Left Shift." },
                            new JObject { ["value"] = "feixue", ["label"] = "Feixue", ["blurb"] = "Game panel with W, A, S, D and Space, and a mouse paw." },
                            new JObject { ["value"] = "mania", ["label"] = "Mania", ["blurb"] = "Rhythm game keys Z, X, comma and period." }
                        ),
                        ["screenshareCaptureModes"] = new JArray(
                            new JObject { ["value"] = "hybrid_auto", ["label"] = "Auto", ["blurb"] = "Desktop fallback with fullscreen Game Capture on top." },
                            new JObject { ["value"] = "desktop", ["label"] = "Desktop", ["blurb"] = "Show the full ReplayKit desktop capture." },
                            new JObject { ["value"] = "game_auto", ["label"] = "Game only", ["blurb"] = "Use OBS Game Capture for any fullscreen game." },
                            new JObject { ["value"] = "game_window", ["label"] = "Specific game", ["blurb"] = "Use Window Capture for the selected game window." }
                        ),
                        ["screenshareGameWindows"] = new JArray(GetGameWindowCandidates(settings["screenshareGameWindow"]?.Value<string>() ?? "")),
                        ["captureDisplays"] = CaptureDisplayOptions(settings["captureDisplay"]?.Value<string>() ?? "primary"),
                        ["discordOutputModes"] = new JArray(
                            new JObject { ["value"] = "projector", ["label"] = "Projector", ["blurb"] = "OBS Windowed Projector parked by ReplayKit." }
                        ),
                        ["keybinds"] = new JArray(
                            new JObject { ["value"] = "f8", ["label"] = "F8", ["blurb"] = "Default ReplayKit save hotkey.", ["combo"] = new JObject { ["key"] = "OBS_KEY_F8" } },
                            new JObject { ["value"] = "shift_backslash", ["label"] = "Shift + \\", ["blurb"] = "The classic ReplayKit save hotkey.", ["combo"] = new JObject { ["shift"] = true, ["key"] = "OBS_KEY_BACKSLASH" } },
                            new JObject { ["value"] = "ctrl_shift_s", ["label"] = "Ctrl + Shift + S", ["blurb"] = "Easy to remember, uses two modifiers.", ["combo"] = new JObject { ["control"] = true, ["shift"] = true, ["key"] = "OBS_KEY_S" } },
                            new JObject { ["value"] = "f9", ["label"] = "F9", ["blurb"] = "Single function key.", ["combo"] = new JObject { ["key"] = "OBS_KEY_F9" } },
                            new JObject { ["value"] = "f10", ["label"] = "F10", ["blurb"] = "Single function key.", ["combo"] = new JObject { ["key"] = "OBS_KEY_F10" } }
                        ),
                    },
                    // resolved default so the clip-dir field can show a real path (with the actual username) as its placeholder.
                    ["defaultClipDir"] = AppConfig.GetDefaultClipDir(),
                };
            }
        }

        internal static bool TestRestartRequired(JObject previous, JObject settings)
        {
            // recordingScaleMode + downscaleHeight apply live (stop outputs -> SetVideoSettings -> restart outputs, see ApplyRuntimeOutputsLive). downscaleFilter still needs a restart -- obs only re-reads Video/ScaleType from the profile ini on load, SetVideoSettings has no scale-type parameter.
            foreach (var key in new[] { "recordingPreset", "compressionMode", "codecPreference", "fpsNumerator", "fpsDenominator", "downscaleFilter", "recordingSeparateAudioTracks" })
            {
                if (previous[key]?.ToString() != settings[key]?.ToString()) return true;
            }
            // theme: obs only reads user.ini [Appearance] Theme= at startup, so a theme change needs a restart to reach obs (the dock + replaykit windows re-theme live on the fresh load). the Appearance tab shows a live preview meanwhile, the restart is what applies it
            if (previous["theme"]?.ToString() != settings["theme"]?.ToString()) return true;
            // the clean layout is a frameless window with its own title bar, which the native plugin can only set up before obs shows its window
            if (previous["interfaceLayout"]?.ToString() != settings["interfaceLayout"]?.ToString()) return true;
            string theme = settings["theme"]?.ToString() ?? "default";
            // the overlay style used to restart obs for the opengl bongo plugin, which could not be created and removed while obs ran; every overlay now switches live through ApplyOverlayLive
            return (theme == "custom" || theme.StartsWith("user/", StringComparison.Ordinal)) && !JToken.DeepEquals(previous["themeCustom"], settings["themeCustom"]);
        }

        internal static bool TestRuntimeVideoSettingsChanged(JObject previous, JObject settings)
        {
            return previous["recordingPreset"]?.ToString() != settings["recordingPreset"]?.ToString() ||
                previous["fpsNumerator"]?.ToString() != settings["fpsNumerator"]?.ToString() ||
                previous["fpsDenominator"]?.ToString() != settings["fpsDenominator"]?.ToString() ||
                previous["recordingScaleMode"]?.ToString() != settings["recordingScaleMode"]?.ToString() ||
                previous["downscaleHeight"]?.ToString() != settings["downscaleHeight"]?.ToString() ||
                previous["downscaleFilter"]?.ToString() != settings["downscaleFilter"]?.ToString();
        }

        internal static bool TestReplayBufferOutputChanged(JObject previous, JObject settings)
        {
            // the memory cap follows the recorded size, so a resolution change re-applies the buffer output too
            foreach (var key in new[] { "recordingPreset", "replaySeconds", "clipDir", "recordingScaleMode", "downscaleHeight" })
            {
                if (previous[key]?.ToString() != settings[key]?.ToString()) return true;
            }
            return false;
        }

        internal static bool TestShareModeOnlyRequest(JObject incoming) => incoming.Count == 1 && incoming["shareMode"] != null;

        internal static readonly string[] DiscordOutputOnlyAllowedKeys =
        {
            "shareMode", "discord_screenshare_enabled", "discord_output_mode", "discord_projector_enabled",
            "discord_projector_width", "discord_projector_height", "discord_projector_visible_pixels",
            "discord_projector_monitor_index", "discord_projector_edge", "discord_projector_title_hint", "discord_projector_hide_taskbar",
        };

        internal static bool TestDiscordOutputOnlyRequest(JObject incoming)
        {
            if (incoming.Count < 1) return false;
            foreach (var prop in incoming.Properties())
            {
                if (!DiscordOutputOnlyAllowedKeys.Contains(prop.Name)) return false;
            }
            return true;
        }

        internal static readonly string[] ScreenshareCaptureOnlyAllowedKeys =
            { "screenshareCaptureMode", "screenshareGameWindow", "screenshareGameOverrides", "screenshareAutoGameKeepFocused", "screenshareSwitchDelaySeconds" };

        internal static bool TestScreenshareCaptureOnlyRequest(JObject incoming)
        {
            if (incoming.Count < 1) return false;
            foreach (var prop in incoming.Properties())
            {
                if (!ScreenshareCaptureOnlyAllowedKeys.Contains(prop.Name)) return false;
            }
            return true;
        }

        internal static bool TestIncomingSettingsChanged(JObject incoming, JObject previous, JObject settings)
        {
            foreach (var prop in incoming.Properties())
            {
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return true;
            }
            return false;
        }

        internal static bool TestOnlyMotionBlurChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "motionBlurEnabled" || prop.Name == "motionBlurStrength") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["motionBlurEnabled"]?.Value<bool>() != settings["motionBlurEnabled"]?.Value<bool>() ||
                previous["motionBlurStrength"]?.Value<double>() != settings["motionBlurStrength"]?.Value<double>();
        }

        internal static bool TestOnlyAppIconChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "appIcon" || prop.Name == "appIconCustomPath" || prop.Name == "appIconRecordingDot") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["appIcon"]?.Value<string>() != settings["appIcon"]?.Value<string>() ||
                previous["appIconCustomPath"]?.Value<string>() != settings["appIconCustomPath"]?.Value<string>() ||
                previous["appIconRecordingDot"]?.Value<bool>() != settings["appIconRecordingDot"]?.Value<bool>();
        }

        // pushes the resolved app icon to replaykits own windows (helper) and to obss window + taskbar + system tray (native plugin over the ipc pipe). the obs main window is the plugins job alone now -- the helpers own WM_SETICON on it raced the plugin at 16/32px and kept the win11 taskbar button small. all live, no restart.
        public static JObject ApplyAppIconLive(JObject settings)
        {
            string path = ResolveAppIconPath(settings);
            bool dot = settings["appIconRecordingDot"]?.Value<bool>() ?? true;
            var applied = new List<string>();
            try { Native.SetReplayKitWindowIcons(path); applied.Add("ReplayKit window icons"); }
            catch (Exception ex) { Log.Write("ApplyAppIconLive: ReplayKit windows: " + ex.Message); }
            try { PipeClient.SendSetIcon(path); PipeClient.SendSetIconDot(dot); applied.Add("OBS window + tray icon"); }
            catch (Exception ex) { Log.Write("ApplyAppIconLive: OBS icon: " + ex.Message); }
            return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray() };
        }

        // re-assert the chosen icon after the helper (re)starts with obs. obss main window isnt necessarily up yet, so poll briefly; the native plugin also gets it via the pipe-connect send.
        public static void ApplyAppIconAtStartup()
        {
            JObject settings;
            try { settings = Normalize(ReadSettings()); }
            catch (Exception ex) { Log.Write("ApplyAppIconAtStartup: read: " + ex.Message); return; }
            if ((settings["appIcon"]?.Value<string>() ?? "default") == "default") return;
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 30; i++)
                    {
                        long hwndVal;
                        lock (Server.State.IpcLock) hwndVal = Server.State.ObsMainWindowHwnd;
                        if (hwndVal != 0) { ApplyAppIconLive(settings); return; }
                        Thread.Sleep(1000);
                    }
                    ApplyAppIconLive(settings); // last try even without the hwnd -- covers the replaykit windows + tray
                }
                catch (Exception ex) { Log.Write("ApplyAppIconAtStartup: " + ex.Message); }
            });
        }

        // self-heal: obss recording folder (SimpleOutput.FilePath / AdvOut.RecFilePath) can drift off the RESOLVED clip dir (custom, or the ~/Videos default) -- a reset profile, an obs update, a mid-session force-kill -- and then new replay clips land where nothing is looking. runs deferred (obs websocket needs a moment) and only writes on an actual mismatch.
        public static void EnsureObsRecordingFolderAtStartup()
        {
            Task.Run(() =>
            {
                try
                {
                    string clipDir;
                    try { clipDir = Normalize(ReadSettings())["clipDir"]?.Value<string>(); }
                    catch { clipDir = null; }
                    string resolved = string.IsNullOrWhiteSpace(clipDir) ? AppConfig.GetDefaultClipDir() : clipDir;
                    try { resolved = Path.GetFullPath(resolved); } catch { }
                    // heals installs whose helper_config predates the folder being settable in Settings, so the clip listing stops walking the default folder
                    SyncClipDirToRuntimeConfig(new JObject { ["clipDir"] = resolved });
                    try { Directory.CreateDirectory(resolved); } catch (Exception ex) { Log.Write("EnsureObsRecordingFolder mkdir: " + ex.Message); }

                    for (int i = 0; i < 30; i++)
                    {
                        var cur = GetObsProfileParameterValue("AdvOut", "RecFilePath");
                        if (cur["ok"]?.Value<bool>() == true)
                        {
                            string curPath = cur["value"]?.Value<string>() ?? "";
                            string curNorm; try { curNorm = Path.GetFullPath(curPath); } catch { curNorm = curPath; }
                            if (!string.Equals(curNorm.TrimEnd('\\', '/'), resolved.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                            {
                                SetObsProfileParameterSafe("SimpleOutput", "FilePath", resolved);
                                SetObsProfileParameterSafe("AdvOut", "RecFilePath", resolved);
                                SetObsProfileParameterSafe("AdvOut", "FFFilePath", resolved);
                                // the already-running Replay Buffer output caches its dir from init -- update it live too.
                                try
                                {
                                    var rbEx = ObsWebSocket.InvokeRequest("GetOutputSettings", new JObject { ["outputName"] = "Replay Buffer" }, 3000);
                                    JObject rbSet = rbEx.Ok && rbEx.Data?["outputSettings"] is JObject e ? (JObject)e.DeepClone() : new JObject();
                                    rbSet["directory"] = resolved;
                                    rbSet["path"] = resolved;
                                    ObsWebSocket.InvokeRequest("SetOutputSettings", new JObject { ["outputName"] = "Replay Buffer", ["outputSettings"] = rbSet }, 5000);
                                }
                                catch (Exception ex) { Log.Write("EnsureObsRecordingFolder live output: " + ex.Message); }
                                Log.Write("EnsureObsRecordingFolder: OBS recording folder was '" + curPath + "', pinned to '" + resolved + "'");
                            }
                            return;
                        }
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex) { Log.Write("EnsureObsRecordingFolderAtStartup: " + ex.Message); }
            });
        }

        // self-heal: obs can end up recording at a different size or fps than the saved settings say -- the installer re-seeding the profile, an apply that failed after the settings file was already written, a change made inside obs -- and nothing compared the two, so it stayed that way unnoticed. once per helper start this checks the live video settings against the saved recording resolution and fps and applies the saved ones when they differ; only the output size and fps move (the base canvas is left alone) and a recording or stream in progress is never stopped for it. the choices of the recording audio tab live in the settings, so a scene that came back with the plain list (an update, a fresh template) is put right once obs answers. with no choice made nothing is touched, the source keeps running exactly as it always did
        public static void EnsureRecordingAudioProgramsAtStartup()
        {
            Task.Run(() =>
            {
                try { ReconcileRecordingAudioPrograms(); }
                catch (Exception ex) { Log.Write("EnsureRecordingAudioPrograms: " + ex.Message); }
            });
        }

        internal static bool SameProgramList(JToken live, JToken wanted)
        {
            var a = new HashSet<string>((live as JArray ?? new JArray()).Select(e => e["value"]?.Value<string>() ?? ""), StringComparer.OrdinalIgnoreCase);
            var b = new HashSet<string>((wanted as JArray ?? new JArray()).Select(e => e["value"]?.Value<string>() ?? ""), StringComparer.OrdinalIgnoreCase);
            return a.SetEquals(b);
        }

        internal static void ReconcileRecordingAudioPrograms()
        {
            var early = ReadSettings();
            ObsWebSocketResult current = null;
            for (int i = 0; i < 45; i++)
            {
                current = GetDesktopAudioCaptureSettings();
                if (current.Ok) break;
                Thread.Sleep(1000);
            }
            if (current?.Ok != true)
            {
                if (UsesOutputDeviceRecording(early)) Log.Write("EnsureRecordingAudioPrograms: OBS could not apply output device capture: " + current?.Message);
                return;
            }

            // read again under the settings lock so a save that landed while waiting is respected
            lock (SettingsStore.Gate)
            {
                var settings = ReadSettings();
                current = GetDesktopAudioCaptureSettings();
                if (!current.Ok)
                {
                    Log.Write("EnsureRecordingAudioPrograms: could not read Desktop Audio settings: " + current.Message);
                    return;
                }
                bool chosen = UsesOutputDeviceRecording(settings) || settings["recordingAudioMode"]?.Value<string>() == "specific"
                    || ((settings["recordingAudioApps"] as JObject)?.HasValues ?? false);
                if (!chosen && !CapturesOutputDevice(current.Data?["inputSettings"]))
                {
                    var discord = RestoreDiscordAudioTracks(settings);
                    if (!discord.Ok) Log.Write("EnsureRecordingAudioPrograms: could not restore Discord audio tracks: " + discord.Message);
                    return;
                }
                var result = ApplyRecordingAudioLive(settings, current);
                foreach (var warning in result["warnings"].Values<string>()) Log.Write("EnsureRecordingAudioPrograms: " + warning);
            }
        }

        public static void EnsureObsVideoMatchesSettingsAtStartup()
        {
            Task.Run(() =>
            {
                try { ReconcileObsVideoWithSettings(); }
                catch (Exception ex) { Log.Write("EnsureObsVideoMatchesSettings: " + ex.Message); }
            });
        }

        internal static void ReconcileObsVideoWithSettings()
        {
            // obs answers its websocket a beat after it starts and the script load starts the replay buffer, so wait for both -- the stop, reset and start below then never land while obs is still starting up
            bool answered = false;
            for (int i = 0; i < 45 && !answered; i++)
            {
                answered = ObsWebSocket.InvokeRequest("GetVideoSettings", null, 3000).Ok;
                if (!answered) Thread.Sleep(1000);
            }
            if (!answered) { Log.Write("EnsureObsVideoMatchesSettings: OBS did not answer in time, nothing checked."); return; }
            for (int i = 0; i < 15; i++)
            {
                var buffer = ObsWebSocket.InvokeRequest("GetReplayBufferStatus", null, 3000);
                if (buffer.Ok && (buffer.Data?["outputActive"]?.Value<bool>() ?? false)) break;
                Thread.Sleep(1000);
            }
            Thread.Sleep(2000);

            // read again under the settings lock so a save that landed while waiting is respected, and so a user apply cannot interleave with this one
            lock (SettingsStore.Gate)
            {
                var settings = Normalize(ReadSettings());
                string presetName = settings["recordingPreset"]?.Value<string>() ?? "";
                var wantedFps = GetPresetSpec(presetName, settings)["video"];
                int fpsNumerator = wantedFps["fpsNumerator"].Value<int>(), fpsDenominator = wantedFps["fpsDenominator"].Value<int>();

                var now = ObsWebSocket.InvokeRequest("GetVideoSettings", null, 3000);
                if (!now.Ok || !(now.Data is JObject live)) { Log.Write("EnsureObsVideoMatchesSettings: could not read the OBS video settings: " + now.Message); return; }
                int baseWidth = live["baseWidth"]?.Value<int>() ?? 0, baseHeight = live["baseHeight"]?.Value<int>() ?? 0;
                if (baseWidth < 2 || baseHeight < 2) { Log.Write("EnsureObsVideoMatchesSettings: OBS reported no base canvas size, nothing changed."); return; }

                // the output is worked out from the live base canvas rather than the detected monitor size, since only the output size is being reconciled here
                var output = GetScaledEvenSize(baseWidth, baseHeight, 8192, OutputHeightCap(presetName, settings["recordingScaleMode"]?.Value<string>(), settings["downscaleHeight"]?.Value<int>() ?? 1080));
                int outputWidth = output["width"].Value<int>(), outputHeight = output["height"].Value<int>();
                bool sizeDiffers = live["outputWidth"]?.Value<int>() != outputWidth || live["outputHeight"]?.Value<int>() != outputHeight;
                bool fpsDiffers = live["fpsNumerator"]?.Value<int>() != fpsNumerator || live["fpsDenominator"]?.Value<int>() != fpsDenominator;
                bool videoDiffers = sizeDiffers || fpsDiffers;

                // the replay buffer memory cap follows the recorded size; the stored profile value is what gets compared, since that is what this code writes, so the check settles once it has been applied
                var wanted = new JObject
                {
                    ["video"] = new JObject
                    {
                        ["baseWidth"] = baseWidth, ["baseHeight"] = baseHeight, ["outputWidth"] = outputWidth, ["outputHeight"] = outputHeight,
                        ["fpsNumerator"] = fpsNumerator, ["fpsDenominator"] = fpsDenominator,
                    },
                };
                int wantedBufferMb = (int)ScaledRbSizeMb(presetName, settings["replaySeconds"]?.Value<int>() ?? 0, wanted);
                var storedBuffer = GetObsProfileParameterValue("AdvOut", "RecRBSize");
                bool bufferDiffers = storedBuffer["ok"]?.Value<bool>() == true &&
                    !(double.TryParse(storedBuffer["value"]?.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out double storedBufferMb) && (int)storedBufferMb == wantedBufferMb);
                if (!videoDiffers && !bufferDiffers) return;

                // fail closed: if obs cannot say whether it is recording or streaming, leave it alone
                var record = ObsWebSocket.InvokeRequest("GetRecordStatus", null, 3000);
                var stream = ObsWebSocket.InvokeRequest("GetStreamStatus", null, 3000);
                if (!record.Ok || !stream.Ok) { Log.Write("EnsureObsVideoMatchesSettings: could not tell whether OBS is recording or streaming, left alone."); return; }
                if ((record.Data?["outputActive"]?.Value<bool>() ?? false) || (stream.Data?["outputActive"]?.Value<bool>() ?? false))
                {
                    Log.Write("EnsureObsVideoMatchesSettings: OBS is recording or streaming, left alone until the next helper start.");
                    return;
                }

                Log.Write("EnsureObsVideoMatchesSettings: OBS is at " + live["outputWidth"] + "x" + live["outputHeight"] + " " + live["fpsNumerator"] + "/" + live["fpsDenominator"] + " with a replay buffer cap of " + storedBuffer["value"] +
                    " MB, saved settings want " + outputWidth + "x" + outputHeight + " " + fpsNumerator + "/" + fpsDenominator + " and " + wantedBufferMb + " MB; applying.");
                // the values go into the profile first so the next launch starts right even if the live changes below do not take
                var profileWrites = new List<string[]>();
                if (videoDiffers)
                {
                    profileWrites.Add(new[] { "Video", "OutputCX", outputWidth.ToString() });
                    profileWrites.Add(new[] { "Video", "OutputCY", outputHeight.ToString() });
                }
                if (bufferDiffers) profileWrites.Add(new[] { "AdvOut", "RecRBSize", wantedBufferMb.ToString() });
                foreach (var write in profileWrites)
                {
                    var written = SetObsProfileParameterSafe(write[0], write[1], write[2]);
                    if (!written.Ok) Log.Write("EnsureObsVideoMatchesSettings: OBS did not accept " + write[0] + "." + write[1] + ": " + written.Message);
                }
                var applied = ApplyRuntimeOutputsLive(settings, wanted, restartObs: false, applyVideoSettings: videoDiffers, applyReplayBufferOutput: true);
                foreach (var warning in applied["warnings"] as JArray ?? new JArray()) Log.Write("EnsureObsVideoMatchesSettings: " + warning);

                var after = ObsWebSocket.InvokeRequest("GetVideoSettings", null, 3000);
                bool videoMatches = after.Ok && after.Data is JObject result && result["outputWidth"]?.Value<int>() == outputWidth && result["outputHeight"]?.Value<int>() == outputHeight &&
                    result["fpsNumerator"]?.Value<int>() == fpsNumerator && result["fpsDenominator"]?.Value<int>() == fpsDenominator;
                var bufferAfter = ObsWebSocket.InvokeRequest("GetOutputSettings", new JObject { ["outputName"] = "Replay Buffer" }, 3000);
                Log.Write("EnsureObsVideoMatchesSettings: " + (videoMatches ? "OBS now records at " + outputWidth + "x" + outputHeight + " " + fpsNumerator + "/" + fpsDenominator + "." : "OBS did not take the saved video settings.") +
                    " The replay buffer cap is " + (bufferAfter.Ok ? bufferAfter.Data?["outputSettings"]?["max_size_mb"]?.ToString() : "unknown") + " MB.");
            }
        }

        internal const string BongoCatNoticeKey = "bongo-cat-not-loaded";

        // bongo is a plugin source, so when the plugin file is gone or this obs never registered its input kind nothing runs the scene item and nothing else says so. while the style is on the bell says it once per start, and the row clears itself once the plugin loads or the style changes
        public static void CheckBongoCatAtStartup()
        {
            Task.Run(() =>
            {
                try { ReportBongoCatState(); }
                catch (Exception ex) { Log.Write("CheckBongoCat: " + ex.Message); }
            });
        }

        internal static void ReportBongoCatState()
        {
            var settings = ReadSettings();
            if (settings["overlayStyle"]?.Value<string>() != "bongo_cat") { Notifications.DismissKey(BongoCatNoticeKey); return; }
            // obs answers its websocket a beat after it starts
            ObsWebSocketResult kinds = null;
            for (int i = 0; i < 45; i++)
            {
                kinds = ObsWebSocket.InvokeRequest("GetInputKindList", new JObject { ["unversioned"] = true }, 3000);
                if (kinds.Ok) break;
                Thread.Sleep(1000);
            }
            if (kinds == null || !kinds.Ok) { Log.Write("CheckBongoCat: OBS did not answer in time, nothing checked."); return; }

            bool loaded = (kinds.Data?["inputKinds"] as JArray ?? new JArray()).Any(k => k.Value<string>() == BongoInputKind);
            var input = ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = "Bongo Cat Overlay" }, 3000);
            bool sourceCurrent = input.Ok && input.Data?["inputKind"]?.Value<string>() == BongoInputKind;
            // replaykit.dll draws the Bango Cat folder beside obs64.exe with the models ReplayKit baked from it, and a missing folder or bake leaves the cat silently empty
            string root = GetObsInstallRoot();
            string bakes = Path.Combine(AppConfig.GetDockDir(), "overlay-preview", "bongo-models");
            bool filesPresent = File.Exists(Path.Combine(root, "bin\\64bit\\Bango Cat\\mode\\config.json")) &&
                File.Exists(Path.Combine(root, "bin\\64bit\\Bango Cat\\mode\\standard\\config.json")) &&
                Directory.Exists(bakes) && Directory.EnumerateFiles(bakes, "*.json").Any();
            if (loaded && filesPresent && sourceCurrent)
            {
                lock (SettingsStore.Gate)
                {
                    settings = ReadSettings();
                    if (settings["overlayStyle"]?.Value<string>() == "bongo_cat")
                    {
                        var bindings = BongoSourceBindings(settings);
                        if (!JToken.DeepEquals(input.Data?["inputSettings"]?["key_bindings"], bindings))
                        {
                            var update = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject
                            {
                                ["inputName"] = OverlayScene.BongoSourceName,
                                ["inputSettings"] = new JObject { ["key_bindings"] = bindings },
                                ["overlay"] = true,
                            }, 3000);
                            if (!update.Ok) Log.Write("CheckBongoCat: could not sync key bindings: " + update.Message);
                        }
                    }
                }
                Notifications.DismissKey(BongoCatNoticeKey);
                return;
            }
            Log.Write("CheckBongoCat: bongo cat input kind registered: " + loaded + ", source uses ReplayKit renderer: " + sourceCurrent + ", Bango Cat folder and baked models present: " + filesPresent + ".");
            Notifications.Add("warning", BongoCatNoticeKey, "Bongo Cat is not working", !filesPresent
                ? "The Bongo Cat files are missing (the Bango Cat folder in OBS or the ReplayKit cat models), so the overlay cannot show. Antivirus can remove them. Run the ReplayKit installer again to put them back."
                : !loaded ? "OBS started without the ReplayKit plugin that draws Bongo Cat, so the overlay cannot show. Restart OBS. If it keeps happening, run the ReplayKit installer again."
                : "The Bongo Cat source is missing or still uses the older renderer. Run the ReplayKit installer again to repair the saved scene.");
        }

        internal static bool TestOnlyOverlayVisualChanged(JObject previous, JObject settings)
        {
            var skip = new HashSet<string> { "overlayOpacity", "overlayScale", "overlayRotation", "overlayFlipH", "overlayHueShift", "overlayColorMultiply", "overlayColorAdd" };
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (skip.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["overlayOpacity"]?.Value<int>() != settings["overlayOpacity"]?.Value<int>() ||
                previous["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>() ||
                    previous["overlayRotation"]?.Value<int>() != settings["overlayRotation"]?.Value<int>() ||
                previous["overlayFlipH"]?.Value<bool>() != settings["overlayFlipH"]?.Value<bool>() ||
                previous["overlayHueShift"]?.Value<double>() != settings["overlayHueShift"]?.Value<double>() ||
                previous["overlayColorMultiply"]?.Value<string>() != settings["overlayColorMultiply"]?.Value<string>() ||
                previous["overlayColorAdd"]?.Value<string>() != settings["overlayColorAdd"]?.Value<string>();
        }

        internal static bool TestOnlyScreenshareCaptureChanged(JObject previous, JObject settings)
        {
            var skip = new HashSet<string> { "screenshareCaptureMode", "screenshareGameWindow", "screenshareGameOverrides", "screenshareAutoGameKeepFocused", "screenshareSwitchDelaySeconds" };
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (skip.Contains(prop.Name)) continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["screenshareCaptureMode"]?.Value<string>() != settings["screenshareCaptureMode"]?.Value<string>() ||
                previous["screenshareGameWindow"]?.Value<string>() != settings["screenshareGameWindow"]?.Value<string>() ||
                previous["screenshareAutoGameKeepFocused"]?.Value<bool>() != settings["screenshareAutoGameKeepFocused"]?.Value<bool>() ||
                previous["screenshareSwitchDelaySeconds"]?.Value<double>() != settings["screenshareSwitchDelaySeconds"]?.Value<double>() ||
                !JToken.DeepEquals(previous["screenshareGameOverrides"], settings["screenshareGameOverrides"]);
        }

        // the limit is only read by the storage card, so saving it needs no live work in obs
        internal static bool TestOnlyStorageLimitChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "storageLimitGb") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["storageLimitGb"]?.Value<int>() != settings["storageLimitGb"]?.Value<int>();
        }

        // the settings that are read where they are used and need nothing from obs when they change (start minimized is carried into obs by the native plugin), and the one that only rewrites the startup shortcut
        internal static readonly string[] BehaviorKeys =
            { "autoCopyLink", "linkNotificationEnabled", "hevcFixEnabled", "compressSkipFavorites", "storageCompressionType", "deletedHistoryDays", "browserSleepSeconds", "startMinimized", "hideNotificationsFromCapture", "trimPreciseDefault", "trimGpuEncode", "editorAutoplay", "editorLoopPreview", "editorSeekStep", "gifFrameDelay", "editorWheelZoom", "editorZoomScaling", "clipAntialiasing", "gameRecognition", "cardDateFormat", "developmentMode", "sdkMode", "checkObsUpdates", "logInterfaceEvents", "debugLoggingEnabled", "autoDeleteLogsOnLaunch", "clipSoundVolume", "recordingSoundVolume", "clipNotificationEnabled", "recordingNotificationEnabled", "clipNotificationSeconds", "closeToTray", "disableObsCrashPopup", "lastUpdatePromptVersion", "openClipsKeybind", "openClipsKeybindExtra", "pinObsTrayIcon", "runObsAsAdmin", "setupWizardPending", "showCodecTags" };

        internal static readonly string[] StartupKeys = { "obsStartupEnabled" };

        // the mouse cursor settings, which only touch the capture sources of the running obs
        internal static readonly string[] CursorKeys = { "showCursorDisplayCapture", "showCursorGameCapture", "showCursorWindowCapture" };
        internal static readonly string[] CaptureDisplayKeys = { "captureDisplay" };

        // true when something changed and every change is one of the given keys
        internal static bool TestOnlyKeysChanged(JObject previous, JObject settings, string[] keys)
        {
            bool changed = false;
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) continue;
                if (!keys.Contains(prop.Name)) return false;
                changed = true;
            }
            return changed;
        }

        internal static bool TestOnlySleepOverrideChanged(JObject previous, JObject settings)
        {
            foreach (var prop in GetDefaultSettings().Properties())
            {
                if (prop.Name == "allowSleepWhileActive") continue;
                if (!JToken.DeepEquals(previous[prop.Name], settings[prop.Name])) return false;
            }
            return previous["allowSleepWhileActive"]?.Value<bool>() != settings["allowSleepWhileActive"]?.Value<bool>();
        }

        internal static JArray ConcatArrays(JArray a, JArray b)
        {
            var result = new JArray();
            if (a != null) foreach (var item in a) result.Add(item);
            if (b != null) foreach (var item in b) result.Add(item);
            return result;
        }

        public static JObject SaveOverlayPreviewFromRequest(string body) => OverlayPreviewFromRequest(body, "preview");

        public static JObject SaveSettingsFromRequest(string body)
        {
            lock (SettingsStore.Gate)
            {
                if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Missing settings body.");
                var incoming = JObject.Parse(body);
                var current = ReadSettings();
                var previous = Normalize(current);
                foreach (var prop in incoming.Properties())
                {
                    if (current[prop.Name] == null) throw new InvalidOperationException("Unknown setting: " + prop.Name);
                    current[prop.Name] = prop.Value;
                }
                var settings = Normalize(current);
                // a fresh "custom" pick -> convert + file it as a saved preset, then carry on as if the user had selected that preset. subsequent saves send the "user/..." id directly and skip this.
                if ((settings["appIcon"]?.Value<string>() ?? "") == "custom")
                {
                    string imported = ImportCustomIcon(settings["appIconCustomPath"]?.Value<string>() ?? "");
                    if (imported != null)
                    {
                        settings["appIcon"] = imported;
                        settings["appIconCustomPath"] = "";
                        settings = Normalize(settings);
                    }
                }
                // takes effect on this already-running helper the moment settings are saved, not just after the next reload -- Log.Write gates on this flag on every call, so flipping it here is what actually lets someone enable logging, reproduce a bug, and have it show up without restarting obs.
                Server.State.LogEnabled = DevelopmentFlag(settings, "debugLoggingEnabled");
                var hotkeyRelease = EnsureHotkeyCaptureReleased(settings);
                if (!TestIncomingSettingsChanged(incoming, previous, settings))
                {
                    if (TestOverlayColorRequest(incoming))
                    {
                        var live = ApplyOverlayOpacityForStyleLive(settings);
                        return new JObject
                        {
                            ["ok"] = true, ["settings"] = settings,
                            ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                            ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                            ["restartRequired"] = false, ["restartReason"] = "",
                        };
                    }
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = hotkeyRelease["applied"], ["warnings"] = hotkeyRelease["warnings"],
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestScreenshareCaptureOnlyRequest(incoming))
                {
                    WriteSettings(settings);
                    var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
                    var live = ApplyScreenshareCaptureLive(settings, preset);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestDiscordOutputOnlyRequest(incoming))
                {
                    var live = ApplyDiscordOutputLive(settings);
                    if (live["ok"]?.Value<bool>() != true)
                    {
                        return new JObject
                        {
                            ["ok"] = false, ["settings"] = previous, ["applied"] = live["applied"], ["warnings"] = live["warnings"],
                            ["message"] = live["message"], ["restartRequired"] = false, ["restartReason"] = "",
                        };
                    }
                    WriteSettings(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                WriteSettings(settings);
                if (TestOnlyStorageLimitChanged(previous, settings))
                {
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = hotkeyRelease["applied"], ["warnings"] = hotkeyRelease["warnings"],
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyKeysChanged(previous, settings, BehaviorKeys))
                {
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = hotkeyRelease["applied"], ["warnings"] = hotkeyRelease["warnings"],
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyKeysChanged(previous, settings, CaptureDisplayKeys))
                {
                    var live = ApplyCaptureDisplayLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyKeysChanged(previous, settings, CursorKeys))
                {
                    var live = ApplyCursorLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyKeysChanged(previous, settings, StartupKeys))
                {
                    var startup = SetObsStartupSetting(settings["obsStartupEnabled"]?.Value<bool>() ?? false);
                    var applied = new JArray();
                    var warned = new JArray();
                    if (startup["ok"]?.Value<bool>() == true) applied.Add("Windows startup");
                    else warned.Add("Windows startup was saved, but Windows rejected the change: " + startup["message"]);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, applied),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, warned),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlySleepOverrideChanged(previous, settings))
                {
                    var live = ApplySleepOverrideSetting(settings["allowSleepWhileActive"]?.Value<bool>() ?? false);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyMotionBlurChanged(previous, settings))
                {
                    var live = ApplyMotionBlurLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyMicChanged(previous, settings))
                {
                    var live = ApplyMicLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyRecordingAudioChanged(previous, settings))
                {
                    var live = ApplyRecordingAudioLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyAudioVolumesChanged(previous, settings))
                {
                    var live = ApplyAudioVolumesLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyOverlayVisualChanged(previous, settings))
                {
                    var live = ApplyOverlayVisualSettingsLive(previous, settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyScreenshareCaptureChanged(previous, settings))
                {
                    var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
                    var live = ApplyScreenshareCaptureLive(settings, preset);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (TestOnlyAppIconChanged(previous, settings))
                {
                    var live = ApplyAppIconLive(settings);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = settings,
                        ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, live["applied"] as JArray),
                        ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, live["warnings"] as JArray),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                // theme change -> write the obs .ovt variant + user.ini key now, before ApplyLiveSettings restarts obs to pick it up.
                string nextTheme = settings["theme"]?.ToString() ?? "default";
                if (previous["theme"]?.ToString() != nextTheme ||
                    ((nextTheme == "custom" || nextTheme.StartsWith("user/", StringComparison.Ordinal)) && !JToken.DeepEquals(previous["themeCustom"], settings["themeCustom"])))
                {
                    Themes.ApplyToObs(settings);
                }
                bool overlayStyleChanged = previous["overlayStyle"]?.Value<string>() != settings["overlayStyle"]?.Value<string>();
                bool overlayGeometryChanged = !JToken.DeepEquals(previous["overlayPosition"], settings["overlayPosition"]) ||
                    previous["recordingPreset"]?.Value<string>() != settings["recordingPreset"]?.Value<string>() ||
                    previous["overlayOpacity"]?.Value<int>() != settings["overlayOpacity"]?.Value<int>() ||
                    previous["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>() ||
                    previous["overlayRotation"]?.Value<int>() != settings["overlayRotation"]?.Value<int>() ||
                    previous["overlayFlipH"]?.Value<bool>() != settings["overlayFlipH"]?.Value<bool>() ||
                    previous["overlayHueShift"]?.Value<double>() != settings["overlayHueShift"]?.Value<double>() ||
                    previous["overlayColorMultiply"]?.Value<string>() != settings["overlayColorMultiply"]?.Value<string>() ||
                    previous["overlayColorAdd"]?.Value<string>() != settings["overlayColorAdd"]?.Value<string>();
                bool motionBlurChanged = previous["motionBlurEnabled"]?.Value<bool>() != settings["motionBlurEnabled"]?.Value<bool>() ||
                    previous["motionBlurStrength"]?.Value<double>() != settings["motionBlurStrength"]?.Value<double>();
                // the mode, its keys and the rest of the bongo settings are settings of its source, and the layouts of the wasd overlay are files of its sources, so a change of either reaches obs live
                bool bongoModeChanged = BongoSettingsChanged(previous, settings);
                bool inputOverlayLayoutChanged = InputOverlayLayoutChanged(previous, settings);
                bool applyOverlay = overlayStyleChanged || overlayGeometryChanged || bongoModeChanged || inputOverlayLayoutChanged;
                bool recreateBongo = (overlayStyleChanged || bongoModeChanged) && settings["overlayStyle"]?.Value<string>() == "bongo_cat";
                bool restartObs = TestRestartRequired(previous, settings);
                bool applyVideoSettings = TestRuntimeVideoSettingsChanged(previous, settings);
                bool applyReplayBufferOutput = TestReplayBufferOutputChanged(previous, settings);
                bool applyRuntimeOutputs = restartObs || applyVideoSettings || applyReplayBufferOutput;
                // set on the source before anything restarts obs, which then saves it with the scene
                if (previous["captureDisplay"]?.Value<string>() != settings["captureDisplay"]?.Value<string>())
                {
                    var display = ApplyCaptureDisplayLive(settings);
                    hotkeyRelease["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, display["applied"] as JArray);
                    hotkeyRelease["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, display["warnings"] as JArray);
                }
                var liveResult = ApplyLiveSettings(settings, restartObs, applyOverlay, recreateBongo, motionBlurChanged, applyRuntimeOutputs, applyVideoSettings, applyReplayBufferOutput, MicKeysChanged(previous, settings), AudioVolumeKeysChanged(previous, settings), inputOverlayLayoutChanged && settings["overlayStyle"]?.Value<string>() == "input_overlay",
                    previous["allowSleepWhileActive"]?.Value<bool>() != settings["allowSleepWhileActive"]?.Value<bool>());
                if (previous["appIcon"]?.Value<string>() != settings["appIcon"]?.Value<string>() ||
                    previous["appIconCustomPath"]?.Value<string>() != settings["appIconCustomPath"]?.Value<string>() ||
                    previous["appIconRecordingDot"]?.Value<bool>() != settings["appIconRecordingDot"]?.Value<bool>())
                {
                    var iconLive = ApplyAppIconLive(settings);
                    liveResult["applied"] = ConcatArrays(liveResult["applied"] as JArray, iconLive["applied"] as JArray);
                }
                return new JObject
                {
                    ["ok"] = true, ["settings"] = settings,
                    ["applied"] = ConcatArrays(hotkeyRelease["applied"] as JArray, liveResult["applied"] as JArray),
                    ["warnings"] = ConcatArrays(hotkeyRelease["warnings"] as JArray, liveResult["warnings"] as JArray),
                    ["restartRequired"] = liveResult["restartRequired"], ["restartReason"] = liveResult["restartReason"],
                };
            }
        }

    }
}
