using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReplayKitHelper
{
    internal static class SavedClipAudio
    {
        private const int DiscordTrack = 2;
        private const string ObsAudioHandler = "OBS Audio Handler";

        internal static bool DiscordClosed()
        {
            try
            {
                var processes = Process.GetProcesses();
                try
                {
                    return !processes.Any(process => SettingsLiveApply.DiscordAudioExes.Contains(process.ProcessName + ".exe", StringComparer.OrdinalIgnoreCase));
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                Log.Write("Discord process state unavailable: " + ex.GetType().Name);
                return false;
            }
        }

        internal static void FinishObsFile(string path)
        {
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) || !DiscordClosed()) return;
            var before = new FileInfo(path);
            if (!before.Exists) return;
            long bytes = before.Length, written = before.LastWriteTimeUtc.Ticks, created = before.CreationTimeUtc.Ticks;
            if (RemoveSilentDiscord(path, Path.Combine(Constants.HelperRoot, "ffmpeg.exe"), true))
                ClipGames.RefreshedRetag(path, bytes, written, created);
        }

        internal static bool RemoveSilentDiscord(string path, string ffmpeg, bool discordClosedAtSave)
        {
            if (!discordClosedAtSave || !File.Exists(ffmpeg)) return false;
            string temporary = null;
            try
            {
                var original = new FileInfo(path);
                if (!original.Exists || (original.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var tracks = Mp4Tracks.Read(source);
                    var audio = tracks?.Where(track => track.Audio).ToArray();
                    if (audio == null || audio.Length != Trim.AudioTrackLabels.Length || !tracks.Any(track => track.Video))
                        return false;
                    for (int i = 0; i < audio.Length; i++)
                    {
                        string name = audio[i].HandlerName;
                        if (name != ObsAudioHandler && name != Trim.AudioTrackLabel(i)) return false;
                    }
                    string stream = "0:a:" + DiscordTrack.ToString(CultureInfo.InvariantCulture);
                    if (!IsSilent(path, ffmpeg, stream)) return false;

                    temporary = Path.Combine(original.DirectoryName, "_replaykit_finalize_" + Guid.NewGuid().ToString("N") + ".rkpart");
                    var arguments = new List<string>
                    {
                        "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-copyts", "-i", path,
                        "-map", "0", "-map", "-" + stream, "-c", "copy",
                    };
                    if (tracks.First(track => track.Video).SampleEntry == "hvc1")
                        arguments.AddRange(new[] { "-tag:v:0", "hvc1" });
                    var labels = Trim.AudioTrackLabels.Where((_, i) => i != DiscordTrack).ToArray();
                    for (int i = 0; i < labels.Length; i++)
                        arguments.AddRange(new[] { "-metadata:s:a:" + i.ToString(CultureInfo.InvariantCulture), "handler_name=" + labels[i] });
                    arguments.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", temporary });
                    var remux = Compression.InvokeNativeCapture(ffmpeg, arguments, CancellationToken.None, 120000);
                    var result = remux.ExitCode == 0 ? Mp4Tracks.ReadFile(temporary) : null;
                    if (result == null || result.Count != tracks.Count - 1 ||
                        !result.Where(track => track.Audio).Select(track => track.HandlerName).SequenceEqual(labels) ||
                        !result.Where(track => track.Video).Select(track => track.SampleEntry).SequenceEqual(tracks.Where(track => track.Video).Select(track => track.SampleEntry)))
                    {
                        Log.Write("Silent Discord track kept: the MP4 stream copy could not be verified.");
                        return false;
                    }
                    var normalized = Mp4Tracks.NormalizeAudio(temporary);
                    if (normalized == Mp4Tracks.Fix.Busy || normalized == Mp4Tracks.Fix.Failed) return false;
                }
                var current = new FileInfo(path);
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    current.Length != original.Length || current.LastWriteTimeUtc != original.LastWriteTimeUtc ||
                    current.CreationTimeUtc != original.CreationTimeUtc || !DiscordClosed()) return false;
                File.Replace(temporary, path, null);
                try
                {
                    File.SetCreationTimeUtc(path, original.CreationTimeUtc);
                    File.SetLastWriteTimeUtc(path, original.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("Clip audio finalize could not restore the file timestamps: " + ex.GetType().Name);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException ||
                ex is Win32Exception || ex is TimeoutException)
            {
                Log.Write("Silent Discord track kept: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("Clip audio scratch cleanup: " + ex.GetType().Name); }
                }
            }
        }

        private static bool IsSilent(string path, string ffmpeg, string stream)
        {
            var scan = Compression.InvokeNativeCapture(ffmpeg, new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "info", "-nostats", "-xerror", "-i", path, "-map", stream,
                "-af", "astats=measure_perchannel=none:measure_overall=Peak_level+Number_of_samples+Number_of_NaNs+Number_of_Infs",
                "-c:a", "pcm_f32le", "-f", "null", "NUL",
            }, CancellationToken.None, 120000);
            if (scan.ExitCode != 0) return false;
            var stats = scan.Output.Where(line => line.StartsWith("[Parsed_astats_", StringComparison.Ordinal))
                .Select(line => line.Substring(line.IndexOf(']') + 1).Trim()).ToArray();
            return stats.Contains("Peak level dB: -inf") && ReadStat(stats, "Number of samples: ") > 0 &&
                ReadStat(stats, "Number of NaNs: ") == 0 && ReadStat(stats, "Number of Infs: ") == 0;
        }

        private static double ReadStat(string[] stats, string name)
        {
            var line = stats.FirstOrDefault(value => value.StartsWith(name, StringComparison.Ordinal));
            return line != null && double.TryParse(line.Substring(name.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value : double.NaN;
        }
    }
}
