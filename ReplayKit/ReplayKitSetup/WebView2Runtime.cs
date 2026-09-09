using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace ReplayKitSetup
{
    // the modern setup window (SetupWindow.cs) needs the evergreen webview2 runtime. it ships on every win11 and most
    // win10 boxes; when it is missing this downloads microsofts ~1.8 mb evergreen bootstrapper and runs it silently,
    // the same "fetch a dependency during setup" shape ObsDownloader uses for obs itself. nothing is bundled.
    internal static class WebView2Runtime
    {
        // microsofts stable evergreen bootstrapper link -- redirects to the current MicrosoftEdgeWebview2Setup.exe.
        private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private const long MaxBootstrapperBytes = 16L * 1024 * 1024;
        private const int InstallTimeoutMs = 5 * 60 * 1000;

        public static bool IsAvailable()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch (Exception) { return false; }
        }

        // returns true once the runtime is present. logSink is optional (null before the window exists).
        public static bool Ensure(Action<string> logSink)
        {
            void Log(string m) { try { logSink?.Invoke(m); } catch (Exception) { } }
            if (IsAvailable()) return true;

            Log("WebView2 runtime not found; downloading it from Microsoft...");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string workDir = Path.Combine(Path.GetTempPath(), "ReplayKit", "webview2-setup");
            string bootstrapper = Path.Combine(workDir, "MicrosoftEdgeWebview2Setup.exe");
            try
            {
                Directory.CreateDirectory(workDir);
                if (!Download(BootstrapperUrl, bootstrapper, Log)) return false;
                if (!RunSilent(bootstrapper, Log)) return false;
                bool ok = IsAvailable();
                Log(ok ? "WebView2 runtime installed." : "WebView2 runtime install finished but the runtime is still not detected.");
                return ok;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is System.Threading.Tasks.TaskCanceledException || ex is UnauthorizedAccessException)
            {
                Log("Could not install the WebView2 runtime: " + ex.Message);
                return false;
            }
            finally
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static bool Download(string url, string dst, Action<string> log)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("User-Agent", "OBSReplayKit/1.0 (+https://github.com/7Lzz/OBS-ReplayKit)");
            var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { log("WebView2 bootstrapper download returned HTTP " + (int)resp.StatusCode + "."); return false; }
            long total = resp.Content.Headers.ContentLength ?? 0;
            if (total > MaxBootstrapperBytes) { log("WebView2 bootstrapper exceeded the size cap; aborting."); return false; }

            long done = 0;
            using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var outFile = File.Open(dst, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    done += read;
                    if (done > MaxBootstrapperBytes) { log("WebView2 bootstrapper exceeded the size cap mid-download; aborting."); return false; }
                    outFile.Write(buffer, 0, read);
                }
            }
            log("WebView2 bootstrapper downloaded (" + (done / 1024) + " KB).");
            return true;
        }

        // the bootstrapper is a signed microsoft binary from a microsoft link; /silent /install pulls + installs the
        // per-machine runtime (the setup exe is already elevated, app.manifest).
        private static bool RunSilent(string bootstrapper, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo(bootstrapper, "/silent /install")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(bootstrapper),
                };
                using var proc = Process.Start(psi);
                if (!proc.WaitForExit(InstallTimeoutMs))
                {
                    try { proc.Kill(); } catch (InvalidOperationException) { }
                    log("WebView2 runtime installer did not finish within 5 minutes.");
                    return false;
                }
                if (proc.ExitCode != 0) log("WebView2 runtime installer exit code " + proc.ExitCode);
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                log("Could not start the WebView2 runtime installer: " + ex.Message);
                return false;
            }
        }
    }
}
