using System;
using System.Collections.Generic;
using System.IO;
using ReplayKitShared;

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

    // the three buttons of the setup window: Install on a pc without replaykit, Update and Re-Install on one that has it
    public enum SetupMode { Install, Update, Reinstall }

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

        // exceptions stop the flow; false results are reported as incomplete steps.
        public static bool RunApplyStep(IInstallProgress progress, int index, SetupLocale.Text title, SetupLocale.Text detail, Func<object> action)
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
                    SetupLocale.Pair("setup.stepFailedError", "step", title.English, "error", SetupLocale.EnglishOf(ex)).English));
                progress.Render(index - 1, title, detail, "failed");
                throw;
            }
            if (result is bool b && !b)
            {
                progress.AddIssue(new SetupLocale.Text(SetupLocale.Message("setup.stepDidNotComplete", "step", title.Shown), SetupLocale.Pair("setup.stepDidNotComplete", "step", title.English).English));
                progress.Render(index, title, detail, "failed");
                return false;
            }
            progress.Render(index, title, detail, "done");
            System.Threading.Thread.Sleep(120);
            return true;
        }

        // clean setup uses the defaults and clears old settings only after prerequisites and backup succeed. an update keeps the obs config as obs saved it and refreshes the replaykit files the way the in-app updater does; one asked for where there is no settings file to keep becomes a full install, since a first install is what that pc needs
        public static List<string> RunInstallFlow(bool? discordScreenshare, IInstallProgress progress, SetupMode mode)
        {
            using var operation = ObsExit.TryTakeOperation(ObsExit.SetupGateWaitMs);
            if (operation == null) throw new IOException(SetupLocale.Message("setup.anotherReplaykitUpdateIsAlready"));
            using var transaction = RuntimeTransaction.Open();
            bool cleanInstall = mode == SetupMode.Reinstall;
            bool updateRequested = mode == SetupMode.Update;
            bool update = updateRequested && File.Exists(Prefs.RUNTIME_SETTINGS_FILE);
            if (mode == SetupMode.Update && !update) progress.LogLine("no ReplayKit settings to keep, so this update runs as a full install");
            Transform.CleanReinstall = cleanInstall;
            var prefs = Prefs.LoadPrefs(cleanInstall);
            // the setup checkbox turns the whole Share Preview feature on or off: turning it on from off also switches Share Preview on, turning it off switches it off, and an update that leaves it on keeps the Share Preview state saved in the settings. null (headless) leaves whatever was loaded.
            if (discordScreenshare.HasValue)
            {
                if (discordScreenshare.Value && !prefs.DiscordScreenshareEnabled) prefs.DiscordProjectorEnabled = true;
                prefs.DiscordScreenshareEnabled = discordScreenshare.Value;
            }
            if (!prefs.DiscordScreenshareEnabled) prefs.DiscordProjectorEnabled = false;

            // capture before any step runs -- a genuine first install has no runtime settings file yet, and that is the only case that should arm the in-obs first-run wizard. a clean re-install just deleted it, so it counts too.
            bool firstInstall = cleanInstall || !File.Exists(Prefs.RUNTIME_SETTINGS_FILE);

            var steps = new List<(SetupLocale.Text Title, SetupLocale.Text Detail, Func<object> Action)>();
            void Add(SetupLocale.Text title, SetupLocale.Text detail, Func<object> action) => steps.Add((title, detail, action));
            bool externalRuntimeWriting = false;

            // hard prerequisite: every step after this writes into the OBS install. a failed download used to fall through as a soft "issue" and the flow kept going, scattering plugins into a dead folder and still showing "Done".
            Add(SetupLocale.Pair("setup.installObsStudio"), SetupLocale.Pair("setup.installsObsOrUpdatesItTo"), () =>
            {
                Installer.ValidateRuntimeAssets();
                if (transaction.Pending) Obs.CloseObs(progress.LogLine, forceCloseRemaining: !updateRequested);
                transaction.Recover(progress.LogLine);
                externalRuntimeWriting = Obs.FindObsExe() != null && !ObsVersions.IsInstalledSupported();
                ObsDownloader.EnsureObsVersion(progress, forceClose: !updateRequested);
                if (Obs.FindObsExe() == null)
                {
                    var reason = SetupLocale.Pair("setup.obsCouldNotBeInstalledAutomatically");
                    throw new InvalidOperationException(reason.Shown).WithEnglish(reason);
                }
                // every plugin and the process host are made for the one supported version, so nothing is installed into another
                if (!ObsVersions.IsInstalledSupported())
                {
                    var reason = SetupLocale.Pair("setup.obsVersionNeeded", "version", ObsVersions.Install.Tested);
                    throw new InvalidOperationException(reason.Shown).WithEnglish(reason);
                }
                ProcessHost.Validate();
                return (object)true;
            });
            bool obsClosed = false;
            bool launchAttempted = false;
            bool configWriting = false;
            ConfigBackups.Snapshot configBackup = null;
            Add(SetupLocale.Pair("setup.closeObs"), SetupLocale.Pair("setup.stopsObsToWriteSettings"), () =>
            {
                int closed = Obs.CloseObs(progress.LogLine, forceCloseRemaining: !updateRequested);
                obsClosed = true;
                return (object)closed;
            });
            Add(SetupLocale.Pair("setup.backUpCurrentObsSettings"), SetupLocale.Pair("setup.savesARestoreCopyFirst"), () =>
            {
                string reason = cleanInstall ? "reinstall" : mode == SetupMode.Update ? "update" : "install";
                configBackup = ConfigBackups.Create(reason, SetupLog.Line);
                return (object)true;
            });
            Add(SetupLocale.Pair("setup.lockObsUpdates"), SetupLocale.Pair("setup.keepsObsOnAVersionReplaykit"), () => (object)ObsUpdateLock.Lock(progress.LogLine));
            Add(SetupLocale.Pair("setup.installObsProcessRoles"), SetupLocale.Pair("setup.preparesObsBrowserAndReplaykitHelper"), () =>
            {
                if (externalRuntimeWriting) Update.MarkUpdatePending();
                ProcessHost.Install(progress.LogLine, () =>
                {
                    Update.MarkUpdatePending();
                    externalRuntimeWriting = true;
                });
                return (object)true;
            });
            Add(SetupLocale.Pair("setup.prepareObsSettingsFolder"), SetupLocale.Pair("setup.createsTheConfigFolder"), () =>
            {
                Update.MarkUpdatePending();
                configWriting = true;
                Directory.CreateDirectory(Config.OBS_CONFIG);
                Obs.CleanupCrashFlags(progress.LogLine);
                return (object)true;
            });
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
            if (update)
                Add(SetupLocale.Pair("setup.updateReplaykitFiles"), SetupLocale.Pair("setup.replacesTheReplaykitFilesAndKeeps"),
                    () =>
                    {
                        if (!Installer.UpdateInstalledConfig(transaction, prefs, discordScreenshare.HasValue, progress.LogLine))
                            return (object)false;
                        externalRuntimeWriting = true;
                        return (object)ShareWindowInstall.Install(progress.LogLine);
                    });
            else
                Add(SetupLocale.Pair("setup.writeReplaykitObsProfile"), SetupLocale.Pair("setup.writesTheReplaykitObsProfile"), () =>
                {
                    if (cleanInstall) WipePreservedReplaykitState(progress);
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
            Add(SetupLocale.Pair("setup.launchObs"), SetupLocale.Pair("setup.startsObs"), () =>
            {
                if (update) transaction.Complete(progress.LogLine);
                configWriting = false;
                if (update) Installer.CleanupReplaykitLegacyFiles(SetupLog.Line);
                launchAttempted = true;
                if (!Obs.LaunchObs(progress.LogLine))
                    throw new IOException(SetupLocale.Message("setup.replaykitWasInstalledButObsCould", "version", VersionInfo.Version));
                return (object)true;
            });

            progress.TotalSteps = steps.Count;
            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    if (!RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action) && configWriting)
                        throw new IOException(SetupLocale.Message("setup.stepDidNotComplete", "step", steps[i].Title.Shown));
                }
            }
            catch (Exception error)
            {
                bool consistent = true;
                var failure = configWriting ? Update.RecoverInstall(transaction, configBackup, error, SetupLog.Line, out consistent) : error;
                Update.RecordSetupFailure(failure);
                if (update && obsClosed && consistent && !transaction.Pending && !launchAttempted && !externalRuntimeWriting)
                {
                    try { Obs.LaunchObs(progress.LogLine); }
                    catch (Exception ex) { progress.LogLine("warn: could not start OBS again: " + ex.Message); }
                }
                if (update && obsClosed && externalRuntimeWriting && !launchAttempted)
                    SetupLog.Line("OBS was left closed because an external runtime change may require repair");
                if (failure == error) throw;
                throw failure;
            }
            finally { configBackup?.Dispose(); }
            bool complete = progress.Issues.Count == 0;
            bool recorded = Update.RecordSetupResult(complete);
            progress.Render(progress.TotalSteps, complete ? SetupLocale.Pair("setup.setupComplete") : SetupLocale.Pair("setup.setupIncomplete"), complete ? SetupLocale.Pair("setup.obsReplaykitIsReady") : SetupLocale.Pair("setup.someStepsNeedAttentionSeeThe"), complete ? "done" : "failed");
            if (complete && recorded) ConfigBackups.StartCleanup();
            return progress.Issues;
        }

        // clean re-install: drop the ReplayKit settings + prefs the normal flow would preserve, so everything is rebuilt from the bundle and defaults and the in-obs first-run wizard re-arms. clip history (clips_db/index) is left alone.
        private static void WipePreservedReplaykitState(IInstallProgress progress)
        {
            var targets = new HashSet<string>(StateLayout.Paths(Config.REPLAYKIT_CONFIG, StateLayout.SettingsFile), StringComparer.OrdinalIgnoreCase)
            {
                Prefs.PREFS_FILE,
                Prefs.SETUP_CACHE_PREFS_FILE,
            };
            targets.UnionWith(StateLayout.Paths(Config.REPLAYKIT_CONFIG, StateLayout.DefaultsAppliedFile));
            foreach (var path in targets)
            {
                try { File.Delete(path); }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
