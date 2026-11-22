using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReplayKitShared
{
    // starts a program thru a shortcut that the running explorer opens, so a program started from an elevated process runs with the normal rights of the signed in user (the installer starts obs this way, the helper the share window when obs runs as admin)
    public static class ShellLaunch
    {
        public static bool Start(string exe, string arguments, string linkPath, Action<string> log)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? "" });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception exc) when (exc is COMException || exc is TargetInvocationException || exc is MissingMemberException || exc is ArgumentNullException || exc is IOException || exc is UnauthorizedAccessException)
            {
                log?.Invoke("failed to prepare the launch shortcut for " + Path.GetFileName(exe) + ": " + exc.Message);
                return false;
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }

            try
            {
                // a path never holds a quote, so quoting the one argument is all explorer needs
                var psi = new ProcessStartInfo(SystemPrograms.Explorer, "\"" + linkPath + "\"") { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception || exc is InvalidOperationException)
            {
                log?.Invoke("failed to start " + Path.GetFileName(exe) + " through the shell: " + exc.Message);
                return false;
            }
        }
    }
}
