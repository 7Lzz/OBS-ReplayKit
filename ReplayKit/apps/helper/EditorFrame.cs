using System;
using System.Collections.Generic;

namespace ReplayKitHelper
{
    internal static class EditorFrame
    {
        private static readonly Dictionary<string, (int W, int H)> Ratios = new Dictionary<string, (int, int)>
        {
            ["9:16"] = (9, 16), ["16:9"] = (16, 9), ["16:10"] = (8, 5),
            ["21:9"] = (7, 3), ["4:3"] = (4, 3), ["1:1"] = (1, 1),
        };

        public static bool Valid(string aspect) => aspect == "original" || aspect != null && Ratios.ContainsKey(aspect);

        // the size of the picture once it is cut to the aspect, which is what the look, the layers and the subtitles are laid out on; the editor works it out the same way
        public static (int W, int H) Size(string aspect, int width, int height)
        {
            if (aspect == "original") return (width, height);
            if (!Ratios.TryGetValue(aspect, out var ratio)) throw new ArgumentException("Unknown aspect ratio");
            int unit = (int)Math.Floor(Math.Min((double)width / ratio.W, (double)height / ratio.H) / 2) * 2;
            if (unit < 2) throw new ArgumentException("The video is too small for this aspect ratio");
            return (unit * ratio.W, unit * ratio.H);
        }

        public static string Filter(string aspect, int width, int height)
        {
            if (aspect == "original") return null;
            var size = Size(aspect, width, height);
            return "crop=" + size.W + ":" + size.H + ":(iw-ow)/2:(ih-oh)/2,setsar=1";
        }
    }
}
