using System;
using System.Collections.Generic;
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
        // how the card is drawn apart from the theme: the sizes at size 100 in pixels of a 1080 pixel tall card (the logo side, the text height, the plate a picked image puts behind the text), the weight of the text, how strong the outline logo shows, the colours of that plate (opacity 0 to 255) and of the text over an image or the last frame, and the moving ground of the theme card; the plugin draws the picture, the share window the ground, and the card editor of the settings page shows the same
        private static readonly JObject Look = new JObject
        {
            ["logo"] = 220, ["text"] = 120, ["textWeight"] = 700, ["logoOpacity"] = 0.85,
            ["plateX"] = 56, ["plateY"] = 28, ["plateRadius"] = 24,
            ["plateColor"] = "#000000", ["plateOpacity"] = 150, ["lightText"] = "#f2f2f2",
            // the ground of the theme card: circles a shade darker than the ground and circles off the corners whose rim glows in the accent colour, each as x and y in fractions of the card and a radius in fractions of its width; shade and glow are how far they go toward black and toward the accent, rim and blur the bright edge and the soft band inside it in pixels of a 1080 pixel tall card
            ["backdrop"] = new JObject
            {
                ["shade"] = 0.3,
                ["shades"] = new JArray { new JArray(0.37, 0.13, 0.165), new JArray(0.71, 0.72, 0.23) },
                ["glow"] = 0.06,
                ["rim"] = 3,
                ["blur"] = 46,
                ["glows"] = new JArray { new JArray(1.05, -0.5, 0.538), new JArray(0.09, 1.306, 0.392) },
                // how it moves: every circle on an orbit of drift times the card width, one turn in the seconds of its period (shades first, then glows; a minus turns it the other way, and the circle index offsets where it starts), and the rim swinging breathe either side of half way to the accent over breatheSeconds
                ["drift"] = 0.035,
                ["periods"] = new JArray(-22, 18, 26, -20),
                ["breathe"] = 0.3,
                ["breatheSeconds"] = 5,
            },
        };

        public static string ImagePath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverImageFile);
        public static string VideoPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverVideoFile);
        public static string VideoSoundPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverVideoSoundFile);
        public static string SoundPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverSoundFile);
        public static string CardPath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverCardFile);
        // the sound the default card plays, shipped from resources/sounds once there is one; without it the default card is silent
        public static string DefaultSoundPath => Path.GetFullPath(Path.Combine(Constants.HelperRoot, "..", "..", "sounds", "share-card-default.wav"));

        public static JObject Status()
        {
            // the size the card is drawn at, so the card editor has its shape; zero while no share window has a texture
            var state = ShareWindow.KnownWindowAlive() ? ShareWindow.Send("STATE") : null;
            return new JObject
            {
                ["ok"] = true,
                ["hasImage"] = File.Exists(ImagePath),
                ["hasVideo"] = File.Exists(VideoPath),
                ["videoHasSound"] = File.Exists(VideoSoundPath),
                ["maxVideoSeconds"] = MaxVideoSeconds,
                ["hasSound"] = File.Exists(SoundPath),
                ["hasDefaultSound"] = File.Exists(DefaultSoundPath),
                ["hasCard"] = File.Exists(CardPath),
                ["width"] = state?["texture"]?[0]?.Value<int>() ?? 0,
                ["height"] = state?["texture"]?[1]?.Value<int>() ?? 0,
                ["look"] = Look.DeepClone(),
            };
        }

        private static string Stamp(string path) => path != null && File.Exists(path) ? new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : "";

        // the sound the card plays now, or null for none; a video background plays the sound of its video while share_cover_video_sound is on and the video has one, and the card sound otherwise
        private static string SoundFor(JObject settings, string background)
        {
            if (background == "video" && (settings["share_cover_video_sound"]?.Value<bool>() ?? SettingDefaults.ShareCoverVideoSound) && File.Exists(VideoSoundPath)) return VideoSoundPath;
            switch (settings["share_cover_sound"]?.Value<string>() ?? SettingDefaults.ShareCoverSound)
            {
                case "custom": return File.Exists(SoundPath) ? SoundPath : null;
                case "default": return File.Exists(DefaultSoundPath) ? DefaultSoundPath : null;
                default: return null;
            }
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
                // an image or video background whose file is gone draws the theme card, since the setting alone shows nothing
                if (background == "image" && !File.Exists(ImagePath)) background = "theme";
                if (background == "video" && !File.Exists(VideoPath)) background = "theme";
                var colors = SettingsHttp.ActiveMenuColors(settings);
                bool showText = settings["share_cover_show_text"]?.Value<bool>() ?? SettingDefaults.ShareCoverShowText;
                string custom = (settings["share_cover_text"]?.Value<string>() ?? "").Trim();
                string text = !showText ? "" : custom.Length > 0 ? custom : Locales.Message("screenshare.coverDefaultText");
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
                    // the default card has the obs icon over its text; a picked image or video is the picture itself
                    ["icon"] = background == "theme" || background == "lastframe",
                    ["layout"] = settings["share_cover_layout"] as JObject ?? ReplaykitSettings.DefaultShareCoverLayout(),
                    ["look"] = Look.DeepClone(),
                };
                string sound = SoundFor(settings, background);
                string video = background == "video" ? VideoPath : null;
                string backdrop = background == "theme" ? BackdropCommand(colors) : "BACKDROP none";
                int volume = settings["share_cover_sound_volume"]?.Value<int>() ?? SettingDefaults.ShareCoverSoundVolume;
                long obsWindow;
                lock (Server.State.IpcLock) obsWindow = Server.State.ObsMainWindowHwnd;
                // the plugin that draws the card only changes when obs starts again, so each obs start draws it once and a plugin update shows up on the card
                string signature = request.ToString(Formatting.None) + "|" + backdrop + "|" + Stamp(request["image"].Value<string>()) + "|" + Stamp(video) + "|" + (sound ?? "") + "|" + Stamp(sound) + "|" + volume + "|" + state["pid"] + "|" + obsWindow;
                if (!force && signature == appliedSignature) return;

                if (!PipeClient.RenderShareCardAndWait(request.ToString(Formatting.None), 8000))
                {
                    Log.Write("warn: the share card could not be drawn; the share window keeps the card it had");
                    return;
                }
                // the video goes first, so a card drawn to go over it never shows over the plain ground
                var videoReply = ShareWindow.Send(video == null ? "VIDEO none" : "VIDEO " + request["fit"].Value<string>() + " " + video);
                var backdropReply = ShareWindow.Send(backdrop);
                var card = ShareWindow.Send("CARD " + (background == "theme" ? "theme " : background == "lastframe" ? "over " : background == "video" ? "video " : "full ") + CardPath);
                var soundReply = ShareWindow.Send(sound == null ? "SOUND none" : "SOUND " + volume + " " + sound);
                bool ok = videoReply["ok"]?.Value<bool>() == true && backdropReply["ok"]?.Value<bool>() == true && card["ok"]?.Value<bool>() == true && soundReply["ok"]?.Value<bool>() == true;
                Log.Write("Share card " + width + "x" + height + " " + background + (sound == null ? " without sound" : " with " + Path.GetFileName(sound)) + (ok ? "" : ": " + (videoReply["message"] ?? backdropReply["message"] ?? card["message"] ?? soundReply["message"])));
                if (ok) appliedSignature = signature;
            }
        }

        // the BACKDROP line of the share window: the theme ground and accent, then the backdrop of the look with each circle and its period
        private static string BackdropCommand(JObject colors)
        {
            var backdrop = (JObject)Look["backdrop"];
            var shades = (JArray)backdrop["shades"];
            var glows = (JArray)backdrop["glows"];
            var periods = (JArray)backdrop["periods"];
            var words = new List<string> { "BACKDROP", colors["bg"]?.Value<string>() ?? "", colors["accent"]?.Value<string>() ?? "" };
            foreach (string name in new[] { "shade", "glow", "rim", "blur", "drift", "breathe", "breatheSeconds" }) words.Add(Number(backdrop[name]));
            words.Add(shades.Count.ToString(CultureInfo.InvariantCulture));
            words.Add(glows.Count.ToString(CultureInfo.InvariantCulture));
            int index = 0;
            foreach (JToken circle in shades.Concat(glows))
            {
                words.Add(Number(circle[0]));
                words.Add(Number(circle[1]));
                words.Add(Number(circle[2]));
                words.Add(Number(periods[index++]));
            }
            return string.Join(" ", words);
        }

        private static string Number(JToken value) => value.Value<double>().ToString("R", CultureInfo.InvariantCulture);

        // the picked image, scaled down to 4k at most and saved as a png in user-data, so the card never reads a file the user moves or deletes later
        public static JObject ImportImage(string source)
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

        // the picked sound converted to a wav the share window plays (48 khz stereo, ten minutes at most), so any format ffmpeg reads works
        public static JObject ImportSound(string source)
        {
            string ffmpeg = Compression.FindCompressionFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg)) return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theSoundNeedsFfmpeg") };
            Directory.CreateDirectory(Path.GetDirectoryName(SoundPath));
            string temporary = SoundPath + ".tmp.wav";
            try
            {
                var result = Compression.InvokeNativeCapture(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-vn", "-t", MaxSoundSeconds.ToString(), "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", temporary }, timeoutMs: 120000);
                if (result.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length < 1024)
                {
                    Log.Write("Share card sound import failed: " + string.Join(" ", result.Output));
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.theSoundCouldNotBeUsed") };
                }
                Native.MoveFileReplace(temporary, SoundPath);
                return new JObject { ["ok"] = true };
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // the picked video converted for the share window: h.264 in mp4, which media foundation on every windows decodes, no bigger or faster than the card is presented, and its sound as a wav; a picture longer than MaxVideoSeconds is refused rather than cut, while its sound may run on past the picture up to MaxSoundSeconds and then loops on its own
        public static JObject ImportVideo(string source)
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
            string temporaryVideo = VideoPath + ".tmp.mp4";
            string temporarySound = VideoSoundPath + ".tmp.wav";
            string fittedSound = VideoSoundPath + ".fit.tmp.wav";
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
                Native.MoveFileReplace(temporaryVideo, VideoPath);
                if (hasSound) Native.MoveFileReplace(temporarySound, VideoSoundPath);
                else Remove(VideoSoundPath);
                Log.Write("Share card video imported: " + seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s" + (hasSound ? " with " + soundSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s of sound" : " without sound"));
                return new JObject { ["ok"] = true, ["hasSound"] = hasSound };
            }
            finally
            {
                foreach (string leftover in new[] { temporaryVideo, temporarySound, fittedSound })
                    try { if (File.Exists(leftover)) File.Delete(leftover); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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
