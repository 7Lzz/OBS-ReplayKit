using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    // the overlay and scene rules both the installer (Transform.cs) and the helper (ReplaykitSettings.cs) write into the obs scene collection: the managed names and uuids, the overlay clamps and geometry, and the json of the managed filters and sources. each side still reads its own inputs (Preferences or the settings json) and passes plain values in.
    public static class OverlayScene
    {
        public const string BongoSourceName = "Bongo Cat Overlay";
        public const string BongoSourceKind = "replaykit-bongo";
        // the opengl plugin older installs used, still matched so its leftovers get converted or dropped
        public const string LegacyBongoSourceKind = "bongobs-cat";
        public const double BongoSourceWidth = 1280.0;
        public const double BongoSourceHeight = 768.0;
        public const string WasdSourceName = "WASD Overlay";
        public const string MouseSourceName = "Mouse Overlay";
        public const string OpacityFilterName = "ReplayKit Overlay Opacity";
        public const string OpacityFilterKind = "color_filter";
        public const string OpacityFilterVersionedKind = "color_filter_v2";
        public const string MotionBlurFilterName = "ReplayKit Motion Blur";
        public const string MotionBlurFilterKind = "shader_filter";
        // the blur the motion blur filter used before obs-shaderfilter, still matched so it gets replaced
        public const string RetiredMotionBlurFilterKind = "obs_composite_blur";

        // the bongo cat is sized against a 1080 high content area, the input overlay group against a 1440 high one, and the input overlay keeps a 15 px left and 16 px bottom margin at 1080
        private const double BongoRefHeight = 1080.0;
        private const double BongoScaleX = 0.4892578125;
        private const double BongoScaleY = 0.48828125;
        private const double InputOverlayGroupRefHeight = 1440.0;
        private const double CornerRefHeight = 1080.0;
        private const double CornerLeft = 15.0;
        private const double CornerBottom = 16.0;

        // the fixed uuid of the managed opacity filter on each overlay source
        public static readonly IReadOnlyDictionary<string, string> OpacityFilterUuids = new Dictionary<string, string>
        {
            [WasdSourceName] = "a65fb4f0-a894-463e-9b9b-f0a9d5fb4fa1",
            [MouseSourceName] = "c097fe72-641f-4da5-94f6-71f7c6353f9f",
            [BongoSourceName] = "4ecb70c4-e8f0-4207-a2cc-0307ff771722",
        };

        // the fixed uuid of the managed motion blur filter on each capture source
        public static readonly IReadOnlyDictionary<string, string> MotionBlurFilterUuids = new Dictionary<string, string>
        {
            ["Display Capture"] = "e371efc8-8c99-44cb-95e7-94381d9c9e41",
            ["Game Capture"] = "26bc5a11-5315-4390-b028-f77667c7fda3",
            ["Window Capture"] = "9b73d6cb-b65e-44a1-895f-4e2f326a8d77",
        };

        public static int Opacity(int percent) => percent < 0 ? 0 : percent > 100 ? 100 : percent;

        public static double HueShift(double degrees) => degrees < -180.0 ? -180.0 : degrees > 180.0 ? 180.0 : degrees;

        public static double ScaleFactor(int percent) => (percent < 50 ? 50 : percent > 200 ? 200 : percent) / 100.0;

        // a #rrggbb colour in lower case, or the fallback for anything else
        public static string HexColor(string raw, string fallback)
        {
            string value = (raw ?? "").Trim().ToLowerInvariant();
            return value.Length == 7 && value[0] == '#' && value.Substring(1).All(ch => "0123456789abcdef".IndexOf(ch) >= 0) ? value : fallback;
        }

        // obs keeps a colour as 0xBBGGRR
        public static int ColorValue(string hex, string fallback)
        {
            if (!Regex.IsMatch(hex ?? "", "^#[0-9a-fA-F]{6}$")) hex = fallback;
            int red = Convert.ToInt32(hex.Substring(1, 2), 16);
            int green = Convert.ToInt32(hex.Substring(3, 2), 16);
            int blue = Convert.ToInt32(hex.Substring(5, 2), 16);
            return red | (green << 8) | (blue << 16);
        }

        // takes the already clamped and normalized values
        public static bool HasColorAdjustments(double hueShift, string multiply, string add) =>
            Math.Abs(hueShift) >= 0.001 || multiply != "#ffffff" || add != "#000000";

        public struct ContentRect
        {
            public double X, Y, Width, Height, Scale, CanvasWidth, CanvasHeight, SourceWidth, SourceHeight;
        }

        // where the capture shows inside the canvas, letterboxed to its own aspect; a missing canvas counts as 1920x1080 and a missing capture size as the canvas
        public static ContentRect Content(double canvasW, double canvasH, double sourceW, double sourceH)
        {
            if (canvasW <= 0.0 || canvasH <= 0.0) { canvasW = 1920.0; canvasH = 1080.0; }
            sourceW = sourceW > 0.0 ? sourceW : canvasW;
            sourceH = sourceH > 0.0 ? sourceH : canvasH;
            double scale = Math.Min(canvasW / sourceW, canvasH / sourceH);
            if (scale <= 0.0) scale = 1.0;
            double width = sourceW * scale;
            double height = sourceH * scale;
            return new ContentRect
            {
                X = (canvasW - width) / 2.0, Y = (canvasH - height) / 2.0, Width = width, Height = height, Scale = scale,
                CanvasWidth = canvasW, CanvasHeight = canvasH, SourceWidth = sourceW, SourceHeight = sourceH,
            };
        }

        // the bottom-left corner of the content area inside the input overlay margin, never off the canvas
        public static void InputOverlayCorner(ContentRect content, double sourceH, double scaleY, out double x, out double y)
        {
            double refScale = content.Height / CornerRefHeight;
            x = content.X + CornerLeft * refScale;
            y = content.Y + content.Height - sourceH * scaleY - CornerBottom * refScale;
            x = Math.Max(0.0, x);
            y = Math.Max(0.0, y);
        }

        // the bottom-left corner of the content area, never off the canvas
        public static void BottomLeftCorner(ContentRect content, double sourceH, double scaleY, out double x, out double y)
        {
            x = content.X;
            y = content.Y + content.Height - sourceH * scaleY;
            x = Math.Max(0.0, x);
            y = Math.Max(0.0, y);
        }

        public static double InputOverlayGroupScale(ContentRect content, double scaleFactor) => content.Height / InputOverlayGroupRefHeight * scaleFactor;

        public static void BongoScale(ContentRect content, double scaleFactor, out double scaleX, out double scaleY)
        {
            double ratio = content.Height / BongoRefHeight * scaleFactor;
            scaleX = BongoScaleX * ratio;
            scaleY = BongoScaleY * ratio;
        }

        // the settings of the managed colour filter; filters made before color_filter_v2 keep opacity as a percent and only one colour
        public static JObject OpacityFilterSettings(int opacityPercent, double hueShift, int multiply, int add, bool legacyPercent)
        {
            var settings = new JObject { ["hue_shift"] = hueShift };
            if (legacyPercent)
            {
                settings["opacity"] = Opacity(opacityPercent);
                settings["color"] = multiply;
                return settings;
            }
            settings["opacity"] = Math.Max(0.0, Math.Min(1.0, opacityPercent / 100.0));
            settings["color_multiply"] = multiply;
            settings["color_add"] = add;
            return settings;
        }

        public static JObject MotionBlurFilterSettings(string shaderPath, double strength) => new JObject
        {
            ["from_file"] = true,
            ["shader_file_name"] = shaderPath,
            ["override_entire_effect"] = false,
            ["strength"] = Math.Max(0.0, Math.Min(1.0, strength)),
        };

        // the fields obs saves for every source and filter, in the order obs writes them; settings goes right after the ids when given, and a caller that keeps an existing settings object passes null and sets it itself
        public static void SetSourceFields(JObject source, string name, string uuid, string kind, string versionedKind, JObject settings, bool enabled)
        {
            source["prev_ver"] = 536936450;
            source["name"] = name;
            source["uuid"] = uuid;
            source["id"] = kind;
            source["versioned_id"] = versionedKind;
            if (settings != null) source["settings"] = settings;
            source["mixers"] = 0;
            source["sync"] = 0;
            source["flags"] = 0;
            source["volume"] = 1.0;
            source["balance"] = 0.5;
            source["enabled"] = enabled;
            source["muted"] = false;
            source["push-to-mute"] = false;
            source["push-to-mute-delay"] = 0;
            source["push-to-talk"] = false;
            source["push-to-talk-delay"] = 0;
            source["hotkeys"] = new JObject();
            source["deinterlace_mode"] = 0;
            source["deinterlace_field_order"] = 0;
            source["monitoring_type"] = 0;
            source["monitoring_enabled"] = false;
            source["private_settings"] = new JObject();
        }
    }
}
