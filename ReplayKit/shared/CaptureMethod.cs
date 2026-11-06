using System;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    // which way the Display Capture source reads the screen. obs's automatic choice uses desktop duplication (dxgi) unless the display sits on another gpu than obs, and dxgi gives a black picture on some machines anyway (all-in-ones, some laptops and hdr or very wide displays), where windows graphics capture (wgc) works. on windows 11 wgc captures without the yellow border windows 10 draws around the whole screen, so there it is the default; on windows 10 the source stays on automatic, since replaykit captures all the time and the border would never go away
    internal static class CaptureMethod
    {
        // the "method" values of obs's monitor_capture source
        public const int Auto = 0, Dxgi = 1, Wgc = 2;
        public const string Key = "method";

        private static int? build;

        public static int WindowsBuild
        {
            get
            {
                if (build.HasValue) return build.Value;
                int value = 0;
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    int.TryParse(key?.GetValue("CurrentBuildNumber") as string ?? "", out value);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException) { }
                build = value;
                return value;
            }
        }

        public static bool PreferWgc => WindowsBuild >= 22000;

        // the method a Display Capture source should be given, or null to leave it alone: only a source whose method was never set (obs writes it once it is picked in the properties) is moved off automatic, so a method someone chose stays theirs
        public static int? DisplayDefault(JObject settings)
        {
            if (!PreferWgc) return null;
            if (settings != null && settings[Key] != null && settings[Key].Type != JTokenType.Null) return null;
            return Wgc;
        }
    }
}
