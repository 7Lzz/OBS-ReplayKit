using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // the look of the setup and uninstall windows: the theme stylesheet the helper keeps in the dock folder goes into the page, and the window frame takes its colours from the same tokens, so they match the rest of replaykit; with no stylesheet (a first install) or the default theme the page keeps its own :root and the frame these defaults
    internal sealed class WindowTheme
    {
        // the page sits in an elevated installer, and the helper never writes markup, at-rules or urls into the sheet, so one that has any is not used
        private static readonly Regex Unsafe = new Regex(@"<|@|url\s*\(", RegexOptions.IgnoreCase);
        private const int MaxSheetBytes = 64 * 1024;

        public string Css { get; private set; } = "";
        public Color Window { get; private set; } = Color.FromArgb(0x1D, 0x1F, 0x26);
        public Color Panel { get; private set; } = Color.FromArgb(0x27, 0x2A, 0x33);
        public Color Border { get; private set; } = Color.FromArgb(0x3C, 0x40, 0x4D);
        public Color Text { get; private set; } = Color.FromArgb(0xFF, 0xFF, 0xFF);
        public bool Dark { get; private set; } = true;

        public static WindowTheme Load()
        {
            var theme = new WindowTheme();
            string path = Path.Combine(Config.DOCK_TARGET, ThemeSheet.FileName);
            string css;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return theme;
                if (file.Length > MaxSheetBytes) { SetupLog.Line("theme sheet ignored: larger than " + MaxSheetBytes + " bytes"); return theme; }
                css = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SetupLog.Line("theme sheet unreadable, using the default look: " + ex.Message);
                return theme;
            }
            // the default theme writes a comment only
            if (css.IndexOf(":root{", StringComparison.Ordinal) < 0) return theme;
            if (Unsafe.IsMatch(css)) { SetupLog.Line("theme sheet ignored: it holds markup, an at-rule or a url"); return theme; }

            theme.Css = css;
            theme.Window = Token(css, "--window-solid", theme.Window);
            theme.Panel = Token(css, "--panel-solid", theme.Panel);
            theme.Border = Token(css, "--line", theme.Border);
            theme.Text = Token(css, "--text", theme.Text);
            theme.Dark = !Regex.IsMatch(css, @"color-scheme:\s*light", RegexOptions.IgnoreCase);
            return theme;
        }

        // splice the sheet in right before </head>, where the helper puts it in the pages it serves, so it wins over the page :root
        public string ApplyTo(string html)
        {
            if (Css.Length == 0) return html;
            int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (head < 0) return html;
            return html.Insert(head, "<style id=\"rk-theme\">" + Css + "</style>");
        }

        // win32 COLORREF, 0x00bbggrr
        public static int ColorRef(Color c) => (c.B << 16) | (c.G << 8) | c.R;

        private static Color Token(string css, string name, Color fallback)
        {
            Match m = Regex.Match(css, @"(?<![\w-])" + Regex.Escape(name) + @":\s*#([0-9A-Fa-f]{6})\s*[;}]");
            if (!m.Success) return fallback;
            int rgb = Convert.ToInt32(m.Groups[1].Value, 16);
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
    }
}
