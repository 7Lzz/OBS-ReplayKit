using System;
using System.IO;

namespace ReplayKitHelper
{
    // the picture of the primary desktop behind the overlay preview
    internal static class DesktopPreview
    {
        // primary-desktop jpeg for the overlay-preview backdrop (setup wizard + settings overlay page), which asks for a fresh one every second while it is on screen; a grab is shared for a little less than that, so pages that ask in the same moment get one capture between them. never written to disk, and the response is no-store so cef cannot replay an old image.
        internal static byte[] _desktopPreviewJpeg;
        internal static readonly System.Diagnostics.Stopwatch _desktopPreviewAge = new System.Diagnostics.Stopwatch();
        internal const int DesktopPreviewMaxAgeMs = 800;
        internal static readonly object _desktopPreviewGate = new object();

        // capture + cache the primary screen as a downscaled jpeg; returns null when there is no usable screen, and a near-black grab (secure desktop up) is returned but not cached so the next call retries.
        internal static byte[] GetDesktopPreviewJpeg()
        {
            lock (_desktopPreviewGate)
            {
                if (_desktopPreviewJpeg != null && _desktopPreviewAge.ElapsedMilliseconds < DesktopPreviewMaxAgeMs) return _desktopPreviewJpeg;
                var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? System.Drawing.Rectangle.Empty;
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                const int targetW = 1280;
                double scale = Math.Min(1.0, (double)targetW / bounds.Width);
                int outW = Math.Max(1, (int)Math.Round(bounds.Width * scale));
                int outH = Math.Max(1, (int)Math.Round(bounds.Height * scale));
                using (var full = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(full))
                        g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    byte[] jpeg = EncodeScaledJpeg(full, outW, outH);
                    if (!IsNearlyBlack(full))
                    {
                        _desktopPreviewJpeg = jpeg;
                        _desktopPreviewAge.Restart();
                    }
                    return jpeg;
                }
            }
        }

        // scale src into a w x h 24bpp jpeg (quality 72), same encode path as Native.SaveThumbnail minus the letterbox.
        internal static byte[] EncodeScaledJpeg(System.Drawing.Bitmap src, int w, int h)
        {
            using (var canvas = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (var g = System.Drawing.Graphics.FromImage(canvas))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, w, h);
                var jpegCodec = Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using (var eps = new System.Drawing.Imaging.EncoderParameters(1))
                using (var ms = new MemoryStream())
                {
                    eps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
                    canvas.Save(ms, jpegCodec, eps);
                    return ms.ToArray();
                }
            }
        }

        // true when a coarse pixel grid is all within a few levels of black -- cheap guard against caching a secure-desktop capture for the whole session.
        internal static bool IsNearlyBlack(System.Drawing.Bitmap bmp)
        {
            int stepX = Math.Max(1, bmp.Width / 16), stepY = Math.Max(1, bmp.Height / 16);
            for (int y = 0; y < bmp.Height; y += stepY)
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R > 8 || p.G > 8 || p.B > 8) return false;
                }
            return true;
        }
    }
}
