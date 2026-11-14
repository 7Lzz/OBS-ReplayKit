using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // scans a clips keyframe (i-frame) timestamps with ffprobe, for the trim uis snap-to-keyframe markers. two strategies: a fast packet-flags pass first, falling back to a slower full frame decode pass when the fast pass comes back empty (some containers/codecs dont expose keyframe flags on the packet stream). runs as an in-process task from Trim.StartTrimKeyframeScan and returns its result directly.
    internal static class TrimKeyframesWorker
    {
        private static void AddKeyframeTime(List<double> times, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "N/A", StringComparison.OrdinalIgnoreCase)) return;
            string clean = raw.Trim().Trim('"');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0)
                times.Add(value);
        }

        private static List<double> CompleteKeyframeTimes(List<double> times)
        {
            var unique = new List<double>();
            double last = double.NaN;
            foreach (var time in times.OrderBy(t => t))
            {
                if (double.IsNaN(last) || Math.Abs(time - last) > 0.001)
                {
                    unique.Add(time);
                    last = time;
                }
            }
            if (unique.Count == 0 || Math.Abs(unique[0]) > 0.001) unique.Insert(0, 0.0);
            return unique;
        }

        private static void AddPacketKeyframeTime(List<double> times, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            int lastComma = line.LastIndexOf(',');
            if (lastComma < 0 || line.IndexOf('K', lastComma + 1) < 0) return;
            var parts = line.Split(',');
            AddKeyframeTime(times, parts[0]);
        }

        private static List<double> ConvertFrameJsonToKeyframes(string json)
        {
            var data = JObject.Parse(json);
            var times = new List<double>();
            if (data["frames"] is JArray frames)
            {
                foreach (var frame in frames)
                {
                    string raw = frame["best_effort_timestamp_time"]?.Value<string>();
                    if (string.IsNullOrEmpty(raw)) raw = frame["pkt_pts_time"]?.Value<string>();
                    AddKeyframeTime(times, raw ?? "");
                }
            }
            return CompleteKeyframeTimes(times);
        }

        private static string ShortOutput(string combined)
        {
            return combined.Length > 300 ? combined.Substring(0, 300) + "..." : combined;
        }

        public static KeyframeScanResult Run(string ffprobe, string sourcePath, string clipName, CancellationToken cancellationToken)
        {
            try
            {
                using var reader = JobCoordinator.ReadSource(sourcePath);
                if (!File.Exists(ffprobe)) return new KeyframeScanResult { Ok = false, Message = "ffprobe.exe not found" };
                if (!File.Exists(sourcePath)) return new KeyframeScanResult { Ok = false, Message = "Source clip not found" };

                var sw = Stopwatch.StartNew();
                var packetTimes = new List<double>();
                var packetOutput = new System.Text.StringBuilder(301);
                var packetResult = Compression.InvokeNativeCapture(ffprobe, new[]
                {
                    "-v", "error", "-select_streams", "v:0", "-show_packets",
                    "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", sourcePath,
                }, cancellationToken, onStdoutLine: line =>
                {
                    AddPacketKeyframeTime(packetTimes, line);
                    if (packetOutput.Length < 301)
                    {
                        packetOutput.Append(line, 0, Math.Min(line.Length, 301 - packetOutput.Length));
                        if (packetOutput.Length < 301) packetOutput.Append('\n');
                    }
                });
                string method = "packets";
                foreach (var line in packetResult.Output) AddPacketKeyframeTime(packetTimes, line);
                var unique = packetResult.ExitCode == 0 ? CompleteKeyframeTimes(packetTimes) : new List<double>();

                if (packetResult.ExitCode != 0 || unique.Count <= 1)
                {
                    var frameResult = Compression.InvokeNativeCapture(ffprobe, new[]
                    {
                        "-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-show_frames",
                        "-show_entries", "frame=best_effort_timestamp_time,pkt_pts_time", "-of", "json=compact=1", sourcePath,
                    }, cancellationToken);
                    method = "frames";
                    if (frameResult.ExitCode != 0)
                    {
                        string packetMsg = packetResult.ExitCode != 0 ? ShortOutput(packetOutput + string.Join("\n", packetResult.Output)) : "packet probe returned no keyframe packet data";
                        string frameMsg = ShortOutput(string.Join("\n", frameResult.Output));
                        sw.Stop();
                        return new KeyframeScanResult
                        {
                            Ok = false,
                            Message = string.Format("ffprobe keyframe probe failed (packet exit={0}, frame exit={1}): {2} {3}", packetResult.ExitCode, frameResult.ExitCode, packetMsg, frameMsg),
                            ProbeMs = (int)sw.ElapsedMilliseconds,
                        };
                    }

                    string json = string.Join("\n", frameResult.Output);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        sw.Stop();
                        return new KeyframeScanResult { Ok = false, Message = "ffprobe returned no keyframe data", ProbeMs = (int)sw.ElapsedMilliseconds };
                    }
                    unique = ConvertFrameJsonToKeyframes(json);
                }
                sw.Stop();

                return new KeyframeScanResult
                {
                    Ok = true,
                    Name = clipName,
                    Keyframes = unique,
                    Count = unique.Count,
                    Cached = false,
                    Method = method,
                    ProbeMs = (int)sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                return new KeyframeScanResult { Ok = false, Message = Locales.T("Could not read keyframes: {error}", "error", ex.Message) };
            }
        }
    }
}
