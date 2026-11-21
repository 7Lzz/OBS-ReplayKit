using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // the card the share window shows while obs is away: what it says and shows comes from the share_cover settings, the theme and the language, the plugin draws its picture into a png (qt there has the svg, fonts and image scaling), and the window is told to show it, over the moving theme ground it draws itself or a picked video it loops, and to loop its sound
    internal static class ShareCover
    {
        private const int MaxImageWidth = 3840;
        private const int MaxImageHeight = 2160;
        private const int MaxSoundSeconds = 600;
        public const int MaxVideoSeconds = 30;
        // a video that players show as 30 seconds can run a few frames over, so up to half a second more still counts as 30
        private const double VideoSecondsSlack = 0.5;
        // the converted video is at most this big and this fast, the most the share window presents the card at, and its bitrate is capped so the share window holds the whole file in memory
        private const int MaxVideoWidth = 1920;
        private const int MaxVideoHeight = 1080;
        private const int MaxVideoFps = 30;
        private static readonly object Gate = new object();
        private static string appliedSignature;
        // how the card is drawn apart from the theme and the share_cover colours: the sizes at size 100 in pixels of a 1080 pixel tall card (the logo side, the text height, the larger text height of the theme card, the plate a picked image puts behind the text), the weight of the text, how strong the outline logo shows, and the colour the logo takes over an image, a video or the last frame; the plugin draws the card with them and the card editor of the settings page shows the same, while the moving ground of the theme card is the shader in ui/_share-motion.html
        private static readonly JObject Look = new JObject
        {
            ["logo"] = 220, ["text"] = 120, ["themeText"] = 148, ["textWeight"] = 700, ["logoOpacity"] = 0.85,
            ["plateX"] = 56, ["plateY"] = 28, ["plateRadius"] = 24,
            ["lightText"] = "#f2f2f2",
        };

        public static string ImagePath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverImageFile);
        private static string VideoPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverVideoFile);
        private static string VideoSoundPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverVideoSoundFile);
        private static string VideoSelectionPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverVideoSelectionFile);
        public static string SoundPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverSoundFile);
        public static string CardPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverCardFile);
        // the sound the default card plays, shipped from resources/sounds as opus since a wav of it was most of the setup exe, and the wav of it the share window plays; without the shipped file the default card is silent
        private static string DefaultSoundSource => Path.GetFullPath(Path.Combine(Constants.HelperRoot, "..", "..", "sounds", "share-card-default.opus"));
        private static string DefaultSoundPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverDefaultSoundFile);
        // the shipped file a conversion failed for, so a file ffmpeg cannot read is not tried again at every park
        private static string failedDefaultSound;

        private const string NoVideo = "none";

        private static string VersionPath(string path, string revision) =>
            Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "." + revision + Path.GetExtension(path));

        private static (string Video, string Sound) SelectedVideo()
        {
            var selection = new FileInfo(VideoSelectionPath);
            if (!selection.Exists) return (VideoPath, VideoSoundPath);
            if (selection.Length > 32) throw new IOException("Invalid share cover video selection.");
            string value = File.ReadAllText(selection.FullName);
            if (value == NoVideo) return (null, null);
            if (!Guid.TryParseExact(value, "N", out var revision)) throw new IOException("Invalid share cover video selection.");
            string token = revision.ToString("N");
            return (VersionPath(VideoPath, token), VersionPath(VideoSoundPath, token));
        }

        private static void SelectVideo(string revision)
        {
            lock (Gate)
            {
                var previous = SelectedVideo();
                AppConfig.WriteUtf8(VideoSelectionPath, revision);
                Remove(previous.Video);
                Remove(previous.Sound);
            }
        }

        public static string CurrentVideoPath()
        {
            lock (Gate)
            {
                string path = SelectedVideo().Video;
                return File.Exists(path) ? path : null;
            }
        }

        public static void RemoveVideo()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VideoSelectionPath));
            SelectVideo(NoVideo);
        }

        public static JObject Status()
        {
            // the size the card is drawn at, so the card editor has its shape; zero while no share window has a texture
            var state = ShareWindow.KnownWindowAlive() ? ShareWindow.Send("STATE") : null;
            bool hasVideo, videoHasSound;
            lock (Gate)
            {
                var selected = SelectedVideo();
                hasVideo = File.Exists(selected.Video);
                videoHasSound = hasVideo && File.Exists(selected.Sound);
            }
            return new JObject
            {
                ["ok"] = true,
                ["hasImage"] = File.Exists(ImagePath),
                ["hasVideo"] = hasVideo,
                ["videoHasSound"] = videoHasSound,
                ["maxVideoSeconds"] = MaxVideoSeconds,
                ["hasSound"] = File.Exists(SoundPath),
                ["hasDefaultSound"] = File.Exists(DefaultSoundSource),
                ["hasCard"] = File.Exists(CardPath),
                ["width"] = state?["texture"]?[0]?.Value<int>() ?? 0,
                ["height"] = state?["texture"]?[1]?.Value<int>() ?? 0,
                ["look"] = Look.DeepClone(),
            };
        }

        // a #rrggbb or #rrggbbaa setting as the #rrggbb colour and the opacity from 0 to 1 the plugin draws with
        private static (string Color, double Opacity) SplitOpacity(string value) =>
            value.Length == 9 ? (value.Substring(0, 7), Convert.ToInt32(value.Substring(7, 2), 16) / 255.0) : (value, 1.0);

        private static string Stamp(string path) => path != null && File.Exists(path) ? new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : "";

        // the sound the card plays now, or null for none; a video background plays the sound of its video while share_cover_video_sound is on and the video has one, and the card sound otherwise
        private static string SoundFor(JObject settings, string videoSound)
        {
            if ((settings["share_cover_video_sound"]?.Value<bool>() ?? SettingDefaults.ShareCoverVideoSound) && File.Exists(videoSound)) return videoSound;
            switch (settings["share_cover_sound"]?.Value<string>() ?? SettingDefaults.ShareCoverSound)
            {
                case "custom": return File.Exists(SoundPath) ? SoundPath : null;
                case "default": return DefaultSoundWav();
                default: return null;
            }
        }

        // the wav of the default sound, made again when the shipped file is newer than it, which an update that ships another sound gives; null while ffmpeg is not downloaded yet, which a later park tries again
        private static string DefaultSoundWav()
        {
            string source = DefaultSoundSource;
            string wav = DefaultSoundPath;
            if (!File.Exists(source)) return null;
            if (File.Exists(wav) && File.GetLastWriteTimeUtc(wav) >= File.GetLastWriteTimeUtc(source)) return wav;
            string stamp = Stamp(source);
            if (stamp == failedDefaultSound) return null;
            string ffmpeg = Compression.FindCompressionFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg)) return null;
            if (ConvertSound(source, wav)["ok"]?.Value<bool>() == true) return wav;
            failedDefaultSound = stamp;
            return null;
        }

        // brings the card and sound of the running share window in line with the settings; drawn again only when something it is made of changed, unless forced
        public static void Apply(JObject settings, bool force)
        {
            lock (Gate)
            {
                if (settings == null || !ShareWindow.KnownWindowAlive()) return;
                var state = ShareWindow.Send("STATE");
                int width = state["texture"]?[0]?.Value<int>() ?? 0;
                int height = state["texture"]?[1]?.Value<int>() ?? 0;
                // a window that just started gets its texture size within a second, once the plugin finds it
                for (int i = 0; force && i < 12 && width < 16; i++)
                {
                    System.Threading.Thread.Sleep(250);
                    state = ShareWindow.Send("STATE");
                    width = state["texture"]?[0]?.Value<int>() ?? 0;
                    height = state["texture"]?[1]?.Value<int>() ?? 0;
                }
                // the card is drawn at the canvas size, which the window only knows once obs sent it frames; the next park tries again
                if (state["ok"]?.Value<bool>() != true || width < 16 || height < 16) return;

                string background = settings["share_cover_background"]?.Value<string>() ?? SettingDefaults.ShareCoverBackground;
                var selected = background == "video" ? SelectedVideo() : (Video: (string)null, Sound: (string)null);
                // an image or video background whose file is gone draws the theme card, since the setting alone shows nothing
                if (background == "image" && !File.Exists(ImagePath)) background = "theme";
                if (background == "video" && !File.Exists(selected.Video)) background = "theme";
                var colors = SettingsHttp.ActiveMenuColors(settings);
                var written = settings["share_cover_text"];
                string text = written == null || written.Type == JTokenType.Null ? Locales.Message("screenshare.coverDefaultText") : written.Value<string>().Trim();
                var textColor = SplitOpacity(settings["share_cover_text_color"]?.Value<string>() ?? SettingDefaults.ShareCoverTextColor);
                var boxColor = SplitOpacity(settings["share_cover_box_color"]?.Value<string>() ?? SettingDefaults.ShareCoverBoxColor);
                var request = new JObject
                {
                    ["out"] = CardPath,
                    ["width"] = width,
                    ["height"] = height,
                    ["background"] = background,
                    ["ground"] = colors["bg"]?.Value<string>() ?? "",
                    ["ink"] = colors["text"]?.Value<string>() ?? "",
                    ["image"] = background == "image" ? ImagePath : "",
                    ["fit"] = settings["share_cover_fit"]?.Value<string>() ?? SettingDefaults.ShareCoverFit,
                    ["text"] = text,
                    ["textColor"] = textColor.Color,
                    ["textOpacity"] = textColor.Opacity,
                    // the box behind the text is only drawn over a picked image or video, which can be any colour under it
                    ["boxColor"] = boxColor.Color,
                    ["boxOpacity"] = boxColor.Opacity,
                    // the default card has the obs icon over its text; a picked image or video is the picture itself
                    ["icon"] = background == "theme" || background == "lastframe",
                    ["layout"] = settings["share_cover_layout"] as JObject ?? ReplaykitSettings.DefaultShareCoverLayout(),
                    ["look"] = Look.DeepClone(),
                };
                string sound = SoundFor(settings, background == "video" ? selected.Sound : null);
                string video = background == "video" ? selected.Video : null;
                string backdrop = background == "theme" ? $"BACKDROP {colors["bg"]} {colors["accent"]}" : "BACKDROP none";
                // a card that fades in or moves is presented no faster than obs records
                double fps = (settings["fpsNumerator"]?.Value<double>() ?? 0) / (settings["fpsDenominator"]?.Value<double>() ?? 0);
                string rate = "FPS " + fps.ToString("0.###", CultureInfo.InvariantCulture);
                int volume = settings["share_cover_sound_volume"]?.Value<int>() ?? SettingDefaults.ShareCoverSoundVolume;
                long obsWindow;
                lock (Server.State.IpcLock) obsWindow = Server.State.ObsMainWindowHwnd;
                // the plugin that draws the card only changes when obs starts again, so each obs start draws it once and a plugin update shows up on the card
                string signature = request.ToString(Formatting.None) + "|" + backdrop + "|" + rate + "|" + Stamp(request["image"].Value<string>()) + "|" + Stamp(video) + "|" + (sound ?? "") + "|" + Stamp(sound) + "|" + volume + "|" + state["pid"] + "|" + obsWindow;
                if (!force && signature == appliedSignature) return;

                if (!PipeClient.RenderShareCardAndWait(request.ToString(Formatting.None), 8000))
                {
                    Log.Write("warn: the share card could not be drawn; the share window keeps the card it had");
                    return;
                }
                // the video goes first, so a card drawn to go over it never shows over the plain ground
                var videoReply = ShareWindow.Send(video == null ? "VIDEO none" : "VIDEO " + request["fit"].Value<string>() + " " + video);
                var backdropReply = ShareWindow.Send(backdrop);
                var rateReply = ShareWindow.Send(rate);
                var card = ShareWindow.Send("CARD " + (background == "theme" ? "theme " : background == "lastframe" ? "over " : background == "video" ? "video " : "full ") + CardPath);
                var soundReply = ShareWindow.Send(sound == null ? "SOUND none" : "SOUND " + volume + " " + sound);
                bool ok = videoReply["ok"]?.Value<bool>() == true && backdropReply["ok"]?.Value<bool>() == true && rateReply["ok"]?.Value<bool>() == true && card["ok"]?.Value<bool>() == true && soundReply["ok"]?.Value<bool>() == true;
                Log.Write("Share card " + width + "x" + height + " " + background + " at " + rate.Substring(4) + " fps" + (sound == null ? " without sound" : " with " + Path.GetFileName(sound)) + (ok ? "" : ": " + (videoReply["message"] ?? backdropReply["message"] ?? rateReply["message"] ?? card["message"] ?? soundReply["message"])));
                if (ok) appliedSignature = signature;
            }
        }

        // one pick for the own image or video: a still image is saved as the image, while a gif with more than one frame and any video are converted to the looping card video, since the png the card draws keeps only the first frame; the other kind is dropped so the background is always the last pick
        public static JObject ImportMedia(string source)
        {
            bool still = IsStillImage(source);
            var result = still ? ImportImage(source) : ImportVideo(source);
            if (result["ok"]?.Value<bool>() != true) return result;
            if (still) RemoveVideo();
            else Remove(ImagePath);
            result["background"] = still ? "image" : "video";
            return result;
        }

        private static bool IsStillImage(string source)
        {
            try
            {
                using (var picked = Image.FromFile(source))
                    return !picked.RawFormat.Equals(ImageFormat.Gif) || picked.GetFrameCount(FrameDimension.Time) <= 1;
            }
            catch (Exception ex) when (ex is OutOfMemoryException || ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException || ex is System.Runtime.InteropServices.ExternalException)
            {
                // gdi+ reports a file that is not an image as out of memory, and anything it cannot read goes to ffmpeg as a video
                return false;
            }
        }

        // the picked image, scaled down to 4k at most and saved as a png in user-data, so the card never reads a file the user moves or deletes later
        private static JObject ImportImage(string source)
        {
            try
            {
                using (var picked = Image.FromFile(source))
                {
                    double scale = Math.Min(1.0, Math.Min((double)MaxImageWidth / picked.Width, (double)MaxImageHeight / picked.Height));
                    int width = Math.Max(1, (int)Math.Round(picked.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(picked.Height * scale));
                    using (var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(copy))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(picked, 0, 0, width, height);
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(ImagePath));
                        string temporary = ImagePath + ".tmp";
                        copy.Save(temporary, ImageFormat.Png);
                        Native.MoveFileReplace(temporary, ImagePath);
                    }
                }
                return new JObject { ["ok"] = true };
            }
            catch (Exception ex) when (ex is OutOfMemoryException || ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException || ex is System.Runtime.InteropServices.ExternalException)
            {
                // gdi+ reports a file that is not an image as out of memory
                return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theImageCouldNotBeUsed") };
            }
        }

        // the picked sound converted to a wav the share window plays
        public static JObject ImportSound(string source) => ConvertSound(source, SoundPath);

        // a sound as the wav the share window plays (48 khz stereo, ten minutes at most), so any format ffmpeg reads works
        private static JObject ConvertSound(string source, string target)
        {
            string ffmpeg = Compression.FindCompressionFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg)) return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theSoundNeedsFfmpeg") };
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temporary = target + ".tmp.wav";
            try
            {
                var result = Compression.InvokeNativeCapture(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn", "-t", MaxSoundSeconds.ToString(), "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", temporary }, timeoutMs: 120000);
                if (result.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length < 1024)
                {
                    Log.Write("Share card sound conversion failed: " + string.Join(" ", result.Output));
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theSoundCouldNotBeUsed") };
                }
                Native.MoveFileReplace(temporary, target);
                return new JObject { ["ok"] = true };
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // the picked video converted for the share window: h.264 in mp4, which media foundation on every windows decodes, no bigger or faster than the card is presented, and its sound as a wav; a picture longer than MaxVideoSeconds is refused rather than cut, while its sound may run on past the picture up to MaxSoundSeconds and then loops on its own
        private static JObject ImportVideo(string source)
        {
            string ffmpeg = Compression.FindCompressionFfmpeg();
            string ffprobe = Compression.FindCompressionFfprobe();
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg) || string.IsNullOrEmpty(ffprobe) || !File.Exists(ffprobe)) return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theVideoNeedsFfmpeg") };
            var unusable = new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theVideoCouldNotBeUsed") };
            var tooLong = new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theVideoIsTooLong", "count", MaxVideoSeconds) };
            double limit = MaxVideoSeconds + VideoSecondsSlack;

            // the picture is a video stream that is no cover picture, the kind an audio file carries, which is what 0:V:0 picks below
            var probe = Probe(ffprobe, source);
            var picture = (probe?["streams"] as JArray)?.FirstOrDefault(s => s["codec_type"]?.Value<string>() == "video" && s["disposition"]?["attached_pic"]?.Value<int>() != 1);
            if (picture == null) return unusable;
            bool hasSound = ((JArray)probe["streams"]).Any(s => s["codec_type"]?.Value<string>() == "audio");
            // only the picture counts, since the sound may run longer; a picture that names its length is refused before any work, one that does not is measured once converted
            double named = Seconds(picture["duration"]);
            if (named > limit) return tooLong;
            double fps = FrameRate(picture["r_frame_rate"]?.Value<string>());

            Directory.CreateDirectory(Path.GetDirectoryName(VideoPath));
            string revision = Guid.NewGuid().ToString("N");
            string temporaryVideo = VersionPath(VideoPath, revision);
            string temporarySound = VersionPath(VideoSoundPath, revision);
            string fittedSound = VersionPath(VideoSoundPath, revision + ".fit");
            bool published = false;
            try
            {
                string filter = "scale=w='min(" + MaxVideoWidth + ",iw)':h='min(" + MaxVideoHeight + ",ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:out_color_matrix=bt709,setsar=1" + (fps > MaxVideoFps ? ",fps=" + MaxVideoFps : "") + ",format=yuv420p";
                var converted = Compression.InvokeNativeCapture(ffmpeg, new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:V:0", "-an", "-sn", "-dn", "-t", limit.ToString("0.###", CultureInfo.InvariantCulture),
                    "-vf", filter, "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-maxrate", "12M", "-bufsize", "24M",
                    "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv", "-movflags", "+faststart", temporaryVideo,
                }, timeoutMs: 300000);
                double seconds = File.Exists(temporaryVideo) ? Seconds(Probe(ffprobe, temporaryVideo)?["format"]?["duration"]) : 0;
                if (converted.ExitCode != 0 || seconds <= 0)
                {
                    Log.Write("Share card video import failed: " + string.Join(" ", converted.Output));
                    return unusable;
                }
                // a cut at the limit is as long as the limit, which only a source longer than it gives
                if (seconds >= limit - 0.01) return tooLong;
                double soundSeconds = 0;
                if (hasSound)
                {
                    var sound = Compression.InvokeNativeCapture(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:a:0", "-vn", "-t", MaxSoundSeconds.ToString(), "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", temporarySound }, timeoutMs: 120000);
                    soundSeconds = File.Exists(temporarySound) ? Seconds(Probe(ffprobe, temporarySound)?["format"]?["duration"]) : 0;
                    if (sound.ExitCode != 0 || soundSeconds <= 0)
                    {
                        Log.Write("Share card video sound import failed: " + string.Join(" ", sound.Output));
                        return unusable;
                    }
                    // a sound about as long as the picture or shorter is made exactly as long, so the two loop together instead of drifting apart a little every loop
                    if (soundSeconds <= seconds + VideoSecondsSlack)
                    {
                        string length = seconds.ToString("0.######", CultureInfo.InvariantCulture);
                        var fitted = Compression.InvokeNativeCapture(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", temporarySound, "-af", "apad=whole_dur=" + length, "-t", length, "-c:a", "pcm_s16le", fittedSound }, timeoutMs: 120000);
                        if (fitted.ExitCode != 0 || !File.Exists(fittedSound))
                        {
                            Log.Write("Share card video sound fit failed: " + string.Join(" ", fitted.Output));
                            return unusable;
                        }
                        Native.MoveFileReplace(fittedSound, temporarySound);
                        soundSeconds = seconds;
                    }
                }
                SelectVideo(revision);
                published = true;
                Log.Write("Share card video imported: " + seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s" + (hasSound ? " with " + soundSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s of sound" : " without sound"));
                return new JObject { ["ok"] = true, ["hasSound"] = hasSound };
            }
            finally
            {
                Remove(fittedSound);
                if (!published)
                {
                    Remove(temporaryVideo);
                    Remove(temporarySound);
                }
            }
        }

        private static JObject Probe(string ffprobe, string path)
        {
            var run = Compression.InvokeNativeCapture(ffprobe, new[] { "-v", "error", "-show_entries", "stream=codec_type,r_frame_rate,duration:stream_disposition=attached_pic:format=duration", "-of", "json", path }, timeoutMs: 30000);
            if (run.ExitCode != 0) return null;
            try { return JObject.Parse(string.Join("\n", run.Output)); }
            catch (JsonException) { return null; }
        }

        // a length as ffprobe writes it; zero when it names none, which some containers do for a single stream
        private static double Seconds(JToken duration) =>
            double.TryParse(duration?.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 ? seconds : 0;

        // a frame rate as ffprobe writes it, 30000/1001 or 25/1; zero when it names none
        private static double FrameRate(string rate)
        {
            string[] parts = (rate ?? "").Split('/');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double top) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double bottom) || bottom <= 0) return 0;
            return top / bottom;
        }

        public static void Remove(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("warn: could not remove " + Path.GetFileName(path) + ": " + ex.Message); }
        }
    }
}
