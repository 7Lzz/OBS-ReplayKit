using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;

namespace ReplayKitSetup
{
    // .net framework 4.8 is the one prerequisite this exe needs before it can do anything, since it runs on it; windows 10 and 11 ship it and an older 4.x still starts the exe this far, so with the consent of the user it downloads the microsoft web installer and opens it with its own window, the way other installers bring it in, instead of installing it silently
    internal static class NetFramework
    {
        // the lowest Release value microsoft documents for 4.8
        private const int Release48 = 528040;
        // microsofts link to the .net framework 4.8 web installer, which fetches the rest of the framework itself
        private const string InstallerUrl = "https://go.microsoft.com/fwlink/?LinkId=2085155";
        private const string DownloadPage = "https://dotnet.microsoft.com/download/dotnet-framework/net48";
        private const long MaxInstallerBytes = 16L * 1024 * 1024;

        private const uint MB_OK = 0x0, MB_OKCANCEL = 0x1, MB_ICONERROR = 0x10, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000;
        private const int IDOK = 1;

        // the exit codes the .net framework installers document
        private const int ExitRestarting = 1641, ExitRestartRequired = 3010, ExitCancelled = 1602;

        public static bool IsInstalled()
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            return key?.GetValue("Release") is int release && release >= Release48;
        }

        // the running process keeps the runtime it started on, so once 4.8 is in the setup starts again as a new process with the same arguments
        public static void OfferInstall(string[] args)
        {
            if (Ask(SetupLocale.Message("setup.netFrameworkInstallPrompt"), MB_OKCANCEL | MB_ICONINFORMATION) != IDOK) return;
            int exitCode = DownloadAndRun();
            SetupLog.Line(".NET Framework 4.8 installer exit code " + exitCode);
            if (exitCode == ExitCancelled || exitCode == ExitRestarting) return;
            if (exitCode == ExitRestartRequired)
            {
                Ask(SetupLocale.Message("setup.netFrameworkNeedsRestart"), MB_OK | MB_ICONINFORMATION);
                return;
            }
            if (exitCode == 0 && IsInstalled())
            {
                string exe = NetCompat.ProcessPath;
                using (Process.Start(new ProcessStartInfo(exe, Win32Args.Build(args)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) })) { }
                return;
            }
            // explorer opens the page in the browser of the signed in user, so the browser never runs with the rights of this elevated setup
            if (Ask(SetupLocale.Message("setup.netFrameworkInstallFailed"), MB_OKCANCEL | MB_ICONERROR) == IDOK)
                using (Process.Start(new ProcessStartInfo(ReplayKitShared.SystemPrograms.Explorer, "\"" + DownloadPage + "\"") { UseShellExecute = false })) { }
        }

        private static int Ask(string text, uint type) =>
            Native.MessageBoxW(IntPtr.Zero, text, SetupLocale.Message("setup.windowTitle"), type | MB_SETFOREGROUND);

        // -1 when the installer could not be fetched, was not signed by microsoft or could not be started
        private static int DownloadAndRun()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string workDir = null;
            try
            {
                workDir = NetCompat.CreateTempSubdirectory("ReplayKit-netfx48-").FullName;
                string installer = Path.Combine(workDir, "ndp48-web.exe");
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)");
                    Download.Save(client, InstallerUrl, installer, MaxInstallerBytes, TimeSpan.FromMinutes(5));
                }
                if (!InputOverlay.IsMicrosoftSigned(installer, SetupLog.Line)) return -1;
                // no switches, so it shows its own window and the user goes thru it as with any download; the setup is already elevated, so it asks for nothing more
                using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = workDir });
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is InvalidDataException || ex is IOException || ex is OperationCanceledException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                SetupLog.Line("could not run the .NET Framework 4.8 installer: " + ex.Message);
                return -1;
            }
            finally
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
