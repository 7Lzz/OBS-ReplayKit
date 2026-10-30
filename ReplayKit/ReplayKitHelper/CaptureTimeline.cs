using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class CaptureTimeline
    {
        private sealed class Sample
        {
            internal long At;
            internal long Until;
            internal string Kind, Id, Name;
            // why a stretch has no game or desktop, and the program that was not recognised as a game
            internal string Why, App;
        }

        // the reasons a stretch of a clip can be unassigned, which the clips page puts into words
        private static readonly HashSet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
        {
            "no-visible-capture", "capture-not-live", "multiple-captures", "foreground-unavailable", "capture-target-not-focused",
            "display-not-found", "shown-window-unreadable", "not-recognised", "no-evidence",
        };

        private static readonly object Gate = new object();
        private static readonly List<Sample> History = new List<Sample>();
        private static string session = "";
        internal static int Count { get { lock (Gate) return History.Count; } }

        internal static void Observe(long at, string key, string kind, string id, string name, string why = "", string app = "")
        {
            why = kind == "unknown" && Reasons.Contains(why ?? "") ? why : "";
            app = why == "not-recognised" ? CleanApp(app) : "";
            lock (Gate)
            {
                if (key != session) { History.Clear(); session = key; }
                var last = History.LastOrDefault();
                if (last != null && at <= last.Until - 1500) return;
                if (last != null && at <= last.Until && last.Kind == kind && last.Id == (id ?? "") && last.Name == (name ?? "") && last.Why == why && last.App == app) last.Until = at + 1500;
                else History.Add(new Sample { At = at, Until = at + 1500, Kind = kind, Id = id ?? "", Name = name ?? "", Why = why, App = app });
                History.RemoveAll(s => s.Until < at - 604800000);
                if (History.Count > 8192) History.RemoveRange(0, History.Count - 8192);
            }
        }

        internal static JObject Between(long start, long end)
        {
            var rows = new JArray();
            if (end <= start) return Pack(0, rows);
            lock (Gate)
            {
                for (int i = 0; i < History.Count; i++)
                {
                    var sample = History[i];
                    long next = i + 1 < History.Count ? History[i + 1].At : end;
                    long until = Math.Min(next, sample.Until);
                    long left = Math.Max(start, sample.At), right = Math.Min(end, until);
                    if (right <= left) continue;
                    Add(rows, (left - start) / 1000.0, (right - start) / 1000.0, sample.Kind, sample.Id, sample.Name, sample.Why, sample.App);
                }
            }
            return Pack((end - start) / 1000.0, rows);
        }

        internal static JObject Pack(double duration, JArray rows, bool manual = false) => new JObject
        {
            ["v"] = 1, ["duration"] = duration, ["intervals"] = rows, ["manual"] = manual,
        };

        internal static JObject Pieces(JArray bounds, IList<double> lengths, double duration)
        {
            var output = new List<JObject>();
            if (!Number(duration) || duration <= 0 || bounds == null || bounds.Count != lengths.Count) return Pack(Math.Max(0, duration), new JArray());
            double remaining = duration;
            for (int i = lengths.Count - 1; i >= 0 && remaining > .000001; i--)
            {
                double length = lengths[i];
                if (!Number(length) || length <= 0) return Pack(duration, new JArray());
                double used = Math.Min(remaining, length), offset = remaining - used;
                long start = bounds[i]["s"]?.Value<long>() ?? 0, end = bounds[i]["e"]?.Value<long>() ?? 0;
                if (start > 0 && end > start && Math.Abs((end - start) / 1000.0 - length) <= .25)
                {
                    long left = start + (long)Math.Round((end - start) * ((length - used) / length));
                    var captured = Between(left, end);
                    double wallLength = captured["duration"].Value<double>();
                    if (wallLength > 0)
                        foreach (var row in (JArray)captured["intervals"])
                        {
                            var mapped = (JObject)row.DeepClone();
                            mapped["s"] = offset + row["s"].Value<double>() * used / wallLength;
                            mapped["e"] = offset + row["e"].Value<double>() * used / wallLength;
                            output.Add(mapped);
                        }
                }
                remaining -= used;
            }
            var intervals = new JArray();
            foreach (var row in output.OrderBy(r => r["s"].Value<double>()))
                Add(intervals, row["s"].Value<double>(), row["e"].Value<double>(), row);
            return Pack(duration, intervals);
        }

        internal static bool Number(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        private static double ReadNumber(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? token.Value<double>() : double.NaN;
        private static string ReadText(JToken token) => token?.Type == JTokenType.String ? token.Value<string>() : "";

        internal static void Add(JArray rows, double start, double end, string kind, string id = "", string name = "", string why = "", string app = "")
        {
            if (!Number(start) || !Number(end) || start < 0 || end <= start || rows.Count >= 8192) return;
            why = kind == "unknown" && Reasons.Contains(why ?? "") ? why : "";
            app = why == "not-recognised" ? CleanApp(app) : "";
            if (rows.Last is JObject previous && previous["kind"]?.Value<string>() == kind && previous["id"]?.Value<string>() == id && ReadText(previous["why"]) == why && ReadText(previous["app"]) == app && Math.Abs(previous["e"].Value<double>() - start) < .002)
                previous["e"] = end;
            else
            {
                var row = new JObject { ["s"] = start, ["e"] = end, ["kind"] = kind, ["id"] = id, ["name"] = name };
                if (why.Length > 0) row["why"] = why;
                if (app.Length > 0) row["app"] = app;
                rows.Add(row);
            }
        }

        // a stored row copied onto a new place in time, keeping what it says and why
        internal static void Add(JArray rows, double start, double end, JToken row) =>
            Add(rows, start, end, ReadText(row["kind"]), ReadText(row["id"]), ReadText(row["name"]), ReadText(row["why"]), ReadText(row["app"]));

        // only the file name of a program, never its folder, and nothing a tooltip could not show
        private static string CleanApp(string app)
        {
            string name = System.IO.Path.GetFileName((app ?? "").Replace('/', '\\'));
            return name.Length == 0 || name.Length > 80 || name.Any(char.IsControl) ? "" : name;
        }

        internal static JObject Cut(JObject capture, double start, double end, double rate = 1)
        {
            var rows = new JArray();
            if (!Number(start) || !Number(end) || !Number(rate) || start < 0 || end <= start || rate <= 0) return Pack(0, rows);
            foreach (var row in capture?["intervals"] as JArray ?? new JArray())
            {
                double left = Math.Max(start, row["s"]?.Value<double>() ?? 0), right = Math.Min(end, row["e"]?.Value<double>() ?? 0);
                if (right > left) Add(rows, (left - start) / rate, (right - start) / rate, row);
            }
            return Pack((end - start) / rate, rows, capture?["manual"]?.Value<bool>() ?? false);
        }

        // the seconds at the end of a clip that may pass without evidence, the time it takes to switch to obs and press clip, before the game focused last stops being the game the clip ends on
        private const double EndSlack = 10;

        internal static JObject Describe(JObject capture)
        {
            var result = new JObject { ["kind"] = "unknown", ["name"] = "Unassigned", ["games"] = new JArray() };
            double duration = ReadNumber(capture?["duration"]);
            if (!Number(duration) || duration <= 0 || duration > 604800) return result;
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            double last = 0, covered = 0;
            // the time each reason for having no game covers, keyed by reason and program
            var unknown = new Dictionary<string, double>(StringComparer.Ordinal);
            string endKind = "", endKey = "";
            double endAt = 0;
            var intervals = capture?["intervals"] as JArray;
            if (intervals == null || intervals.Count > 8192) return result;
            foreach (var row in intervals)
            {
                if (!(row is JObject)) return result;
                double start = ReadNumber(row["s"]), end = ReadNumber(row["e"]);
                if (!Number(start) || !Number(end) || start < last - .002 || end <= start || end > duration + .05) return result;
                last = end;
                covered += end - start;
                string kind = ReadText(row["kind"]), id = ReadText(row["id"]);
                string name = ReadText(row["name"]);
                if (kind == "unknown")
                {
                    string why = ReadText(row["why"]);
                    if (Reasons.Contains(why))
                    {
                        string reason = why + "|" + (why == "not-recognised" ? CleanApp(ReadText(row["app"])) : "");
                        unknown[reason] = (unknown.TryGetValue(reason, out double held) ? held : 0) + end - start;
                    }
                }
                if (kind != "desktop" && (kind != "game" || id.Length == 0 || id.Length > 80 || name.Length == 0 || name.Length > 160)) continue;
                string key = kind == "desktop" ? "desktop" : id;
                totals[key] = (totals.TryGetValue(key, out double amount) ? amount : 0) + end - start;
                names[key] = kind == "desktop" ? "Desktop" : name;
                endKind = kind;
                endKey = key;
                endAt = end;
            }
            var games = (JArray)result["games"];
            foreach (var pair in totals.Where(p => p.Key != "desktop" && p.Value >= Math.Min(2, duration * .1)))
                games.Add(new JObject { ["id"] = pair.Key, ["name"] = names[pair.Key] });
            var dominant = totals.OrderByDescending(p => p.Value).FirstOrDefault();
            // the game focused last before the clip was saved is what was clipped, however much else the clip holds
            if (endKind == "game" && endAt >= duration - EndSlack && games.Any(g => g["id"].Value<string>() == endKey))
            {
                result["kind"] = "game";
                result["id"] = endKey;
                result["name"] = names[endKey];
            }
            else if (totals.Count > 0 && dominant.Value >= duration * (.7 - 1e-9))
            {
                result["kind"] = dominant.Key == "desktop" ? "desktop" : "game";
                result["id"] = dominant.Key;
                result["name"] = names[dominant.Key];
            }
            else if (totals.Values.Sum() >= duration * (.7 - 1e-9) && totals.Count > 1)
            {
                result["kind"] = "mixed";
                result["name"] = "Mixed: " + string.Join(" · ", totals.OrderByDescending(p => p.Value).Select(p => names[p.Key]));
            }
            // the one game a clip without a main game holds is the game it goes by (name, folder, section) only when that game fills at least as much of it as the desktop does, so seconds of a game passing by do not take over a desktop clip
            if (result["kind"].Value<string>() == "unknown" && games.Count == 1 && totals[games[0]["id"].Value<string>()] >= (totals.TryGetValue("desktop", out double desktop) ? desktop : 0))
                result["partial"] = games[0].DeepClone();
            // a clip without a game says why, by the reason that covers most of it; time nothing was recorded for is time replaykit was not watching
            if (result["kind"].Value<string>() == "unknown")
            {
                double gap = duration - covered;
                if (gap > .5) unknown["no-evidence|"] = (unknown.TryGetValue("no-evidence|", out double held) ? held : 0) + gap;
                if (unknown.Count > 0)
                {
                    var reason = unknown.OrderByDescending(p => p.Value).First().Key.Split('|');
                    result["why"] = reason[0];
                    if (reason[1].Length > 0) result["app"] = reason[1];
                }
            }
            result["manual"] = capture?["manual"]?.Type == JTokenType.Boolean && capture["manual"].Value<bool>();
            return result;
        }

        internal static string GameName(JObject capture)
        {
            var summary = Describe(capture);
            if (summary["kind"].Value<string>() == "game") return summary["name"].Value<string>();
            return summary["partial"] is JObject partial ? partial["name"].Value<string>() : "";
        }
    }
}
