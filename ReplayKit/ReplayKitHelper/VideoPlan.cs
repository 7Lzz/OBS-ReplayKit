using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the video edits of the clip editor: the pieces of the source video that make up the saved clip, each put on the timeline of the saved clip, and the part of that timeline the saved clip is. what lies between the pieces, or past either end of them, is black. the places on the timeline are seconds of the saved clip, the source timeline divided by the speed of the video; the cuts of the pieces are seconds of the source
    internal sealed class VideoPlan
    {
        internal sealed class Piece
        {
            public double At;
            public double SrcStart;
            public double SrcEnd;

            // how long the piece is on the timeline, which the speed changes
            public double Length(double speed) => (SrcEnd - SrcStart) / speed;
        }

        // what ffprobe says about the video of the source, which the black parts have to match
        internal sealed class SourceInfo
        {
            public int Width;
            public int Height;
            public string Fps;
            public double Duration;
        }

        private const int MaxPieces = 64;

        public List<Piece> Pieces = new List<Piece>();
        // the part of the timeline that is the saved clip
        public double Start;
        public double End;

        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        private static string Num(double x)
        {
            string text = x.ToString("0.######", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        // reads what the editor sends; null and a message when anything is out of range, so a bad edit never turns into a half made clip. the pieces may not overlap, since the video is one track
        public static VideoPlan Parse(JToken json, double speed, double sourceDuration, out string error)
        {
            error = null;
            var o = json as JObject;
            var pieces = o?["pieces"] as JArray;
            var window = o?["window"] as JObject;
            if (pieces == null || window == null || pieces.Count > MaxPieces) { error = "Bad video edit"; return null; }
            var plan = new VideoPlan
            {
                Start = window.Value<double?>("start") ?? double.NaN,
                End = window.Value<double?>("end") ?? double.NaN,
            };
            if (!Finite(plan.Start) || !Finite(plan.End) || plan.Start < -86400 || plan.End > 86400 || plan.End - plan.Start < 0.5) { error = "Bad video edit"; return null; }
            foreach (var token in pieces)
            {
                var po = token as JObject;
                var piece = new Piece
                {
                    At = po?.Value<double?>("at") ?? double.NaN,
                    SrcStart = po?.Value<double?>("srcStart") ?? double.NaN,
                    SrcEnd = po?.Value<double?>("srcEnd") ?? double.NaN,
                };
                bool valid = po != null && Finite(piece.At) && Finite(piece.SrcStart) && Finite(piece.SrcEnd)
                    && piece.At >= -86400 && piece.At <= 86400
                    && piece.SrcStart >= 0 && piece.SrcEnd - piece.SrcStart >= 0.01 && piece.SrcEnd <= sourceDuration + 0.1;
                if (!valid) { error = "Bad video edit"; return null; }
                plan.Pieces.Add(piece);
            }
            plan.Pieces.Sort((a, b) => a.At.CompareTo(b.At));
            for (int i = 1; i < plan.Pieces.Count; i++)
            {
                var before = plan.Pieces[i - 1];
                if (plan.Pieces[i].At < before.At + before.Length(speed) - 0.005) { error = "Bad video edit"; return null; }
            }
            return plan;
        }

        // the filter graph that puts the saved clip together from the source video: every piece is cut out of the source and the places no piece covers are black frames of the same size and rate, all joined one after the other. the pieces keep the order of the source, which is what keeps the decoder from having to hold frames back. reads the video of input inputIndex and ends in the label vout
        public string BuildGraph(int inputIndex, double speed, SourceInfo info)
        {
            var chains = new List<string>();
            var labels = new List<string>();
            double cursor = Start;
            string speedFilter = Math.Abs(speed - 1) > 1e-6 ? ",setpts=(PTS-STARTPTS)/" + Num(speed) : ",setpts=PTS-STARTPTS";
            Action<double, double> black = (a, b) =>
            {
                string label = "v" + labels.Count;
                chains.Add("color=c=black:s=" + info.Width + "x" + info.Height + ":r=" + info.Fps + ":d=" + Num(b - a) + ",setsar=1,format=yuv420p[" + label + "]");
                labels.Add(label);
            };
            foreach (var piece in Pieces)
            {
                double pieceStart = piece.At;
                double pieceEnd = piece.At + piece.Length(speed);
                if (pieceEnd <= Start + 0.001 || pieceStart >= End - 0.001) continue;
                double a = Math.Max(pieceStart, Start);
                double b = Math.Min(pieceEnd, End);
                if (b - a < 0.001) continue;
                if (a > cursor + 0.004) black(cursor, a);
                // the part of the piece that lies inside the saved clip, in seconds of the source
                double srcA = piece.SrcStart + (a - pieceStart) * speed;
                double srcB = piece.SrcStart + (b - pieceStart) * speed;
                string label = "v" + labels.Count;
                chains.Add("[" + inputIndex + ":v:0]trim=start=" + Num(srcA) + ":end=" + Num(srcB) + speedFilter + ",fps=" + info.Fps + ",setsar=1,format=yuv420p[" + label + "]");
                labels.Add(label);
                cursor = b;
            }
            if (End > cursor + 0.001) black(cursor, End);
            if (labels.Count == 1)
                chains.Add("[" + labels[0] + "]null[vout]");
            else
                chains.Add(string.Concat(labels.Select(l => "[" + l + "]")) + "concat=n=" + labels.Count + ":v=1:a=0[vout]");
            return string.Join(";", chains);
        }
    }
}
