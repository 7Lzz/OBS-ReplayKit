using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // the picture the account circle shows in place of the initial or the outline of a person: an image the user picks, cut to its centred square and kept as a png of at most 256px next to the settings
    internal static class ProfilePicture
    {
        private const int Side = 256;
        private const long MaxSourceBytes = 32L * 1024 * 1024;
        private const int OrientationTag = 0x0112;
        private static readonly object Gate = new object();

        internal static string FilePath => StateLayout.PathFor(AppConfig.GetReplayKitRoot(), StateLayout.ProfilePictureFile);

        // the write time of the saved picture, which the account menu and the plugin compare to notice a new one; empty when there is none
        internal static string Version()
        {
            var file = new FileInfo(FilePath);
            return file.Exists ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "";
        }

        internal static void Save(string source)
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSourceBytes) throw new InvalidDataException("Pick an image file smaller than 32 MB.");
            // read into memory first, since gdi+ keeps the file it decodes from locked for as long as the image lives
            using (var memory = new MemoryStream(File.ReadAllBytes(source)))
            using (var image = Image.FromStream(memory, false, true))
            {
                // a phone photo is stored sideways with a tag that says how to turn it
                if (image.PropertyIdList.Contains(OrientationTag))
                {
                    byte[] value = image.GetPropertyItem(OrientationTag).Value;
                    int orientation = value != null && value.Length > 0 ? value[0] : 1;
                    if (orientation == 3) image.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if (orientation == 6) image.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if (orientation == 8) image.RotateFlip(RotateFlipType.Rotate270FlipNone);
                }
                int square = Math.Min(image.Width, image.Height);
                if (square < 1) throw new InvalidDataException("The image is empty.");
                int target = Math.Min(Side, square);
                using (var canvas = new Bitmap(target, target, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(canvas))
                    using (var attributes = new ImageAttributes())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        // the filter reads past the edge of the square, which otherwise blends a dark rim into the picture
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(image, new Rectangle(0, 0, target, target), (image.Width - square) / 2, (image.Height - square) / 2, square, square, GraphicsUnit.Pixel, attributes);
                    }
                    lock (Gate)
                    {
                        string path = FilePath;
                        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            canvas.Save(temp, ImageFormat.Png);
                            Native.MoveFileReplace(temp, path);
                        }
                        finally
                        {
                            if (File.Exists(temp)) File.Delete(temp);
                        }
                    }
                }
            }
        }

        internal static void Clear()
        {
            lock (Gate)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
        }
    }
}
