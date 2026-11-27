using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace ReplayKitSetup
{
    // what the setup and uninstall windows share around the page they host: loading it with no way off it, the art it shows, the window icon, the dpi scale and the error log
    internal static class BuiltinPage
    {
        internal static bool IsTrusted(string source) => string.Equals(source, "about:blank", StringComparison.Ordinal);

        internal static void Load(CoreWebView2 web, string html)
        {
            // NavigateToString can report its encoded HTML here even though the document source is about:blank.
            string initialUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            bool initial = true;
            web.NavigationStarting += (sender, e) =>
            {
                bool allowed = initial && (IsTrusted(e.Uri) || string.Equals(e.Uri, initialUri, StringComparison.Ordinal));
                e.Cancel = !allowed;
                if (allowed) { initial = false; initialUri = null; }
            };
            web.FrameNavigationStarting += (sender, e) => { e.Cancel = true; };
            web.NewWindowRequested += (sender, e) => { e.Handled = true; };
            web.PermissionRequested += (sender, e) => { e.State = CoreWebView2PermissionState.Deny; };
            web.NavigateToString(html);
        }

        // the page as the exe embeds it, with the hero art and brand icon inlined from the asset bundle, since there is no helper here to fetch them from
        internal static string Read(string resource, string file)
        {
            string html;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ReplayKitSetup." + resource))
            {
                if (stream == null) throw SetupLocale.Error("setup.aFileIsMissingFromThisSetup", m => new InvalidOperationException(m), "file", file);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                html = reader.ReadToEnd();
            }
            string folder = "obs-studio/" + ReplayKitShared.ReplayKitFolder.Name + "/";
            string helperDir = Path.GetDirectoryName(ReplayKitShared.ReplayKitFolder.HelperExe).Replace('\\', '/');
            html = html.Replace("{{HERO_ART}}", BundledDataUri(folder + "obs-custom-dock/update-art.png", "image/png"));
            return html.Replace("{{BRAND_ICON}}", BundledDataUri(folder + helperDir + "/obs-replaykit.ico", "image/x-icon"));
        }

        internal static string BundledDataUri(string entryPath, string mime)
        {
            try
            {
                using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("ReplayKitSetup.assets.bundle.zip");
                if (res == null) return "";
                using var zip = new System.IO.Compression.ZipArchive(res, System.IO.Compression.ZipArchiveMode.Read);
                var entry = zip.GetEntry(entryPath);
                if (entry == null) return "";
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                return "data:" + mime + ";base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception)
            {
                return "";
            }
        }

        internal static double DpiScale()
        {
            try
            {
                uint dpi = Native.GetDpiForSystem();
                if (dpi >= 72 && dpi <= 480) return dpi / 96.0;
            }
            catch (Exception) { }
            return 1.0;
        }

        internal static void SetIconFromExe(IntPtr hwnd)
        {
            try
            {
                string exe = NetCompat.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe)) return;
                IntPtr hIcon = Native.ExtractIconW(Native.GetModuleHandleW(null), exe, 0);
                if (hIcon != IntPtr.Zero && hIcon != (IntPtr)1)
                {
                    Native.SendMessageW(hwnd, 0x0080, (IntPtr)1, hIcon);
                    Native.SendMessageW(hwnd, 0x0080, IntPtr.Zero, hIcon);
                }
            }
            catch (Exception) { }
        }

        internal static void WriteErrorLog(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "OBSReplayKit-error.log");
                File.WriteAllText(path, ex.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }
    }
}
