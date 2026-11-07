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
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHotkeys;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // the canvas size, the recording presets and the encoder spec the profile gets
    internal static class SettingsRecording
    {
        // -- canvas sizing + recording preset/encoder spec --

        internal static int GetEvenDimension(double value)
        {
            int n = (int)Math.Round(value);
            n = Math.Max(2, Math.Min(4096, n));
            if (n % 2 != 0) n -= 1;
            return Math.Max(2, n);
        }

        internal static JObject GetScaledEvenSize(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
        {
            if (sourceWidth < 2 || sourceHeight < 2 || maxWidth < 2 || maxHeight < 2)
                return new JObject { ["width"] = 1920, ["height"] = 1080 };
            double scale = Math.Min(1.0, Math.Min(maxWidth / (double)sourceWidth, maxHeight / (double)sourceHeight));
            return new JObject { ["width"] = GetEvenDimension(sourceWidth * scale), ["height"] = GetEvenDimension(sourceHeight * scale) };
        }

        // canvas-aware default for recordingScaleMode: monitors above 1080p downscale to 1080 by default, 1080p and below record native (nothing to downscale). only used when the settings file has no explicit value.
        internal static string DefaultRecordingScaleMode()
        {
            try
            {
                var mon = GetPrimaryMonitorCanvasSize();
                if (mon["ok"]?.Value<bool>() == true && mon["height"].Value<int>() > 1080) return "downscale";
            }
            catch (Exception ex) { Log.Write("DefaultRecordingScaleMode: " + ex.Message); }
            return "native";
        }

        // OBS-style "Output (Scaled) Resolution" list derived from the real canvas -- same scale-factor steps OBSs own Video settings use, so a 1440p canvas yields 2560x1440 / 2048x1152 / 1920x1080 / ... and a 4K canvas yields its own set. value passed back is the height; ResolvePresetVideoSpec keeps the canvas aspect.
        internal static JArray BuildDownscaleResolutionList()
        {
            var mon = GetPrimaryMonitorCanvasSize();
            var baseSize = GetScaledEvenSize(
                mon["ok"]?.Value<bool>() == true ? mon["width"].Value<int>() : 1920,
                mon["ok"]?.Value<bool>() == true ? mon["height"].Value<int>() : 1080, 4096, 4096);
            int bw = baseSize["width"].Value<int>(), bh = baseSize["height"].Value<int>();
            // exact same scale steps + rounding OBSs Video settings use: width truncated to a multiple of 4, height to a multiple of 2.
            double[] scales = { 1.0, 1.25, 1.0 / 0.75, 1.5, 1.0 / 0.6, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0 };
            var list = new JArray();
            var seen = new HashSet<int>();
            foreach (var s in scales)
            {
                int cw = (int)(bw / s) & ~3;
                int ch = (int)(bh / s) & ~1;
                if (ch < 240 || !seen.Add(ch)) continue;
                list.Add(new JObject { ["label"] = cw + "x" + ch, ["height"] = ch });
            }
            return list;
        }

        internal static JObject GetPrimaryMonitorCanvasSize()
        {
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int width = bounds.Width, height = bounds.Height;
                if (width >= 320 && height >= 240) return new JObject { ["ok"] = true, ["width"] = width, ["height"] = height };
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NullReferenceException)
            {
                Log.Write("Primary monitor size detection failed: " + ex.Message);
            }
            return new JObject { ["ok"] = false, ["width"] = 1920, ["height"] = 1080 };
        }

        // outputHeightCap of 8192 means "never downscale" (output == base); a real value scales the output down to that height, aspect preserved from the canvas. scaleType is the OBS downscale filter, ignored by OBS when base==output.
        internal static JObject ResolvePresetVideoSpec(int outputHeightCap, string scaleType, int fpsNumerator, int fpsDenominator)
        {
            var monitor = GetPrimaryMonitorCanvasSize();
            int sourceWidth = monitor["ok"]?.Value<bool>() == true ? monitor["width"].Value<int>() : 1920;
            int sourceHeight = monitor["ok"]?.Value<bool>() == true ? monitor["height"].Value<int>() : 1080;
            var baseSize = GetScaledEvenSize(sourceWidth, sourceHeight, 4096, 4096);
            var output = GetScaledEvenSize(baseSize["width"].Value<int>(), baseSize["height"].Value<int>(), 8192, outputHeightCap);

            return new JObject
            {
                ["video"] = new JObject
                {
                    ["baseWidth"] = baseSize["width"], ["baseHeight"] = baseSize["height"],
                    ["outputWidth"] = output["width"], ["outputHeight"] = output["height"],
                    ["fpsNumerator"] = fpsNumerator, ["fpsDenominator"] = fpsDenominator,
                },
                ["profile"] = new JArray(
                    new JArray("Output", "Mode", "Advanced"),
                    new JArray("AdvOut", "RecType", "Standard"),
                    new JArray("Video", "BaseCX", baseSize["width"].Value<int>().ToString()),
                    new JArray("Video", "BaseCY", baseSize["height"].Value<int>().ToString()),
                    new JArray("Video", "OutputCX", output["width"].Value<int>().ToString()),
                    new JArray("Video", "OutputCY", output["height"].Value<int>().ToString()),
                    new JArray("Video", "FPSType", "2"),
                    new JArray("Video", "FPSNum", fpsNumerator.ToString()),
                    new JArray("Video", "FPSDen", fpsDenominator.ToString()),
                    new JArray("Video", "ScaleType", string.IsNullOrEmpty(scaleType) ? "lanczos" : scaleType)
                ),
                ["source"] = new JObject { ["width"] = sourceWidth, ["height"] = sourceHeight },
            };
        }

        // performance stays capped low no matter the global scale mode -- the preset exists to keep load and file size down; balanced/quality honor recordingScaleMode: native = no downscale (cap 8192), downscale = cap at downscaleHeight.
        internal static int OutputHeightCap(string presetName, string scaleMode, int downscaleHeight) => presetName == "performance" ? 720 : (scaleMode == "downscale" ? downscaleHeight : 8192);

        internal static JObject GetPresetSpec(string name, int fpsNumerator, int fpsDenominator, string scaleMode, int downscaleHeight, string downscaleFilter)
        {
            int cap = OutputHeightCap(name, scaleMode, downscaleHeight);
            string scaleType = string.IsNullOrEmpty(downscaleFilter) ? "lanczos" : downscaleFilter;
            int cqp;
            switch (name)
            {
                case "performance": cqp = 26; break;
                case "quality": cqp = 20; break;
                default: cqp = 22; break;
            }
            var videoSpec = ResolvePresetVideoSpec(cap, scaleType, fpsNumerator, fpsDenominator);
            return new JObject { ["cqp"] = cqp, ["video"] = videoSpec["video"], ["profile"] = videoSpec["profile"], ["source"] = videoSpec["source"] };
        }

        internal static JObject GetPresetSpec(string name, JObject settings) => GetPresetSpec(name,
            settings?["fpsNumerator"]?.Value<int>() ?? settings?["fps"]?.Value<int>() ?? 60,
            settings?["fpsDenominator"]?.Value<int>() ?? 1,
            settings?["recordingScaleMode"]?.Value<string>() ?? "native",
            settings?["downscaleHeight"]?.Value<int>() ?? 1080,
            settings?["downscaleFilter"]?.Value<string>() ?? "lanczos");

        // vendor/generation candidate table, effort tuning, and cqp->icq/crf conversion live in Encoder.PickEncoder (shared/Encoder.cs, which the setup compiles too) so both agree on the same tuning tables; this just adapts that shared choice into the shape Write-ReplayKitRecordEncoderJson expects, plus the "requested codec unavailable on this gpu" warning that PickEncoder itself has no concept of (Setup never surfaces that warning to a user, since it has no way to ask one at install time).
        internal static JObject GetEncoderSpec(JObject settings, JObject preset)
        {
            var gpu = Gpu.PrimaryGpu();
            string codecPreference = settings["codecPreference"]?.Value<string>() ?? DefaultCodecPreference;
            int cqp = preset["cqp"]?.Value<int>() ?? 22;
            string compressionMode = settings["compressionMode"]?.Value<string>() ?? "balanced";
            var choice = Encoder.PickEncoder(gpu, codecPreference, cqp, compressionMode);
            string unloaded = null;
            // an encoder obs could not load this session (an outdated driver) is never written to the profile: the best one that did load takes its place, another gpu's first, then the processor
            if (!GpuDrivers.IsAvailable(choice.ObsEncoderId))
            {
                unloaded = choice.Label;
                EncoderChoice alternative = null;
                var gpus = new List<GpuInfo>();
                if (gpu != null) gpus.Add(gpu);
                gpus.AddRange(Gpu.ListGpus().Where(g => g != null && !ReferenceEquals(g, gpu) && g.Name != gpu?.Name));
                foreach (var candidateGpu in gpus)
                {
                    foreach (var codec in new[] { codecPreference, "h264" })
                    {
                        var candidate = Encoder.PickEncoder(candidateGpu, codec, cqp, compressionMode);
                        if (candidate.IsHardware && GpuDrivers.IsAvailable(candidate.ObsEncoderId)) { alternative = candidate; break; }
                    }
                    if (alternative != null) break;
                }
                choice = alternative ?? Encoder.PickEncoder(null, "auto", cqp, compressionMode);
            }

            var result = new JObject
            {
                ["id"] = choice.ObsEncoderId,
                ["codec"] = choice.Codec,
                ["label"] = choice.Label,
                ["settings"] = JObject.FromObject(choice.Settings),
            };
            string pref = (codecPreference ?? "").Trim().ToLowerInvariant();
            if (unloaded != null)
                result["warning"] = unloaded + " did not load in OBS (usually an outdated graphics driver), so ReplayKit records with " + choice.Label + " until it does.";
            else if (pref != "" && choice.Codec != pref)
                result["warning"] = "Requested codec '" + pref + "' is not supported by the detected GPU, so ReplayKit selected " + choice.Label + ".";
            return result;
        }

        internal static string GetCurrentProfileDir()
        {
            string profilesRoot = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "profiles");
            string profileName = "Untitled";
            var profileList = ObsWebSocket.InvokeRequest("GetProfileList", null, 3000);
            if (profileList.Ok && !string.IsNullOrEmpty(profileList.Data?["currentProfileName"]?.Value<string>()))
                profileName = profileList.Data["currentProfileName"].Value<string>();
            string rootFull = Path.GetFullPath(profilesRoot).TrimEnd('\\');
            string profileDir = Path.GetFullPath(Path.Combine(profilesRoot, profileName));
            if (!profileDir.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBS profile path resolved outside the OBS profiles directory.");
            return profileDir;
        }

        internal static JObject WriteRecordEncoderJson(JObject encoder)
        {
            try
            {
                string profileDir = GetCurrentProfileDir();
                if (!Directory.Exists(profileDir)) Directory.CreateDirectory(profileDir);
                string path = Path.Combine(profileDir, "recordEncoder.json");
                AppConfig.WriteUtf8(path, encoder["settings"].ToString(Formatting.None));
                return new JObject { ["ok"] = true, ["path"] = path };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        internal const string BongoInactiveName = "Bongo Cat Overlay (Inactive)";
        internal const string BongoSwitchingName = "Bongo Cat Overlay (Switching)";

        // the bongo input in the live scene. one an older install left as the opengl bongobs-cat is hidden and renamed out of the way, never removed: that plugin keeps its keyboard hook running on freed state once its source is destroyed, and the next key press takes obs down. SetOverlaySceneFile and the installer drop it while obs is not running
        internal static JObject SelectBongoInput(string sceneName, JArray items, JObject inputSettings, List<string> warnings, bool refresh)
        {
            var kinds = ObsWebSocket.InvokeRequest("GetInputKindList", new JObject { ["unversioned"] = true }, 3000);
            if (!kinds.Ok || !(kinds.Data?["inputKinds"] as JArray ?? new JArray()).Any(k => k.Value<string>() == BongoInputKind))
            {
                warnings.Add("Bongo Cat needs the ReplayKit OBS plugin. Run the ReplayKit installer again, then restart OBS.");
                return null;
            }
            string Kind(string name)
            {
                var result = ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = name }, 3000);
                if (!result.Ok) throw new InvalidOperationException("Could not inspect Bongo Cat: " + result.Message);
                return result.Data?["inputKind"]?.Value<string>();
            }
            bool Rename(string from, string to)
            {
                var result = ObsWebSocket.InvokeRequest("SetInputName", new JObject { ["inputName"] = from, ["newInputName"] = to }, 3000);
                if (!result.Ok) warnings.Add("Could not set up Bongo Cat: " + result.Message);
                return result.Ok;
            }
            bool Enable(JObject item, bool enabled)
            {
                var result = SetSceneItemEnabled(sceneName, item, enabled);
                if (result["ok"]?.Value<bool>() != true) { warnings.Add("Could not change Bongo Cat visibility: " + result["message"]); return false; }
                return true;
            }

            var current = FindSceneItem(items, "Bongo Cat Overlay");
            var inactive = FindSceneItem(items, BongoInactiveName);
            JObject legacy = null;
            if (current != null && Kind("Bongo Cat Overlay") != BongoInputKind)
            {
                if (!Enable(current, false) || !Rename("Bongo Cat Overlay", BongoSwitchingName)) return null;
                legacy = current;
                current = null;
            }
            // a replaykit-bongo input parked by an earlier renderer switch comes back instead of a new one
            if (current == null && inactive != null && Kind(BongoInactiveName) == BongoInputKind)
            {
                if (!Rename(BongoInactiveName, "Bongo Cat Overlay")) return null;
                current = inactive;
                current["sourceName"] = "Bongo Cat Overlay";
                inactive = null;
                refresh = true;
            }
            if (legacy != null && inactive == null) Rename(BongoSwitchingName, BongoInactiveName);
            if (inactive != null) Enable(inactive, false);
            if (current == null)
            {
                refresh = true;
                var created = ObsWebSocket.InvokeRequest("CreateInput", new JObject
                {
                    ["sceneName"] = sceneName, ["inputName"] = "Bongo Cat Overlay", ["inputKind"] = BongoInputKind,
                    ["inputSettings"] = inputSettings, ["sceneItemEnabled"] = true,
                }, 5000);
                if (!created.Ok)
                {
                    warnings.Add("Could not create Bongo Cat overlay: " + created.Message);
                    return null;
                }
                current = new JObject { ["sceneItemId"] = created.Data?["sceneItemId"]?.Value<int>() ?? 0, ["sourceName"] = "Bongo Cat Overlay" };
            }
            if (refresh)
            {
                var update = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject
                {
                    ["inputName"] = "Bongo Cat Overlay", ["inputSettings"] = inputSettings, ["overlay"] = true,
                }, 3000);
                if (!update.Ok) warnings.Add("Could not update Bongo Cat settings: " + update.Message);
            }
            Enable(current, true);
            return current;
        }

        internal static void RemoveBongoStandby(JArray sources, JArray groups)
        {
            var standby = sources.OfType<JObject>().Where(source =>
                (source["name"]?.Value<string>() == BongoInactiveName || source["name"]?.Value<string>() == BongoSwitchingName) &&
                IsBongoInputKind(source["id"]?.Value<string>())).ToList();
            foreach (var source in standby)
            {
                string uuid = source["uuid"]?.Value<string>();
                string name = source["name"]?.Value<string>();
                foreach (var owner in sources.Concat(groups).OfType<JObject>())
                    RemoveJsonListWhere(owner["settings"]?["items"] as JArray, item => item["name"]?.Value<string>() == name ||
                        (!string.IsNullOrEmpty(uuid) && item["source_uuid"]?.Value<string>() == uuid));
                sources.Remove(source);
            }
        }

        // where WASD Overlay and Mouse Overlay sit inside Group, the member transforms SetInputOverlayGroupMemberJson writes
        internal static JObject InputOverlayMemberTransform(string name, JObject settings)
        {
            var fit = InputOverlayMemberFit(name, settings);
            return new JObject
            {
                ["positionX"] = fit.X, ["positionY"] = fit.Y, ["scaleX"] = fit.Scale, ["scaleY"] = fit.Scale,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
        }

        // a member already in Group is moved to where the picked layouts put it, and the mouse hidden while it is off; obs-websocket takes the group by its name like a scene
        internal static void PlaceInputOverlayMember(string name, JObject settings, List<string> warnings)
        {
            var list = ObsWebSocket.InvokeRequest("GetGroupSceneItemList", new JObject { ["sceneName"] = "Group" }, 3000);
            if (!list.Ok) { warnings.Add("Could not read the overlay group: " + list.Message); return; }
            var member = (list.Data?["sceneItems"] as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(i => i["sourceName"]?.Value<string>() == name);
            if (member == null) return;
            var moved = SetSceneItemTransform("Group", member, InputOverlayMemberTransform(name, settings));
            if (moved["ok"]?.Value<bool>() != true) warnings.Add("Could not place " + name + ": " + moved["message"]);
            if (name != "Mouse Overlay") return;
            var shown = SetSceneItemEnabled("Group", member, InputOverlayMouseShown(settings));
            if (shown["ok"]?.Value<bool>() != true) warnings.Add("Could not show or hide " + name + ": " + shown["message"]);
        }

        // removing the inputs takes their items inside Group with them, and the empty Group item goes after
        internal static bool RemoveInputOverlayLive(string sceneName, JArray items, List<string> warnings)
        {
            bool removed = false;
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
            {
                if (!ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = name }, 3000).Ok) continue;
                var result = ObsWebSocket.InvokeRequest("RemoveInput", new JObject { ["inputName"] = name }, 3000);
                if (result.Ok) removed = true;
                else warnings.Add("Could not remove " + name + ": " + result.Message);
            }
            var group = FindSceneItem(items, "Group");
            if (group != null)
            {
                var result = RemoveSceneItem(sceneName, group);
                if (result["ok"]?.Value<bool>() == true) removed = true;
                else warnings.Add("Could not remove the input overlay group: " + result["message"]);
            }
            return removed;
        }

        // the bongo input and any input parked by an older renderer switch; one of the opengl bongobs-cat kind is only removed while that plugin is not loaded, since destroying its source leaves its keyboard hook running on freed state
        internal static bool RemoveBongoLive(List<string> warnings)
        {
            var kinds = ObsWebSocket.InvokeRequest("GetInputKindList", new JObject { ["unversioned"] = true }, 3000);
            bool legacyLoaded = !kinds.Ok || (kinds.Data?["inputKinds"] as JArray ?? new JArray()).Any(k => k.Value<string>() == LegacyBongoInputKind);
            bool removed = false;
            foreach (var name in new[] { "Bongo Cat Overlay", BongoInactiveName, BongoSwitchingName })
            {
                var input = ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = name }, 3000);
                if (!input.Ok) continue;
                if (input.Data?["inputKind"]?.Value<string>() == LegacyBongoInputKind && legacyLoaded) continue;
                var result = ObsWebSocket.InvokeRequest("RemoveInput", new JObject { ["inputName"] = name }, 3000);
                if (result.Ok) removed = true;
                else warnings.Add("Could not remove " + name + ": " + result.Message);
            }
            return removed;
        }

        // the parallel live-websocket version of Set-OverlaySceneFile, used when obs must not be restarted: toggle visibility, refresh the bongo settings and reposition.
        internal static JObject ApplyOverlayLive(JObject settings, JObject preset, bool recreateBongo = true, bool rebuildInputOverlay = false)
        {
            var warnings = new List<string>();
            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["warnings"] = new JArray("Overlay setting was saved, but OBS scene lookup failed: " + scene["message"]) };

            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["warnings"] = new JArray("Overlay setting was saved, but OBS scene items could not be read: " + itemsResult["message"]) };
            var items = itemsResult["items"] as JArray;
            int opacity = OverlayOpacityValue(settings);
            string overlayStyle = settings["overlayStyle"]?.Value<string>() ?? "";

            // only the picked overlay lives in obs: the others are removed rather than hidden, so they do not sit in the sources list, and get built again when picked
            bool removed = false;
            // input-overlay reads its layout and picture only when the source is made, so other layouts take new sources, built below the way a freshly picked overlay is
            if (overlayStyle != "input_overlay" || rebuildInputOverlay) removed |= RemoveInputOverlayLive(sceneName, items, warnings);
            // a removed source keeps its name until obs has let go of it, and a new source of that name is refused until then
            if (rebuildInputOverlay)
                for (int i = 0; i < 30 && new[] { "WASD Overlay", "Mouse Overlay" }.Any(n => ObsWebSocket.InvokeRequest("GetInputSettings", new JObject { ["inputName"] = n }, 3000).Ok); i++)
                    Thread.Sleep(100);
            if (overlayStyle != "bongo_cat") removed |= RemoveBongoLive(warnings);
            if (removed)
            {
                var afterRemoval = GetSceneItems(sceneName);
                if (afterRemoval["ok"]?.Value<bool>() == true) items = afterRemoval["items"] as JArray ?? items;
            }

            // SetOverlaySceneFile builds WASD/Mouse Overlay as members of "Group". GetSceneItemList only returns top-level items, so a prior ApplyOverlayLive that ran EnsureInputSceneItem on them saw them as missing and its CreateInput->CreateSceneItem fallback added a duplicate top-level copy every apply ("two Mouse Overlays"). If the Group is present, any top-level WASD/Mouse item is a stray -- drop it (the source survives).
            if (FindSceneItem(items, "Group") != null)
            {
                foreach (var stray in items.OfType<JObject>()
                             .Where(i => { var n = i["sourceName"]?.Value<string>(); return n == "WASD Overlay" || n == "Mouse Overlay"; })
                             .ToList())
                {
                    var rm = RemoveSceneItem(sceneName, stray);
                    if (rm["ok"]?.Value<bool>() != true) warnings.Add("Could not remove a duplicate " + stray["sourceName"]?.Value<string>() + " item: " + rm["message"]);
                }
                var refreshed = GetSceneItems(sceneName);
                if (refreshed["ok"]?.Value<bool>() == true) items = refreshed["items"] as JArray ?? items;
            }

            bool bongoEnabled = overlayStyle == "bongo_cat";
            var bongo = FindSceneItem(items, "Bongo Cat Overlay");
            if (bongoEnabled)
            {
                var inputSettings = NewBongoSourceJson(Guid.NewGuid().ToString(), settings)["settings"] as JObject;
                bongo = SelectBongoInput(sceneName, items, inputSettings, warnings, recreateBongo);
            }

            var cursorResult = SetDisplayCaptureCursorLive(settings);
            if (cursorResult["ok"]?.Value<bool>() != true) warnings.Add("Could not update the Display Capture cursor: " + cursorResult["message"]);

            foreach (var captureName in new[] { "Display Capture", "Window Capture", "Game Capture" })
            {
                var capture = FindSceneItem(items, captureName);
                var captureTransform = MainCaptureTransform(captureName, preset);
                var captureResult = SetSceneItemTransform(sceneName, capture, captureTransform);
                if (captureResult["ok"]?.Value<bool>() != true) warnings.Add("Could not fit " + captureName + " to canvas: " + captureResult["message"]);
            }

            if (bongoEnabled && bongo != null)
            {
                var rTransform = SetSceneItemTransform(sceneName, bongo, BongoTransform(preset, settings));
                if (rTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position Bongo Cat overlay: " + rTransform["message"]);
                var rOpacity = ApplyOverlayOpacityLive("Bongo Cat Overlay", opacity, settings);
                if (rOpacity["ok"]?.Value<bool>() != true) warnings.Add("Could not set Bongo Cat overlay opacity: " + rOpacity["message"]);
            }

            if (overlayStyle == "input_overlay")
            {
                var group = FindSceneItem(items, "Group");
                if (group != null)
                {
                    var rGroupTransform = SetSceneItemTransform(sceneName, group, InputOverlayGroupTransform(preset, settings));
                    if (rGroupTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position input overlay group: " + rGroupTransform["message"]);
                }
                var keyboardFiles = InputOverlayMemberFiles("WASD Overlay", settings);
                var mouseFiles = InputOverlayMemberFiles("Mouse Overlay", settings);
                string wasdPng = keyboardFiles?.Sheet, wasdJson = keyboardFiles?.Json, mousePng = mouseFiles?.Sheet, mouseJson = mouseFiles?.Json;
                if (string.IsNullOrWhiteSpace(wasdPng) || string.IsNullOrWhiteSpace(wasdJson) || string.IsNullOrWhiteSpace(mousePng) || string.IsNullOrWhiteSpace(mouseJson))
                {
                    warnings.Add("Input overlay assets are missing from the ReplayKit install, so OBS could not switch that overlay live.");
                }
                else
                {
                    var entries = new[]
                    {
                        new { name = "WASD Overlay", image = wasdPng, layout = wasdJson },
                        new { name = "Mouse Overlay", image = mousePng, layout = mouseJson },
                    };
                    // with a "Group" the two overlays are its members -- only refresh the source settings; adding them as standalone scene items is exactly the duplicate-stacking bug. without one (the overlay was just picked) they are built the way SetOverlaySceneFile builds them: members at their spots inside the group, grouped by the plugin, then the group placed
                    foreach (var entry in entries)
                    {
                        var io = new JObject
                        {
                            ["io.input_source"] = "This computer", ["io.overlay_image"] = entry.image, ["io.layout_file"] = entry.layout,
                        };
                        if (group != null)
                        {
                            var s = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = entry.name, ["inputSettings"] = io, ["overlay"] = true }, 3000);
                            if (!s.Ok) warnings.Add("Could not update " + entry.name + ": " + s.Message);
                            PlaceInputOverlayMember(entry.name, settings, warnings);
                            continue;
                        }
                        var created = EnsureInputSceneItem(sceneName, entry.name, "input-overlay", io, true);
                        if (created["ok"]?.Value<bool>() != true) { warnings.Add("Could not create " + entry.name + ": " + created["message"]); continue; }
                        var rTransform = SetSceneItemTransform(sceneName, created["item"] as JObject, InputOverlayMemberTransform(entry.name, settings));
                        if (rTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position " + entry.name + ": " + rTransform["message"]);
                    }
                    if (group == null)
                    {
                        PipeClient.SendGroupInputOverlay();
                        for (int i = 0; i < 30 && group == null; i++)
                        {
                            Thread.Sleep(100);
                            var grouped = GetSceneItems(sceneName);
                            if (grouped["ok"]?.Value<bool>() == true) group = FindSceneItem(grouped["items"] as JArray, "Group");
                        }
                        if (group == null) warnings.Add("Could not group the WASD and mouse overlays. Restart OBS to rebuild them.");
                        else
                        {
                            if (!InputOverlayMouseShown(settings)) PlaceInputOverlayMember("Mouse Overlay", settings, warnings);
                            var rGroupTransform = SetSceneItemTransform(sceneName, group, InputOverlayGroupTransform(preset, settings));
                            if (rGroupTransform["ok"]?.Value<bool>() != true) warnings.Add("Could not position input overlay group: " + rGroupTransform["message"]);
                        }
                    }
                    foreach (var entry in entries)
                    {
                        var rOpacity = ApplyOverlayOpacityLive(entry.name, opacity, settings);
                        if (rOpacity["ok"]?.Value<bool>() != true) warnings.Add("Could not set " + entry.name + " opacity: " + rOpacity["message"]);
                    }
                }
                if (group != null) SetSceneItemEnabled(sceneName, group, true);
            }

            return new JObject { ["ok"] = warnings.Count == 0, ["warnings"] = new JArray(warnings) };
        }

    }
}
