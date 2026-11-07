using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    // the keyboard and mouse layouts of the wasd overlay and where the two sit in their group, for the helper that saves and draws them and the installer that writes them into the scene; obs sizes a group to what it holds, so the group is as big as the two side by side, fitted into the 628x292 box the first wasd and mouse filled
    public static class InputOverlayLayout
    {
        public const string DefaultKeyboard = "wasd-minimal";
        public const string DefaultMouse = "buttons";
        public const string Custom = "custom";
        public const double GroupWidth = 628.0, GroupHeight = 292.0;
        // the space between the keyboard and the mouse, the one the first group had
        public const double Gap = 11.0;
        // the size of the first wasd keyboard and mouse, for a member whose layout cannot be read
        public const double DefaultKeyboardWidth = 568.0, DefaultKeyboardHeight = 394.0;
        public const double DefaultMouseWidth = 285.0, DefaultMouseHeight = 421.0;
        // the custom keyboard the helper writes into the obs-replayKit folder and the sheet of its keys; the number changes whenever the drawing does
        public const string CustomFile = "input-overlay-custom.json";
        public const string CustomSheetFile = "input-overlay-custom-keys-2.png";

        public sealed class Layout
        {
            public string Id, Label, Blurb, Json, Sheet;
        }

        // the json and sheet paths are relative to the input-overlay-presets folder
        public static readonly Layout[] Keyboards =
        {
            new Layout { Id = "wasd-minimal", Label = "WASD", Blurb = "W, A, S and D with Q, E, Shift, Ctrl and Space.", Json = "wasd/wasd-minimal.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-full", Label = "WASD Full", Blurb = "WASD with Tab, R and F as well.", Json = "wasd/wasd-full.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-valorant", Label = "Valorant", Blurb = "WASD with Tab, F, C and 4 for agent abilities.", Json = "wasd/wasd-valorant.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-valorant-micro", Label = "Valorant Small", Blurb = "WASD with Tab and C, and a shorter Space.", Json = "wasd/wasd-valorant-micro.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = "wasd-numbers", Label = "WASD Numbers", Blurb = "WASD with 1 to 5, Tab, Alt, R, T, F, G, Z, X, C, V and B.", Json = "wasd-extended-numeric/wasd-extended-numeric.json", Sheet = "wasd-extended-numeric/wasd-extended-numeric.png" },
            new Layout { Id = "wasd-numbers-small", Label = "WASD Numbers Small", Blurb = "WASD with 1 to 4, Tab, Alt, R, Z, X and C.", Json = "wasd-extended-numeric/wasd-extended-numeric-min.json", Sheet = "wasd/wasd.png" },
            new Layout { Id = Custom, Label = "Custom", Blurb = "Any keys you pick, in the look of WASD.", Json = null, Sheet = null },
        };

        public static readonly Layout[] Mice =
        {
            new Layout { Id = "buttons", Label = "Buttons", Blurb = "Left, right and side buttons and the wheel.", Json = "mouse/mouse-no-movement.json", Sheet = "mouse/mouse.png" },
            new Layout { Id = "off", Label = "Off", Blurb = "Only the keyboard.", Json = null, Sheet = null },
        };

        public static Layout FindKeyboard(string id) => Keyboards.FirstOrDefault(l => l.Id == id) ?? Keyboards[0];
        public static Layout FindMouse(string id) => Mice.FirstOrDefault(l => l.Id == id) ?? Mice[0];

        public sealed class Picked
        {
            public string Json, Sheet;
            public double Width, Height;
        }

        // the files of a layout with the size input-overlay gives its source, the overlay size its json names; null when the sheet is missing or the json has no size. readText and exists say where the files are, the installed folder for the helper and the bundled zip for the installer
        public static Picked Pick(string json, string sheet, Func<string, string> readText, Func<string, bool> exists)
        {
            if (json == null || sheet == null || !exists(sheet)) return null;
            double width, height;
            try
            {
                var layout = JObject.Parse(readText(json));
                width = layout["overlay_width"]?.Value<double>() ?? 0;
                height = layout["overlay_height"]?.Value<double>() ?? 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is FormatException || ex is InvalidCastException || ex is ArgumentException) { return null; }
            return width > 0 && height > 0 ? new Picked { Json = json, Sheet = sheet, Width = width, Height = height } : null;
        }

        public sealed class Fit
        {
            public double X, Y, Scale;
        }

        // the keyboard and the mouse at one height side by side, as tall as the box lets both fit; the mouse is null when it is off, and width x height is the group they make
        public static void Place(Picked keyboard, Picked mouse, out Fit keys, out Fit pointer, out double width, out double height)
        {
            double kw = keyboard?.Width ?? DefaultKeyboardWidth, kh = keyboard?.Height ?? DefaultKeyboardHeight;
            double across = kw / kh + (mouse != null ? mouse.Width / mouse.Height : 0);
            height = Math.Min(GroupHeight, (GroupWidth - (mouse != null ? Gap : 0)) / across);
            keys = new Fit { X = 0, Y = 0, Scale = height / kh };
            pointer = mouse == null ? null : new Fit { X = kw * height / kh + Gap, Y = 0, Scale = height / mouse.Height };
            width = kw * height / kh + (mouse != null ? Gap + mouse.Width * height / mouse.Height : 0);
        }
    }
}
