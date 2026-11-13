using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace ReplayKitHelper
{
    // shows a file or folder in file explorer, raising a window that already has the folder open instead of stacking another one
    internal static class Explorer
    {
        // normalize explorer paths before comparing them. file explorer may report the active tab thru locationurl or thru document.folder, depending on windows version and tab state.
        internal static string ConvertToExplorerComparePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        internal static string GetExplorerWindowPath(dynamic window)
        {
            try
            {
                string loc = (string)window.LocationURL;
                if (!string.IsNullOrEmpty(loc) && loc.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return new Uri(loc).LocalPath;
            }
            catch (COMException) { } catch (RuntimeBinderException) { }
            try
            {
                string path = (string)window.Document.Folder.Self.Path;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch (COMException) { } catch (RuntimeBinderException) { }
            return null;
        }

        // shows a file in explorer: a window that already has its folder open is raised, else the folder opens with the file selected. we deliberately dont try to re-select the file in a window that is already open -- the com dance is fragile and the user is usually just trying to surface the folder. true when an open window was raised
        internal static bool RevealInExplorer(string full)
        {
            string parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                var existing = GetExplorerHwndForPath(parent);
                if (existing != IntPtr.Zero) return Native.FocusHwnd(existing);
            }
            // explorer.exe /select,"<path>" opens the containing folder with the file already highlighted. arguments must be one string becuase explorer parses /select,<rest> as a unit.
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select," + ProcessArgs.Quote(full), UseShellExecute = false })?.Dispose();
            return false;
        }

        // find an already-open file explorer window thats showing targetPath. returns the windows hwnd or IntPtr.Zero if no match. used by /open-clip-folder, /open-log-folder and /open-folder to raise an existing explorer window for the clip folder instead of spawning yet another explorer.exe -- windows otherwise stacks duplicate folder windows every time the user clicks the docks open folder button. uses Shell.Applications Windows() collection (the same one explorer itself uses); LocationURL on each item is a file:// url we can convert to a local path and compare case-insensitively to the target. ie/edge browser windows show up here too -- we skip anything whose LocationURL isnt file://.
        public static IntPtr GetExplorerHwndForPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return IntPtr.Zero;
            string full = ConvertToExplorerComparePath(targetPath);
            if (full == null) return IntPtr.Zero;

            dynamic shell = null;
            dynamic windows = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                shell = Activator.CreateInstance(shellType);
                windows = shell.Windows();
                foreach (dynamic w in windows)
                {
                    try
                    {
                        string here = ConvertToExplorerComparePath(GetExplorerWindowPath(w));
                        if (string.Equals(here, full, StringComparison.OrdinalIgnoreCase))
                        {
                            long raw = (long)w.HWND;
                            if (raw != 0) return new IntPtr(raw);
                        }
                    }
                    catch (COMException) { } catch (RuntimeBinderException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Get-ExplorerHwndForPath failed: " + ex.Message);
            }
            finally
            {
                if (windows != null) { try { Marshal.ReleaseComObject(windows); } catch (ArgumentException) { } }
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch (ArgumentException) { } }
            }
            return IntPtr.Zero;
        }
    }
}
