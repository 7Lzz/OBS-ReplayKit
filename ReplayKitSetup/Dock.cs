using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReplayKitSetup
{
    // post-install sanity check that the replaykit browser html landed. ported from obs_replaykit/dock.py.
    public static class Dock
    {
        // canonical files we expect under DOCK_TARGET. anything else the walker copies is fine; this is just the sanity-check set.
        private static readonly string[] RequiredFiles = { "controls.html", "controls_app.html", "clips.html" };

        // count how many of the canonical dock files landed. logs a warning for any missing one so an install failure shows up in apply output instead of as a confusing "couldnt load that page" inside obs.
        public static int VerifyDockInstall(Action<string> log = null)
        {
            if (!Directory.Exists(Config.DOCK_TARGET))
            {
                log?.Invoke("warn: " + Config.DOCK_TARGET + " does not exist after install");
                return 0;
            }

            int present = 0;
            var missing = new List<string>();
            foreach (var name in RequiredFiles)
            {
                if (File.Exists(Path.Combine(Config.DOCK_TARGET, name))) present++;
                else missing.Add(name);
            }

            if (missing.Count > 0) log?.Invoke("warn: dock install missing: " + string.Join(", ", missing));
            else log?.Invoke("-> " + Config.DOCK_TARGET + $" ({present} file(s))");
            return present;
        }
    }
}
