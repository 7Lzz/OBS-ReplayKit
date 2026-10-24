using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // what the editor lays over the video: other clips of the folder, and pictures or gifs (shapes and stickers the page drew among them), each for a stretch of the saved clip, placed by its centre and its width as fractions of the picture, turned and with an opacity. every one is read as an input of its own and laid on in the order the page sends them, the last on top; all numbers are checked and anything missing, unknown or out of range stops the save
    internal sealed class LayerPlan
    {
        private const int MaxLayers = 50;

        private sealed class Layer
        {
            public string Path;
            public bool Clip;
            public bool Gif;
            public double At, End, Src, X, Y, W, Opacity, Rotate;
            // a clip row has a look of its own, laid on at its size before it goes over the picture; height over width of the clip says that size
            public VideoLook Look;
            public double Aspect;
        }

        private readonly List<Layer> layers = new List<Layer>();
        public int Count => layers.Count;

        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private sealed class BadLayer : Exception { public BadLayer(string m) : base(m) { } }

        private static double Num(JToken parent, string key, double min, double max)
        {
            var t = parent?[key];
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) throw new BadLayer(key + " is missing");
            double v = t.Value<double>();
            if (double.IsNaN(v) || v < min || v > max) throw new BadLayer(key + " is out of range");
            return v;
        }

        // length is how long the saved clip runs, which every layer has to start inside
        public static LayerPlan Parse(JToken token, double length, out string error)
        {
            error = null;
            try
            {
                if (!(token is JArray list) || list.Count == 0) throw new BadLayer("the list is empty");
                if (list.Count > MaxLayers) throw new BadLayer("there are too many");
                var plan = new LayerPlan();
                foreach (var item in list)
                {
                    if (!(item is JObject o)) throw new BadLayer("one is not readable");
                    string kind = o.Value<string>("kind");
                    string reference = o.Value<string>("ref");
                    var layer = new Layer
                    {
                        At = Num(o, "at", 0, length),
                        End = Num(o, "end", 0, length + 0.01),
                        Src = Num(o, "src", 0, 86400),
                        X = Num(o, "x", -0.5, 1.5),
                        Y = Num(o, "y", -0.5, 1.5),
                        W = Num(o, "w", 0.02, 4),
                        Opacity = Num(o, "opacity", 0, 1),
                        Rotate = Num(o, "rotate", -180, 180),
                    };
                    if (layer.End - layer.At < 0.04) throw new BadLayer("one is too short");
                    if (o["look"] != null)
                    {
                        layer.Look = VideoLook.Parse(o["look"], out string lookError);
                        if (layer.Look == null) throw new BadLayer(lookError ?? "a look is not readable");
                        layer.Aspect = Num(o, "aspect", 0.01, 100);
                    }
                    if (kind == "clip")
                    {
                        var clip = Clips.GetEditorSource(reference);
                        if (clip == null || !File.Exists(clip.Full)) { error = VideoImports.IsRef(reference) ? "A video added in the editor was moved or changed. Add it again." : "A clip added in the editor is no longer in the clip folder."; return null; }
                        layer.Path = clip.Full;
                        layer.Clip = true;
                    }
                    else if (kind == "media")
                    {
                        string media = MediaImports.PathFor(reference);
                        if (media == null) { error = "A GIF, sticker or shape added in the editor is no longer available. Add it again."; return null; }
                        layer.Path = media;
                        layer.Gif = MediaImports.IsGif(media);
                    }
                    else throw new BadLayer("one is of an unknown kind");
                    plan.layers.Add(layer);
                }
                return plan;
            }
            catch (BadLayer ex)
            {
                error = "Bad layer: " + ex.Message;
                return null;
            }
        }

        // the layers that show at the time t of the saved clip and nothing else, for a picture of that one frame: each is read from half a second before t rather than from its start, at the same place on the timeline, so the frame comes out the same while far less is decoded; a gif cannot be seeked into and keeps its start
        public LayerPlan AtFrame(double t)
        {
            var plan = new LayerPlan();
            foreach (var l in layers)
            {
                if (t < l.At || t >= l.End) continue;
                double skip = l.Gif ? 0 : Math.Max(0, t - l.At - 0.5);
                plan.layers.Add(new Layer
                {
                    Path = l.Path, Clip = l.Clip, Gif = l.Gif,
                    At = l.At + skip, End = l.End, Src = l.Clip ? l.Src + skip : l.Src,
                    X = l.X, Y = l.Y, W = l.W, Opacity = l.Opacity, Rotate = l.Rotate,
                    Look = l.Look, Aspect = l.Aspect,
                });
            }
            return plan;
        }

        // the reads of the layers, one input each, in their order; a picture is repeated for as long as it shows, a gif loops from its first frame, and a clip is read from its own start for that long
        public List<string> Inputs()
        {
            var argv = new List<string>();
            foreach (var l in layers)
            {
                string dur = N(l.End - l.At);
                if (l.Clip) argv.AddRange(new[] { "-ss", N(l.Src), "-t", dur, "-i", l.Path });
                else if (l.Gif) argv.AddRange(new[] { "-ignore_loop", "0", "-t", N(l.End - l.At + l.Src), "-i", l.Path });
                else argv.AddRange(new[] { "-loop", "1", "-framerate", "30", "-t", dur, "-i", l.Path });
            }
            return argv;
        }

        // the part of the graph that lays every layer over what arrives at input and leaves it at output; first is the number of the input of the first layer, and the picture is width x height. each layer is moved to its start on the timeline, scaled to its width with its own shape, given its opacity and laid on by its centre while its stretch lasts
        public string Graph(int first, string input, string output, int width, int height)
        {
            var parts = new List<string>();
            string current = input;
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                int pw = Math.Max(2, (int)Math.Round(l.W * width / 2) * 2);
                // a gif trimmed at its start is read from its own start and the frames before the cut are dropped, since a looping gif cannot be seeked into
                string cut = l.Gif && l.Src > 0 ? "trim=start=" + N(l.Src) + "," : "";
                string look = l.Look != null ? "," + l.Look.Filter(pw, Math.Max(2, (int)Math.Round(pw * l.Aspect / 2) * 2)) : "";
                string chain = "[" + (first + i) + ":v]" + cut + "setpts=PTS-STARTPTS+" + N(l.At) + "/TB,scale=" + pw + ":-2" + look + ",format=rgba";
                if (l.Opacity < 1) chain += ",colorchannelmixer=aa=" + N(l.Opacity);
                // turned clockwise about its centre like the preview turns it, on a canvas grown to hold it with clear corners, so laying it on by its centre keeps it in place
                if (l.Rotate != 0)
                {
                    string a = N(l.Rotate) + "*PI/180";
                    chain += ",rotate=" + a + ":ow=rotw(" + a + "):oh=roth(" + a + "):c=none";
                }
                parts.Add(chain + "[ly" + i + "]");
                string next = i == layers.Count - 1 ? output : "[lo" + i + "]";
                parts.Add(current + "[ly" + i + "]overlay=x='" + N(l.X) + "*main_w-overlay_w/2':y='" + N(l.Y) + "*main_h-overlay_h/2':enable='between(t," + N(l.At) + "," + N(l.End) + ")':eof_action=pass:format=auto" + next);
                current = next;
            }
            return string.Join(";", parts);
        }
    }
}
