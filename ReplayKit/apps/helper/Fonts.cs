using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReplayKitHelper
{
    // the fonts replaykit offers, one list for the subtitles of the editor and for the font of the whole interface; a font windows does not have ships in obs-replayKit\fonts
    internal static class Fonts
    {
        // in the order the pickers show them
        public static readonly string[] Families = { "Arial", "Bahnschrift", "Comic Sans MS", "Consolas", "Courier New", "Fredoka One", "Georgia", "Impact", "Segoe UI", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana" };

        private static readonly Dictionary<string, string> Bundled = new Dictionary<string, string> { ["Fredoka One"] = "FredokaOne-Regular.ttf" };

        public static bool IsKnown(string family) => family != null && Array.IndexOf(Families, family) >= 0;

        // the file replaykit ships for a family, or null for a font of windows
        public static string BundledFile(string family) => family != null && Bundled.TryGetValue(family, out string file) ? file : null;

        public static string BundledPath(string family)
        {
            string file = BundledFile(family);
            return file == null ? null : Path.Combine(Constants.APP_FONTS_DIR, file);
        }

        // the plugin hands this to qt as is, so it takes forward slashes and carries no json escapes; empty for a font of windows
        public static string NativePath(string family) => BundledPath(family)?.Replace('\\', '/') ?? "";

        // the @font-face of every shipped font, served by the /fonts route
        public static string FaceRules()
        {
            var sb = new StringBuilder();
            foreach (var pair in Bundled)
                sb.Append("@font-face{font-family:\"").Append(pair.Key).Append("\";src:url(/fonts/").Append(pair.Value).Append(") format(\"truetype\");font-display:block;}");
            return sb.ToString();
        }

        // the family of the interface under both names the pages read it by: --font in settings and the popups, --font_family in clips, home, the editor and the controls dock; the font of windows stands behind it for any letter the family lacks
        public static string InterfaceRules(string family)
        {
            if (!IsKnown(family)) throw new ArgumentException("Unknown interface font: " + family);
            string stack = "\"" + family + "\",system-ui,sans-serif";
            return ":root{--font:" + stack + ";--font_family:" + stack + ";}";
        }
    }
}
