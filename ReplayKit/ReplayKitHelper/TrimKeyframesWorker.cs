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
    // scans a clips keyframe (i-frame) timestamps with ffprobe, for the trim uis snap-to-keyframe markers. two strategies: a fast packet-flags pass first, falling back to a slower full frame decode pass when the fast pass comes back empty (some containers/codecs dont expose keyframe flags on the packet stream). runs as an in-process Task from Trim.StartTrimKeyframeScan instead of a spawned child process writing a json status file -- the caller reads Task.Result directly, so Write-Result/the status-file round-trip from the ps original has no equivalent here. ported from obs_replaykit helper trim_keyframes_worker.ps1.
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

        private static List<double> ConvertPacketOutputToKeyframes(List<string> output)
        {
            var times = new List<double>();
            foreach (var line in output)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                string flags = parts[parts.Length - 1];
                if (!flags.Contains("K")) continue;
                AddKeyframeTime(times, parts[0]);
            }
            return CompleteKeyframeTimes(times);
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

        private static string ShortOutput(List<string> output)
        {
            string combined = string.Join("\n", output);
            return combined.Length > 300 ? combined.Substring(0, 300) + "..." : combined;
        }

        public static KeyframeScanResult Run(string ffprobe, string sourcePath, string clipName, CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(ffprobe)) return new KeyframeScanResult { Ok = false, Message = "ffprobe.exe not found" };
                if (!File.Exists(sourcePath)) return new KeyframeScanResult { Ok = false, Message = "Source clip not found" };

                var sw = Stopwatch.StartNew();
                var packetResult = Compression.InvokeNativeCapture(ffprobe, new[]
                {
                    "-v", "error", "-select_streams", "v:0", "-show_packets",
                    "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", sourcePath,
                }, cancellationToken);
                string method = "packets";
                var unique = packetResult.ExitCode == 0 ? ConvertPacketOutputToKeyframes(packetResult.Output) : new List<double>();

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
                        string packetMsg = packetResult.ExitCode != 0 ? ShortOutput(packetResult.Output) : "packet probe returned no keyframe packet data";
                        string frameMsg = ShortOutput(frameResult.Output);
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
                return new KeyframeScanResult { Ok = false, Message = "Could not read keyframes: " + ex.Message };
            }
        }
    }
}
