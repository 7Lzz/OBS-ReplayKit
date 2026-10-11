using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the audio edits of the clip editor: the audio streams of the saved clip, each one put together from pieces cut from the audio streams of the source, and the ffmpeg filter graph that makes them. every time in a plan is on the timeline of the saved clip before the window is cut out, which is the source timeline divided by the speed of the video
    internal sealed class AudioPlan
    {
        // one cut of a source audio stream; a piece that was made by combining tracks has several, each starting off seconds after the start of the piece
        internal sealed class Part
        {
            // the audio stream of the source the part is cut from, 0 being the first audio stream of the file
            public int Src;
            public double SrcStart;
            public double SrcEnd;
            public double Off;
            public double Gain = 1;

            public double Length => SrcEnd - SrcStart;
        }

        internal sealed class Segment
        {
            public List<Part> Parts = new List<Part>();
            // where the piece starts on the timeline
            public double At;
            public double Volume = 1;
            public double FadeIn;
            public double FadeOut;
            public double Speed = 1;
            public bool Pitch = true;

            // how long the parts take one after the other before the speed is applied
            public double LocalLength => Parts.Max(p => p.Off + p.Length);
            // how long the piece is on the timeline, which the speed changes
            public double Length => LocalLength / Speed;
            // a piece of one cut with nothing to mix is the plain case, and its graph stays as it always was
            public bool IsPlain => Parts.Count == 1 && Parts[0].Off < 0.0005 && Math.Abs(Parts[0].Gain - 1) < 1e-6;
        }

        internal sealed class Lane
        {
            public string Label;
            public List<Segment> Segments = new List<Segment>();
        }

        // an audio file the editor brought in from outside the clip, which parts reach by the number Src
        internal sealed class Import
        {
            public int Src;
            public string Id;
        }

        internal sealed class Rendering
        {
            public string Graph;
            // one entry for every audio stream of the saved clip in order: the label of the filter output and the name the stream carries
            public List<string> Maps = new List<string>();
            public List<string> Names = new List<string>();
        }

        // the audio streams of the source are numbered below this, and the files the editor brought in from here up
        internal const int ImportBase = 100;
        private const int MaxImports = 32;
        private const int MaxLanes = 8;
        private const int MaxSegments = 200;
        private const int MaxParts = 8;
        private static readonly Regex UnsafeLabel = new Regex(@"[^A-Za-z0-9 _.()&\-]", RegexOptions.Compiled);

        public List<Lane> Lanes = new List<Lane>();
        public List<Import> Imports = new List<Import>();

        // reads the plan the editor sends; null and a message when anything in it is out of range, so a bad plan never turns into a half made clip. muted pieces and lanes are left out here, a muted piece is the same as no piece
        public static AudioPlan Parse(JToken json, int audioCount, out string error)
        {
            error = null;
            var plan = new AudioPlan();
            var lanes = json?["lanes"] as JArray;
            if (lanes == null || lanes.Count > MaxLanes) { error = "Bad audio edit"; return null; }
            // the files brought in from outside the clip are listed by number and id; whether a file is still there is for the one who renders to check
            var importsToken = json["imports"];
            if (importsToken != null && importsToken.Type != JTokenType.Null)
            {
                var imports = importsToken as JArray;
                if (imports == null || imports.Count > MaxImports) { error = "Bad audio edit"; return null; }
                foreach (var importToken in imports)
                {
                    var io = importToken as JObject;
                    int importSrc = io?.Value<int?>("src") ?? -1;
                    string importId = io?.Value<string>("id");
                    if (importSrc < ImportBase || importSrc >= ImportBase + 100000 || !AudioImports.IsId(importId) || plan.Imports.Any(i => i.Src == importSrc)) { error = "Bad audio edit"; return null; }
                    plan.Imports.Add(new Import { Src = importSrc, Id = importId });
                }
            }
            int total = 0;
            int laneNumber = 0;
            foreach (var laneToken in lanes)
            {
                laneNumber++;
                var laneJson = laneToken as JObject;
                var segments = laneJson?["segments"] as JArray;
                if (segments == null) { error = "Bad audio edit"; return null; }
                string label = UnsafeLabel.Replace(laneJson.Value<string>("label") ?? "", "").Trim();
                if (label.Length > 60) label = label.Substring(0, 60).Trim();
                if (label.Length == 0) label = "Track " + laneNumber;
                var lane = new Lane { Label = label };
                bool laneMuted = laneJson.Value<bool?>("muted") == true;
                foreach (var segToken in segments)
                {
                    var o = segToken as JObject;
                    if (o == null || ++total > MaxSegments) { error = "Bad audio edit"; return null; }
                    var seg = new Segment
                    {
                        At = o.Value<double?>("at") ?? double.NaN,
                        Volume = o.Value<double?>("volume") ?? 1,
                        FadeIn = o.Value<double?>("fadeIn") ?? 0,
                        FadeOut = o.Value<double?>("fadeOut") ?? 0,
                        Speed = o.Value<double?>("speed") ?? 1,
                        Pitch = o.Value<bool?>("pitch") ?? true,
                    };
                    // a piece is written with its parts, and one written the plain way is one part
                    var partsJson = o["parts"] as JArray;
                    if (partsJson == null)
                    {
                        partsJson = new JArray(new JObject { ["src"] = o["src"], ["srcStart"] = o["srcStart"], ["srcEnd"] = o["srcEnd"] });
                    }
                    bool valid = partsJson.Count >= 1 && partsJson.Count <= MaxParts;
                    foreach (var partToken in partsJson)
                    {
                        var po = partToken as JObject;
                        if (po == null) { valid = false; break; }
                        var part = new Part
                        {
                            Src = po.Value<int?>("src") ?? -1,
                            SrcStart = po.Value<double?>("srcStart") ?? double.NaN,
                            SrcEnd = po.Value<double?>("srcEnd") ?? double.NaN,
                            Off = po.Value<double?>("off") ?? 0,
                            Gain = po.Value<double?>("gain") ?? 1,
                        };
                        valid = valid && part.Src >= 0 && (part.Src < audioCount || plan.Imports.Any(i => i.Src == part.Src))
                            && Finite(part.SrcStart) && Finite(part.SrcEnd) && Finite(part.Off) && Finite(part.Gain)
                            && part.SrcStart >= 0 && part.SrcEnd - part.SrcStart >= 0.01 && part.SrcEnd <= 86400
                            && part.Off >= 0 && part.Off <= 86400 && part.Gain >= 0 && part.Gain <= 4;
                        seg.Parts.Add(part);
                    }
                    valid = valid
                        && Finite(seg.At) && Finite(seg.Volume) && Finite(seg.FadeIn) && Finite(seg.FadeOut) && Finite(seg.Speed)
                        && seg.At >= -86400 && seg.At <= 86400
                        && seg.Volume >= 0 && seg.Volume <= 4
                        && seg.FadeIn >= 0 && seg.FadeIn <= 3600 && seg.FadeOut >= 0 && seg.FadeOut <= 3600
                        && seg.Speed >= 0.0625 && seg.Speed <= 16;
                    if (!valid) { error = "Bad audio edit"; return null; }
                    if (laneMuted || o.Value<bool?>("muted") == true || seg.Volume == 0) continue;
                    lane.Segments.Add(seg);
                }
                plan.Lanes.Add(lane);
            }
            return plan;
        }

        // adelay hands on frames without a timestamp when the first frame it gets is short, which a cut in the middle of a frame or a change of tempo makes it; counting the samples gives every frame its time again
        private const string AfterDelay = ",asetpts=N/SR/TB";

        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        private static string Num(double x)
        {
            string text = x.ToString("0.######", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        // atempo takes 0.5 to 2 per stage, so a bigger change is a chain of stages
        internal static string TempoChain(double speed)
        {
            var sb = new StringBuilder();
            double x = speed;
            while (x < 0.5 - 1e-9) { sb.Append(",atempo=0.5"); x /= 0.5; }
            while (x > 2.0 + 1e-9) { sb.Append(",atempo=2.0"); x /= 2.0; }
            sb.Append(",atempo=" + Num(x));
            return sb.ToString();
        }

        // the filter graph of the plan for the window start to end of the timeline, reading the audio streams of input inputIndex and each file brought in from the input importInputs gives for its number. the window may start before the timeline does and may end after it, and a piece may start before the window. null when nothing audible is left in the window. a lane with no piece in the window is not written; one lane becomes the only audio stream, several lanes become a mix followed by every lane
        public Rendering Build(double start, double end, int inputIndex, IReadOnlyList<int> sampleRates, IReadOnlyDictionary<int, int> importInputs)
        {
            double outLength = end - start;
            var chains = new List<string>();
            var laneOutputs = new List<KeyValuePair<string, string>>();
            int pieceCount = 0;
            Func<int, int> rateOf = src => src >= ImportBase ? AudioImports.SampleRate : src < sampleRates.Count && sampleRates[src] > 0 ? sampleRates[src] : 48000;
            // where a part is cut from: an audio stream of the source, or the one stream of a file that was brought in
            Func<Part, string> cutFrom = part => part.Src >= ImportBase ? "[" + importInputs[part.Src] + ":a:0]" : "[" + inputIndex + ":a:" + part.Src + "]";
            foreach (var lane in Lanes)
            {
                var pieces = new List<string>();
                foreach (var seg in lane.Segments)
                {
                    // where the piece starts, counted from the start of the window
                    double a = seg.At - start;
                    double b = a + seg.Length;
                    if (b <= 0.005 || a >= outLength - 0.005) continue;
                    string label = "s" + (pieceCount++);
                    var f = new StringBuilder();
                    if (seg.IsPlain)
                    {
                        var part = seg.Parts[0];
                        f.Append(cutFrom(part) + "atrim=start=" + Num(part.SrcStart) + ":end=" + Num(part.SrcEnd) + ",asetpts=PTS-STARTPTS");
                    }
                    else
                    {
                        // a combined piece mixes its parts first, and everything after that treats the mix like one cut
                        var partLabels = new List<string>();
                        for (int i = 0; i < seg.Parts.Count; i++)
                        {
                            var part = seg.Parts[i];
                            var pf = new StringBuilder();
                            pf.Append(cutFrom(part) + "atrim=start=" + Num(part.SrcStart) + ":end=" + Num(part.SrcEnd) + ",asetpts=PTS-STARTPTS");
                            if (Math.Abs(part.Gain - 1) > 1e-6) pf.Append(",volume=" + Num(part.Gain));
                            if (part.Off > 0.0005) pf.Append(",adelay=" + (long)Math.Round(part.Off * 1000) + ":all=1" + AfterDelay);
                            string partLabel = label + "p" + i;
                            pf.Append("[" + partLabel + "]");
                            chains.Add(pf.ToString());
                            partLabels.Add(partLabel);
                        }
                        f.Append(string.Concat(partLabels.Select(p => "[" + p + "]")) + "amix=inputs=" + partLabels.Count + ":normalize=0:duration=longest");
                    }
                    if (Math.Abs(seg.Speed - 1) > 1e-6)
                    {
                        if (seg.Pitch)
                        {
                            f.Append(TempoChain(seg.Speed));
                        }
                        else
                        {
                            int rate = rateOf(seg.Parts[0].Src);
                            f.Append(",asetrate=" + (int)Math.Round(rate * seg.Speed) + ",aresample=" + rate);
                        }
                    }
                    if (Math.Abs(seg.Volume - 1) > 1e-6) f.Append(",volume=" + Num(seg.Volume));
                    double length = seg.Length;
                    if (seg.FadeIn > 0.001) f.Append(",afade=t=in:st=0:d=" + Num(Math.Min(seg.FadeIn, length)));
                    if (seg.FadeOut > 0.001)
                    {
                        double d = Math.Min(seg.FadeOut, length);
                        f.Append(",afade=t=out:st=" + Num(length - d) + ":d=" + Num(d));
                    }
                    // the part of the piece that lies before the window is cut off after the fades, which belong to the piece
                    if (a < -0.0005)
                    {
                        f.Append(",atrim=start=" + Num(-a) + ",asetpts=PTS-STARTPTS");
                        a = 0;
                    }
                    if (a > 0.0005) f.Append(",adelay=" + (long)Math.Round(a * 1000) + ":all=1" + AfterDelay);
                    f.Append("[" + label + "]");
                    chains.Add(f.ToString());
                    pieces.Add(label);
                }
                if (pieces.Count == 0) continue;
                string laneLabel = "l" + laneOutputs.Count;
                string head = pieces.Count == 1
                    ? "[" + pieces[0] + "]"
                    : string.Concat(pieces.Select(p => "[" + p + "]")) + "amix=inputs=" + pieces.Count + ":normalize=0:duration=longest,";
                chains.Add(head + "atrim=end=" + Num(outLength) + ",asetpts=PTS-STARTPTS,apad=whole_dur=" + Num(outLength) + "[" + laneLabel + "]");
                laneOutputs.Add(new KeyValuePair<string, string>(laneLabel, lane.Label));
            }
            if (laneOutputs.Count == 0) return null;

            var result = new Rendering();
            if (laneOutputs.Count == 1)
            {
                result.Maps.Add("[" + laneOutputs[0].Key + "]");
                result.Names.Add(Trim.AudioTrackLabels[0]);
            }
            else
            {
                for (int j = 0; j < laneOutputs.Count; j++) chains.Add("[" + laneOutputs[j].Key + "]asplit=2[m" + j + "][t" + j + "]");
                chains.Add(string.Concat(laneOutputs.Select((l, j) => "[m" + j + "]")) + "amix=inputs=" + laneOutputs.Count + ":normalize=0:duration=longest[mix]");
                result.Maps.Add("[mix]");
                result.Names.Add(Trim.AudioTrackLabels[0]);
                for (int j = 0; j < laneOutputs.Count; j++)
                {
                    result.Maps.Add("[t" + j + "]");
                    result.Names.Add(laneOutputs[j].Value);
                }
            }
            result.Graph = string.Join(";", chains);
            return result;
        }
    }
}
