using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the subtitles of the editor, burned into the saved copy: an ass file written next to the job and drawn by the ass filter of ffmpeg (libass, fonts found by fontconfig), with the times of the saved clip. the page breaks every cue into lines and measures them, so this only places what the preview showed: rows stacked per position in their order, a rounded box drawn behind a row that has one, and the spoken word coloured
    internal sealed class SubtitleBurn
    {
        private const int MaxRows = 50;
        private const int MaxCues = 2000;
        private const int MaxLines = 20;
        private const int MaxTextLength = 300;
        private const int MaxWords = 20000;
        public const string FileName = "subs.ass";
        // the soft shadow of the editor in pixels of a 1080p picture: its offset, a blur of half the css blur radius since ass blurs by spread, and 80 percent black
        private const double ShadowOffset = 6;
        private const double ShadowBlur = 6;
        private const string ShadowAlpha = "&H33&";

        private static readonly string[] Fonts = { "Arial", "Segoe UI", "Verdana", "Tahoma", "Trebuchet MS", "Impact", "Georgia", "Times New Roman", "Courier New", "Comic Sans MS", "Bahnschrift", "Consolas", "Fredoka One" };
        // fonts windows does not have, shipped with replaykit; the ones a save uses are copied next to the subtitles for the ass filter to load
        private static readonly Dictionary<string, string> BundledFonts = new Dictionary<string, string> { ["Fredoka One"] = "FredokaOne-Regular.ttf" };
        private const string FontsFolder = "fonts";
        private static readonly Regex ColorRe = new Regex("^#[0-9a-f]{6}([0-9a-f]{2})?$", RegexOptions.CultureInvariant);

        private sealed class Style
        {
            public string Font = "Arial";
            public int Size = 64;
            public string Color = "#ffffff";
            public bool Bold = true;
            public bool Italic;
            public string Align = "center";
            public string Position = "bottom";
            public string BorderColor = "#000000";
            public int BorderWidth = 4;
            public string Background = "#00000000";
            public int Radius = 8;
            public int PadX = 14;
            public int PadY = 6;
            // degrees clockwise, as the editor turns it
            public int Rotate;
            public bool Shadow;
            public bool WordOn = true;
            // word lights one word at a time; fill sweeps the colour through each word as it is said and keeps it on the words already said
            public string WordFx = "word";
            public string Word = "#ffe500";
            // a row dragged on the preview: the centre of its block as fractions of the picture, NaN when it stacks at its position
            public double X = double.NaN;
            public double Y = double.NaN;
            public bool Boxed => Alpha(Background) > 0;
            public bool Placed => !double.IsNaN(X);
        }

        private sealed class Cue
        {
            public double Start;
            public double End;
            public List<string> Lines;
            // widths of the lines in ems of the font, as the page measured them
            public double MaxWidth;
            // start and end of every word of the lines, in order; null when the cue has no word times or they do not line up with its words
            public List<double> Words;
            public List<double> Ends;
        }

        private sealed class Row
        {
            public int Index;
            public Style Style;
            // the height of a line over the size of the font
            public double Cell;
            public readonly List<Cue> Cues = new List<Cue>();
        }

        private readonly List<Row> _rows = new List<Row>();

        public int Count
        {
            get { int n = 0; foreach (var r in _rows) n += r.Cues.Count; return n; }
        }

        // rows of cues in seconds of the saved clip; a cue past its length is cut at the end, one wholly outside it is left out, and null comes back with the reason when the edit is not something the editor makes
        public static SubtitleBurn Parse(JToken edit, double outDuration, out string error)
        {
            error = null;
            var burn = new SubtitleBurn();
            if (!(edit is JObject obj) || !(obj["rows"] is JArray rows) || rows.Count > MaxRows) { error = "Bad subtitles"; return null; }
            int cueCount = 0, wordCount = 0;
            foreach (var rowToken in rows)
            {
                if (!(rowToken is JObject r) || !(r["cues"] is JArray cues)) { error = "Bad subtitles"; return null; }
                var style = ParseStyle(r["style"] as JObject);
                double cell = Number(r["cell"]);
                if (style == null || double.IsNaN(cell) || cell < 0.5 || cell > 3) { error = "Bad subtitles"; return null; }
                cueCount += cues.Count;
                if (cueCount > MaxCues) { error = "Too many subtitles"; return null; }
                var row = new Row { Index = burn._rows.Count, Style = style, Cell = cell };
                foreach (var token in cues)
                {
                    if (!(token is JObject c) || !(c["lines"] is JArray lines) || !(c["widths"] is JArray widths)) { error = "Bad subtitles"; return null; }
                    double start = Number(c["start"]);
                    double end = Number(c["end"]);
                    if (double.IsNaN(start) || double.IsNaN(end) || lines.Count == 0 || lines.Count > MaxLines || widths.Count != lines.Count) { error = "Bad subtitles"; return null; }
                    var kept = new List<string>();
                    double maxWidth = 0;
                    int length = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string line = lines[i]?.Type == JTokenType.String ? lines[i].Value<string>() : null;
                        double width = Number(widths[i]);
                        if (line == null || double.IsNaN(width) || width < 0 || width > 200) { error = "Bad subtitles"; return null; }
                        line = Clean(line);
                        length += line.Length;
                        if (line.Length == 0 || length > MaxTextLength) continue;
                        kept.Add(line);
                        maxWidth = Math.Max(maxWidth, width);
                    }
                    List<double> words = null, ends = null;
                    if (c["words"] != null && c["words"].Type != JTokenType.Null)
                    {
                        if (!(c["words"] is JArray list) || list.Count > MaxTextLength) { error = "Bad subtitles"; return null; }
                        wordCount += list.Count;
                        if (wordCount > MaxWords) { error = "Too many subtitles"; return null; }
                        words = new List<double>(list.Count);
                        ends = new List<double>(list.Count);
                        foreach (var w in list)
                        {
                            var pair = w as JArray;
                            double at = pair != null && pair.Count == 2 ? Number(pair[0]) : double.NaN;
                            double to = pair != null && pair.Count == 2 ? Number(pair[1]) : double.NaN;
                            if (double.IsNaN(at) || double.IsNaN(to) || to < at) { error = "Bad subtitles"; return null; }
                            words.Add(at);
                            ends.Add(to);
                        }
                    }
                    start = Math.Max(0, start);
                    end = Math.Min(outDuration, end);
                    if (kept.Count == 0 || end - start < 0.05) continue;
                    // the times only mean something while there is one per word of the lines
                    if (words != null && words.Count != CountWords(kept)) words = ends = null;
                    row.Cues.Add(new Cue { Start = start, End = end, Lines = kept, MaxWidth = maxWidth, Words = words, Ends = ends });
                }
                if (row.Cues.Count > 0) burn._rows.Add(row);
            }
            return burn;
        }

        private static Style ParseStyle(JObject s)
        {
            if (s == null) return null;
            var style = new Style();
            string font = s["font"]?.Type == JTokenType.String ? s["font"].Value<string>() : null;
            if (Array.IndexOf(Fonts, font) < 0) return null;
            style.Font = font;
            style.Size = Whole(s["size"], 16, 200, style.Size);
            style.BorderWidth = Whole(s["borderWidth"], 0, 20, style.BorderWidth);
            style.Radius = Whole(s["radius"], 0, 60, style.Radius);
            style.PadX = Whole(s["padX"], 0, 80, style.PadX);
            style.PadY = Whole(s["padY"], 0, 60, style.PadY);
            style.Rotate = Whole(s["rotate"], -180, 180, 0);
            style.Color = Colour(s["color"], style.Color);
            style.BorderColor = Colour(s["borderColor"], style.BorderColor);
            style.Background = Colour(s["bg"], style.Background);
            style.Word = Colour(s["word"], style.Word);
            style.Bold = Flag(s["bold"], style.Bold);
            style.Italic = Flag(s["italic"], style.Italic);
            style.Shadow = Flag(s["shadow"], style.Shadow);
            style.WordOn = Flag(s["wordOn"], style.WordOn);
            style.WordFx = Pick(s["wordFx"]?.Type == JTokenType.String ? s["wordFx"].Value<string>() : null, "word", "word", "fill");
            style.Align = Pick(s["align"]?.Value<string>(), "center", "left", "center", "right");
            style.Position = Pick(s["position"]?.Value<string>(), "bottom", "bottom", "middle", "top");
            double x = Number(s["x"]), y = Number(s["y"]);
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                if (x < 0 || x > 1 || y < 0 || y > 1) return null;
                style.X = x;
                style.Y = y;
            }
            return style;
        }

        private static double Number(JToken t)
        {
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return double.NaN;
            double v = t.Value<double>();
            return double.IsInfinity(v) ? double.NaN : v;
        }

        private static int Whole(JToken t, int min, int max, int fallback)
        {
            double v = Number(t);
            return double.IsNaN(v) ? fallback : (int)Math.Round(Math.Max(min, Math.Min(max, v)));
        }

        private static string Colour(JToken t, string fallback)
        {
            string v = t?.Type == JTokenType.String ? t.Value<string>().Trim().ToLowerInvariant() : null;
            return v != null && ColorRe.IsMatch(v) ? v : fallback;
        }

        private static bool Flag(JToken t, bool fallback) => t?.Type == JTokenType.Boolean ? t.Value<bool>() : fallback;

        private static string Pick(string value, string fallback, params string[] allowed) => Array.IndexOf(allowed, value) >= 0 ? value : fallback;

        // control characters go, and the two characters that start ass commands are swapped for look-alikes so a subtitle can never restyle itself
        private static string Clean(string text)
        {
            var sb = new StringBuilder();
            foreach (char ch in text)
            {
                if (sb.Length >= MaxTextLength) break;
                if (char.IsControl(ch)) continue;
                if (ch == '{') sb.Append('(');
                else if (ch == '}') sb.Append(')');
                else if (ch == '\\') sb.Append('＼');
                else sb.Append(ch);
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        private static int CountWords(List<string> lines)
        {
            int n = 0;
            foreach (var line in lines) n += line.Split(' ').Length;
            return n;
        }

        private static double Alpha(string hex) => hex.Length == 9 ? int.Parse(hex.Substring(7, 2), NumberStyles.HexNumber) / 255.0 : 1;

        // an ass colour override is &HBBGGRR& with the opacity apart as &HAA&, where 00 is opaque
        private static string Bgr(string hex) => "&H" + hex.Substring(5, 2).ToUpperInvariant() + hex.Substring(3, 2).ToUpperInvariant() + hex.Substring(1, 2).ToUpperInvariant() + "&";
        private static string AssAlpha(string hex) => "&H" + (255 - (int)Math.Round(Alpha(hex) * 255)).ToString("X2") + "&";
        // a colour of a style line is &HAABBGGRR
        private static string StyleColour(string hex) => "&H" + (255 - (int)Math.Round(Alpha(hex) * 255)).ToString("X2") + Bgr(hex).Substring(2, 6);

        private static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

        private static string Time(double s)
        {
            long cs = (long)Math.Round(Math.Max(0, s) * 100);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", cs / 360000, cs / 6000 % 60, cs / 100 % 60, cs % 100);
        }

        private static string RoundedRect(double w, double h, double r)
        {
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
            if (r < 0.5) return "m 0 0 l " + N(w) + " 0 l " + N(w) + " " + N(h) + " l 0 " + N(h);
            // a quarter circle as a bezier curve, the usual 0.5523 of the radius for the control points
            double k = r * 0.5523;
            return "m " + N(r) + " 0 l " + N(w - r) + " 0 b " + N(w - r + k) + " 0 " + N(w) + " " + N(r - k) + " " + N(w) + " " + N(r) +
                " l " + N(w) + " " + N(h - r) + " b " + N(w) + " " + N(h - r + k) + " " + N(w - r + k) + " " + N(h) + " " + N(w - r) + " " + N(h) +
                " l " + N(r) + " " + N(h) + " b " + N(r - k) + " " + N(h) + " 0 " + N(h - r + k) + " 0 " + N(h - r) +
                " l 0 " + N(r) + " b 0 " + N(r - k) + " " + N(r - k) + " 0 " + N(r) + " 0";
        }

        // the lines of a cue as ass text, with the word at index lit (none when -1)
        private static string Text(Cue cue, Style style, int lit)
        {
            var sb = new StringBuilder();
            int word = 0;
            for (int i = 0; i < cue.Lines.Count; i++)
            {
                if (i > 0) sb.Append("\\N");
                var parts = cue.Lines[i].Split(' ');
                for (int j = 0; j < parts.Length; j++, word++)
                {
                    if (j > 0) sb.Append(' ');
                    if (word == lit) sb.Append("{\\1c").Append(Bgr(style.Word)).Append("\\1a").Append(AssAlpha(style.Word)).Append('}').Append(parts[j]).Append("{\\r}");
                    else sb.Append(parts[j]);
                }
            }
            return sb.ToString();
        }

        // the lines of a cue as ass karaoke from the time a on: the words already said are in the word colour, and each word after that sweeps from the text colour to the word colour while it is said; a slice starting partway into a word sweeps what is left of it
        private static string FillText(Cue cue, Style style, double a)
        {
            var sb = new StringBuilder();
            sb.Append("{\\1c").Append(Bgr(style.Word)).Append("\\1a").Append(AssAlpha(style.Word)).Append("\\2c").Append(Bgr(style.Color)).Append("\\2a").Append(AssAlpha(style.Color)).Append('}');
            // karaoke times are whole centiseconds counted from the start of the line, so each one is rounded against the running total to keep the words from drifting
            long spent = 0;
            int word = 0;
            for (int i = 0; i < cue.Lines.Count; i++)
            {
                var parts = cue.Lines[i].Split(' ');
                for (int j = 0; j < parts.Length; j++, word++)
                {
                    string gap = i > 0 && j == 0 ? "\\N" : j > 0 ? " " : "";
                    double from = cue.Words[word], to = cue.Ends[word];
                    if (to <= a + 1e-6) { sb.Append(gap).Append(parts[j]); continue; }
                    long wait = Math.Max(spent, (long)Math.Round((Math.Max(from, a) - a) * 100));
                    long done = Math.Max(wait, (long)Math.Round((to - a) * 100));
                    sb.Append("{\\k").Append(wait - spent).Append('}').Append(gap);
                    sb.Append("{\\kf").Append(done - wait).Append('}').Append(parts[j]);
                    spent = done;
                }
            }
            return sb.ToString();
        }

        // the look follows the size of the video, so a 720p and a 1440p save show the same subtitles
        // offset is added to every time, for a filter that sees the frames in the time of the source rather than of the saved clip
        public void Write(string dir, int width, int height, double offset)
        {
            if (width <= 0 || height <= 0) { width = 1920; height = 1080; }
            double k = height / 1080.0;
            var sb = new StringBuilder();
            sb.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: ").Append(width).Append("\nPlayResY: ").Append(height).Append("\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n");
            sb.Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
            // a font size of ass is the height of a line, which is the size of the page times the cell the page measured
            foreach (var row in _rows)
            {
                var s = row.Style;
                sb.Append(string.Format(CultureInfo.InvariantCulture, "Style: R{0},{1},{2},{3},{3},{4},&H80000000,{5},{6},0,0,100,100,0,0,1,{7},0,7,0,0,0,1\n",
                    row.Index, s.Font, N(s.Size * k * row.Cell), StyleColour(s.Color), StyleColour(s.BorderColor), s.Bold ? -1 : 0, s.Italic ? -1 : 0,
                    N(s.BorderWidth * k)));
            }
            sb.Append("Style: Box,Arial,20,&H00000000,&H00000000,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1\n\n");
            sb.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

            // the times where anything on screen changes: a cue coming or going, or a word lighting up
            var cuts = new SortedSet<double>();
            foreach (var row in _rows)
                foreach (var cue in row.Cues)
                {
                    cuts.Add(Math.Round(cue.Start, 3));
                    cuts.Add(Math.Round(cue.End, 3));
                    // a fill animates within one stretch by itself, so only the word that lights up needs a cut of its own
                    if (cue.Words != null && row.Style.WordOn && row.Style.WordFx == "word")
                        foreach (var w in cue.Words) if (w > cue.Start && w < cue.End) cuts.Add(Math.Round(w, 3));
                }
            var times = new List<double>(cuts);
            for (int t = 0; t + 1 < times.Count; t++)
            {
                double a = times[t], b = times[t + 1];
                if (b - a < 0.01) continue;
                WriteSlice(sb, a, b, width, height, k, offset);
            }
            File.WriteAllText(Path.Combine(dir, FileName), sb.ToString(), new UTF8Encoding(true));
            CopyBundledFonts(dir);
        }

        private bool _hasFonts;

        // a font that is missing would be swapped for another without a word, so a missing file fails the save instead
        private void CopyBundledFonts(string dir)
        {
            foreach (var row in _rows)
            {
                if (!BundledFonts.TryGetValue(row.Style.Font, out string file)) continue;
                string from = Path.Combine(Constants.APP_FONTS_DIR, file);
                if (!File.Exists(from)) throw new InvalidOperationException("The " + row.Style.Font + " font is missing. Run the ReplayKit setup again.");
                string to = Path.Combine(dir, FontsFolder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (!File.Exists(to)) File.Copy(from, to);
                _hasFonts = true;
            }
        }

        // everything on screen from a to b: per position the rows stack in their order (the last row of the bottom nearest the edge), and each block is its text and, when the row has one, its box
        private void WriteSlice(StringBuilder sb, double a, double b, int width, int height, double k, double offset)
        {
            double marginX = width * 0.05, marginV = height * 0.07, gap = height * 0.01;
            var groups = new Dictionary<string, List<KeyValuePair<Row, Cue>>>
            {
                ["top"] = new List<KeyValuePair<Row, Cue>>(), ["middle"] = new List<KeyValuePair<Row, Cue>>(), ["bottom"] = new List<KeyValuePair<Row, Cue>>(),
            };
            foreach (var row in _rows)
            {
                // with two cues of a row over one another the later one shows, as it does in the preview
                Cue shown = null;
                foreach (var cue in row.Cues) if (cue.Start <= a + 1e-6 && cue.End >= b - 1e-6) shown = cue;
                if (shown == null) continue;
                if (row.Style.Placed)
                {
                    // a placed row stands on its own, centred on its point
                    double h = BlockHeight(row, shown, k);
                    WriteBlock(sb, row, shown, row.Style.Y * height - h / 2, h, a, b, width, k, marginX, offset, row.Style.X * width);
                }
                else groups[row.Style.Position].Add(new KeyValuePair<Row, Cue>(row, shown));
            }
            foreach (var pair in groups)
            {
                var list = pair.Value;
                if (list.Count == 0) continue;
                var heights = new double[list.Count];
                double total = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    heights[i] = BlockHeight(list[i].Key, list[i].Value, k);
                    total += heights[i] + (i > 0 ? gap : 0);
                }
                double y = pair.Key == "top" ? marginV : pair.Key == "middle" ? height / 2.0 - total / 2 : height - marginV - total;
                for (int i = 0; i < list.Count; i++)
                {
                    WriteBlock(sb, list[i].Key, list[i].Value, y, heights[i], a, b, width, k, marginX, offset);
                    y += heights[i] + gap;
                }
            }
        }

        private static double BlockHeight(Row row, Cue cue, double k) => cue.Lines.Count * row.Style.Size * k * row.Cell + (row.Style.Boxed ? 2 * row.Style.PadY * k : 0);

        // centreX places the block around that point; without it the block keeps to the margins by its alignment
        private static void WriteBlock(StringBuilder sb, Row row, Cue cue, double top, double blockH, double a, double b, int width, double k, double marginX, double offset, double centreX = double.NaN)
        {
            var s = row.Style;
            double padX = s.Boxed ? s.PadX * k : 0, padY = s.Boxed ? s.PadY * k : 0;
            double blockW = cue.MaxWidth * s.Size * k + 2 * padX;
            double boxLeft = !double.IsNaN(centreX) ? centreX - blockW / 2 : s.Align == "left" ? marginX : s.Align == "right" ? width - marginX - blockW : width / 2.0 - blockW / 2;
            double textX = s.Align == "left" ? boxLeft + padX : s.Align == "right" ? boxLeft + blockW - padX : boxLeft + blockW / 2;
            int an = s.Align == "left" ? 7 : s.Align == "right" ? 9 : 8;
            // every part of the block turns about the centre of the block; ass turns counter clockwise, so the angle is negated
            string turn = s.Rotate == 0 ? "" : "\\org(" + N(boxLeft + blockW / 2) + "," + N(top + blockH / 2) + ")\\frz" + N(-s.Rotate);
            string start = Time(a + offset), end = Time(b + offset);
            if (s.Boxed)
            {
                sb.Append("Dialogue: 0,").Append(start).Append(',').Append(end).Append(",Box,,0,0,0,,{").Append(turn).Append("\\an7\\pos(").Append(N(boxLeft)).Append(',').Append(N(top))
                    .Append(")\\bord0\\shad0\\1c").Append(Bgr(s.Background)).Append("\\1a").Append(AssAlpha(s.Background)).Append("\\p1}")
                    .Append(RoundedRect(blockW, blockH, s.Radius * k)).Append("{\\p0}\n");
            }
            int lit = -1;
            if (s.WordOn && cue.Words != null)
                for (int i = 0; i < cue.Words.Count; i++) if (cue.Words[i] <= a + 1e-6) lit = i;
            // the shadow is the text again in black, moved down and right and blurred, under the text; the shadow of ass itself has no blur, and the preview draws a soft one
            if (s.Shadow)
            {
                sb.Append("Dialogue: 1,").Append(start).Append(',').Append(end).Append(",R").Append(row.Index).Append(",,0,0,0,,{").Append(turn).Append("\\an").Append(an).Append("\\pos(")
                    .Append(N(textX + ShadowOffset * k)).Append(',').Append(N(top + padY + ShadowOffset * k)).Append(")\\1c&H000000&\\3c&H000000&\\1a").Append(ShadowAlpha)
                    .Append("\\3a").Append(ShadowAlpha).Append("\\blur").Append(N(ShadowBlur * k)).Append('}').Append(Text(cue, s, -1)).Append('\n');
            }
            bool fill = s.WordOn && s.WordFx == "fill" && cue.Words != null && cue.Ends != null;
            sb.Append("Dialogue: 2,").Append(start).Append(',').Append(end).Append(",R").Append(row.Index).Append(",,0,0,0,,{").Append(turn).Append("\\an").Append(an).Append("\\pos(")
                .Append(N(textX)).Append(',').Append(N(top + padY)).Append(")}").Append(fill ? FillText(cue, s, a) : Text(cue, s, lit)).Append('\n');
        }

        // the filter reads the file by its name from the folder ffmpeg runs in, so no path has to be escaped into the filter graph; the bundled fonts sit in a folder beside it
        public string Filter => "ass=" + FileName + (_hasFonts ? ":fontsdir=" + FontsFolder : "");
    }
}
