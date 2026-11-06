using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ReplayKitHelper
{
    // where a precise save starts reading the source: at a keyframe near the cut instead of the start of the clip, which skips decoding everything before it, taken only when the packets show that the frames from there come out of the decoder with the same times as when the whole clip before them is decoded
    internal static class TrimSeek
    {
        internal struct Packet
        {
            public long Pts;
            public long Dts;
            public bool Key;
        }

        // how many packets apart a frame and the packet that times it may be, which bounds the frames the decoder holds back
        private const int Window = 16;

        // runs ffprobe over the source up to a little past the cut and returns the second to seek the input to, or null to read it from the start; from and to are the first and last second of the source the save shows
        internal static decimal? Find(string ffprobe, string path, decimal from, decimal to, CancellationToken token)
        {
            // a second before the cut stays for the output seek, so a cut in the first seconds has nothing worth skipping
            if (from < 3 || string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return null;
            try
            {
                decimal? point = Probe(ffprobe, path, from, to, token);
                Log.Write("Trim read point: " + (point == null ? "start of the clip" : point.Value.ToString("0.######", CultureInfo.InvariantCulture) + " s") + " for a cut from " + from.ToString("0.###", CultureInfo.InvariantCulture) + " s");
                return point;
            }
            // a scan that cannot finish leaves the save reading from the start, and a cancel is seen again by the encode that follows
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is OperationCanceledException || ex is TimeoutException)
            {
                Log.Write("Trim read point scan failed: " + ex.Message);
                return null;
            }
        }

        private static decimal? Probe(string ffprobe, string path, decimal from, decimal to, CancellationToken token)
        {
            var info = Compression.InvokeNativeCapture(ffprobe, new[] { "-v", "error", "-show_entries", "stream=codec_type,time_base:format=start_time", "-of", "csv=p=0", path }, token);
            if (info.ExitCode != 0) return null;
            // the video first, then every audio stream, since a save maps the first video and all the audio
            var bases = new List<long>();
            bool video = false;
            long startUs = -1;
            foreach (string line in info.Output)
            {
                var parts = line.Trim().Split(',');
                if (parts.Length == 2 && (parts[0] == "video" || parts[0] == "audio"))
                {
                    if (parts[0] == "video" && video) continue;
                    var tb = parts[1].Split('/');
                    if (tb.Length != 2 || tb[0] != "1" || !long.TryParse(tb[1], NumberStyles.None, CultureInfo.InvariantCulture, out long den) || den <= 0) return null;
                    if (parts[0] == "video") { bases.Insert(0, den); video = true; }
                    else bases.Add(den);
                }
                else if (parts.Length == 1 && decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal start))
                {
                    startUs = (long)Math.Round(start * 1000000, MidpointRounding.AwayFromZero);
                }
            }
            if (!video || startUs < 0) return null;
            string until = "%+" + (to + 3).ToString("0.###", CultureInfo.InvariantCulture);
            var scan = Compression.InvokeNativeCapture(ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-read_intervals", until, "-show_entries", "packet=pts,dts,flags", "-of", "csv=p=0", path }, token, 120000);
            if (scan.ExitCode != 0) return null;
            var packets = new List<Packet>();
            foreach (string line in scan.Output)
            {
                var parts = line.Trim().Split(',');
                if (parts.Length != 3) continue;
                // a packet without both times leaves the decoder guessing in ways this cannot follow
                if (!long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long pts) || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long dts)) return null;
                packets.Add(new Packet { Pts = pts, Dts = dts, Key = parts[2].StartsWith("K", StringComparison.Ordinal) });
            }
            return ReadPoint(packets, bases, startUs, from);
        }

        // the decoder times each frame by its pts until it has seen the pts go backwards more often than the dts, and by its dts from then on (guess_correct_pts in libavcodec), so a read that starts at a keyframe has not counted the faults before it and can time a frame differently from a read of the whole clip -- obs leaves two frames with one pts where the pieces of a disk buffer clip are joined. a keyframe is taken when no fault lies before it, or when a fault after it, which turns that read over to the dts too, is done before the cut. the read point sits on the microsecond grid that every time base holds exactly, so the seek moves every timestamp by a whole number of ticks
        internal static decimal? ReadPoint(IList<Packet> packets, IList<long> bases, long startUs, decimal from)
        {
            if (packets.Count == 0 || bases.Count == 0 || startUs < 0) return null;
            long den = bases[0];
            long unit = 1;
            foreach (long d in bases)
            {
                unit = unit / Gcd(unit, 1000000 / Gcd(1000000, d)) * (1000000 / Gcd(1000000, d));
                if (unit > 1000000) return null;
            }
            // the start of the file in ticks, rounded the way ffmpeg moves the timestamps by it
            long offset = (long)Math.Round((decimal)startUs * den / 1000000, MidpointRounding.AwayFromZero);
            decimal cut = offset + from * den;
            // pairs of packets with one pts, earlier first; any other pts going backwards past the reorder window is a fault this cannot place
            var pairs = new List<int[]>();
            var firstAt = new Dictionary<long, int>();
            long settled = long.MinValue;
            for (int i = 0; i < packets.Count; i++)
            {
                var p = packets[i];
                if (p.Pts < p.Dts || (i > 0 && p.Dts <= packets[i - 1].Dts)) return null;
                if (i >= Window) settled = Math.Max(settled, packets[i - Window].Pts);
                if (p.Pts <= settled) return null;
                if (firstAt.TryGetValue(p.Pts, out int first)) pairs.Add(new[] { first, i });
                else firstAt[p.Pts] = i;
            }
            var keys = new List<int>();
            for (int i = 0; i < packets.Count; i++) if (packets[i].Key) keys.Add(i);
            for (int n = keys.Count - 1; n >= 0; n--)
            {
                var key = packets[keys[n]];
                // past this keyframe by its pts and its dts and short of the next by both, so the seek lands on it whichever the demuxer goes by
                long lo = Math.Max(key.Pts, key.Dts) + 1;
                long hi = n + 1 < keys.Count ? Math.Min(packets[keys[n + 1]].Pts, packets[keys[n + 1]].Dts) : long.MaxValue;
                decimal us = Math.Ceiling((decimal)(lo - offset) * 1000000 / den / unit) * unit;
                if (us > (from - 1) * 1000000) continue;
                if (us <= 0) return null;
                if (offset + us * den / 1000000 >= hi) continue;
                if (Same(packets, pairs, keys[n], cut)) return us / 1000000;
            }
            return null;
        }

        // whether a read from the keyframe at index key gives every frame from the cut on the time a read of the whole clip gives it
        private static bool Same(IList<Packet> packets, List<int[]> pairs, int key, decimal cut)
        {
            long keyPts = packets[key].Pts;
            bool before = false;
            int[] first = null;
            foreach (var pair in pairs)
            {
                // a fault the read from the keyframe decodes both frames of; frames shown before the keyframe are dropped by the decoder after a seek
                if (pair[0] >= key && packets[pair[0]].Pts >= keyPts)
                {
                    if (first == null || packets[pair[0]].Pts < packets[first[0]].Pts) first = pair;
                }
                else before = true;
            }
            if (first == null) return !before;
            // the decoder pipeline is still filling right after a seek, so a fault that close to the keyframe is not taken as timed the same
            if (first[0] < key + Window) return false;
            if (!before) return true;
            // until that fault the read from the keyframe times frames by pts and the whole read by dts, so those frames must fall before the cut either way: their pts are at most the pts of the fault and their dts at most the dts of a packet decoded by the time it comes out
            long reach = packets[first[0]].Pts;
            for (int i = first[1]; i < Math.Min(packets.Count, first[1] + Window); i++) reach = Math.Max(reach, packets[i].Dts);
            return reach + 1 < cut;
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0) { long t = a % b; a = b; b = t; }
            return a;
        }
    }
}
