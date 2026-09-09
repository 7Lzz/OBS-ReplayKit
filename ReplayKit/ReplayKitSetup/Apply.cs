using System;
using System.Collections.Generic;
using System.IO;

namespace ReplayKitSetup
{
    // shared shape for a running install/cleanup progress view. the WinForms installer window (InstallerProgress),
    // Update.CleanupProgress (headless --cleanup) and RunUninstallDiscordScreenshareMode all implement this so the
    // apply flow and Cleanup.RunCleanup run from any caller.
    public interface IInstallProgress
    {
        int TotalSteps { get; set; }
        List<string> Issues { get; }
        void Render(int completed, string title, string detail, string state);
        void LogLine(string message);
        void AddIssue(string message);
        // fine-grained progress inside the step currently being rendered, fraction 0..1 -- lets the bar move during the
        // one long step (the OBS download) instead of sitting still. no-op for the headless reporters.
        void SubProgress(double fraction, string detail);
    }

    // headless installer flow: make sure OBS is present, write the ReplayKit setup, launch OBS. the only interactive choice, discord screenshare, comes from the setup window.
    public static class Apply
    {
        // shared step-runner for the apply flow and Cleanup.RunCleanup. renders "working" before the action and "done"
        // after; a thrown action rethrows (so the caller stops the flow) after logging, a false result just adds an issue.
        public static void RunApplyStep(IInstallProgress progress, int index, string title, string detail, Func<object> action)
        {
            progress.Render(index - 1, title, detail, "working");
            object result;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                progress.AddIssue($"{title} failed: {ex.Message}");
                progress.Render(index - 1, title, detail, "failed");
                throw;
            }
            if (result is bool b && !b)
            {
                progress.AddIssue($"{title} did not complete. Re-run the installer after fixing the issue.");
            }
            progress.Render(index, title, detail, "done");
            System.Threading.Thread.Sleep(120);
        }

        // build + run every apply step against the given progress sink. rethrows if a step throws; otherwise returns the
        // accumulated non-fatal issues. discordScreenshare is the one user choice -- null means "leave it as loaded"
        // (the live settings for an update; the default after cleanInstall wiped them). cleanInstall drops the preserved
        // ReplayKit settings/prefs first so the run rebuilds from the bundle + defaults, like a first install.
        public static List<string> RunInstallFlow(bool? discordScreenshare, IInstallProgress progress, bool cleanInstall = false)
        {
            if (cleanInstall) WipePreservedReplaykitState(progress);

            var prefs = Prefs.LoadPrefs();
            // the setup checkbox is a single on/off for the whole Share Preview feature: enable -> cable + hidden
            // projector both on; disable -> both off. null (headless) leaves whatever was loaded.
            if (discordScreenshare.HasValue)
            {
                prefs.DiscordScreenshareEnabled = discordScreenshare.Value;
                prefs.DiscordProjectorEnabled = discordScreenshare.Value;
            }
            if (!prefs.DiscordScreenshareEnabled) prefs.DiscordProjectorEnabled = false;

            // capture before any step runs -- a genuine first install has no runtime settings file yet, and that is the
            // only case that should arm the in-obs first-run wizard. a clean re-install just deleted it, so it counts too.
            bool firstInstall = !File.Exists(Prefs.RUNTIME_SETTINGS_FILE);

            var steps = new List<(string Title, string Detail, Func<object> Action)>();
            void Add(string title, string detail, Func<object> action) => steps.Add((title, detail, action));

            // hard prerequisite: every step after this writes into the OBS install. a failed download used to fall through
            // as a soft "issue" and the flow kept going, scattering plugins into a dead folder and still showing "Done".
            Add("Install OBS Studio", "Downloads OBS if it's missing.", () =>
            {
                string existing = Obs.FindObsExe();
                if (existing != null) { progress.LogLine("OBS is already installed: " + existing); return (object)true; }
                if (!ObsDownloader.EnsureObsInstalled(progress) || Obs.FindObsExe() == null)
                    throw new InvalidOperationException(
                        "OBS Studio could not be installed automatically. Install OBS from obsproject.com, then run this installer again.");
                return (object)true;
            });
            Add("Close OBS", "Stops OBS to write settings.", () => (object)Obs.CloseObs(progress.LogLine));
            Add("Back up current OBS settings", "Saves a restore copy first.", () => { Installer.BackupExistingConfig(progress.LogLine); return (object)true; });
            Add("Prepare OBS settings folder", "Creates the config folder.", () => { Directory.CreateDirectory(Config.OBS_CONFIG); Obs.CleanupCrashFlags(progress.LogLine); return (object)true; });
            // no helper means no dock, no clips, no settings ui -- throw so RunApplyStep stops the flow instead of the old
            // behaviour where a false here only added a "needs attention" line while the rest ran anyway.
            Add("Build ReplayKit helper", "Prepares the local helper.", () =>
            {
                if (Installer.EnsureLauncherBuilt(progress.LogLine)) return (object)true;
                throw new InvalidOperationException("OBS ReplayKit cannot run without the helper -- see the warning above for why it's missing.");
            });
            if (prefs.DiscordScreenshareEnabled)
                Add("Enable Discord screenshare", "Installs the audio cable driver.", () =>
                {
                    // no double install: if the cable is already there and its endpoint is renamed, EnsureVbcable would
                    // only re-bounce audiosrv for nothing.
                    if (VbCable.IsShareAudioReady())
                    {
                        progress.LogLine("OBS Stream Audio already set up -- nothing to install.");
                        return (object)true;
                    }
                    return (object)VbCable.EnsureVbcable(progress.LogLine);
                });
            // user explicitly unticked the box (not headless) and the cable is on the machine -- take it back off, so the
            // checkbox is a real on/off for the whole feature on Update and Re-Install too.
            else if (discordScreenshare == false && VbCable.IsVbcableInstalled())
                Add("Remove Discord screenshare", "Uninstalls the audio cable driver.", () => (object)VbCable.UninstallVbcable(progress.LogLine));
            else
                Add("Skip Discord screenshare", "Share Preview stays off.", () => (object)true);
            Add("Write ReplayKit OBS profile", "Writes the ReplayKit OBS profile.", () =>
            {
                bool ok = Installer.InstallObsConfig(prefs, progress.LogLine) > 0;
                if (ok && firstInstall) Installer.MarkSetupWizardPending(progress.LogLine);
                return (object)ok;
            });
            Add("Enable OBS WebSocket", "Enables the local OBS WebSocket.", () => (object)Installer.ConfigureObsWebsocket(progress.LogLine));
            Add("Install Custom Controls and Clips", "Adds the dock and Clips files.", () => (object)(Installer.InstallObsCustomDock(progress.LogLine) > 0));
            Add("Install tray plugin", "Adds the ReplayKit tray menu.", () => (object)TrayPlugin.InstallReplaykitTrayPlugin(progress.LogLine));
            Add("Register clip notifications", "Lets clip-ready notifications show up as OBS in the Action Center.", () => (object)Installer.RegisterClipNotificationShortcut(progress.LogLine));
            Add("Remove legacy launcher task", "Removes the old launcher task.", () => (object)Installer.RemoveObsElevationTask(progress.LogLine));
            if (prefs.AllowSleepWhileActive)
                Add("Allow monitor and PC sleep", "Lets Windows sleep during capture.", () => (object)Installer.InstallObsSleepOverride(true, progress.LogLine));
            else
                Add("Restore OBS sleep blocking", "Restores OBS sleep blocking.", () => (object)Installer.InstallObsSleepOverride(false, progress.LogLine));
            if (prefs.PinObsTrayIcon)
                Add("Pin OBS tray icon", "Pins the OBS tray icon.", () => { TrayPin.PinObsTrayIcon(progress.LogLine); return (object)true; });
            else
                Add("Skip tray icon pinning", "Leaves the tray icon default.", () => { TrayPin.UnpinObsTrayIcon(progress.LogLine); return (object)true; });
            Add("Install video tools", "Verifies the trim/compress tools.", () => (object)Installer.InstallObsFfmpeg(progress.LogLine));
            Add("Install WASD/mouse overlay", "Adds the WASD/mouse overlay plugin.",
                () => (object)(InputOverlay.InstallInputOverlayPlugin(progress.LogLine) && InputOverlay.InstallInputOverlayPresets(progress.LogLine)));
            Add("Install Bongo Cat overlay", "Adds the Bongo Cat overlay plugin.",
                () => (object)BongoCat.InstallBongoCatPlugin(progress.LogLine));
            Add("Install motion blur filter", "Adds the motion blur filter.",
                () => (object)ShaderFilter.InstallReplaykitMotionBlurPlugin(progress.LogLine));
            Add("Install desktop audio capture", "Adds desktop audio capture.", () => (object)WinCapture.InstallWinCaptureAudio(progress.LogLine));
            Add("Prepare clip folders", "Creates the clip folders.", () => { Installer.EnsureRecordingDirs(prefs, progress.LogLine); return (object)true; });
            Add("Set Windows startup", "Adds OBS to Windows startup.", () => (object)Startup.ConfigureObsStartup(prefs.ObsStartupEnabled, progress.LogLine));
            Add("Launch OBS", "Starts OBS.", () => (object)Obs.LaunchObs(progress.LogLine));

            progress.TotalSteps = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action);
            }
            progress.Render(progress.TotalSteps, "Setup complete", "OBS ReplayKit is ready.", "done");
            return progress.Issues;
        }

        // clean re-install: drop the ReplayKit settings + prefs the normal flow would preserve, so everything is rebuilt
        // from the bundle and defaults and the in-obs first-run wizard re-arms. clip history (clips_db/index) is left alone.
        private static void WipePreservedReplaykitState(IInstallProgress progress)
        {
            var targets = new[]
            {
                Prefs.RUNTIME_SETTINGS_FILE,
                Prefs.PREFS_FILE,
                Prefs.SETUP_CACHE_PREFS_FILE,
            };
            foreach (var path in targets)
            {
                try { if (File.Exists(path)) { File.Delete(path); progress.LogLine("clean re-install: removed " + path); } }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    progress.LogLine("warn: could not remove " + path + ": " + ex.Message);
                }
            }
            try
            {
                if (Directory.Exists(Config.REPLAYKIT_USER_STATE_CACHE))
                {
                    Directory.Delete(Config.REPLAYKIT_USER_STATE_CACHE, true);
                    progress.LogLine("clean re-install: cleared saved settings cache");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                progress.LogLine("warn: could not clear " + Config.REPLAYKIT_USER_STATE_CACHE + ": " + ex.Message);
            }
        }
    }
}
