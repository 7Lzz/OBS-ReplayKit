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
using static ReplayKitHelper.SettingsOverlayLive;
using static ReplayKitHelper.SettingsObsConfig;
using static ReplayKitHelper.SettingsRecording;
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHotkeys;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // the scene collection file half of the settings: the overlay geometry, the scene source and scene item json, and the motion blur and overlay opacity filters as written into the file
    internal static class SettingsSceneJson
    {
        // -- overlay geometry: the rules live in shared/OverlayScene.cs, which the installer uses too; these read the settings json and the preset and pass plain values in.

        internal static int OverlayOpacityValue(JObject settings)
        {
            var value = settings?["overlayOpacity"];
            if (value == null || value.Type == JTokenType.Null) return 100;
            return OverlayScene.Opacity(value.Value<int>());
        }

        internal static double OverlayHueShiftValue(JObject settings)
        {
            var value = settings?["overlayHueShift"];
            if (value == null || value.Type == JTokenType.Null) return 0.0;
            double v;
            try { v = value.Value<double>(); } catch (FormatException) { v = 0.0; } catch (InvalidCastException) { v = 0.0; }
            return OverlayScene.HueShift(v);
        }

        internal static string OverlayHexColor(JObject settings, string key, string defaultValue)
        {
            var token = settings?[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            return OverlayScene.HexColor(token.Value<string>(), defaultValue);
        }

        internal static int OverlayColorValue(string value, string defaultValue) => OverlayScene.ColorValue(value, defaultValue);

        internal static bool HasOverlayColorAdjustments(JObject settings) =>
            OverlayScene.HasColorAdjustments(OverlayHueShiftValue(settings), OverlayHexColor(settings, "overlayColorMultiply", "#ffffff"), OverlayHexColor(settings, "overlayColorAdd", "#000000"));

        internal static double OverlayScaleFactor(JObject settings)
        {
            var value = settings?["overlayScale"];
            if (value == null || value.Type == JTokenType.Null) return 1.0;
            return OverlayScene.ScaleFactor(value.Value<int>());
        }

        internal static OverlayScene.ContentRect OverlayContent(JObject preset)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            double sourceW = 0.0, sourceH = 0.0;
            if (preset["source"] != null)
            {
                sourceW = GetDoubleValue(preset["source"], "width", 0.0);
                sourceH = GetDoubleValue(preset["source"], "height", 0.0);
            }
            return OverlayScene.Content(canvasW, canvasH, sourceW, sourceH);
        }

        internal static JObject OverlayContentRect(JObject preset)
        {
            var content = OverlayContent(preset);
            return new JObject
            {
                ["x"] = content.X, ["y"] = content.Y,
                ["width"] = content.Width, ["height"] = content.Height, ["scale"] = content.Scale,
                ["canvasWidth"] = content.CanvasWidth, ["canvasHeight"] = content.CanvasHeight,
                ["sourceWidth"] = content.SourceWidth, ["sourceHeight"] = content.SourceHeight,
            };
        }

        internal static JObject InputOverlayPos(JObject preset, double sourceW, double sourceH, double scaleX, double scaleY)
        {
            OverlayScene.InputOverlayCorner(OverlayContent(preset), sourceH, scaleY, out double x, out double y);
            return new JObject { ["x"] = x, ["y"] = y };
        }

        internal static JObject BottomLeftCornerOverlayPos(JObject preset, double sourceW, double sourceH, double scaleX, double scaleY)
        {
            OverlayScene.BottomLeftCorner(OverlayContent(preset), sourceH, scaleY, out double x, out double y);
            return new JObject { ["x"] = x, ["y"] = y };
        }

        // moves a freshly computed overlay transform onto the spot the user dragged it to, if there is one for this style. runs before ApplyOverlayFlipH so the mirror shift still lands on top of it, and is what makes a drag survive the scene rewrite + restart that every overlay-style change goes through -- a live SetSceneItemTransform alone would be thrown away by that reload.
        internal static void ApplyOverlayPositionOverride(JObject transform, JObject preset, JObject settings, string style)
        {
            if (!(settings?["overlayPosition"] is JObject map)) return;
            if (!(map[style] is JObject at)) return;
            var content = OverlayContentRect(preset);
            double cw = content["canvasWidth"].Value<double>(), ch = content["canvasHeight"].Value<double>();
            if (cw <= 0.0 || ch <= 0.0) return;
            transform["positionX"] = at["x"].Value<double>() * cw;
            transform["positionY"] = at["y"].Value<double>() * ch;
        }

        internal static bool OverlayFlipHorizontal(JObject settings) => settings?["overlayFlipH"]?.Value<bool>() ?? false;

        // horizontal mirror: negate scaleX and shift positionX right by the rendered width so the item stays in the same on-screen box (every overlay item is top-left aligned). sourceWidth is the items unscaled px width.
        internal static void ApplyOverlayFlipH(JObject transform, double sourceWidth, JObject settings)
        {
            if (!OverlayFlipHorizontal(settings)) return;
            double sx = transform["scaleX"]?.Value<double>() ?? 1.0;
            transform["positionX"] = (transform["positionX"]?.Value<double>() ?? 0.0) + sourceWidth * Math.Abs(sx);
            transform["scaleX"] = -sx;
        }

        internal static JObject InputOverlayGroupTransform(JObject preset, JObject settings = null)
        {
            double scale = OverlayScene.InputOverlayGroupScale(OverlayContent(preset), OverlayScaleFactor(settings));
            InputOverlayLayouts.GroupSize(settings, out double groupW, out double groupH);
            var pos = InputOverlayPos(preset, groupW, groupH, scale, scale);
            var transform = new JObject
            {
                ["positionX"] = pos["x"].Value<double>(), ["positionY"] = pos["y"].Value<double>(), ["scaleX"] = scale, ["scaleY"] = scale,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
            ApplyOverlayPositionOverride(transform, preset, settings, "input_overlay");
            ApplyOverlayFlipH(transform, groupW, settings);
            transform.Merge(OverlayRotation.Rotate(transform, settings?["overlayRotation"]?.Value<int>() ?? 0, groupW, groupH));
            return transform;
        }

        // where the picked keyboard or mouse sits inside Group; a mouse that is off stays in the group hidden, shrunk to a point in its corner, since obs counts hidden members in the box it gives a group
        internal static InputOverlayLayout.Fit InputOverlayMemberFit(string name, JObject settings)
        {
            InputOverlayLayout.Place(InputOverlayLayouts.Keyboard(settings), InputOverlayLayouts.Mouse(settings), out var keys, out var pointer, out _, out _);
            if (name != "Mouse Overlay") return keys;
            return pointer ?? new InputOverlayLayout.Fit { X = 0.0, Y = 0.0, Scale = 0.001 };
        }

        internal static bool InputOverlayMouseShown(JObject settings) => InputOverlayLayout.FindMouse(settings?["inputOverlayMouse"]?.Value<string>()).Json != null;

        // the files of a member: the picked keyboard or mouse, and the default mouse for one that is off, since its source stays in the group hidden
        internal static InputOverlayLayout.Picked InputOverlayMemberFiles(string name, JObject settings)
        {
            if (name != "Mouse Overlay") return InputOverlayLayouts.Keyboard(settings);
            return InputOverlayLayouts.Mouse(settings) ?? InputOverlayLayouts.Mouse(new JObject { ["inputOverlayMouse"] = InputOverlayLayout.DefaultMouse });
        }

        internal static JObject InputOverlayTransform(string name, JObject preset, JObject settings = null)
        {
            double scale = OverlayScene.InputOverlayGroupScale(OverlayContent(preset), OverlayScaleFactor(settings));
            // un-flipped group base -- these transforms feed the WASD/Mouse group_item_backup items, which dont render; the visible mirror comes from InputOverlayGroupTransform on the Group scene item.
            InputOverlayLayouts.GroupSize(settings, out double groupW, out double groupH);
            var pos = InputOverlayPos(preset, groupW, groupH, scale, scale);
            var fit = InputOverlayMemberFit(name, settings);
            return new JObject
            {
                ["positionX"] = pos["x"].Value<double>() + fit.X * scale, ["positionY"] = pos["y"].Value<double>() + fit.Y * scale,
                ["scaleX"] = fit.Scale * scale, ["scaleY"] = fit.Scale * scale,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
        }

        internal static JObject BongoTransform(JObject preset, JObject settings = null)
        {
            OverlayScene.BongoScale(OverlayContent(preset), OverlayScaleFactor(settings), out double scaleX, out double scaleY);
            var pos = BottomLeftCornerOverlayPos(preset, OverlayScene.BongoSourceWidth, OverlayScene.BongoSourceHeight, scaleX, scaleY);
            var transform = new JObject
            {
                ["positionX"] = pos["x"].Value<double>(), ["positionY"] = pos["y"].Value<double>(), ["scaleX"] = scaleX, ["scaleY"] = scaleY,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
            };
            ApplyOverlayPositionOverride(transform, preset, settings, "bongo_cat");
            ApplyOverlayFlipH(transform, OverlayScene.BongoSourceWidth, settings);
            transform.Merge(OverlayRotation.Rotate(transform, settings?["overlayRotation"]?.Value<int>() ?? 0, OverlayScene.BongoSourceWidth, OverlayScene.BongoSourceHeight));
            return transform;
        }

        internal static JObject MainCaptureTransform(string name, JObject preset)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }

            if (name == "Display Capture")
            {
                double sourceW = canvasW, sourceH = canvasH;
                if (preset["source"] != null)
                {
                    double sw = preset["source"]["width"]?.Value<double>() ?? 0.0;
                    double sh = preset["source"]["height"]?.Value<double>() ?? 0.0;
                    if (sw > 0.0) sourceW = sw;
                    if (sh > 0.0) sourceH = sh;
                }
                double scale = Math.Min(canvasW / sourceW, canvasH / sourceH);
                double scaledW = sourceW * scale, scaledH = sourceH * scale;
                return new JObject
                {
                    ["positionX"] = (canvasW - scaledW) / 2.0, ["positionY"] = (canvasH - scaledH) / 2.0,
                    ["scaleX"] = scale, ["scaleY"] = scale, ["rotation"] = 0.0, ["alignment"] = 5,
                    ["boundsType"] = "OBS_BOUNDS_NONE", ["boundsAlignment"] = 0, ["cropToBounds"] = false,
                    ["sourceWidth"] = sourceW, ["sourceHeight"] = sourceH,
                };
            }

            return new JObject
            {
                ["positionX"] = 0.0, ["positionY"] = 0.0, ["scaleX"] = 1.0, ["scaleY"] = 1.0,
                ["rotation"] = 0.0, ["alignment"] = 5, ["boundsType"] = "OBS_BOUNDS_SCALE_INNER",
                ["boundsWidth"] = canvasW, ["boundsHeight"] = canvasH, ["boundsAlignment"] = 0, ["cropToBounds"] = false,
                ["sourceWidth"] = canvasW, ["sourceHeight"] = canvasH,
            };
        }

        // resolves which live scene collection file obs currently has active. no Setup equivalent -- Setup always targets the single fixed bundled path at install time and never has to ask a running obs.
        internal static string GetSceneCollectionPath()
        {
            string scenesRoot = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "scenes");
            string collectionName = "Untitled";
            var list = ObsWebSocket.InvokeRequest("GetSceneCollectionList", null, 3000);
            if (list.Ok && !string.IsNullOrEmpty(list.Data?["currentSceneCollectionName"]?.Value<string>()))
                collectionName = list.Data["currentSceneCollectionName"].Value<string>();
            if (string.IsNullOrWhiteSpace(collectionName) || collectionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("OBS scene collection name is invalid.");

            string rootFull = Path.GetFullPath(scenesRoot).TrimEnd('\\');
            string path = Path.GetFullPath(Path.Combine(scenesRoot, collectionName + ".json"));
            if (!path.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBS scene collection path resolved outside the OBS scenes directory.");
            return path;
        }

        // -- json-file scene-source/scene-item builders. the fields every source and filter carries come from OverlayScene.SetSourceFields, shared with the installer. unlike Transform.cs (which always mints a fresh BongoCatSourceUuid constant for a brand-new template file), the live-apply functions below preserve whatever uuid an existing source already has and only mint a new one when creating from scratch -- callers pass whichever uuid is appropriate, these functions never generate one themselves.

        internal const string BongoInputKind = OverlayScene.BongoSourceKind;
        internal const string LegacyBongoInputKind = OverlayScene.LegacyBongoSourceKind;
        internal static readonly string[] BongoModes = SettingLimits.BongoModes;

        // the keys a bongo cat key can be bound to, as KeyboardEvent.code with the windows virtual key the source reads
        internal static readonly Dictionary<string, int> BongoKeyCodes = BuildBongoKeyCodes();

        internal static Dictionary<string, int> BuildBongoKeyCodes()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (char c = 'A'; c <= 'Z'; c++) map["Key" + c] = c;
            for (char c = '0'; c <= '9'; c++) { map["Digit" + c] = c; map["Numpad" + c] = 0x60 + (c - '0'); }
            for (int i = 1; i <= 12; i++) map["F" + i] = 0x6F + i;
            var named = new Dictionary<string, int>
            {
                ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Backspace"] = 0x08, ["CapsLock"] = 0x14, ["Escape"] = 0x1B,
                ["ShiftLeft"] = 0xA0, ["ShiftRight"] = 0xA1, ["ControlLeft"] = 0xA2, ["ControlRight"] = 0xA3, ["AltLeft"] = 0xA4, ["AltRight"] = 0xA5,
                ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26, ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
                ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
                ["Semicolon"] = 0xBA, ["Equal"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE, ["Slash"] = 0xBF, ["Backquote"] = 0xC0,
                ["BracketLeft"] = 0xDB, ["Backslash"] = 0xDC, ["BracketRight"] = 0xDD, ["Quote"] = 0xDE,
                ["NumpadAdd"] = 0x6B, ["NumpadSubtract"] = 0x6D, ["NumpadMultiply"] = 0x6A, ["NumpadDivide"] = 0x6F, ["NumpadDecimal"] = 0x6E, ["NumpadEnter"] = 0x0D,
            };
            foreach (var pair in named) map[pair.Key] = pair.Value;
            return map;
        }

        // { mode: [code per key of the mode] }; an unknown code keeps the key of the mode ("") and a mode with nothing of its own is left out
        internal static JObject NormalizeBongoKeys(JToken token)
        {
            var result = new JObject();
            if (!(token is JObject map)) return result;
            foreach (var mode in BongoModes)
            {
                if (!(map[mode] is JArray list)) continue;
                var keys = list.Take(32).Select(k => k.Type == JTokenType.String && BongoKeyCodes.ContainsKey(k.Value<string>()) ? k.Value<string>() : "").ToList();
                while (keys.Count > 0 && keys[keys.Count - 1] == "") keys.RemoveAt(keys.Count - 1);
                if (keys.Count > 0) result[mode] = new JArray(keys);
            }
            return result;
        }

        // both forms of the bindings for each mode: the virtual keys to read and the codes for drawing their labels
        internal static JObject BongoSourceBindings(JObject settings)
        {
            var bindings = new JObject();
            foreach (var entry in NormalizeBongoKeys(settings?["bongoKeys"]).Properties())
            {
                var codes = (JArray)entry.Value;
                bindings[entry.Name] = new JObject
                {
                    ["keys"] = string.Join(",", codes.Values<string>().Select(code => BongoKeyCodes.TryGetValue(code, out int vk) ? vk : 0)),
                    ["key_codes"] = string.Join(",", codes.Values<string>()),
                };
            }
            return bindings;
        }

        internal static bool BongoSettingsChanged(JObject previous, JObject settings) =>
            new[] { "bongoMode", "bongoKeys", "bongoLive2d", "bongoTrack", "bongoBreath", "bongoBlink", "bongoFaces", "bongoRelativeMouse", "bongoSpeed" }
                .Any(key => !JToken.DeepEquals(previous?[key], settings?[key]));

        internal static bool InputOverlayLayoutChanged(JObject previous, JObject settings) =>
            new[] { "inputOverlayKeyboard", "inputOverlayMouse", "inputOverlayCustomKeys" }.Any(key => !JToken.DeepEquals(previous?[key], settings?[key]));

        internal static bool IsBongoInputKind(string id) => id == BongoInputKind || id == LegacyBongoInputKind;

        internal static void SetBongoSourceJson(JObject source, string uuid, JObject settings)
        {
            OverlayScene.SetSourceFields(source, OverlayScene.BongoSourceName, uuid, BongoInputKind, BongoInputKind, null, true);

            var sourceSettings = source["settings"] as JObject;
            if (sourceSettings == null) { sourceSettings = new JObject(); source["settings"] = sourceSettings; }
            string mode = settings?["bongoMode"]?.Value<string>();
            if (!BongoModes.Contains(mode)) mode = "standard";
            sourceSettings["mode"] = mode;
            sourceSettings["Mode"] = mode; sourceSettings["width"] = (int)OverlayScene.BongoSourceWidth; sourceSettings["height"] = (int)OverlayScene.BongoSourceHeight;
            sourceSettings["x"] = 0.0; sourceSettings["y"] = 0.02; sourceSettings["scale"] = 1.83;
            double speed = settings?["bongoSpeed"]?.Value<double>() ?? 1.0;
            sourceSettings["delay"] = speed; sourceSettings["delaytime"] = speed;
            sourceSettings["random_motion"] = true;
            sourceSettings["breath"] = settings?["bongoBreath"]?.Value<bool>() ?? true;
            sourceSettings["eyeblink"] = settings?["bongoBlink"]?.Value<bool>() ?? true;
            sourceSettings["track"] = settings?["bongoTrack"]?.Value<bool>() ?? true;
            sourceSettings["live2d"] = settings?["bongoLive2d"]?.Value<bool>() ?? true;
            sourceSettings["relative_mouse"] = settings?["bongoRelativeMouse"]?.Value<bool>() ?? true;
            sourceSettings["mouse_horizontal_flip"] = true; sourceSettings["mouse_vertical_flip"] = true;
            sourceSettings["mask"] = settings?["bongoFaces"]?.Value<bool>() ?? false;
            sourceSettings["key_bindings"] = BongoSourceBindings(settings);
        }

        internal static JObject NewBongoSourceJson(string uuid, JObject settings)
        {
            var source = new JObject();
            SetBongoSourceJson(source, uuid, settings);
            return source;
        }

        // whether one kind of capture shows the mouse cursor; on when the setting is not there
        internal static bool CursorSetting(JObject settings, string key) => settings?[key]?.Value<bool>() ?? true;

        internal static JObject GetGameCaptureInputSettings(JObject settings) => new JObject
        {
            ["capture_audio"] = false, ["hook_rate"] = 2, ["limit_framerate"] = true, ["capture_cursor"] = CursorSetting(settings, "showCursorGameCapture"),
            ["capture_overlays"] = false, ["anti_cheat_hook"] = true, ["capture_mode"] = "any_fullscreen", ["window"] = "",
        };

        internal static JObject GetWindowCaptureInputSettings(JObject settings) => new JObject
        {
            ["window"] = settings?["screenshareGameWindow"]?.Value<string>() ?? "",
            ["method"] = 2, ["priority"] = 0, ["cursor"] = CursorSetting(settings, "showCursorWindowCapture"), ["client_area"] = true,
            ["compatibility"] = false, ["force_sdr"] = false, ["capture_audio"] = false,
        };

        internal static void SetWindowCaptureSourceJson(JObject source, string uuid, JObject settings)
        {
            OverlayScene.SetSourceFields(source, "Window Capture", uuid, "window_capture", "window_capture", null, true);
            source["settings"] = GetWindowCaptureInputSettings(settings);
        }

        internal static JObject NewWindowCaptureSourceJson(string uuid, JObject settings)
        {
            var source = new JObject();
            SetWindowCaptureSourceJson(source, uuid, settings);
            return source;
        }

        internal static void SetInputOverlaySourceJson(JObject source, string name, string uuid, string image, string layout)
        {
            if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(layout))
                throw new InvalidOperationException("Input overlay assets are missing for " + name + ".");
            OverlayScene.SetSourceFields(source, name, uuid, "input-overlay", "input-overlay", null, true);

            var sourceSettings = new JObject
            {
                ["io.input_source"] = "This computer",
                ["io.overlay_image"] = image.Replace('\\', '/'),
                ["io.layout_file"] = layout.Replace('\\', '/'),
            };
            source["settings"] = sourceSettings;
        }

        internal static JObject NewInputOverlaySourceJson(string name, string uuid, string image, string layout)
        {
            var source = new JObject();
            SetInputOverlaySourceJson(source, name, uuid, image, layout);
            return source;
        }

        internal static void SetSceneItemBaseJson(JObject item, string name, bool visible)
        {
            item["name"] = name; item["visible"] = visible; item["locked"] = false;
            item["rot"] = 0.0; item["align"] = 5;
            item["bounds_type"] = 0; item["bounds_align"] = 0; item["bounds_crop"] = false;
            item["crop_left"] = 0; item["crop_top"] = 0; item["crop_right"] = 0; item["crop_bottom"] = 0;
            item["bounds"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["bounds_rel"] = new JObject { ["x"] = 0.0, ["y"] = 0.0 };
            item["scale_filter"] = "disable"; item["blend_method"] = "default"; item["blend_type"] = "normal";
            item["private_settings"] = new JObject();
        }

        // fit-to-canvas item builder shared by Display/Game/Window Capture.
        internal static void SetMainCaptureSceneItemJson(JObject item, string name, JObject preset)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }
            bool visible = item["visible"]?.Value<bool>() ?? true;
            var transform = MainCaptureTransform(name, preset);
            SetSceneItemBaseJson(item, name, visible);
            double srcW = transform["sourceWidth"].Value<double>(), srcH = transform["sourceHeight"].Value<double>();
            double posX = transform["positionX"].Value<double>(), posY = transform["positionY"].Value<double>();
            item["scale_ref"] = new JObject { ["x"] = srcW, ["y"] = srcH };
            item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
            item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = transform["scaleX"].Value<double>(), ["y"] = transform["scaleY"].Value<double>() };
            item["scale_rel"] = new JObject { ["x"] = 1.0, ["y"] = 1.0 };
            item["group_item_backup"] = false;
            if (transform["boundsType"]?.Value<string>() == "OBS_BOUNDS_SCALE_INNER")
            {
                item["bounds_type"] = 2;
                item["bounds"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
                item["bounds_rel"] = new JObject { ["x"] = 2.0 * canvasW / canvasH, ["y"] = 2.0 };
            }
        }

        internal static void SetWindowCaptureSceneItemJson(JObject item, string uuid, JObject preset, bool visible)
        {
            SetMainCaptureSceneItemJson(item, "Window Capture", preset);
            item["source_uuid"] = uuid;
            item["visible"] = visible;
        }

        internal static void SetBongoSceneItemJson(JObject item, string uuid, JObject preset, bool visible, JObject settings = null)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            var transform = BongoTransform(preset, settings);
            SetSceneItemBaseJson(item, OverlayScene.BongoSourceName, visible);
            item["rot"] = transform["rotation"];
            item["source_uuid"] = uuid;
            item["scale_ref"] = new JObject { ["x"] = OverlayScene.BongoSourceWidth, ["y"] = OverlayScene.BongoSourceHeight };
            double posX = transform["positionX"].Value<double>(), posY = transform["positionY"].Value<double>();
            double scaleX = transform["scaleX"].Value<double>(), scaleY = transform["scaleY"].Value<double>();
            item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
            item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX * OverlayScene.BongoSourceHeight / canvasH, ["y"] = scaleY * OverlayScene.BongoSourceHeight / canvasH };
            item["group_item_backup"] = false;
        }

        internal static JObject NewJsonTransition(int duration) => new JObject { ["duration"] = duration };

        internal static void SetInputOverlaySceneItemJson(JObject item, string name, JObject preset, bool visible, string sourceUuid = "", bool groupBackup = false, JObject settings = null)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            var transform = InputOverlayTransform(name, preset, settings);
            var files = InputOverlayMemberFiles(name, settings);
            double sourceW = files?.Width ?? (name == "Mouse Overlay" ? InputOverlayLayout.DefaultMouseWidth : InputOverlayLayout.DefaultKeyboardWidth);
            double sourceH = files?.Height ?? (name == "Mouse Overlay" ? InputOverlayLayout.DefaultMouseHeight : InputOverlayLayout.DefaultKeyboardHeight);
            SetSceneItemBaseJson(item, name, visible);
            if (!string.IsNullOrWhiteSpace(sourceUuid)) item["source_uuid"] = sourceUuid;
            item["scale_ref"] = new JObject { ["x"] = sourceW, ["y"] = sourceH };
            double posX = transform["positionX"].Value<double>(), posY = transform["positionY"].Value<double>();
            double scaleX = transform["scaleX"].Value<double>(), scaleY = transform["scaleY"].Value<double>();
            item["pos"] = new JObject { ["x"] = posX, ["y"] = posY };
            item["pos_rel"] = new JObject { ["x"] = (posX - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (posY - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX * 1440.0 / canvasH, ["y"] = scaleY * 1440.0 / canvasH };
            item["group_item_backup"] = groupBackup;
        }

        internal static void SetInputOverlayGroupMemberJson(JObject item, string name, string sourceUuid, JObject preset, int id, JObject overlaySettings)
        {
            const double canvasW = 2560.0, canvasH = 1440.0;
            var fit = InputOverlayMemberFit(name, overlaySettings);
            double x = fit.X, y = fit.Y, scaleX = fit.Scale, scaleY = fit.Scale;
            SetSceneItemBaseJson(item, name, name != "Mouse Overlay" || InputOverlayMouseShown(overlaySettings));
            item["source_uuid"] = sourceUuid;
            item["scale_ref"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
            item["id"] = id;
            item["group_item_backup"] = false;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = new JObject { ["x"] = (x - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (y - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["scale_rel"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            item["show_transition"] = NewJsonTransition(300);
            item["hide_transition"] = NewJsonTransition(300);
        }

        // WASD Overlay / Mouse Overlay -> source uuid + the scene-item id it ends up at once placed. Id starts at 0 (unknown) and gets patched in by SetOverlaySceneFile once the corresponding scene item is written -- a plain mutable field, not a tuple, since that patch-after-construction is exactly what a tuple cant do.
        internal sealed class InputOverlaySourceRef
        {
            public string Uuid;
            public int Id;
        }

        // builds the group-kind source that wraps WASD+Mouse together, with its own nested items array using canvas-relative 2560x1440 reference geometry independent of the real canvas size. inputSources maps "WASD Overlay"/"Mouse Overlay" -> {uuid, id} for whichever of the two are actually present.
        internal static void SetInputOverlayGroupSourceJson(JObject group, string groupUuid, Dictionary<string, InputOverlaySourceRef> inputSources, JObject preset, JObject overlaySettings)
        {
            group["prev_ver"] = 536936450; group["name"] = "Group"; group["uuid"] = groupUuid;
            group["id"] = "group"; group["versioned_id"] = "group";

            InputOverlayLayouts.GroupSize(overlaySettings, out double groupW, out double groupH);
            var settings = new JObject { ["id_counter"] = 0, ["custom_size"] = true, ["cx"] = (int)Math.Round(groupW), ["cy"] = (int)Math.Round(groupH) };
            var items = new JArray();
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
            {
                if (!inputSources.TryGetValue(name, out var src)) continue;
                var member = new JObject();
                SetInputOverlayGroupMemberJson(member, name, src.Uuid, preset, src.Id, overlaySettings);
                items.Add(member);
            }
            settings["items"] = items;
            group["settings"] = settings;

            group["mixers"] = 0; group["sync"] = 0; group["flags"] = 0; group["volume"] = 1.0; group["balance"] = 0.5;
            group["enabled"] = true; group["muted"] = false;
            group["push-to-mute"] = false; group["push-to-mute-delay"] = 0; group["push-to-talk"] = false; group["push-to-talk-delay"] = 0;
            var hotkeys = new JObject();
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
            {
                if (!inputSources.TryGetValue(name, out var src)) continue;
                hotkeys["libobs.show_scene_item." + src.Id] = new JArray();
                hotkeys["libobs.hide_scene_item." + src.Id] = new JArray();
            }
            group["hotkeys"] = hotkeys;
            group["deinterlace_mode"] = 0; group["deinterlace_field_order"] = 0; group["monitoring_type"] = 0; group["monitoring_enabled"] = false;
            group["canvas_uuid"] = "6c69626f-6273-4c00-9d88-c5136d61696e";
            group["private_settings"] = new JObject();
        }

        internal static void SetInputOverlayGroupSceneItemJson(JObject item, string groupUuid, JObject preset, bool visible, JObject settings = null)
        {
            double canvasW = preset["video"]?["baseWidth"]?.Value<double>() ?? 0.0;
            double canvasH = preset["video"]?["baseHeight"]?.Value<double>() ?? 0.0;
            var transform = InputOverlayGroupTransform(preset, settings);
            double scaleX = transform["scaleX"].Value<double>(), scaleY = transform["scaleY"].Value<double>();
            double x = transform["positionX"].Value<double>(), y = transform["positionY"].Value<double>();
            SetSceneItemBaseJson(item, "Group", visible);
            item["rot"] = transform["rotation"];
            item["source_uuid"] = groupUuid;
            item["scale_ref"] = new JObject { ["x"] = canvasW, ["y"] = canvasH };
            item["group_item_backup"] = false;
            item["pos"] = new JObject { ["x"] = x, ["y"] = y };
            item["pos_rel"] = new JObject { ["x"] = (x - canvasW / 2.0) / (canvasH / 2.0), ["y"] = (y - canvasH / 2.0) / (canvasH / 2.0) };
            item["scale"] = new JObject { ["x"] = scaleX, ["y"] = scaleY };
            double scaleRel = OverlayScaleFactor(settings);
            item["scale_rel"] = new JObject { ["x"] = OverlayFlipHorizontal(settings) ? -scaleRel : scaleRel, ["y"] = scaleRel };
            item["show_transition"] = NewJsonTransition(0);
            item["hide_transition"] = NewJsonTransition(0);
            item["private_settings"] = new JObject { ["collapsed"] = false };
        }

        internal static int GetNextJsonSceneItemId(JArray items)
        {
            int max = 0;
            if (items == null) return 1;
            foreach (var item in items)
            {
                var idToken = item["id"];
                if (idToken == null) continue;
                try { int id = idToken.Value<int>(); if (id > max) max = id; } catch (FormatException) { } catch (InvalidCastException) { }
            }
            return max + 1;
        }

        // moves the named item to just after another named item in the scenes item list -- used to keep Window Capture positioned right after Display Capture when inserting it into an existing item list.
        internal static void MoveJsonSceneItemAfter(JArray items, string targetName, string afterName)
        {
            if (items == null) return;
            JObject target = null;
            foreach (var item in items)
            {
                if (item["name"]?.Value<string>() == targetName) { target = (JObject)item; break; }
            }
            if (target == null) return;
            items.Remove(target);
            int insertAt = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i]["name"]?.Value<string>() == afterName) { insertAt = i + 1; break; }
            }
            items.Insert(insertAt, target);
        }

        // -- motion blur filter (Display/Game/Window Capture) --

        internal static string MotionBlurFilterUuid(string sourceName) =>
            sourceName != null && OverlayScene.MotionBlurFilterUuids.TryGetValue(sourceName, out string uuid) ? uuid : "";

        internal static string GetObsInstallRoot()
        {
            string obs = Constants.ResolveObsExe();
            try
            {
                return Directory.GetParent(Directory.GetParent(Directory.GetParent(obs).FullName).FullName).FullName;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is DirectoryNotFoundException || ex is NullReferenceException)
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                return !string.IsNullOrEmpty(programFiles) ? Path.Combine(programFiles, "obs-studio") : "C:\\Program Files\\obs-studio";
            }
        }

        internal static string ShaderfilterMotionBlurPath() =>
            Path.Combine(GetObsInstallRoot(), "data\\obs-plugins\\obs-shaderfilter\\examples\\motion_blur.shader").Replace('\\', '/');

        internal static double MotionBlurStrength(JObject settings)
        {
            double value = settings["motionBlurStrength"]?.Value<double>() ?? 0.0;
            return value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
        }

        internal static JObject MotionBlurFilterSettingsLive(double strength) => OverlayScene.MotionBlurFilterSettings(ShaderfilterMotionBlurPath(), strength);

        internal static void SetMotionBlurFilterJson(JObject filter, string sourceName, bool enabled, double strength)
        {
            string uuid = MotionBlurFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("Unsupported motion blur source: " + sourceName);
            OverlayScene.SetSourceFields(filter, OverlayScene.MotionBlurFilterName, uuid, OverlayScene.MotionBlurFilterKind, OverlayScene.MotionBlurFilterKind,
                MotionBlurFilterSettingsLive(strength), enabled);
        }

        internal static JObject NewMotionBlurFilterJson(string sourceName, bool enabled, double strength)
        {
            var filter = new JObject();
            SetMotionBlurFilterJson(filter, sourceName, enabled, strength);
            return filter;
        }

        internal static bool IsShaderfilterMotionBlurJson(JObject filter)
        {
            if (filter["id"]?.Value<string>() != OverlayScene.MotionBlurFilterKind) return false;
            var filterSettings = filter["settings"];
            if (filterSettings == null) return false;
            string shaderFile = filterSettings["shader_file_name"]?.Value<string>() ?? "";
            return shaderFile.Replace('\\', '/').ToLowerInvariant().EndsWith("/motion_blur.shader");
        }

        internal static bool IsManagedMotionBlurJson(JObject filter, string uuid)
        {
            string name = filter["name"]?.Value<string>() ?? "";
            string filterUuid = filter["uuid"]?.Value<string>() ?? "";
            string filterId = filter["id"]?.Value<string>() ?? "";
            return name == OverlayScene.MotionBlurFilterName || filterUuid == uuid || filterId == OverlayScene.RetiredMotionBlurFilterKind || IsShaderfilterMotionBlurJson(filter);
        }

        internal static void SetMotionBlurSourceJson(JObject source, bool enabled, double strength)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string uuid = MotionBlurFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) return;

            var filters = source["filters"] as JArray ?? new JArray();
            source["filters"] = filters;

            foreach (var filter in filters)
            {
                if (IsManagedMotionBlurJson((JObject)filter, uuid))
                {
                    SetMotionBlurFilterJson((JObject)filter, sourceName, enabled, strength);
                    return;
                }
            }
            filters.Add(NewMotionBlurFilterJson(sourceName, enabled, strength));
        }

        internal static void RemoveMotionBlurSourceJson(JObject source)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string uuid = MotionBlurFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) return;

            var filters = source["filters"] as JArray;
            if (filters == null) return;
            for (int i = filters.Count - 1; i >= 0; i--)
            {
                if (IsManagedMotionBlurJson((JObject)filters[i], uuid)) filters.RemoveAt(i);
            }
            source["filters"] = filters;
        }

        // -- overlay opacity/color filter (WASD/Mouse/Bongo Cat) --

        internal static string OverlayOpacityFilterUuid(string sourceName) =>
            sourceName != null && OverlayScene.OpacityFilterUuids.TryGetValue(sourceName, out string uuid) ? uuid : "";

        internal static JObject OverlayOpacityFilterSettingsLive(int opacityPercent, bool legacyPercent, JObject settings = null) =>
            OverlayScene.OpacityFilterSettings(opacityPercent, OverlayHueShiftValue(settings),
                OverlayColorValue(OverlayHexColor(settings, "overlayColorMultiply", "#ffffff"), "#ffffff"),
                OverlayColorValue(OverlayHexColor(settings, "overlayColorAdd", "#000000"), "#000000"), legacyPercent);

        internal static bool OverlayOpacityFilterUsesLegacyPercent(string kind, JObject settings = null)
        {
            if (kind == "color_filter_v2") return false;
            if (kind == "color_filter") return true;
            if (settings != null)
            {
                if (settings["versioned_id"]?.Value<string>() == "color_filter_v2") return false;
                var raw = settings["opacity"];
                if (raw != null)
                {
                    try { if (raw.Value<double>() > 1.0) return true; } catch (FormatException) { } catch (InvalidCastException) { }
                }
            }
            return false;
        }

        internal static void SetOverlayOpacityFilterJson(JObject filter, string sourceName, int opacityPercent, JObject settings = null)
        {
            string uuid = OverlayOpacityFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("Unsupported overlay opacity source: " + sourceName);
            OverlayScene.SetSourceFields(filter, OverlayScene.OpacityFilterName, uuid, OverlayScene.OpacityFilterKind, OverlayScene.OpacityFilterVersionedKind,
                OverlayOpacityFilterSettingsLive(opacityPercent, false, settings), true);
        }

        internal static JObject NewOverlayOpacityFilterJson(string sourceName, int opacityPercent, JObject settings = null)
        {
            var filter = new JObject();
            SetOverlayOpacityFilterJson(filter, sourceName, opacityPercent, settings);
            return filter;
        }

        internal static bool IsManagedOverlayOpacityJson(JObject filter, string uuid)
        {
            string name = filter["name"]?.Value<string>() ?? "";
            string filterUuid = filter["uuid"]?.Value<string>() ?? "";
            return name == "ReplayKit Overlay Opacity" || (!string.IsNullOrWhiteSpace(uuid) && filterUuid == uuid);
        }

        internal static void SetOverlayOpacitySourceJson(JObject source, JObject settings)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string uuid = OverlayOpacityFilterUuid(sourceName);
            if (string.IsNullOrWhiteSpace(uuid)) return;
            int opacity = OverlayOpacityValue(settings);
            bool hasColorAdjustments = HasOverlayColorAdjustments(settings);

            var filters = source["filters"] as JArray ?? new JArray();
            JObject managedFilter = null;
            var keptFilters = new JArray();
            foreach (var filterToken in filters)
            {
                var filter = (JObject)filterToken;
                string name = filter["name"]?.Value<string>() ?? "";
                string filterUuid = filter["uuid"]?.Value<string>() ?? "";
                if (name != "ReplayKit Overlay Opacity" && (string.IsNullOrWhiteSpace(uuid) || filterUuid != uuid))
                {
                    keptFilters.Add(filter);
                    continue;
                }
                if (managedFilter == null) { managedFilter = filter; keptFilters.Add(filter); }
            }
            filters = keptFilters;
            if (managedFilter != null)
            {
                var filterSettings = managedFilter["settings"] as JObject ?? new JObject();
                string id = managedFilter["id"]?.Value<string>() ?? "";
                string versionedId = managedFilter["versioned_id"]?.Value<string>() ?? "";
                bool legacyPercent = id == "color_filter" && versionedId != "color_filter_v2";
                var opacitySettings = OverlayOpacityFilterSettingsLive(opacity, legacyPercent, settings);
                foreach (var kv in opacitySettings) filterSettings[kv.Key] = kv.Value;
                managedFilter["settings"] = filterSettings;
                managedFilter["enabled"] = true;
            }
            else if (opacity < 100 || hasColorAdjustments)
            {
                filters.Add(NewOverlayOpacityFilterJson(sourceName, opacity, settings));
            }
            if (filters.Count > 0) source["filters"] = filters;
            else if (source["filters"] != null) source.Remove("filters");
        }

    }
}
