// obs replaykit launcher. native exe that hosts windows powershell in-process via the system.management.automation sdk and runs local_helper_server.ps1. branded process entry in task manager ('obs replaykit' + obs icon) instead of generic 'windows powershell'. hosted powershell runs in this same process so $pid inside the helper is this exe, get-process returns obsreplaykit, and 90_runtime.ps1s parent watchdog sees obs64.exe as grandparent thru this exe. build: utils\launcher\build_launcher.ps1.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;

[assembly: AssemblyTitle("OBS ReplayKit")]
[assembly: AssemblyDescription("OBS ReplayKit upload + clip-management helper")]
[assembly: AssemblyProduct("OBS ReplayKit")]
[assembly: AssemblyCompany("OBS ReplayKit")]
[assembly: AssemblyCopyright("Released under the MIT license. See LICENSE.")]

// AssemblyVersion / AssemblyFileVersion live in AssemblyVersion.Generated.cs, written by build_launcher.ps1 from obs_replaykit/__init__.py so there is one version to bump instead of two.

namespace OBSReplayKit
{
    internal static class Program
    {
        // sta becuase the helper uses com sta objects (shell.application, system.windows.forms.clipboard, etc.) the same way a normal powershell.exe console host would. without it the runspaces sta apartment requirement clashes with the defualt mta on the main thread: "when the runspace is set to use the current thread, the apartment state in the invocation settings must match that of the current thread."
        [STAThread]
        internal static int Main(string[] args)
        {
            // sibling-only resolution -- no PATH lookup, no user-configurable location. a missing sibling is a hard install error worth surfacing.
            string exePath = Assembly.GetExecutingAssembly().Location;
            string exeDir = Path.GetDirectoryName(exePath);
            string helperScript = Path.Combine(exeDir ?? ".", "local_helper_server.ps1");
            if (!File.Exists(helperScript))
            {
                Console.Error.WriteLine(
                    "OBS ReplayKit: local_helper_server.ps1 not found next to " + exePath);
                return 2;
            }

            // no port pre-check -- the helpers takeover dance (post /shutdown, wait, retry, force-kill) is what makes elevation handovers work. pre-checking would skip the dance and leave the orphan alive forever. we rely on the singleton mutex + environment.exit() at the end of main to keep the process count clean.

            // createdefault2 is the lean variant (engine snap-in only, no microsoft.powershell.utility); helper add-types in what it needs (system.drawing etc.) so cold-start is faster.
            InitialSessionState iss = InitialSessionState.CreateDefault2();
            iss.ExecutionPolicy = ExecutionPolicy.Bypass;
            // the thread model matters here: sta so that com helpers (shell.application, the named clipboard interfaces) work the same way they do from a normal powershell.exe.
            iss.ApartmentState = System.Threading.ApartmentState.STA;
            iss.ThreadOptions = PSThreadOptions.UseCurrentThread;

            using (Runspace rs = RunspaceFactory.CreateRunspace(iss))
            using (PowerShell ps = PowerShell.Create())
            {
                rs.Open();
                ps.Runspace = rs;

                // equivalent of `powershell.exe -file <script> <args>`. addcommand(scriptpath) loads the file as a script and binds subsequent addparameter/addargument calls to its param() block.
                ps.AddCommand(helperScript);

                // route -name value pairs into addparameter so they bind to the helper scripts param() block.
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].StartsWith("-"))
                    {
                        string name = args[i].TrimStart('-');
                        bool hasValue = (i + 1 < args.Length) && !args[i + 1].StartsWith("-");
                        if (hasValue)
                        {
                            ps.AddParameter(name, args[i + 1]);
                            i++;
                        }
                        else
                        {
                            ps.AddParameter(name);
                        }
                    }
                    else
                    {
                        ps.AddArgument(args[i]);
                    }
                }

                // surface helper error/warning streams to stderr. usually quiet (the helper write-logs to %temp%), but an add-type or early-load crash needs to be visible.
                ps.Streams.Error.DataAdded += (sender, e) =>
                {
                    var rec = ((PSDataCollection<ErrorRecord>)sender)[e.Index];
                    Console.Error.WriteLine("[ERROR] " + rec.ToString());
                    if (rec.Exception != null && rec.ScriptStackTrace != null)
                    {
                        Console.Error.WriteLine(rec.ScriptStackTrace);
                    }
                };
                ps.Streams.Warning.DataAdded += (sender, e) =>
                {
                    var rec = ((PSDataCollection<WarningRecord>)sender)[e.Index];
                    Console.Error.WriteLine("[WARN] " + rec.Message);
                };

                int code = 0;
                try
                {
                    ps.Invoke();
                    code = ps.HadErrors ? 1 : 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("OBS ReplayKit: helper terminated: " + ex.Message);
                    code = 1;
                }
                finally
                {
                    try { rs.Close(); } catch { }
                }
                // force-terminate. an `exit n` from the hosted powershell returns control to invoke() but background threads/com apartments/ps engine can keep the process alive after main returns -- environment.exit short-circuits all of that so a 'couldnt bind port, exit 0' helper doesnt linger in task manager.
                Environment.Exit(code);
                return code; // unreachable, but c# requires a return
            }
        }

        // is anything listening on 127.0.0.1:<port>? true if so_reuseaddr-style bind fails. uses the same loopback the helper binds so the result reliably matches the condition the helper script would hit.
        private static bool IsPortAlreadyBound(int port)
        {
            TcpListener probe = null;
            try
            {
                probe = new TcpListener(IPAddress.Loopback, port);
                probe.ExclusiveAddressUse = false;
                probe.Start();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            finally
            {
                if (probe != null) { try { probe.Stop(); } catch { } }
            }
        }
    }
}
