using System;
using System.Collections.Generic;
using System.IO;

namespace ReplayKitSetup
{
    // shared shape for a running install/cleanup progress view. the WinForms installer window (InstallerProgress), Update.CleanupProgress (headless --cleanup) and RunUninstallDiscordScreenshareMode all implement this so the apply flow and Cleanup.RunCleanup run from any caller.
    public interface IInstallProgress
    {
        int TotalSteps { get; set; }
        List<string> Issues { get; }
        void Render(int completed, SetupLocale.Text title, SetupLocale.Text detail, string state);
        void LogLine(string message);
        void AddIssue(SetupLocale.Text message);
        // fine-grained progress inside the step currently being rendered, fraction 0..1 -- lets the bar move during the one long step (the OBS download) instead of sitting still. no-op for the headless reporters.
        void SubProgress(double fraction, string detail);
    }

    // headless installer flow: make sure OBS is present, write the ReplayKit setup, launch OBS. the only interactive choice, discord screenshare, comes from the setup window.
    public static class Apply
    {
        private static readonly System.Text.RegularExpressions.Regex IssueWords = new System.Text.RegularExpressions.Regex(
            @"\b(?:warn\w*|failed|missing|not found|skipped|timed out|permission denied)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        // a path or a file name is never a problem by itself: the list of copied files ("-> icons/action/warning.svg") used to count as two failed steps and left setup "incomplete" when everything had worked
        private static readonly System.Text.RegularExpressions.Regex PathToken = new System.Text.RegularExpressions.Regex(
            @"(?:[A-Za-z]:)?[^\s""']*[\\/][^\s""']*|\S+\.[A-Za-z0-9]{1,5}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        // whether a log line of a step says something went wrong; shared by the install, uninstall and cleanup runs
        public static bool LooksLikeIssue(string line)
        {
            string text = (line ?? "").Trim();
            if (text.Length == 0 || text.StartsWith("->", StringComparison.Ordinal)) return false;
            return IssueWords.IsMatch(PathToken.Replace(text, " "));
        }

        // Exceptions stop the flow; false results are reported as incomplete steps.
        public static void RunApplyStep(IInstallProgress progress, int index, SetupLocale.Text title, SetupLocale.Text detail, Func<object> action)
        {
            progress.Render(index - 1, title, detail, "working");
            object result;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                progress.AddIssue(new SetupLocale.Text(SetupLocale.Message("setup.stepFailedError", "step", title.Shown, "error", ex.Message),
                    SetupLocale.Pair("setup.stepFailedError", "step", title.English, "error", SetupLocale.ToEnglish(ex.Message)).English));
                progress.Render(index - 1, title, detail, "failed");
                throw;
            }
            if (result is bool b && !b)
            {
                progress.AddIssue(new SetupLocale.Text(SetupLocale.Message("setup.stepDidNotComplete", "step", title.Shown), SetupLocale.Pair("setup.stepDidNotComplete", "step", title.English).English));
                progress.Render(index, title, detail, "failed");
                return;
            }
            progress.Render(index, title, detail, "done");
            System.Threading.Thread.Sleep(120);
        }

        // build + run every apply step against the given progress sink. rethrows if a step throws; otherwise returns the accumulated non-fatal issues. discordScreenshare is the one user choice -- null means "leave it as loaded" (the live settings for an update; the default after cleanInstall wiped them). cleanInstall drops the preserved ReplayKit settings/prefs first so the run rebuilds from the bundle + defaults, like a first install.
        public static List<string> RunInstallFlow(bool? discordScreenshare, IInstallProgress progress, bool cleanInstall = false)
        {
            Transform.CleanReinstall = cleanInstall;
            if (cleanInstall) WipePreservedReplaykitState(progress);

            var prefs = Prefs.LoadPrefs();
            // the setup checkbox turns the whole Share Preview feature on or off: turning it on from off also switches Share Preview on, turning it off switches it off, and an update that leaves it on keeps the Share Preview state saved in the settings. null (headless) leaves whatever was loaded.
            if (discordScreenshare.HasValue)
            {
                if (discordScreenshare.Value && !prefs.DiscordScreenshareEnabled) prefs.DiscordProjectorEnabled = true;
                prefs.DiscordScreenshareEnabled = discordScreenshare.Value;
            }
            if (!prefs.DiscordScreenshareEnabled) prefs.DiscordProjectorEnabled = false;

            // capture before any step runs -- a genuine first install has no runtime settings file yet, and that is the only case that should arm the in-obs first-run wizard. a clean re-install just deleted it, so it counts too.
            bool firstInstall = !File.Exists(Prefs.RUNTIME_SETTINGS_FILE);

            var steps = new List<(SetupLocale.Text Title, SetupLocale.Text Detail, Func<object> Action)>();
            void Add(SetupLocale.Text title, SetupLocale.Text detail, Func<object> action) => steps.Add((title, detail, action));

            // hard prerequisite: every step after this writes into the OBS install. a failed download used to fall through as a soft "issue" and the flow kept going, scattering plugins into a dead folder and still showing "Done".
            Add(SetupLocale.Pair("setup.installObsStudio"), SetupLocale.Pair("setup.installsObsOrUpdatesItTo"), () =>
            {
                ObsDownloader.EnsureObsVersion(progress);
                if (Obs.FindObsExe() == null)
                    throw new InvalidOperationException(
                        "OBS Studio could not be installed automatically. Install OBS from obsproject.com, then run this installer again.");
                // every plugin and the process host are made for the one supported version, so nothing is installed into another
                if (!ObsVersions.IsInstalledSupported())
                    throw new InvalidOperationException(
                        "ReplayKit needs OBS " + ObsVersions.Install.Tested + " and the installed OBS could not be brought to it. Install OBS " + ObsVersions.Install.Tested + " from obsproject.com, then run this installer again.");
                ProcessHost.Validate();
                return (object)true;
            });
            Add(SetupLocale.Pair("setup.closeObs"), SetupLocale.Pair("setup.stopsObsToWriteSettings"), () => (object)Obs.CloseObs(progress.LogLine));
            Add(SetupLocale.Pair("setup.lockObsUpdates"), SetupLocale.Pair("setup.keepsObsOnAVersionReplaykit"), () => (object)ObsUpdateLock.Lock(progress.LogLine));
            Add(SetupLocale.Pair("setup.installObsProcessRoles"), SetupLocale.Pair("setup.preparesObsBrowserAndReplaykitHelper"), () => { ProcessHost.Install(progress.LogLine); return (object)true; });
            Add(SetupLocale.Pair("setup.clearOldObsFiles"), SetupLocale.Pair("setup.removesFilesAnObsUpdateLeft"), () => { ObsDownloader.RemoveReplacedCoreModules(progress.LogLine); return (object)true; });
            Add(SetupLocale.Pair("setup.backUpCurrentObsSettings"), SetupLocale.Pair("setup.savesARestoreCopyFirst"), () => { Installer.BackupExistingConfig(progress.LogLine); return (object)true; });
            Add(SetupLocale.Pair("setup.prepareObsSettingsFolder"), SetupLocale.Pair("setup.createsTheConfigFolder"), () => { Directory.CreateDirectory(Config.OBS_CONFIG); Obs.CleanupCrashFlags(progress.LogLine); return (object)true; });
            // no helper means no dock, no clips, no settings ui -- throw so RunApplyStep stops the flow instead of the old behaviour where a false here only added a "needs attention" line while the rest ran anyway.
            Add(SetupLocale.Pair("setup.buildReplaykitHelper"), SetupLocale.Pair("setup.preparesTheLocalHelper"), () =>
            {
                if (Installer.EnsureLauncherBuilt(progress.LogLine)) return (object)true;
                throw new InvalidOperationException(SetupLocale.Message("setup.obsReplaykitCannotRunWithoutThe"));
            });
            if (prefs.DiscordScreenshareEnabled)
                Add(SetupLocale.Pair("setup.enableDiscordScreenshare"), SetupLocale.Pair("setup.installsTheAudioCableDriver"), () =>
                {
                    // no double install: if the cable is already there and its endpoint is renamed, EnsureVbcable would only re-bounce audiosrv for nothing.
                    if (VbCable.IsShareAudioReady())
                    {
                        progress.LogLine("OBS Stream Audio already set up -- nothing to install.");
                        return (object)true;
                    }
                    return (object)VbCable.EnsureVbcable(progress.LogLine);
                });
            // user explicitly unticked the box (not headless) and the cable is on the machine -- take it back off, so the checkbox is a real on/off for the whole feature on Update and Re-Install too.
            else if (discordScreenshare == false && VbCable.IsVbcableInstalled())
                Add(SetupLocale.Pair("setup.removeDiscordScreenshare"), SetupLocale.Pair("setup.uninstallsTheAudioCableDriver"), () => (object)VbCable.UninstallVbcable(progress.LogLine));
            else
                Add(SetupLocale.Pair("setup.skipDiscordScreenshare"), SetupLocale.Pair("setup.sharePreviewStaysOff"), () => (object)true);
            Add(SetupLocale.Pair("setup.writeReplaykitObsProfile"), SetupLocale.Pair("setup.writesTheReplaykitObsProfile"), () =>
            {
                bool ok = Installer.InstallObsConfig(prefs, progress.LogLine) > 0;
                if (ok && firstInstall) Installer.MarkSetupWizardPending(progress.LogLine);
                return (object)ok;
            });
            Add(SetupLocale.Pair("setup.applyDefaultSettings"), SetupLocale.Pair("setup.turnsOnTheDefaultsThisVersion"), () => (object)DefaultsMigration.Run(progress.LogLine));
            Add(SetupLocale.Pair("setup.enableObsWebsocket"), SetupLocale.Pair("setup.enablesTheLocalObsWebsocket"), () => (object)Installer.ConfigureObsWebsocket(progress.LogLine));
            Add(SetupLocale.Pair("setup.installCustomControlsAndClips"), SetupLocale.Pair("setup.addsTheDockAndClipsFiles"), () => (object)(Installer.InstallObsCustomDock(progress.LogLine) > 0));
            Add(SetupLocale.Pair("setup.installReplaykitPlugin"), SetupLocale.Pair("setup.addsTheReplaykitWindowNotificationsAnd"), () => (object)NativePlugin.InstallReplaykitPlugin(progress.LogLine));
            Add(SetupLocale.Pair("setup.registerClipNotifications"), SetupLocale.Pair("setup.letsClipReadyNotificationsShowUp"), () => (object)Installer.RegisterClipNotificationShortcut(progress.LogLine));
            Add(SetupLocale.Pair("setup.removeLegacyLauncherTask"), SetupLocale.Pair("setup.removesTheOldLauncherTask"), () => (object)Installer.RemoveObsElevationTask(progress.LogLine));
            if (prefs.AllowSleepWhileActive)
                Add(SetupLocale.Pair("setup.allowMonitorAndPcSleep"), SetupLocale.Pair("setup.letsWindowsSleepDuringCapture"), () => (object)Installer.InstallObsSleepOverride(true, progress.LogLine));
            else
                Add(SetupLocale.Pair("setup.restoreObsSleepBlocking"), SetupLocale.Pair("setup.restoresObsSleepBlocking"), () => (object)Installer.InstallObsSleepOverride(false, progress.LogLine));
            if (prefs.PinObsTrayIcon)
                Add(SetupLocale.Pair("setup.pinObsTrayIcon"), SetupLocale.Pair("setup.pinsTheObsTrayIcon"), () => { TrayPin.PinObsTrayIcon(progress.LogLine); return (object)true; });
            else
                Add(SetupLocale.Pair("setup.skipTrayIconPinning"), SetupLocale.Pair("setup.leavesTheTrayIconDefault"), () => { TrayPin.UnpinObsTrayIcon(progress.LogLine); return (object)true; });
            Add(SetupLocale.Pair("setup.installVideoTools"), SetupLocale.Pair("setup.verifiesTheTrimCompressTools"), () =>
            {
                if (!Installer.InstallObsFfmpeg(progress.LogLine))
                    throw new IOException(SetupLocale.Message("setup.ffmpegAndFfprobeCouldNotBe"));
                return (object)true;
            });
            Add(SetupLocale.Pair("setup.installWasdMouseOverlay"), SetupLocale.Pair("setup.addsTheWasdMouseOverlayPlugin"),
                () => (object)(InputOverlay.InstallInputOverlayPlugin(progress.LogLine) && InputOverlay.InstallInputOverlayPresets(progress.LogLine)));
            Add(SetupLocale.Pair("setup.installBongoCatOverlay"), SetupLocale.Pair("setup.verifiesTheBongoCatFilesAnd"), () =>
            {
                if (!BongoCat.InstallBongoCat(progress.LogLine))
                    throw new IOException(SetupLocale.Message("setup.bongoCatCouldNotBeInstalled"));
                return (object)true;
            });
            Add(SetupLocale.Pair("setup.installMotionBlurFilter"), SetupLocale.Pair("setup.addsTheMotionBlurFilter"),
                () => (object)ShaderFilter.InstallReplaykitMotionBlurPlugin(progress.LogLine));
            Add(SetupLocale.Pair("setup.installDesktopAudioCapture"), SetupLocale.Pair("setup.addsDesktopAudioCapture"), () => (object)WinCapture.InstallWinCaptureAudio(progress.LogLine));
            Add(SetupLocale.Pair("setup.prepareClipFolders"), SetupLocale.Pair("setup.createsTheClipFolders"), () => { Installer.EnsureRecordingDirs(prefs, progress.LogLine); return (object)true; });
            Add(SetupLocale.Pair("setup.setWindowsStartup"), SetupLocale.Pair("setup.addsObsToWindowsStartup"), () => (object)Startup.ConfigureObsStartup(prefs.ObsStartupEnabled, progress.LogLine));
            Add(SetupLocale.Pair("setup.prepareObsTheme"), SetupLocale.Pair("setup.appliesYourThemeBeforeObsStarts"), () => { Installer.PrepareObsTheme(progress.LogLine); return (object)true; });
            Add(SetupLocale.Pair("setup.launchObs"), SetupLocale.Pair("setup.startsObs"), () => (object)Obs.LaunchObs(progress.LogLine));

            progress.TotalSteps = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action);
            }
            bool complete = progress.Issues.Count == 0;
            Update.RecordSetupResult(complete);
            progress.Render(progress.TotalSteps, complete ? SetupLocale.Pair("setup.setupComplete") : SetupLocale.Pair("setup.setupIncomplete"), complete ? SetupLocale.Pair("setup.obsReplaykitIsReady") : SetupLocale.Pair("setup.someStepsNeedAttentionSeeThe"), complete ? "done" : "failed");
            return progress.Issues;
        }

        // clean re-install: drop the ReplayKit settings + prefs the normal flow would preserve, so everything is rebuilt from the bundle and defaults and the in-obs first-run wizard re-arms. clip history (clips_db/index) is left alone.
        private static void WipePreservedReplaykitState(IInstallProgress progress)
        {
            var targets = new[]
            {
                Prefs.RUNTIME_SETTINGS_FILE,
                Prefs.PREFS_FILE,
                Prefs.SETUP_CACHE_PREFS_FILE,
                DefaultsMigration.MarkerPath,
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
