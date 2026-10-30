using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the colour settings, filter and effects of the editor, burned into the saved copy. the page sends the numbers its shader draws the preview with (_editor-fx.html works them out from the filters and effects it defines), and this writes the same steps as ffmpeg filters in the same order on 8 bit rgb frames, so the saved copy looks like the preview; every number is checked against its range and anything missing or out of range stops the save
    internal sealed class VideoLook
    {
        private double[] hue, sat, gain;
        private double vib, gamma, bright, contrast;
        private bool looked;
        private double[] lookM, lookMo, lookOff;
        private double lookGamma, lookContrast;
        private double zoom, lens, blur, motion, zblur, tilt, glow, glowS, bloomT, bloomS, pixel, shift, sharp, sepia, invert, solar, vignette, scanP, scanA;
        private bool flip, reflect, emboss, sketch, edges, thermal;
        // the labels of the branches of one look, told apart from those of the other looks of the same graph
        private static int lastTag;
        private int poster, noise;

        // the grain draws new noise for each frame from where the frames before left it, so what it adds depends on how many frames went through first; every other step works on one frame alone
        internal bool Temporal => noise > 0;

        private static readonly double[] Luma = { 0.2126, 0.7152, 0.0722 };
        private static readonly double[] Sepia = { 0.393, 0.769, 0.189, 0.349, 0.686, 0.168, 0.272, 0.534, 0.131 };

        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        // whole pixels the way the page works them out: half rounds up
        private static int Px(double v, double k) => (int)Math.Floor(v * k + 0.5);

        private sealed class BadLook : Exception { public BadLook(string m) : base(m) { } }

        private static double Num(JToken parent, string key, double min, double max)
        {
            var t = parent?[key];
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) throw new BadLook(key + " is missing");
            double v = t.Value<double>();
            if (double.IsNaN(v) || v < min || v > max) throw new BadLook(key + " is out of range");
            return v;
        }

        private static bool Flag(JToken parent, string key)
        {
            double v = Num(parent, key, 0, 1);
            if (v != 0 && v != 1) throw new BadLook(key + " is out of range");
            return v == 1;
        }

        private static double[] List(JToken parent, string key, int count, double min, double max)
        {
            if (!(parent?[key] is JArray a) || a.Count != count) throw new BadLook(key + " is missing");
            var o = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (a[i].Type != JTokenType.Float && a[i].Type != JTokenType.Integer) throw new BadLook(key + " is missing");
                o[i] = a[i].Value<double>();
                if (double.IsNaN(o[i]) || o[i] < min || o[i] > max) throw new BadLook(key + " is out of range");
            }
            return o;
        }

        public static VideoLook Parse(JToken token, out string error)
        {
            error = null;
            try
            {
                if (!(token is JObject o)) throw new BadLook("look is missing");
                var l = new VideoLook
                {
                    // the channel mixer of ffmpeg takes factors from -2 to 2
                    hue = List(o, "hue", 9, -2, 2),
                    sat = List(o, "sat", 9, -2, 2),
                    gain = List(o, "gain", 3, 0, 2),
                    vib = Num(o, "vib", -2, 2),
                    gamma = Num(o, "gamma", 0.2, 3),
                    bright = Num(o, "bright", 0, 2),
                    contrast = Num(o, "contrast", 0, 3),
                };
                var lk = o["look"];
                if (lk != null && lk.Type != JTokenType.Null)
                {
                    l.looked = true;
                    l.lookM = List(lk, "m", 9, -2, 2);
                    l.lookMo = List(lk, "mo", 3, -1, 1);
                    l.lookOff = List(lk, "off", 3, -1, 1);
                    l.lookGamma = Num(lk, "gamma", 0.2, 3);
                    l.lookContrast = Num(lk, "contrast", 0, 3);
                }
                var s = o["steps"] as JObject ?? throw new BadLook("steps are missing");
                l.zoom = Num(s, "zoom", 1, 2);
                l.lens = Num(s, "lens", -1, 1);
                l.flip = Flag(s, "flip");
                l.reflect = Flag(s, "reflect");
                l.blur = Num(s, "blur", 0, 20);
                l.motion = Num(s, "motion", 0, 40);
                l.zblur = Num(s, "zblur", 0, 0.3);
                l.tilt = Num(s, "tilt", 0, 20);
                l.glow = Num(s, "glow", 0, 1);
                l.glowS = Num(s, "glowS", 0, 30);
                l.bloomT = Num(s, "bloomT", 0, 0.95);
                l.bloomS = Num(s, "bloomS", 0, 30);
                l.pixel = Num(s, "pixel", 0, 64);
                l.shift = Num(s, "shift", 0, 20);
                l.sharp = Num(s, "sharp", 0, 3);
                l.emboss = Flag(s, "emboss");
                l.sketch = Flag(s, "sketch");
                l.edges = Flag(s, "edges");
                l.sepia = Num(s, "sepia", 0, 1);
                l.invert = Num(s, "invert", 0, 1);
                l.solar = Num(s, "solar", 0, 1);
                double poster = Num(s, "poster", 0, 16);
                if (poster != Math.Floor(poster) || poster == 1) throw new BadLook("poster is out of range");
                l.poster = (int)poster;
                l.thermal = Flag(s, "thermal");
                l.vignette = Num(s, "vignette", 0, 1.6);
                double noise = Num(s, "noise", 0, 100);
                if (noise != Math.Floor(noise)) throw new BadLook("noise is out of range");
                l.noise = (int)noise;
                l.scanP = Num(s, "scanP", 0, 8);
                l.scanA = Num(s, "scanA", 0, 1);
                return l;
            }
            catch (BadLook ex)
            {
                error = "Bad video look: " + ex.Message;
                return null;
            }
        }

        private static bool Plain(double[] m) => m.Select((v, i) => Math.Abs(v - (i % 4 == 0 ? 1 : 0)) < 1e-4).All(x => x);

        private static string Mixer(double[] m)
        {
            string[] names = { "rr", "rg", "rb", "gr", "gg", "gb", "br", "bg", "bb" };
            return "colorchannelmixer=" + string.Join(":", names.Select((n, i) => n + "=" + N(m[i])));
        }

        // a lookup table per channel from an expression of v (the channel from 0 to 1) to a value from 0 to 1; lutrgb drops the fraction, so a half is added to round like the preview does
        private static string Lut(string r, string g, string b) =>
            "lutrgb=r='clip(" + r + ",0,1)*255+0.5':g='clip(" + g + ",0,1)*255+0.5':b='clip(" + b + ",0,1)*255+0.5'";

        // the expressions below are written in v, which no function name they use contains
        private static string V(string expr) => expr.Replace("v", "(val/255)");

        // the copies of the picture blurred and laid over it, each taken from the picture as it arrives the way the preview draws them: tilt shift merged through a mask of rows, glow as a screen, bloom added. the result is a graph whose last chain carries on with the filters after it, so it fits wherever a chain of filters does
        private string Overlays(int width, int height, double k, string tag)
        {
            bool tilted = tilt > 0, glowing = glow > 0 && glowS > 0, blooming = bloomS > 0;
            if (!tilted && !glowing && !blooming) return null;
            var heads = new List<string> { "[" + tag + "s]" };
            var chains = new List<string>();
            // what each merge lays over the picture so far, and the filter that does it
            var merges = new List<(string over, string filter)>();
            if (tilted)
            {
                heads.Add("[" + tag + "t]");
                heads.Add("[" + tag + "m]");
                chains.Add("[" + tag + "t]gblur=sigma=" + N(tilt * k / zoom) + ":steps=3,format=gbrp[" + tag + "tb]");
                // the mask is worked out on a strip two pixels wide and stretched, so the expression runs once a row; sharp in the middle quarter and fully blurred a fifth of the height past it, eased like smoothstep
                string d = "clip((abs((Y+0.5)/H-0.5)-0.12)/0.2,0,1)";
                string m = "'round(255*" + d + "*" + d + "*(3-2*" + d + "))'";
                chains.Add("[" + tag + "m]scale=2:" + height + ":flags=neighbor,format=gbrp,geq=r=" + m + ":g=" + m + ":b=" + m + ",scale=" + width + ":" + height + ":flags=neighbor,format=gbrp[" + tag + "tm]");
                merges.Add(("[" + tag + "tb][" + tag + "tm]", "maskedmerge"));
            }
            if (glowing)
            {
                heads.Add("[" + tag + "g]");
                chains.Add("[" + tag + "g]gblur=sigma=" + N(glowS * k / zoom) + ":steps=3,format=gbrp[" + tag + "gb]");
                merges.Add(("[" + tag + "gb]", "blend=all_mode=screen:all_opacity=" + N(glow)));
            }
            if (blooming)
            {
                heads.Add("[" + tag + "b]");
                string e = V("(v-" + N(bloomT) + ")/" + N(1 - bloomT));
                chains.Add("[" + tag + "b]" + Lut(e, e, e) + ",format=gbrp,gblur=sigma=" + N(bloomS * k / zoom) + ":steps=3,format=gbrp[" + tag + "bb]");
                merges.Add(("[" + tag + "bb]", "blend=all_mode=addition"));
            }
            // each merge takes the one before it as its first input, and the last is left open for the chain to go on from
            for (int i = 0; i < merges.Count; i++)
            {
                string input = i == 0 ? "[" + tag + "s]" : "[" + tag + "c" + (i - 1) + "]";
                chains.Add(input + merges[i].over + merges[i].filter + ",format=gbrp" + (i == merges.Count - 1 ? "" : "[" + tag + "c" + i + "]"));
            }
            return "split=" + heads.Count + string.Concat(heads) + ";" + string.Join(";", chains);
        }

        // the picture averaged with four copies of itself cut to its middle and stretched back, each zoomed a quarter further toward the most
        private string ZoomBlur(int width, int height, string tag)
        {
            var parts = new List<string> { "split=5" + string.Concat(Enumerable.Range(0, 5).Select(i => "[" + tag + "z" + i + "]")) };
            for (int i = 1; i < 5; i++)
            {
                double z = 1 + zblur * i / 4;
                int cw = Math.Max(2, (int)Math.Floor(width / z + 0.5)), ch = Math.Max(2, (int)Math.Floor(height / z + 0.5));
                parts.Add("[" + tag + "z" + i + "]crop=" + cw + ":" + ch + ":" + (width - cw) / 2 + ":" + (height - ch) / 2 + ",scale=" + width + ":" + height + ":flags=bilinear,format=gbrp[" + tag + "y" + i + "]");
            }
            parts.Add("[" + tag + "z0]" + string.Concat(Enumerable.Range(1, 4).Select(i => "[" + tag + "y" + i + "]")) + "mix=inputs=5");
            return string.Join(";", parts);
        }

        // the filters for a picture of width x height, from whatever the frames are to 8 bit planar rgb for the filters after them
        public string Filter(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new InvalidOperationException("the size of the video is not known");
            double k = height / 1080.0;
            string tag = "fx" + Interlocked.Increment(ref lastTag) + "_";
            var f = new List<string>();
            string overlays = Overlays(width, height, k, tag);
            if (overlays != null) f.Add(overlays);
            // one of the three blurs, the way the preview draws them
            if (zblur > 0) f.Add(ZoomBlur(width, height, tag));
            else if (motion > 0) f.Add("gblur=sigma=" + N(motion * k / zoom) + ":sigmaV=0:steps=3");
            else if (blur > 0) f.Add("gblur=sigma=" + N(blur * k / zoom) + ":steps=3");
            if (zoom > 1)
            {
                int cw = Math.Max(2, (int)Math.Floor(width / zoom + 0.5)), ch = Math.Max(2, (int)Math.Floor(height / zoom + 0.5));
                f.Add("crop=" + cw + ":" + ch + ":" + (width - cw) / 2 + ":" + (height - ch) / 2);
                f.Add("scale=" + width + ":" + height + ":flags=bicubic");
            }
            if (lens != 0) f.Add("lenscorrection=k1=" + N(lens) + ":k2=0:i=bilinear");
            if (flip) f.Add("hflip");
            if (reflect)
            {
                // the top half, and below it the rows above the middle upside down; an odd middle row stays in the top half
                int top = (height + 1) / 2;
                f.Add("split[" + tag + "ra][" + tag + "rb];[" + tag + "ra]crop=iw:" + top + ":0:0[" + tag + "rt];[" + tag + "rb]crop=iw:" + (height - top) + ":0:0,vflip[" + tag + "ru];[" + tag + "rt][" + tag + "ru]vstack");
            }
            if (pixel > 0)
            {
                int b = Math.Max(1, Px(pixel, k));
                if (b > 1) f.Add("pixelize=w=" + b + ":h=" + b + ":m=avg");
            }
            if (shift > 0)
            {
                int d = Px(shift, k);
                if (d > 0) f.Add("rgbashift=rh=" + (-d) + ":bh=" + d + ":edge=smear");
            }
            // one of the four, the way the preview draws them
            if (emboss)
            {
                const string m = "'-2 -1 0 -1 1 1 0 1 2'";
                f.Add("convolution=0m=" + m + ":1m=" + m + ":2m=" + m + ":0rdiv=1:1rdiv=1:2rdiv=1");
            }
            else if (sketch)
            {
                f.Add("sobel=planes=7");
                string e = V("1-clip((v-0.1)/0.2,0,1)");
                f.Add(Lut(e, e, e));
            }
            else if (edges)
            {
                f.Add("sobel=planes=7");
                string e = V("(v-0.04)/0.3");
                f.Add(Lut(e, e, e));
            }
            else if (sharp > 0)
            {
                // the pixel plus sharp times its difference from the four pixels two away, scaled to whole numbers for the matrix
                int c = (int)Math.Floor(1000 * (1 + sharp) + 0.5), t = -(int)Math.Floor(250 * sharp + 0.5);
                var cells = new int[25];
                cells[12] = c;
                cells[2] = cells[10] = cells[14] = cells[22] = t;
                string m = "'" + string.Join(" ", cells) + "'";
                f.Add("convolution=0m=" + m + ":1m=" + m + ":2m=" + m + ":0rdiv=0.001:1rdiv=0.001:2rdiv=0.001");
            }
            if (!Plain(hue)) f.Add(Mixer(hue));
            if (!Plain(sat)) f.Add(Mixer(sat));
            if (gain.Any(v => v != 1)) f.Add(Mixer(new[] { gain[0], 0, 0, 0, gain[1], 0, 0, 0, gain[2] }));
            if (vib != 0) f.Add("vibrance=intensity=" + N(vib) + ":rlum=" + N(Luma[0]) + ":glum=" + N(Luma[1]) + ":blum=" + N(Luma[2]));
            if (gamma != 1 || bright != 1 || contrast != 1)
            {
                string e = V("(pow(v," + N(1 / gamma) + ")*" + N(bright) + "-0.5)*" + N(contrast) + "+0.5");
                f.Add(Lut(e, e, e));
            }
            if (looked)
            {
                // the offset inside the matrix is added to the alpha column, which is full on frames that gain an alpha plane
                if (lookMo.Any(v => v != 0)) f.Add("format=gbrap," + Mixer(lookM) + ":ra=" + N(lookMo[0]) + ":ga=" + N(lookMo[1]) + ":ba=" + N(lookMo[2]));
                else if (!Plain(lookM)) f.Add(Mixer(lookM));
                string[] ch = new string[3];
                for (int i = 0; i < 3; i++) ch[i] = V("(pow(clip(v+" + N(lookOff[i]) + ",0,1)," + N(1 / lookGamma) + ")-0.5)*" + N(lookContrast) + "+0.5");
                f.Add(Lut(ch[0], ch[1], ch[2]));
            }
            if (sepia > 0) f.Add(Mixer(Enumerable.Range(0, 9).Select(i => (i % 4 == 0 ? 1 - sepia : 0) + Sepia[i] * sepia).ToArray()));
            if (invert > 0)
            {
                string e = V("v*" + N(1 - 2 * invert) + "+" + N(invert));
                f.Add(Lut(e, e, e));
            }
            if (solar > 0)
            {
                string e = V("if(gt(v," + N(solar) + "),1-v,v)");
                f.Add(Lut(e, e, e));
            }
            if (poster > 0)
            {
                string e = "floor(val*" + poster + "/256)/" + (poster - 1);
                f.Add(Lut(e, e, e));
            }
            if (thermal)
            {
                f.Add(Mixer(new[] { Luma[0], Luma[1], Luma[2], Luma[0], Luma[1], Luma[2], Luma[0], Luma[1], Luma[2] }));
                f.Add(Lut(V("3*v-1"), V("3*v-2"), V("1-abs(3*v-1)")));
            }
            if (vignette > 0) f.Add("vignette=angle=" + N(vignette) + ":dither=0");
            if (noise > 0) f.Add("noise=alls=" + noise + ":allf=t+u");
            if (scanA > 0)
            {
                // a grid wider than the picture by its line on both sides, so only the rows are drawn
                int per = Math.Max(1, Px(scanP, k)), th = Math.Max(1, (int)Math.Floor(per / 3.0 + 0.5));
                f.Add("drawgrid=x=-" + th + ":y=0:w=" + (width + 2 * th) + ":h=" + per + ":t=" + th + ":c=black@" + N(scanA));
            }
            // a filter that cannot write planar rgb (vignette) would otherwise be followed by a conversion ffmpeg picks itself, which can go through yuv and shift the colours, so every step is pinned to it
            return "format=gbrp" + string.Concat(f.Select(x => "," + x + ",format=gbrp"));
        }
    }
}
