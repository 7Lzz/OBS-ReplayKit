using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // theming for the whole surface: the ReplayKit dock pages (a :root override injected when the helper serves the
    // html), ReplayKit's native window chrome (Native.StyleWindow), and OBS itself (a generated Yami .ovt variant +
    // user.ini [Appearance] Theme=, applied on the next OBS start). one small token model feeds all three.
    internal static class Themes
    {
        // the 15 colours every surface maps from. all "#rrggbb".
        public sealed class Tokens
        {
            public string Bg, Panel, Field, Field2, Border, BorderStrong,
                          Text, Muted, Disabled, Accent, Accent2, Danger, Success, Warning,
                          Gradient;
            public bool Dark = true;

            // a gradient per surface token, keyed by the swatch name the editor uses. only these four: text, border
            // and danger are colour roles -- they end up in border-color, color and the Mix/Shift/ToColorRef maths,
            // where a gradient value paints nothing at all. the colour field above always stays a real hex so every
            // derived token, the win32 chrome and the qt theme keep working; the gradient is an extra paint layer.
            public static readonly string[] GradientTargets = { "bg", "panel", "field", "accent" };
            public Dictionary<string, GradientSpec> Gradients = new Dictionary<string, GradientSpec>(StringComparer.OrdinalIgnoreCase);

            public GradientSpec GradientFor(string target)
            {
                GradientSpec spec;
                return Gradients != null && Gradients.TryGetValue(target ?? "", out spec) && spec.IsSet ? spec : null;
            }

            public Tokens Clone()
            {
                var clone = (Tokens)MemberwiseClone();
                clone.Gradients = new Dictionary<string, GradientSpec>(StringComparer.OrdinalIgnoreCase);
                if (Gradients != null)
                {
                    foreach (var pair in Gradients) clone.Gradients[pair.Key] = pair.Value.Clone();
                }
                return clone;
            }

            public JObject ToJson() => new JObject
            {
                ["bg"] = Bg, ["panel"] = Panel, ["field"] = Field, ["field2"] = Field2,
                ["border"] = Border, ["borderStrong"] = BorderStrong,
                ["text"] = Text, ["muted"] = Muted, ["disabled"] = Disabled,
                ["accent"] = Accent, ["accent2"] = Accent2,
                ["danger"] = Danger, ["success"] = Success, ["warning"] = Warning,
                ["gradient"] = Gradient ?? "", ["gradients"] = GradientsJson(),
                ["dark"] = Dark,
            };

            // the 7 fields the custom-theme editor exposes -- used to seed "Custom" from whatever preset was active.
            public JObject ToSeedJson() => new JObject
            {
                ["bg"] = Bg, ["panel"] = Panel, ["field"] = Field, ["text"] = Text,
                ["accent"] = Accent, ["border"] = Border, ["danger"] = Danger,
                ["gradient"] = Gradient ?? "", ["gradients"] = GradientsJson(), ["dark"] = Dark,
            };

            private JObject GradientsJson()
            {
                var result = new JObject();
                foreach (string target in GradientTargets)
                {
                    GradientSpec spec = GradientFor(target);
                    if (spec != null) result[target] = spec.ToJson();
                }
                return result;
            }
        }

        public sealed class GradientStop
        {
            public string Color;
            public int Position;
        }

        public sealed class GradientSpec
        {
            public string Type = "linear";
            public int Angle = 135;
            public int CenterX = 50;
            public int CenterY = 50;
            public List<GradientStop> Stops = new List<GradientStop>();

            // under two stops there is nothing to interpolate, so the token just stays its solid colour.
            public bool IsSet { get { return Stops != null && Stops.Count >= 2; } }

            public GradientSpec Clone()
            {
                var clone = new GradientSpec { Type = Type, Angle = Angle, CenterX = CenterX, CenterY = CenterY };
                foreach (GradientStop stop in Stops) clone.Stops.Add(new GradientStop { Color = stop.Color, Position = stop.Position });
                return clone;
            }

            public JObject ToJson()
            {
                var stops = new JArray();
                foreach (GradientStop stop in Stops)
                    stops.Add(new JObject { ["color"] = stop.Color, ["position"] = stop.Position });
                return new JObject
                {
                    ["type"] = Type, ["angle"] = Angle,
                    ["centerX"] = CenterX, ["centerY"] = CenterY, ["stops"] = stops,
                };
            }
        }

        // presets. keep "default" first + exactly matching the dock's shipped :root so selecting it is a true no-op.
        public static readonly List<KeyValuePair<string, string>> PresetOrder = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("default", "OBS Default"),
            new KeyValuePair<string, string>("obs-darker", "OBS Darker"),
            new KeyValuePair<string, string>("obs-black", "OBS Black"),
            new KeyValuePair<string, string>("medal", "Medal"),
            new KeyValuePair<string, string>("discord", "Discord"),
            new KeyValuePair<string, string>("light", "Light"),
        };

        private static readonly Dictionary<string, Tokens> Presets = new Dictionary<string, Tokens>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new Tokens { Bg = "#1D1F26", Panel = "#272A33", Field = "#3C404D", Field2 = "#323540", Border = "#3C404D", BorderStrong = "#5B6273", Text = "#FFFFFF", Muted = "#969696", Disabled = "#7E828C", Accent = "#284CB8", Accent2 = "#476BD7", Danger = "#E33B57", Success = "#37D247", Warning = "#E2A33B", Dark = true },
            // same OBS Yami hues + accent, just the background greys stepped down darker.
            ["obs-darker"] = new Tokens { Bg = "#14161B", Panel = "#1B1D24", Field = "#2A2D36", Field2 = "#23252D", Border = "#2A2D36", BorderStrong = "#404551", Text = "#FFFFFF", Muted = "#969696", Disabled = "#7E828C", Accent = "#284CB8", Accent2 = "#476BD7", Danger = "#E33B57", Success = "#37D247", Warning = "#E2A33B", Dark = true },
            // same OBS blue accent, backgrounds sit around medal's darkness floor with a cooler blue lean (was near-pure-black, too dark).
            ["obs-black"] = new Tokens { Bg = "#0A0B0F", Panel = "#16181E", Field = "#22242C", Field2 = "#1E2028", Border = "#282A33", BorderStrong = "#3C3F49", Text = "#FFFFFF", Muted = "#9597A1", Disabled = "#696B75", Accent = "#284CB8", Accent2 = "#476BD7", Danger = "#E33B57", Success = "#37D247", Warning = "#E2A33B", Dark = true },
            // shadcn "zinc" dark scale + medal brand-primary lime, pulled from %LOCALAPPDATA%\Medal\app-*\resources\app\renderer.min.css (--background/--card/--muted oklch -> hex, --color-brand-primary-400 = #BFF83E). the older #E94F56 coral is medal's secondary, not the ui accent.
            ["medal"] = new Tokens { Bg = "#09090B", Panel = "#18181B", Field = "#232327", Field2 = "#1C1C1F", Border = "#27272A", BorderStrong = "#3F3F46", Text = "#FAFAFA", Muted = "#A1A1AA", Disabled = "#71717A", Accent = "#BFF83E", Accent2 = "#CFFF53", Danger = "#EB4D55", Success = "#01D28E", Warning = "#FFCA49", Dark = true },
            // discord dark, verified against themeandcolor.com's token dump: bg tertiary/secondary/floating, text-normal #DBDEE1 (not the brighter header #F2F3F5), brand #5865F2, brand-hover family for accent2
            ["discord"] = new Tokens { Bg = "#1E1F22", Panel = "#2B2D31", Field = "#383A40", Field2 = "#313338", Border = "#3F4147", BorderStrong = "#4E5058", Text = "#DBDEE1", Muted = "#B5BAC1", Disabled = "#80848E", Accent = "#5865F2", Accent2 = "#7984F5", Danger = "#F23F43", Success = "#23A559", Warning = "#F0B232", Dark = true },
            ["light"] = new Tokens { Bg = "#F2F3F5", Panel = "#FFFFFF", Field = "#E9EBEF", Field2 = "#DFE2E7", Border = "#CDD0D6", BorderStrong = "#B4B8C0", Text = "#1A1D24", Muted = "#5C6270", Disabled = "#9AA0AC", Accent = "#2F6FE8", Accent2 = "#5590F5", Danger = "#E23B2E", Success = "#1E8A3C", Warning = "#C05A0B", Dark = false },
        };

        public static bool IsPreset(string id) => id != null && Presets.ContainsKey(id);

        // "theme" id -> tokens. "default"/preset -> that preset; "user/<name>" -> the saved json; "custom" -> the
        // themeCustom object (6 picks + dark, rest derived); anything else -> default.
        public static Tokens Resolve(JObject settings)
        {
            string id = settings?["theme"]?.Value<string>() ?? "default";
            if (Presets.TryGetValue(id, out var p)) return p.Clone();
            if (id != null && id.StartsWith("user/", StringComparison.Ordinal))
            {
                var t = LoadUserTheme(id);
                if (t != null) return t;
            }
            if (id == "custom")
            {
                var t = FromCustom(settings?["themeCustom"] as JObject);
                if (t != null) return t;
            }
            return Presets["default"].Clone();
        }

        // the six-plus-dark custom editor payload -> a full token set (the other nine derived).
        public static Tokens FromCustom(JObject c)
        {
            if (c == null) return null;
            string Bg = Hex(c["bg"], "#161617"), Panel = Hex(c["panel"], "#1D1F26"),
                   Field = Hex(c["field"], "#2F323C"), Text = Hex(c["text"], "#FFFFFF"),
                   Accent = Hex(c["accent"], "#284CB8"), Border = Hex(c["border"], "#3C404D"),
                   Danger = Hex(c["danger"], "#E33B57");
            bool dark = c["dark"] == null ? true : c["dark"].Value<bool>();
            var tokens = new Tokens
            {
                Bg = Bg,
                Panel = Panel,
                Field = Field,
                Field2 = Shift(Field, dark ? -0.06 : -0.04),
                Border = Border,
                BorderStrong = Shift(Border, dark ? 0.16 : -0.16),
                Text = Text,
                Muted = Mix(Text, Bg, 0.42),
                Disabled = Mix(Text, Bg, 0.6),
                Accent = Accent,
                Accent2 = Shift(Accent, dark ? 0.14 : 0.1),
                Danger = Danger,
                Success = dark ? "#3BA55D" : "#1A7F37",
                Warning = dark ? "#E2A33B" : "#B45309",
                Dark = dark,
            };
            ApplyGradient(c, tokens);
            return tokens;
        }

        // the :root override the helper splices into every dock page before </head>. covers both var-naming schemes
        // in use across the dock html (settings.html vs clips/controls.html).
        public static string DockCss(Tokens t)
        {
            string grey8 = Shift(t.Bg, t.Dark ? -0.28 : -0.06);
            string grey5 = Mix(t.Panel, t.Field, 0.45); // a real step between panel and field even when a theme sets them equal
            string grey2 = Mix(t.Border, t.BorderStrong, 0.5);
            var sb = new StringBuilder();
            // beat the anti-FOUC inline style on <html>/<body> and the theme-color meta
            string pageBackground = BackgroundCss(t);
            sb.Append("html,body{background:").Append(pageBackground).Append("!important;color:").Append(t.Text).Append("!important;}");
            // always emitted, even with no gradient: this stylesheet replaces one that may have set a gradient on
            // .shell, and a theme that simply omitted the rule left that gradient painted -- html,body{background}
            // cannot reach .shell. "none" is what actually clears it when switching back to a flat preset.
            sb.Append("body,.shell{background-image:").Append(t.GradientFor("bg") != null ? pageBackground : "none")
              .Append("!important;background-attachment:fixed!important;}");
            sb.Append("html{color-scheme:").Append(t.Dark ? "dark" : "light").Append("!important;}"); // beat the inline style + <meta>, so native controls/scrollbars flip on a light theme
            // a surface token paints with its gradient where one is set; everything that needs a real colour --
            // borders, text, the Mix/Shift maths above, the win32 chrome -- keeps reading the plain field. the
            // *-solid vars exist for the handful of dock rules that use a surface token as a border colour.
            string bgPaint = Paint(t, "bg", t.Bg);
            string panelPaint = Paint(t, "panel", t.Panel);
            string fieldPaint = Paint(t, "field", t.Field);
            string accentPaint = Paint(t, "accent", t.Accent);
            sb.Append(":root{");
            sb.Append("--window-solid:").Append(t.Bg).Append(';');
            sb.Append("--panel-solid:").Append(t.Panel).Append(';');
            sb.Append("--field-solid:").Append(t.Field).Append(';');
            sb.Append("--selected-solid:").Append(t.Accent).Append(';');
            // same four solids under the clips/controls names, so those files can stay in their own scheme
            sb.Append("--grey4-solid:").Append(t.Field).Append(';');
            sb.Append("--grey6-solid:").Append(t.Panel).Append(';');
            sb.Append("--grey7-solid:").Append(t.Bg).Append(';');
            sb.Append("--blue3-solid:").Append(t.Accent).Append(';');
            sb.Append("--primary-solid:").Append(t.Accent).Append(';');
            sb.Append("--button_bg-solid:").Append(t.Field).Append(';');
            // settings.html scheme
            sb.Append("--window:").Append(bgPaint).Append(';');
            sb.Append("--side:").Append(panelPaint).Append(';');
            sb.Append("--panel:").Append(panelPaint).Append(';');
            sb.Append("--field:").Append(fieldPaint).Append(';');
            sb.Append("--field2:").Append(t.Field2).Append(';');
            sb.Append("--field-dark:").Append(bgPaint).Append(';');
            sb.Append("--line:").Append(t.Border).Append(';');
            sb.Append("--line2:").Append(t.BorderStrong).Append(';');
            sb.Append("--selected:").Append(accentPaint).Append(';');
            sb.Append("--selected2:").Append(t.Accent2).Append(';');
            sb.Append("--text:").Append(t.Text).Append(';');
            sb.Append("--muted:").Append(t.Muted).Append(';');
            sb.Append("--disabled:").Append(t.Disabled).Append(';');
            sb.Append("--danger:").Append(t.Danger).Append(';');
            sb.Append("--danger-deep:").Append(Shift(t.Danger, t.Dark ? -0.45 : -0.25)).Append(';');
            sb.Append("--success:").Append(t.Success).Append(';');
            sb.Append("--warning:").Append(t.Warning).Append(';');
            sb.Append("--link:").Append(t.Accent2).Append(';');
            sb.Append("--button:").Append(fieldPaint).Append(';');
            sb.Append("--button-hover:").Append(t.BorderStrong).Append(';');
            // clips.html / controls_app.html scheme
            sb.Append("--grey1:").Append(t.BorderStrong).Append(';');
            sb.Append("--grey2:").Append(grey2).Append(';');
            sb.Append("--grey3:").Append(t.Border).Append(';');
            sb.Append("--grey4:").Append(fieldPaint).Append(';');
            sb.Append("--grey5:").Append(grey5).Append(';');
            sb.Append("--grey6:").Append(panelPaint).Append(';');
            sb.Append("--grey7:").Append(bgPaint).Append(';');
            sb.Append("--grey8:").Append(grey8).Append(';');
            sb.Append("--blue2:").Append(t.Accent2).Append(';');
            sb.Append("--blue3:").Append(accentPaint).Append(';');
            sb.Append("--green:").Append(t.Success).Append(';');
            sb.Append("--red:").Append(t.Danger).Append(';');
            sb.Append("--amber:").Append(t.Warning).Append(';');
            sb.Append("--white1:").Append(t.Text).Append(';');
            sb.Append("--white5:").Append(t.Muted).Append(';');
            sb.Append("--bg_window:").Append(bgPaint).Append(';');
            sb.Append("--bg_card:").Append(grey5).Append(';');
            sb.Append("--bg_dock:").Append(panelPaint).Append(';');
            sb.Append("--text_muted:").Append(t.Muted).Append(';');
            sb.Append("--button_bg:").Append(fieldPaint).Append(';');
            sb.Append("--button_bg_hover:").Append(t.Border).Append(';');
            sb.Append("--button_bg_down:").Append(t.Bg).Append(';');
            sb.Append("--button_border:").Append(t.Field).Append(';');
            sb.Append("--button_border_hover:").Append(t.BorderStrong).Append(';');
            sb.Append("--primary:").Append(accentPaint).Append(';');
            sb.Append("--primary_light:").Append(t.Accent2).Append(';');
            sb.Append("--link_accent:").Append(t.Accent2).Append(';');
            sb.Append("--input_bg:").Append(fieldPaint).Append(';');
            sb.Append("--input_border:").Append(t.Border).Append(';');
            sb.Append("--theme-background:").Append(pageBackground).Append(';');
            // -- cross-theme helpers (used for checkmarks, translucent dividers/overlays, shadows) --
            string onAccent = OnColor(t.Accent);
            sb.Append("--on-accent:").Append(onAccent).Append(';');                 // readable icon/text ON the accent fill
            sb.Append("--on-accent-rgb:").Append(Rgb(onAccent)).Append(';');
            sb.Append("--text-rgb:").Append(Rgb(t.Text)).Append(';');               // rgba(var(--text-rgb), a) -> translucent text-colour
            sb.Append("--bg-rgb:").Append(Rgb(t.Bg)).Append(';');
            sb.Append("--accent-rgb:").Append(Rgb(t.Accent)).Append(';');
            sb.Append("--danger-rgb:").Append(Rgb(t.Danger)).Append(';');
            sb.Append("--hairline:rgba(").Append(Rgb(t.Text)).Append(t.Dark ? ",.08)" : ",.12)").Append(';'); // faint divider
            sb.Append("--overlay-soft:rgba(").Append(Rgb(t.Text)).Append(t.Dark ? ",.06)" : ",.05)").Append(';');
            sb.Append("--overlay-strong:rgba(").Append(Rgb(t.Text)).Append(t.Dark ? ",.14)" : ",.10)").Append(';');
            sb.Append("--shadow:rgba(0,0,0,").Append(t.Dark ? ".45)" : ".18)").Append(';');
            sb.Append("--shadow-weak:rgba(0,0,0,").Append(t.Dark ? ".28)" : ".10)").Append(';');
            sb.Append('}');
            return sb.ToString();
        }

        // physical stylesheet the dock html files <link> to. the "Custom Controls" dock is loaded from a file:// url
        // (user.ini ExtraBrowserDocks), so the helper's http-time injection never reaches it -- this file does.
        // rewritten on every theme change + at startup; empty-ish on the default theme.
        public static void WriteDockThemeCss(JObject settings)
        {
            try
            {
                string dockDir = AppConfig.GetDockDir();
                if (string.IsNullOrEmpty(dockDir) || !Directory.Exists(dockDir)) return;
                string id = settings?["theme"]?.Value<string>() ?? "default";
                string css = id == "default" ? "/* default theme -- dock uses its own :root */\n" : DockCss(Resolve(settings)) + "\n";
                File.WriteAllText(Path.Combine(dockDir, "rk-theme.css"), css, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("Themes.WriteDockThemeCss: " + ex.Message); }
        }

        // the <style> block ServeHtml injects. "" for the default theme (leave the html's own :root untouched).
        public static string DockStyleTag(JObject settings)
        {
            try
            {
                string id = settings?["theme"]?.Value<string>() ?? "default";
                if (id == "default") return "";
                return "<style id=\"rk-theme\">" + DockCss(Resolve(settings)) + "</style>";
            }
            catch (Exception ex) { Log.Write("Themes.DockStyleTag: " + ex.Message); return ""; }
        }

        // where the target obs theme id is staged between "Apply" and the post-exit relaunch. obs rewrites the whole
        // user.ini from memory on a graceful close, so a key written now gets clobbered -- restart_obs.ps1 /
        // ApplyPendingTheme re-splice it once obs is actually gone.
        public static readonly string PendingThemeMarker = Path.Combine(Constants.OBS_CONFIG_DIR, ".replaykit-theme-pending");

        // writes %APPDATA%\obs-studio\themes\rk_<slug>.ovt (a Yami variant) for non-default themes, sets user.ini now
        // AND stages the id so the relaunch path can put it back after obs's exit-save clobbers user.ini. returns the
        // restart-reason token, or "" on failure.
        public static string ApplyToObs(JObject settings)
        {
            try
            {
                string id = settings?["theme"]?.Value<string>() ?? "default";
                string themesDir = Path.Combine(Constants.OBS_CONFIG_DIR, "themes");
                Directory.CreateDirectory(themesDir);

                string obsId = "";
                if (id != "default")
                {
                    var t = Resolve(settings);
                    string slug = Slug(id);
                    obsId = "com.replaykit.theme." + slug;
                    File.WriteAllText(Path.Combine(themesDir, "rk_" + slug + ".ovt"), BuildOvt(t, obsId, PresetLabel(id)), new UTF8Encoding(false));
                }
                SetObsThemeKey(obsId);   // covers the force-kill relaunch path (no exit-save)
                try { File.WriteAllText(PendingThemeMarker, obsId, new UTF8Encoding(false)); }
                catch (Exception ex) { Log.Write("Themes.ApplyToObs marker: " + ex.Message); }
                WriteDockThemeCss(settings);
                return "theme";
            }
            catch (Exception ex) { Log.Write("Themes.ApplyToObs: " + ex.Message); return ""; }
        }

        // consume the staged marker: re-write user.ini [Appearance] Theme= from it (obs just clobbered it on exit).
        // safe only once obs is gone; called by restart_obs.ps1's twin path in Program.RelaunchObsAfterClean.
        public static void ApplyPendingTheme()
        {
            try
            {
                if (!File.Exists(PendingThemeMarker)) return;
                string want = File.ReadAllText(PendingThemeMarker).Trim();
                SetObsThemeKey(want);
                File.Delete(PendingThemeMarker);
                Log.Write("Themes.ApplyPendingTheme: user.ini [Appearance] Theme=" + (want.Length == 0 ? "(cleared)" : want));
            }
            catch (Exception ex) { Log.Write("Themes.ApplyPendingTheme: " + ex.Message); }
        }

        // startup reconciler -- only touches disk if the current theme's .ovt is missing or user.ini points somewhere
        // else, so a normal launch (helper starts after obs has already read user.ini) writes nothing.
        public static void EnsureObsInSync(JObject settings)
        {
            try
            {
                WriteDockThemeCss(settings); // cheap; keeps the file:// dock stylesheet current after a helper restart
                string id = settings?["theme"]?.Value<string>() ?? "default";
                string ini = Path.Combine(Constants.OBS_CONFIG_DIR, "user.ini");
                string cur = ReadObsThemeKey(ini);
                if (id == "default")
                {
                    if (cur != null && cur.StartsWith("com.replaykit.theme.", StringComparison.Ordinal)) SetObsThemeKey("");
                    return;
                }
                string obsId = "com.replaykit.theme." + Slug(id);
                string ovt = Path.Combine(Constants.OBS_CONFIG_DIR, "themes", "rk_" + Slug(id) + ".ovt");
                if (cur != obsId || !File.Exists(ovt)) ApplyToObs(settings);
            }
            catch (Exception ex) { Log.Write("Themes.EnsureObsInSync: " + ex.Message); }
        }

        private static string ReadObsThemeKey(string ini)
        {
            if (!File.Exists(ini)) return null;
            bool inApp = false;
            foreach (var raw in File.ReadAllLines(ini))
            {
                string s = raw.Trim();
                if (s.StartsWith("[") && s.EndsWith("]")) { inApp = s.Equals("[Appearance]", StringComparison.OrdinalIgnoreCase); continue; }
                if (inApp && s.StartsWith("Theme=", StringComparison.OrdinalIgnoreCase)) return s.Substring(6).Trim();
            }
            return null;
        }

        private static string BuildOvt(Tokens t, string obsId, string label)
        {
            var sb = new StringBuilder();
            sb.Append("@OBSThemeMeta {\n");
            sb.Append("    name: 'RK ").Append(label.Replace("'", "")).Append("';\n");
            sb.Append("    id: '").Append(obsId).Append("';\n");
            // a light theme extends Yami's Light variant so it inherits the DARK-coloured icon/checkbox SVG set
            // (theme:Light/*.svg) -- otherwise obs keeps drawing the white Dark icons and they vanish on a light bg.
            sb.Append("    extends: '").Append(t.Dark ? "com.obsproject.Yami" : "com.obsproject.Yami.Light").Append("';\n");
            sb.Append("    dark: '").Append(t.Dark ? "true" : "false").Append("';\n");
            sb.Append("}\n\n");
            sb.Append("@OBSThemeVars {\n");
            void V(string k, string v) => sb.Append("    ").Append(k).Append(": ").Append(v).Append(";\n");
            // background ramp (grey7 = window, grey1 = strongest line)
            V("--grey1", t.BorderStrong);
            V("--grey2", Mix(t.Border, t.BorderStrong, 0.5));
            V("--grey3", t.Border);
            V("--grey4", t.Field);
            V("--grey5", t.Field2);
            V("--grey6", t.Panel);
            V("--grey7", t.Bg);
            V("--grey8", Shift(t.Bg, t.Dark ? -0.28 : -0.06));
            // accent ramp
            V("--blue1", Shift(t.Accent2, 0.16));
            V("--blue2", t.Accent2);
            V("--blue3", t.Accent);
            V("--blue4", Shift(t.Accent, -0.16));
            V("--blue5", Shift(t.Accent, -0.3));
            // Yami-base aliases --primary -> --blue3, but Yami.Light hardcodes --primary, so set the semantic vars
            // directly -- works whichever base we extend.
            V("--primary", t.Accent);
            V("--primary_light", t.Accent2);
            V("--primary_lighter", Shift(t.Accent2, 0.16));
            V("--primary_dark", Shift(t.Accent, -0.16));
            V("--primary_darker", Shift(t.Accent, -0.3));
            V("--text", t.Text);
            V("--text_light", t.Text);
            V("--text_muted", t.Muted);
            V("--text_disabled", t.Disabled);
            V("--white1", t.Text);
            V("--white5", t.Muted);
            V("--red1", Shift(t.Danger, 0.14));
            V("--red2", t.Danger);
            V("--red3", Shift(t.Danger, -0.16));
            V("--green1", Shift(t.Success, 0.12));
            V("--green2", t.Success);
            V("--yellow1", Shift(t.Warning, 0.12));
            V("--yellow2", t.Warning);
            sb.Append("}\n");
            // selected rows / tabs / menu items fill with --primary and keep white --text on top (Yami.obt QListView::item:selected etc) -- a light accent like medal's lime makes that unreadable, so when OnColor wants dark ink pin those selected states to it; blue-accent presets skip this untouched
            string onAccent = OnColor(t.Accent);
            if (!string.Equals(onAccent, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n');
                sb.Append("QListView::item:selected, QListWidget::item:selected, QMenu::item:selected,\n");
                sb.Append("QListView::item:selected:hover, QListWidget::item:selected:hover, QMenu::item:selected:hover,\n");
                sb.Append("QComboBox QAbstractItemView::item:selected, QTabBar::tab:selected,\n");
                sb.Append("SourceTreeItem[selected=\"true\"] { color: ").Append(onAccent).Append("; }\n");
            }
            // only the window gradient goes to OBS: panel/field/accent are used as plain colours all through the
            // generated .ovt, where a qgradient is not a valid value.
            GradientSpec windowGradient = t.GradientFor("bg");
            if (windowGradient != null)
            {
                sb.Append('\n');
                sb.Append("QMainWindow, QDialog { background: ").Append(QtGradient(windowGradient)).Append("; }\n");
            }
            return sb.ToString();
        }

        // minimal ini edit: set (or remove, when value == "") [Appearance] Theme= in user.ini, preserving everything else.
        private static void SetObsThemeKey(string value)
        {
            string ini = Path.Combine(Constants.OBS_CONFIG_DIR, "user.ini");
            var lines = File.Exists(ini) ? new List<string>(File.ReadAllLines(ini)) : new List<string>();
            int appIdx = -1, keyIdx = -1, nextSection = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i].Trim();
                if (s.StartsWith("[") && s.EndsWith("]"))
                {
                    if (appIdx >= 0) { nextSection = i; break; }
                    if (s.Equals("[Appearance]", StringComparison.OrdinalIgnoreCase)) appIdx = i;
                    continue;
                }
                if (appIdx >= 0 && keyIdx < 0 && s.StartsWith("Theme=", StringComparison.OrdinalIgnoreCase)) keyIdx = i;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (keyIdx >= 0) lines.RemoveAt(keyIdx);
            }
            else if (keyIdx >= 0)
            {
                lines[keyIdx] = "Theme=" + value;
            }
            else if (appIdx >= 0)
            {
                lines.Insert(appIdx + 1, "Theme=" + value);
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add("[Appearance]");
                lines.Add("Theme=" + value);
            }
            File.WriteAllLines(ini, lines, new UTF8Encoding(false));
        }

        // -- user (saved) themes --

        public static string SaveUserTheme(string label, JObject customPayload, string existingId = null)
        {
            var t = FromCustom(customPayload);
            if (t == null) return null;
            Directory.CreateDirectory(Constants.USER_THEMES_DIR);
            string cleanLabel = CleanLabel(label);
            string name;
            if (!string.IsNullOrEmpty(existingId))
            {
                if (!existingId.StartsWith("user/", StringComparison.Ordinal)) return null;
                name = Path.GetFileNameWithoutExtension(Path.GetFileName(existingId.Substring(5)));
                if (string.IsNullOrEmpty(name) || existingId != "user/" + name) return null;
                if (!File.Exists(Path.Combine(Constants.USER_THEMES_DIR, name + ".json"))) return null;
            }
            else
            {
                string slug = Slug(string.IsNullOrWhiteSpace(cleanLabel) ? "theme" : cleanLabel);
                name = slug;
                int n = 2;
                while (File.Exists(Path.Combine(Constants.USER_THEMES_DIR, name + ".json"))) name = slug + "-" + n++;
            }
            var j = t.ToJson();
            j["label"] = string.IsNullOrWhiteSpace(cleanLabel) ? name : cleanLabel;
            WriteJsonAtomic(Path.Combine(Constants.USER_THEMES_DIR, name + ".json"), j);
            return "user/" + name;
        }

        public static Tokens LoadUserTheme(string id)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileName(id.Substring(5)));
                string p = Path.Combine(Constants.USER_THEMES_DIR, name + ".json");
                if (string.IsNullOrEmpty(name) || !File.Exists(p)) return null;
                var j = JObject.Parse(File.ReadAllText(p));
                var tokens = new Tokens
                {
                    Bg = Hex(j["bg"], "#161617"), Panel = Hex(j["panel"], "#1D1F26"),
                    Field = Hex(j["field"], "#2F323C"), Field2 = Hex(j["field2"], "#242730"),
                    Border = Hex(j["border"], "#3C404D"), BorderStrong = Hex(j["borderStrong"], "#5B6273"),
                    Text = Hex(j["text"], "#FFFFFF"), Muted = Hex(j["muted"], "#969696"),
                    Disabled = Hex(j["disabled"], "#7E828C"), Accent = Hex(j["accent"], "#284CB8"),
                    Accent2 = Hex(j["accent2"], "#476BD7"), Danger = Hex(j["danger"], "#E33B57"),
                    Success = Hex(j["success"], "#37D247"), Warning = Hex(j["warning"], "#E2A33B"),
                    Dark = j["dark"] == null ? true : j["dark"].Value<bool>(),
                };
                ApplyGradient(j, tokens);
                return tokens;
            }
            catch (Exception ex) { Log.Write("Themes.LoadUserTheme: " + ex.Message); return null; }
        }

        public static string UserThemeLabel(string fileName)
        {
            try
            {
                var j = JObject.Parse(File.ReadAllText(Path.Combine(Constants.USER_THEMES_DIR, fileName)));
                string l = j["label"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(l)) return l;
            }
            catch { }
            return Path.GetFileNameWithoutExtension(fileName);
        }

        // -- helpers --

        public static string PresetLabel(string id)
        {
            foreach (var kv in PresetOrder) if (kv.Key == id) return kv.Value;
            if (id != null && id.StartsWith("user/", StringComparison.Ordinal))
                return UserThemeLabel(Path.GetFileNameWithoutExtension(id.Substring(5)) + ".json");
            return id ?? "";
        }

        private static string Slug(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "theme";
            var sb = new StringBuilder();
            foreach (char c in s.Trim().ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            string outp = sb.ToString().Trim('-');
            while (outp.Contains("--")) outp = outp.Replace("--", "-");
            if (outp.Length > 40) outp = outp.Substring(0, 40).Trim('-');
            return outp.Length == 0 ? "theme" : outp;
        }

        private static string Hex(JToken tok, string fallback) => CleanHex(tok?.Value<string>(), fallback);

        public static string CleanOptionalHex(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return CleanHex(value, "");
        }

        public static int CleanAngle(int value)
        {
            value %= 360;
            return value < 0 ? value + 360 : value;
        }

        // "#abc" / "abcdef" / "#ABCDEF" -> "#AABBCC" uppercase, or fallback if unparseable.
        public static string CleanHex(string s, string fallback)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return fallback;
            if (s[0] != '#') s = "#" + s;
            if (s.Length == 4) s = "#" + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
            if (s.Length != 7) return fallback;
            for (int i = 1; i < 7; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return fallback;
            }
            return s.ToUpperInvariant();
        }

        private static string CleanLabel(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in (value ?? "").Trim())
            {
                if (!char.IsControl(c)) sb.Append(c);
                if (sb.Length == 40) break;
            }
            return sb.ToString().Trim();
        }

        private static void WriteJsonAtomic(string path, JObject value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, value.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public static string BackgroundCss(Tokens t) => Paint(t, "bg", t.Bg);

        // the css a token paints with: its gradient when one is set for that token, otherwise its plain colour.
        // callers that need a real colour (border-color, colour arithmetic, ToColorRef) must use the field directly.
        public static string Paint(Tokens t, string target, string solid)
        {
            GradientSpec spec = t?.GradientFor(target);
            if (spec == null) return solid;
            string stops = CssStops(spec.Stops);
            if (spec.Type == "radial")
                return "radial-gradient(circle at " + spec.CenterX + "% " + spec.CenterY + "%," + stops + ")";
            return "linear-gradient(" + CleanAngle(spec.Angle).ToString(CultureInfo.InvariantCulture) + "deg," + stops + ")";
        }

        private static string QtGradient(GradientSpec spec)
        {
            string stops = QtStops(spec.Stops);
            if (spec.Type == "radial")
            {
                string cx = (spec.CenterX / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
                string cy = (spec.CenterY / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
                return "qradialgradient(cx:" + cx + ",cy:" + cy + ",radius:0.75,fx:" + cx + ",fy:" + cy + "," + stops + ")";
            }
            double radians = CleanAngle(spec.Angle) * Math.PI / 180.0;
            double dx = Math.Sin(radians) / 2.0;
            double dy = -Math.Cos(radians) / 2.0;
            string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
            return "qlineargradient(x1:" + F(0.5 - dx) + ",y1:" + F(0.5 - dy) + ",x2:" + F(0.5 + dx) + ",y2:" + F(0.5 + dy) + "," + stops + ")";
        }

        // reads the per-token gradients. themes written before gradients were per-token carry a flat
        // gradientStops/gradientAngle block plus the even older single "gradient" colour -- both are read here as
        // the bg gradient so an existing custom theme keeps looking the way its author left it.
        private static void ApplyGradient(JObject source, Tokens tokens)
        {
            tokens.Gradients = new Dictionary<string, GradientSpec>(StringComparer.OrdinalIgnoreCase);
            if (source?["gradients"] is JObject map)
            {
                foreach (string target in Tokens.GradientTargets)
                {
                    var spec = ReadGradientSpec(map[target] as JObject, tokens);
                    if (spec != null) tokens.Gradients[target] = spec;
                }
            }
            if (!tokens.Gradients.ContainsKey("bg"))
            {
                var legacy = ReadGradientSpec(source, tokens);
                if (legacy != null) tokens.Gradients["bg"] = legacy;
            }
        }

        // accepts both shapes: the per-token one ({type,angle,centerX,centerY,stops}) and the legacy flat one
        // ({gradientType,gradientAngle,...,gradientStops} plus a bare "gradient" end colour).
        private static GradientSpec ReadGradientSpec(JObject source, Tokens tokens)
        {
            if (source == null) return null;
            bool flat = source["gradientStops"] != null || source["gradientType"] != null || source["gradient"] != null;
            var spec = new GradientSpec
            {
                Type = string.Equals((flat ? source["gradientType"] : source["type"])?.ToString(), "radial", StringComparison.OrdinalIgnoreCase) ? "radial" : "linear",
                Angle = ReadInt(flat ? source["gradientAngle"] : source["angle"], 135, 0, 359),
                CenterX = ReadInt(flat ? source["gradientCenterX"] : source["centerX"], 50, 0, 100),
                CenterY = ReadInt(flat ? source["gradientCenterY"] : source["centerY"], 50, 0, 100),
            };
            if ((flat ? source["gradientStops"] : source["stops"]) is JArray rawStops)
            {
                foreach (JToken raw in rawStops)
                {
                    if (!(raw is JObject item) || spec.Stops.Count >= 6) break;
                    string color = CleanOptionalHex(item["color"]?.ToString());
                    if (string.IsNullOrEmpty(color)) continue;
                    spec.Stops.Add(new GradientStop { Color = color, Position = ReadInt(item["position"], spec.Stops.Count == 0 ? 0 : 100, 0, 100) });
                }
            }
            if (spec.Stops.Count < 2 && flat)
            {
                string legacy = CleanOptionalHex(source["gradient"]?.ToString());
                if (!string.IsNullOrEmpty(legacy))
                {
                    spec.Stops.Clear();
                    spec.Stops.Add(new GradientStop { Color = tokens.Bg, Position = 0 });
                    spec.Stops.Add(new GradientStop { Color = legacy, Position = 100 });
                }
            }
            spec.Stops.Sort((left, right) => left.Position.CompareTo(right.Position));
            return spec.IsSet ? spec : null;
        }

        private static int ReadInt(JToken value, int fallback, int min, int max)
        {
            if (value == null || !int.TryParse(value.ToString(), out int parsed)) return fallback;
            return parsed < min ? min : (parsed > max ? max : parsed);
        }

        private static string CssStops(List<GradientStop> stops)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < stops.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(stops[i].Color).Append(' ').Append(stops[i].Position).Append('%');
            }
            return sb.ToString();
        }

        private static string QtStops(List<GradientStop> stops)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < stops.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("stop:").Append((stops[i].Position / 100.0).ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(stops[i].Color);
            }
            return sb.ToString();
        }

        private static void ToRgb(string hex, out int r, out int g, out int b)
        {
            r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static string ToHex(int r, int g, int b) =>
            "#" + Clamp(r).ToString("X2") + Clamp(g).ToString("X2") + Clamp(b).ToString("X2");

        private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        // move a colour toward white (amt > 0) or black (amt < 0), amt in [-1,1].
        public static string Shift(string hex, double amt)
        {
            ToRgb(Hex(hex, "#000000"), out int r, out int g, out int b);
            int t = amt >= 0 ? 255 : 0;
            double a = Math.Abs(amt);
            return ToHex(
                (int)Math.Round(r + (t - r) * a),
                (int)Math.Round(g + (t - g) * a),
                (int)Math.Round(b + (t - b) * a));
        }

        // linear blend: w=0 -> a, w=1 -> b.
        public static string Mix(string aHex, string bHex, double w)
        {
            ToRgb(Hex(aHex, "#000000"), out int ar, out int ag, out int ab);
            ToRgb(Hex(bHex, "#000000"), out int br, out int bg, out int bb);
            return ToHex(
                (int)Math.Round(ar + (br - ar) * w),
                (int)Math.Round(ag + (bg - ag) * w),
                (int)Math.Round(ab + (bb - ab) * w));
        }

        // "#rrggbb" -> win32 COLORREF (0x00bbggrr) for DwmSetWindowAttribute.
        public static int ToColorRef(string hex)
        {
            ToRgb(Hex(hex, "#000000"), out int r, out int g, out int b);
            return (b << 16) | (g << 8) | r;
        }

        // "#rrggbb" -> "r, g, b" for rgba(var(--x-rgb), a).
        public static string Rgb(string hex)
        {
            ToRgb(Hex(hex, "#000000"), out int r, out int g, out int b);
            return r + ", " + g + ", " + b;
        }

        // black or white, whichever reads better ON the given colour (WCAG relative luminance).
        public static string OnColor(string hex)
        {
            ToRgb(Hex(hex, "#000000"), out int r, out int g, out int b);
            double L = 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
            return L > 0.42 ? "#101014" : "#FFFFFF";
        }

        private static double Lin(int c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
