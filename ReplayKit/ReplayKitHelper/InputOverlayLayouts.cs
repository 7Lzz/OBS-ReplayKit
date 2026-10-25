using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the keyboard and mouse of the wasd overlay: the wasd input-overlay presets ReplayKit ships, a keyboard of picked keys drawn in the look of the default one, and where the two sit in their group. obs sizes a group to what it holds, so the group is as big as the two side by side, fitted into the 628x292 box the first wasd and mouse filled
    internal static class InputOverlayLayouts
    {
        public const string DefaultKeyboard = "wasd-minimal";
        public const string DefaultMouse = "buttons";
        public const string Custom = "custom";
        public const double GroupWidth = 628.0, GroupHeight = 292.0;
        // the space between the keyboard and the mouse, the one the first group had
        private const double Gap = 11.0;
        private const string CustomFile = "input-overlay-custom.json";
        // the sheet of every custom key, drawn once; the number changes whenever the drawing does
        private const string CustomSheetFile = "input-overlay-custom-keys-2.png";

        public sealed class Layout
        {
            public string Id, Label, Blurb, Json, Sheet;
        }

        public static readonly Layout[] Keyboards =
        {
            new Layout { Id = "wasd-minimal", Label = "WASD", Blurb = "W, A, S and D with Q, E, Shift, Ctrl and Space.", Json = "wasd/wasd-minimal.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-full", Label = "WASD Full", Blurb = "WASD with Tab, R and F as well.", Json = "wasd/wasd-full.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-valorant", Label = "Valorant", Blurb = "WASD with Tab, F, C and 4 for agent abilities.", Json = "wasd/wasd-valorant.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-valorant-micro", Label = "Valorant Small", Blurb = "WASD with Tab and C, and a shorter Space.", Json = "wasd/wasd-valorant-micro.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-numbers", Label = "WASD Numbers", Blurb = "WASD with 1 to 5, Tab, Alt, R, T, F, G, Z, X, C, V and B.", Json = "wasd-extended-numeric/wasd-extended-numeric.json", Sheet = "wasd-extended-numeric/wasd-extended-numeric.png" },
            new Layout { Id = "wasd-numbers-small", Label = "WASD Numbers Small", Blurb = "WASD with 1 to 4, Tab, Alt, R, Z, X and C.", Json = "wasd-extended-numeric/wasd-extended-numeric-min.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = Custom, Label = "Custom", Blurb = "Any keys you pick, in the look of WASD.", Json = null, Sheet = null },
        };

        public static readonly Layout[] Mice =
        {
            new Layout { Id = "buttons", Label = "Buttons", Blurb = "Left, right and side buttons and the wheel.", Json = "mouse/mouse-no-movement.json", Sheet = "mouse/mouse.png" },
            new Layout { Id = "off", Label = "Off", Blurb = "Only the keyboard.", Json = null, Sheet = null },
        };

        // a key of the custom keyboard: where it sits (columns 137 apart and rows 133 apart, the pitch of the wasd presets, which line keys up without the stagger of a real keyboard and lean them instead), how wide it is, the key code input-overlay listens for, and an arrow drawn in place of a label
        private sealed class Key
        {
            public string Id, Label, Board;
            public int Code, Col, Row, W, Arrow, Nudge;
            public int U, V;
            public int X => Col * 137 + Nudge;
            public int Y => Row * 133;
        }

        private static Key K(string id, string label, int code, int col, int row, int w = 157, int arrow = -1, string board = null, int nudge = 0) =>
            new Key { Id = id, Label = label, Board = board ?? label, Code = code, Col = col, Row = row, W = w, Arrow = arrow, Nudge = nudge };

        private static readonly Key[] CustomKeys = BuildCustomKeys();

        private static Key[] BuildCustomKeys()
        {
            var keys = new List<Key> { K("esc", "ESC", 1, 0, 0, board: "Esc") };
            int[] fCodes = { 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88 };
            for (int i = 0; i < 12; i++) keys.Add(K("f" + (i + 1), "F" + (i + 1), fCodes[i], i + 1, 0));
            keys.Add(K("grave", "~", 41, 0, 1));
            for (int i = 0; i < 10; i++) keys.Add(K(((i + 1) % 10).ToString(), ((i + 1) % 10).ToString(), i + 2, i + 1, 1));
            keys.Add(K("minus", "-", 12, 11, 1));
            keys.Add(K("equal", "=", 13, 12, 1));
            keys.Add(K("backspace", "BACK", 14, 13, 1, 294, board: "Back"));
            keys.Add(K("tab", "TAB", 15, 0, 2, board: "Tab"));
            string top = "qwertyuiop";
            for (int i = 0; i < top.Length; i++) keys.Add(K(top[i].ToString(), top[i].ToString().ToUpperInvariant(), 16 + i, i + 1, 2));
            keys.Add(K("lbracket", "[", 26, 11, 2));
            keys.Add(K("rbracket", "]", 27, 12, 2));
            keys.Add(K("backslash", "\\", 43, 13, 2));
            keys.Add(K("shift", "SHIFT", 42, 0, 3, board: "Shift"));
            string home = "asdfghjkl";
            for (int i = 0; i < home.Length; i++) keys.Add(K(home[i].ToString(), home[i].ToString().ToUpperInvariant(), 30 + i, i + 1, 3));
            keys.Add(K("semicolon", ";", 39, 10, 3));
            keys.Add(K("quote", "'", 40, 11, 3));
            keys.Add(K("enter", "ENTER", 28, 12, 3, 294, board: "Enter"));
            keys.Add(K("alt", "ALT", 56, 0, 4, board: "Alt"));
            string bottom = "zxcvbnm";
            for (int i = 0; i < bottom.Length; i++) keys.Add(K(bottom[i].ToString(), bottom[i].ToString().ToUpperInvariant(), 44 + i, i + 1, 4));
            keys.Add(K("comma", ",", 51, 8, 4));
            keys.Add(K("period", ".", 52, 9, 4));
            keys.Add(K("slash", "/", 53, 10, 4));
            keys.Add(K("rshift", "SHIFT", 54, 11, 4, 294, board: "R Shift"));
            // up sits on the lean of down, so the two read as one column the way a real arrow cluster does, rather than staggered like w over s
            keys.Add(K("up", "", 57416, 15, 4, arrow: 0, board: "↑", nudge: 34));
            keys.Add(K("ctrl", "CTRL", 29, 0, 5, board: "Ctrl"));
            keys.Add(K("space", "", 57, 1, 5, 421, board: "Space"));
            keys.Add(K("ralt", "ALT", 3640, 4, 5, board: "R Alt"));
            keys.Add(K("rctrl", "CTRL", 3613, 5, 5, board: "R Ctrl"));
            keys.Add(K("left", "", 57419, 14, 5, arrow: 1, board: "←"));
            keys.Add(K("down", "", 57424, 15, 5, arrow: 2, board: "↓"));
            keys.Add(K("right", "", 57421, 16, 5, arrow: 3, board: "→"));
            // each key and its pressed picture 3 pixels under it, in rows of the sheet
            int u = 1, v = 1;
            foreach (var key in keys)
            {
                if (u + key.W > 2048) { u = 1; v += 2 * 128 + 6; }
                key.U = u;
                key.V = v;
                u += key.W + 4;
            }
            return keys.ToArray();
        }

        public static readonly string[] DefaultCustomKeys = { "q", "w", "e", "shift", "a", "s", "d", "ctrl", "space" };

        public static Layout FindKeyboard(string id) => Keyboards.FirstOrDefault(l => l.Id == id) ?? Keyboards[0];
        public static Layout FindMouse(string id) => Mice.FirstOrDefault(l => l.Id == id) ?? Mice[0];

        public static string PresetsRoot() => Path.Combine(Directory.GetParent(ReplaykitSettings.GetScriptsDir().TrimEnd('\\', '/')).FullName, "input-overlay-presets");

        // the picked keys the custom keyboard keeps, in the order of the table; anything else is dropped, and none at all is the default set
        public static JArray NormalizeCustomKeys(JToken token)
        {
            var picked = new HashSet<string>(StringComparer.Ordinal);
            if (token is JArray list)
                foreach (var entry in list)
                    if (entry.Type == JTokenType.String) picked.Add(entry.Value<string>());
            var keys = CustomKeys.Where(k => picked.Contains(k.Id)).Select(k => k.Id).ToList();
            return new JArray(keys.Count > 0 ? keys : DefaultCustomKeys.ToList());
        }

        // the input-overlay layout of the picked keys: the rows that keep a key stacked without the empty ones between them, the way the wasd layout has ctrl right under shift, and groups of keys far apart (wasd and the arrows) moved together, so the keyboard is no wider than what it shows. keys keep their place within their group
        public static JObject CustomLayout(JToken keys)
        {
            var ids = new HashSet<string>(NormalizeCustomKeys(keys).Values<string>(), StringComparer.Ordinal);
            var picked = CustomKeys.Where(k => ids.Contains(k.Id)).ToList();
            var rows = picked.Select(k => k.Y).Distinct().OrderBy(y => y).ToList();
            // spans of the keyboard that hold a key, across every row; a gap wider than two keys closes to a narrow one
            var spans = new List<int[]>();
            foreach (var k in picked.OrderBy(k => k.X))
            {
                if (spans.Count > 0 && k.X <= spans[spans.Count - 1][1] + 2 * 137) spans[spans.Count - 1][1] = Math.Max(spans[spans.Count - 1][1], k.X + k.W);
                else spans.Add(new[] { k.X, k.X + k.W });
            }
            var shift = new int[spans.Count];
            int at = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                shift[i] = at - spans[i][0];
                at += spans[i][1] - spans[i][0] + 40;
            }
            int Moved(Key k) { int i = spans.FindIndex(span => k.X >= span[0] && k.X < span[1]); return k.X + shift[i]; }
            int w = picked.Max(k => Moved(k) + k.W), h = (rows.Count - 1) * 133 + 128;
            var elements = new JArray(picked.Select(k => new JObject
            {
                ["code"] = k.Code, ["id"] = k.Id, ["mapping"] = new JArray(k.U, k.V, k.W, 128),
                ["pos"] = new JArray(Moved(k), rows.IndexOf(k.Y) * 133), ["type"] = 1, ["z_level"] = 1,
            }));
            return new JObject { ["default_width"] = 157, ["default_height"] = 128, ["space_h"] = 0, ["space_v"] = 0, ["flags"] = 0, ["overlay_width"] = w, ["overlay_height"] = h, ["elements"] = elements };
        }

        // the keys of the custom keyboard for the page that picks them, with the place each has on the keyboard
        public static JArray CustomKeyTable() => new JArray(CustomKeys.Select(k => new JObject { ["id"] = k.Id, ["label"] = k.Board, ["x"] = k.Col * 137, ["y"] = k.Y, ["w"] = k.W, ["h"] = 128 }));

        // the sheet of the custom keys in the look of wasd.png: a leaning key with a white rim and a condensed leaning label, released see-through and pressed filled yellow, drawn once into the ReplayKit folder
        public static string CustomSheet()
        {
            string path = Path.Combine(AppConfig.GetReplayKitRoot(), CustomSheetFile);
            if (File.Exists(path)) return path;
            int width = CustomKeys.Max(k => k.U + k.W) + 1, height = CustomKeys.Max(k => k.V) + 2 * 128 + 4;
            try
            {
                using (var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(sheet))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        using (var family = PickFamily())
                            foreach (var key in CustomKeys)
                            {
                                DrawKey(g, family, key, key.U, key.V, false);
                                DrawKey(g, family, key, key.U, key.V + 128 + 3, true);
                            }
                    }
                    string temp = path + ".tmp";
                    sheet.Save(temp, ImageFormat.Png);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ExternalException || ex is ArgumentException)
            {
                Log.Write("InputOverlayLayouts.CustomSheet: " + ex.Message);
                return null;
            }
            return path;
        }

        private static FontFamily PickFamily()
        {
            foreach (string name in new[] { "Bahnschrift Condensed", "Bahnschrift SemiBold Condensed", "Arial Narrow" })
            {
                try { return new FontFamily(name); }
                catch (ArgumentException) { }
            }
            return new FontFamily(GenericFontFamilies.SansSerif);
        }

        // a rounded parallelogram leaning right by a quarter of its height, the shape of the keys of the wasd presets
        private static GraphicsPath KeyShape(float x, float y, float w, float h)
        {
            const float inset = 2f, radius = 9f;
            float lean = (h - 2 * inset) * 0.26f;
            var corners = new[]
            {
                new PointF(x + inset + lean, y + inset), new PointF(x + w - inset, y + inset),
                new PointF(x + w - inset - lean, y + h - inset), new PointF(x + inset, y + h - inset),
            };
            var path = new GraphicsPath();
            for (int i = 0; i < 4; i++)
            {
                PointF prev = corners[(i + 3) % 4], at = corners[i], next = corners[(i + 1) % 4];
                PointF a = Toward(at, prev, radius), b = Toward(at, next, radius);
                if (i == 0) path.StartFigure();
                path.AddBezier(a, at, at, b);
                PointF c = Toward(next, at, radius);
                path.AddLine(b, c);
            }
            path.CloseFigure();
            return path;
        }

        private static PointF Toward(PointF from, PointF to, float distance)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y, length = (float)Math.Sqrt(dx * dx + dy * dy);
            return new PointF(from.X + dx / length * distance, from.Y + dy / length * distance);
        }

        private static void DrawKey(Graphics g, FontFamily family, Key key, int x, int y, bool pressed)
        {
            using (var shape = KeyShape(x, y, key.W, 128))
            {
                if (pressed)
                    using (var fill = new SolidBrush(Color.FromArgb(179, 255, 250, 87))) g.FillPath(fill, shape);
                using (var rim = new Pen(Color.White, 4f) { LineJoin = LineJoin.Round }) g.DrawPath(rim, shape);
            }
            // the outlines of a glyph can overlap, so it is filled by winding rather than by alternating, which would cut holes where they cross
            using (var glyph = new GraphicsPath(FillMode.Winding))
            {
                if (key.Arrow >= 0) AddArrow(glyph, key.Arrow);
                else if (key.Label.Length > 0) glyph.AddString(key.Label, family, (int)FontStyle.Regular, 88f, new PointF(0, 0), StringFormat.GenericTypographic);
                else return;
                // the label stands upright in its own box, is shrunk to fit the key, leaned like the key and put in its middle
                var box = glyph.GetBounds();
                float room = key.W - 2 - 128 * 0.26f - 34;
                float scale = Math.Min(1f, Math.Min(room / Math.Max(1f, box.Width), 64f / Math.Max(1f, box.Height)));
                if (key.Arrow >= 0) scale = 1f;
                using (var place = new Matrix())
                {
                    place.Translate(x + key.W / 2f, y + 64f, MatrixOrder.Append);
                    place.Shear(-0.26f, 0f, MatrixOrder.Prepend);
                    place.Scale(scale, scale, MatrixOrder.Prepend);
                    place.Translate(-(box.X + box.Width / 2f), -(box.Y + box.Height / 2f), MatrixOrder.Prepend);
                    glyph.Transform(place);
                }
                using (var edge = new Pen(pressed ? Color.FromArgb(220, 95, 93, 33) : Color.FromArgb(138, 0, 0, 0), 5f) { LineJoin = LineJoin.Round })
                    g.DrawPath(edge, glyph);
                using (var ink = new SolidBrush(Color.FromArgb(255, 233, 234, 229))) g.FillPath(ink, glyph);
            }
        }

        // an arrow of the arrow keys, pointing up, left, down or right, about the size of a letter
        private static void AddArrow(GraphicsPath path, int direction)
        {
            var up = new[] { new PointF(0, -30), new PointF(24, -2), new PointF(9, -2), new PointF(9, 30), new PointF(-9, 30), new PointF(-9, -2), new PointF(-24, -2) };
            float angle = new[] { 0f, -90f, 180f, 90f }[direction];
            using (var turn = new Matrix())
            {
                turn.Rotate(angle);
                turn.TransformPoints(up);
            }
            path.AddPolygon(up);
        }

        public sealed class Picked
        {
            public string Json, Sheet;
            public double Width, Height;
        }

        private static string Under(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        // the size input-overlay gives a source is the overlay size its layout names
        private static bool ReadSize(string json, out double width, out double height)
        {
            width = height = 0;
            try
            {
                var layout = JObject.Parse(File.ReadAllText(json));
                width = layout["overlay_width"]?.Value<double>() ?? 0;
                height = layout["overlay_height"]?.Value<double>() ?? 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is FormatException || ex is InvalidCastException) { return false; }
            return width > 0 && height > 0;
        }

        // the files of the picked keyboard; the custom one is written into the ReplayKit folder whenever its keys change. null when they are missing
        public static Picked Keyboard(JObject settings)
        {
            string root = PresetsRoot();
            var layout = FindKeyboard(settings?["inputOverlayKeyboard"]?.Value<string>());
            string json;
            if (layout.Id == Custom)
            {
                json = Path.Combine(AppConfig.GetReplayKitRoot(), CustomFile);
                string text = CustomLayout(settings?["inputOverlayCustomKeys"]).ToString(Formatting.Indented);
                try
                {
                    if (!File.Exists(json) || File.ReadAllText(json) != text) File.WriteAllText(json, text, new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("InputOverlayLayouts.Keyboard: " + ex.Message);
                    return null;
                }
            }
            else json = Under(root, layout.Json);
            string sheet = layout.Id == Custom ? CustomSheet() : Under(root, layout.Sheet);
            if (sheet == null) return null;
            if (!File.Exists(sheet) || !ReadSize(json, out double w, out double h)) return null;
            return new Picked { Json = json, Sheet = sheet, Width = w, Height = h };
        }

        // the files of the picked mouse; null for off, and when they are missing
        public static Picked Mouse(JObject settings)
        {
            var layout = FindMouse(settings?["inputOverlayMouse"]?.Value<string>());
            if (layout.Json == null) return null;
            string root = PresetsRoot();
            string json = Under(root, layout.Json), sheet = Under(root, layout.Sheet);
            if (!File.Exists(sheet) || !ReadSize(json, out double w, out double h)) return null;
            return new Picked { Json = json, Sheet = sheet, Width = w, Height = h };
        }

        public sealed class Fit
        {
            public double X, Y, Scale;
        }

        // the keyboard and the mouse at one height side by side, as tall as the box lets both fit; the mouse is null when it is off, and width x height is the group they make
        public static void Place(Picked keyboard, Picked mouse, out Fit keys, out Fit pointer, out double width, out double height)
        {
            double kw = keyboard?.Width ?? 568, kh = keyboard?.Height ?? 394;
            double across = kw / kh + (mouse != null ? mouse.Width / mouse.Height : 0);
            height = Math.Min(GroupHeight, (GroupWidth - (mouse != null ? Gap : 0)) / across);
            keys = new Fit { X = 0, Y = 0, Scale = height / kh };
            pointer = mouse == null ? null : new Fit { X = kw * height / kh + Gap, Y = 0, Scale = height / mouse.Height };
            width = kw * height / kh + (mouse != null ? Gap + mouse.Width * height / mouse.Height : 0);
        }

        public static void GroupSize(JObject settings, out double width, out double height) => Place(Keyboard(settings), Mouse(settings), out _, out _, out width, out height);

        // a file of the presets folder for the preview of the settings page; json and png only, and nothing outside it
        public static string ServedFile(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Length > 200) return null;
            string ext = Path.GetExtension(relative).ToLowerInvariant();
            if (ext != ".json" && ext != ".png") return null;
            string root = Path.GetFullPath(PresetsRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }

        // what the settings page offers and needs to draw the picked layouts the way obs does
        public static JObject Options() => new JObject
        {
            ["keyboards"] = new JArray(Keyboards.Select(l => new JObject { ["value"] = l.Id, ["label"] = l.Label, ["blurb"] = l.Blurb, ["json"] = l.Json, ["sheet"] = l.Sheet })),
            ["mice"] = new JArray(Mice.Select(l => new JObject { ["value"] = l.Id, ["label"] = l.Label, ["blurb"] = l.Blurb, ["json"] = l.Json, ["sheet"] = l.Sheet })),
            ["customKeys"] = CustomKeyTable(),
            ["group"] = new JObject { ["width"] = GroupWidth, ["height"] = GroupHeight, ["gap"] = Gap },
        };
    }
}
