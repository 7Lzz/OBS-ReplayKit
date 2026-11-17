using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // the card the share window shows while obs is away: what it says and shows comes from the share_cover settings, the theme and the language, the plugin draws it into a png (qt there has the svg, fonts and image scaling), and the window is told to show it and to loop its sound
    internal static class ShareCover
    {
        private const int MaxImageWidth = 3840;
        private const int MaxImageHeight = 2160;
        private const int MaxSoundSeconds = 600;
        private static readonly object Gate = new object();
        private static string appliedSignature;
        // how the card is drawn apart from the theme: the sizes at size 100 in pixels of a 1080 pixel tall card (the logo side, the text height, the plate a picked image puts behind the text), the weight of the text, how strong the outline logo shows, and the colours of that plate (opacity 0 to 255) and of the text over an image or the last frame; the plugin draws with them and the card editor of the settings page shows the same
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
            },
        };

        public static string ImagePath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ShareCoverImageFile);
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
                ["hasSound"] = File.Exists(SoundPath),
                ["hasDefaultSound"] = File.Exists(DefaultSoundPath),
                ["hasCard"] = File.Exists(CardPath),
                ["width"] = state?["texture"]?[0]?.Value<int>() ?? 0,
                ["height"] = state?["texture"]?[1]?.Value<int>() ?? 0,
                ["look"] = Look.DeepClone(),
            };
        }

        private static string Stamp(string path) => path != null && File.Exists(path) ? new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : "";

        // the sound the card plays now, or null for none
        private static string SoundFor(JObject settings)
        {
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
                // an image background whose image is gone draws the theme card, since the setting alone shows nothing
                if (background == "image" && !File.Exists(ImagePath)) background = "theme";
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
                    ["accent"] = colors["accent"]?.Value<string>() ?? "",
                    ["image"] = background == "image" ? ImagePath : "",
                    ["fit"] = settings["share_cover_fit"]?.Value<string>() ?? SettingDefaults.ShareCoverFit,
                    ["text"] = text,
                    // the default card has the obs icon over its text; a picked image is the picture itself
                    ["icon"] = background != "image",
                    ["layout"] = settings["share_cover_layout"] as JObject ?? ReplaykitSettings.DefaultShareCoverLayout(),
                    ["look"] = Look.DeepClone(),
                };
                string sound = SoundFor(settings);
                int volume = settings["share_cover_sound_volume"]?.Value<int>() ?? SettingDefaults.ShareCoverSoundVolume;
                long obsWindow;
                lock (Server.State.IpcLock) obsWindow = Server.State.ObsMainWindowHwnd;
                // the plugin that draws the card only changes when obs starts again, so each obs start draws it once and a plugin update shows up on the card
                string signature = request.ToString(Formatting.None) + "|" + Stamp(request["image"].Value<string>()) + "|" + (sound ?? "") + "|" + Stamp(sound) + "|" + volume + "|" + state["pid"] + "|" + obsWindow;
                if (!force && signature == appliedSignature) return;

                if (!PipeClient.RenderShareCardAndWait(request.ToString(Formatting.None), 8000))
                {
                    Log.Write("warn: the share card could not be drawn; the share window keeps the card it had");
                    return;
                }
                var card = ShareWindow.Send("CARD " + (background == "lastframe" ? "over " : "full ") + CardPath);
                var soundReply = ShareWindow.Send(sound == null ? "SOUND none" : "SOUND " + volume + " " + sound);
                bool ok = card["ok"]?.Value<bool>() == true && soundReply["ok"]?.Value<bool>() == true;
                Log.Write("Share card " + width + "x" + height + " " + background + (sound == null ? " without sound" : " with " + Path.GetFileName(sound)) + (ok ? "" : ": " + (card["message"] ?? soundReply["message"])));
                if (ok) appliedSignature = signature;
            }
        }

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

        public static void Remove(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("warn: could not remove " + Path.GetFileName(path) + ": " + ex.Message); }
        }
    }
}
