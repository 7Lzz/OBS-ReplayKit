using System;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    internal static class OverlayRotation
    {
        private static double Number(JObject transform, string key, double fallback)
        {
            double value = transform[key]?.Value<double>() ?? fallback;
            return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
        }

        private static (double W, double H) Size(JObject transform, double sourceWidth = 0, double sourceHeight = 0)
        {
            double w = Math.Abs(Number(transform, "width", 0)), h = Math.Abs(Number(transform, "height", 0));
            if (w == 0) w = Number(transform, "sourceWidth", sourceWidth) * Math.Abs(Number(transform, "scaleX", 1));
            if (h == 0) h = Number(transform, "sourceHeight", sourceHeight) * Math.Abs(Number(transform, "scaleY", 1));
            return (w, h);
        }

        private static (double X, double Y) Offset(JObject transform, double w, double h, double sx, double sy, double angle)
        {
            int alignment = transform["alignment"]?.Value<int>() ?? 5;
            double x = ((alignment & 1) != 0 ? 0.5 : (alignment & 2) != 0 ? -0.5 : 0) * w * Math.Sign(sx);
            double y = ((alignment & 4) != 0 ? 0.5 : (alignment & 8) != 0 ? -0.5 : 0) * h * Math.Sign(sy);
            double radians = angle * Math.PI / 180;
            return (x * Math.Cos(radians) - y * Math.Sin(radians), x * Math.Sin(radians) + y * Math.Cos(radians));
        }

        public static JObject Rectangle(JObject transform, double sourceWidth = 0, double sourceHeight = 0)
        {
            var size = Size(transform, sourceWidth, sourceHeight);
            var offset = Offset(transform, size.W, size.H, Number(transform, "scaleX", 1), Number(transform, "scaleY", 1), Number(transform, "rotation", 0));
            return new JObject
            {
                ["x"] = Number(transform, "positionX", 0) + offset.X - size.W / 2,
                ["y"] = Number(transform, "positionY", 0) + offset.Y - size.H / 2,
                ["w"] = size.W, ["h"] = size.H,
            };
        }

        private static JObject Place(JObject transform, double x, double y, double w, double h, double sx, double sy, double angle)
        {
            var offset = Offset(transform, w, h, sx, sy, angle);
            return new JObject
            {
                ["positionX"] = x + w / 2 - offset.X, ["positionY"] = y + h / 2 - offset.Y,
                ["scaleX"] = sx, ["scaleY"] = sy, ["rotation"] = angle,
            };
        }

        public static JObject Rotate(JObject transform, double angle, double sourceWidth = 0, double sourceHeight = 0)
        {
            var rect = Rectangle(transform, sourceWidth, sourceHeight);
            return Place(transform, rect["x"].Value<double>(), rect["y"].Value<double>(), rect["w"].Value<double>(), rect["h"].Value<double>(),
                Number(transform, "scaleX", 1), Number(transform, "scaleY", 1), angle);
        }

        public static JObject Mirror(JObject transform, double sourceWidth = 0, double sourceHeight = 0)
        {
            var rect = Rectangle(transform, sourceWidth, sourceHeight);
            return Place(transform, rect["x"].Value<double>(), rect["y"].Value<double>(), rect["w"].Value<double>(), rect["h"].Value<double>(),
                -Number(transform, "scaleX", 1), Number(transform, "scaleY", 1), Number(transform, "rotation", 0));
        }

        public static JObject Resize(JObject transform, double ratio, double left, double top)
        {
            var size = Size(transform);
            return Place(transform, left, top, size.W * ratio, size.H * ratio,
                Number(transform, "scaleX", 1) * ratio, Number(transform, "scaleY", 1) * ratio, Number(transform, "rotation", 0));
        }
    }
}
