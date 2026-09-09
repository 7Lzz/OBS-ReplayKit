using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReplayKitSetup
{
    // voicemeeter remote api helpers used to preserve hardware inputs. ported from obs_replaykit/voicemeeter.py. the vb-audio sdk header declares these functions __stdcall, not the .net-default cdecl, so every delegate below is explicitly StdCall.
    public sealed class VoicemeeterDeviceChoice
    {
        public string Name { get; }
        public string Driver { get; }
        public VoicemeeterDeviceChoice(string name, string driver) { Name = name; Driver = driver; }
    }

    public sealed class VoicemeeterStripDevice
    {
        public int Index { get; }
        public string Name { get; }
        public IReadOnlyList<VoicemeeterDeviceChoice> Choices { get; }
        public VoicemeeterStripDevice(int index, string name, IReadOnlyList<VoicemeeterDeviceChoice> choices)
        {
            Index = index; Name = name; Choices = choices;
        }
    }

    public sealed class VoicemeeterSnapshot
    {
        public string DllPath { get; }
        public IReadOnlyList<VoicemeeterStripDevice> Strips { get; }
        public VoicemeeterSnapshot(string dllPath, IReadOnlyList<VoicemeeterStripDevice> strips)
        {
            DllPath = dllPath; Strips = strips;
        }
    }

    internal sealed class VoicemeeterNotRunningException : Exception { }

    internal sealed class VoicemeeterRemote : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long LoginDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long LogoutDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long IsParametersDirtyDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long GetVoicemeeterTypeDelegate(out int type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate long GetParameterStringWDelegate(byte[] paramName, StringBuilder value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate long SetParameterStringWDelegate(byte[] paramName, string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long SetParameterFloatDelegate(byte[] paramName, float value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long GetParameterFloatDelegate(byte[] paramName, out float value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long InputGetDeviceNumberDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate long InputGetDeviceDescWDelegate(long index, out int deviceType, StringBuilder name, StringBuilder hardwareId);

        private static readonly Dictionary<int, string> InputDeviceTypes = new Dictionary<int, string> { [1] = "mme", [3] = "wdm", [4] = "ks" };
        private static readonly Dictionary<string, int> DriverToDeviceType = new Dictionary<string, int> { ["mme"] = 1, ["wdm"] = 3, ["ks"] = 4 };
        private static readonly string[] RestoreDriverOrder = { "wdm", "ks", "mme" };
        private const int PrefixMatchMinChars = 12;

        private readonly IntPtr _hModule;
        private readonly LoginDelegate _login;
        private readonly LogoutDelegate _logout;
        private readonly IsParametersDirtyDelegate _isParametersDirty;
        private readonly GetVoicemeeterTypeDelegate _getVoicemeeterType;
        private readonly GetParameterStringWDelegate _getParameterStringW;
        private readonly SetParameterStringWDelegate _setParameterStringW;
        private readonly SetParameterFloatDelegate _setParameterFloat;
        private readonly InputGetDeviceNumberDelegate _inputGetDeviceNumber;
        private readonly InputGetDeviceDescWDelegate _inputGetDeviceDescW;
        private readonly GetParameterFloatDelegate _getParameterFloat;
        private bool _loggedIn;

        public string DllPath { get; }

        public VoicemeeterRemote(string dllPath)
        {
            DllPath = dllPath;
            _hModule = LoadLibrary(dllPath);
            if (_hModule == IntPtr.Zero) throw new InvalidOperationException("could not load " + dllPath);

            _login = GetDelegate<LoginDelegate>("VBVMR_Login");
            _logout = GetDelegate<LogoutDelegate>("VBVMR_Logout");
            _isParametersDirty = GetDelegate<IsParametersDirtyDelegate>("VBVMR_IsParametersDirty");
            _getVoicemeeterType = GetDelegate<GetVoicemeeterTypeDelegate>("VBVMR_GetVoicemeeterType");
            _getParameterStringW = GetDelegate<GetParameterStringWDelegate>("VBVMR_GetParameterStringW");
            _setParameterStringW = GetDelegate<SetParameterStringWDelegate>("VBVMR_SetParameterStringW");
            _setParameterFloat = GetDelegate<SetParameterFloatDelegate>("VBVMR_SetParameterFloat");
            _inputGetDeviceNumber = GetDelegate<InputGetDeviceNumberDelegate>("VBVMR_Input_GetDeviceNumber");
            _inputGetDeviceDescW = GetDelegate<InputGetDeviceDescWDelegate>("VBVMR_Input_GetDeviceDescW");
            _getParameterFloat = GetDelegate<GetParameterFloatDelegate>("VBVMR_GetParameterFloat");
        }

        private T GetDelegate<T>(string name) where T : class
        {
            IntPtr ptr = GetProcAddress(_hModule, name);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("missing export " + name);
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }

        public VoicemeeterRemote Login()
        {
            long rc = _login();
            if (rc == 1) throw new VoicemeeterNotRunningException();
            if (rc != 0) throw new InvalidOperationException($"Voicemeeter is not running or not reachable (login={rc})");
            _loggedIn = true;
            try { _isParametersDirty(); } catch (AccessViolationException) { }
            return this;
        }

        public void Dispose()
        {
            if (_loggedIn)
            {
                _logout();
                _loggedIn = false;
            }
            if (_hModule != IntPtr.Zero) FreeLibrary(_hModule);
        }

        public int VoicemeeterType()
        {
            long rc = _getVoicemeeterType(out int value);
            if (rc != 0) throw new InvalidOperationException($"Voicemeeter type query failed ({rc})");
            return value;
        }

        private static byte[] AsciiParam(string s) => Encoding.ASCII.GetBytes(s + "\0");

        public string GetStripDeviceName(int index)
        {
            var buf = new StringBuilder(512);
            long rc = _getParameterStringW(AsciiParam($"Strip[{index}].device.name"), buf);
            return rc != 0 ? "" : buf.ToString().Trim();
        }

        public int GetStripDeviceType(int index)
        {
            long rc = _getParameterFloat(AsciiParam($"Strip[{index}].device.type"), out float value);
            return rc != 0 ? 0 : (int)value;
        }

        public Dictionary<string, HashSet<string>> InputDevicesByName()
        {
            var devices = new Dictionary<string, HashSet<string>>();
            long count = _inputGetDeviceNumber();
            if (count <= 0) return devices;
            for (long index = 0; index < count; index++)
            {
                var name = new StringBuilder(512);
                var hardwareId = new StringBuilder(512);
                long rc = _inputGetDeviceDescW(index, out int deviceType, name, hardwareId);
                if (rc != 0) continue;
                if (!InputDeviceTypes.TryGetValue(deviceType, out var driver)) continue;
                string cleanName = name.ToString().Trim();
                if (driver != null && cleanName.Length > 0)
                {
                    if (!devices.TryGetValue(cleanName, out var set))
                    {
                        set = new HashSet<string>();
                        devices[cleanName] = set;
                    }
                    set.Add(driver);
                }
            }
            return devices;
        }

        public bool SetStripDeviceChoice(VoicemeeterStripDevice strip, VoicemeeterDeviceChoice choice)
        {
            long rc = _setParameterStringW(AsciiParam($"Strip[{strip.Index}].Device.{choice.Driver}"), choice.Name);
            return rc == 0;
        }

        public VoicemeeterDeviceChoice SetStripDevice(VoicemeeterStripDevice strip)
        {
            foreach (var choice in strip.Choices)
            {
                ClearStripDevice(strip);
                Thread.Sleep(200);
                if (!SetStripDeviceChoice(strip, choice)) continue;
                Thread.Sleep(500);
                if (StripDeviceMatches(strip.Index, choice)) return choice;
            }
            return null;
        }

        public bool ClearStripDevice(VoicemeeterStripDevice strip)
        {
            bool cleared = false;
            foreach (var driver in RestoreDriverOrder)
            {
                if (_setParameterStringW(AsciiParam($"Strip[{strip.Index}].Device.{driver}"), "") == 0) cleared = true;
            }
            return cleared;
        }

        public bool WaitForInputDevice(VoicemeeterStripDevice strip, double timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow <= deadline)
            {
                var devices = InputDevicesByName();
                bool any = strip.Choices.Any(choice => devices.TryGetValue(choice.Name, out var drivers) && drivers.Contains(choice.Driver));
                if (any) return true;
                Thread.Sleep(350);
            }
            return false;
        }

        public bool RestartAudioEngine()
        {
            long rc = _setParameterFloat(AsciiParam("Command.Restart"), 1.0f);
            return rc == 0;
        }

        public bool StripDeviceMatches(int index, VoicemeeterDeviceChoice choice)
        {
            if (!DriverToDeviceType.TryGetValue(choice.Driver, out var expectedType)) return false;
            return GetStripDeviceType(index) == expectedType && DeviceNamesMatch(GetStripDeviceName(index), choice.Name);
        }

        public static bool DeviceNamesMatch(string left, string right)
        {
            string a = (left ?? "").Trim();
            string b = (right ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return false;
            return a == b || (Math.Min(a.Length, b.Length) >= PrefixMatchMinChars && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));
        }
    }

    public static class VoiceMeeter
    {
        private static readonly string[] DllCandidates =
        {
            @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll",
            @"C:\Program Files\VB\Voicemeeter\VoicemeeterRemote64.dll",
        };

        private static readonly Dictionary<int, int> TypeToPhysicalStrips = new Dictionary<int, int> { [1] = 2, [2] = 3, [3] = 5 };
        private const double DeviceSettleTimeout = 10.0;

        private static string VoicemeeterDll()
        {
            return DllCandidates.FirstOrDefault(File.Exists);
        }

        // return verified restore candidates for voicemeeters sometimes truncated device name.
        private static List<VoicemeeterDeviceChoice> InputDeviceChoices(string selectedName, Dictionary<string, HashSet<string>> devices)
        {
            string name = (selectedName ?? "").Trim();
            var result = new List<VoicemeeterDeviceChoice>();
            if (name.Length == 0) return result;

            var candidates = new List<(int DriverRank, int MatchRank, int LengthRank, string DeviceKey, string DeviceName, string Driver)>();
            foreach (var kv in devices)
            {
                string deviceName = kv.Key;
                int matchRank;
                if (deviceName == name) matchRank = 0;
                else if (VoicemeeterRemote.DeviceNamesMatch(deviceName, name)) matchRank = 1;
                else continue;

                for (int driverRank = 0; driverRank < RestoreDriverOrder.Length; driverRank++)
                {
                    if (kv.Value.Contains(RestoreDriverOrder[driverRank]))
                    {
                        candidates.Add((driverRank, matchRank, -deviceName.Length, deviceName.ToLowerInvariant(), deviceName, RestoreDriverOrder[driverRank]));
                    }
                }
            }

            candidates.Sort((x, y) =>
            {
                int c = x.DriverRank.CompareTo(y.DriverRank);
                if (c != 0) return c;
                c = x.MatchRank.CompareTo(y.MatchRank);
                if (c != 0) return c;
                c = x.LengthRank.CompareTo(y.LengthRank);
                if (c != 0) return c;
                return string.CompareOrdinal(x.DeviceKey, y.DeviceKey);
            });

            var seen = new HashSet<(string, string)>();
            foreach (var c in candidates)
            {
                var key = (c.DeviceName, c.Driver);
                if (!seen.Add(key)) continue;
                result.Add(new VoicemeeterDeviceChoice(c.DeviceName, c.Driver));
            }
            return result;
        }

        private static readonly string[] RestoreDriverOrder = { "wdm", "ks", "mme" };

        // capture voicemeeter physical input selections if voicemeeter is running.
        public static VoicemeeterSnapshot SnapshotVoicemeeterInputs(Action<string> log = null)
        {
            string dllPath = VoicemeeterDll();
            if (dllPath == null) return null;

            var strips = new List<VoicemeeterStripDevice>();
            try
            {
                using (var remote = new VoicemeeterRemote(dllPath).Login())
                {
                    int vmType = remote.VoicemeeterType();
                    if (!TypeToPhysicalStrips.TryGetValue(vmType, out int stripCount) || stripCount <= 0)
                    {
                        log?.Invoke($"warn: unsupported Voicemeeter type {vmType}; input restore skipped");
                        return null;
                    }
                    var devices = remote.InputDevicesByName();
                    for (int index = 0; index < stripCount; index++)
                    {
                        string name = remote.GetStripDeviceName(index);
                        if (string.IsNullOrEmpty(name)) continue;
                        var choices = InputDeviceChoices(name, devices);
                        if (choices.Count == 0)
                        {
                            log?.Invoke($"warn: Voicemeeter strip {index + 1} input '{name}' was not in the device list");
                            continue;
                        }
                        var first = choices[0];
                        if (first.Name != name) log?.Invoke($"resolved Voicemeeter strip {index + 1} input '{name}' -> '{first.Name}'");
                        strips.Add(new VoicemeeterStripDevice(index, name, choices));
                    }
                }
            }
            catch (VoicemeeterNotRunningException)
            {
                return null;
            }
            catch (Exception exc)
            {
                log?.Invoke("warn: Voicemeeter input snapshot skipped: " + exc.Message);
                return null;
            }

            if (strips.Count == 0) return null;
            log?.Invoke($"saved Voicemeeter hardware input selection ({strips.Count} strip(s))");
            return new VoicemeeterSnapshot(dllPath, strips);
        }

        // restore previously captured voicemeeter hardware inputs and restart its audio engine.
        public static bool RestoreVoicemeeterInputs(VoicemeeterSnapshot snapshot, Action<string> log = null)
        {
            if (snapshot == null) return true;
            if (!File.Exists(snapshot.DllPath))
            {
                log?.Invoke("warn: Voicemeeter Remote API DLL disappeared; input restore skipped");
                return false;
            }

            int restored = 0;
            try
            {
                using (var remote = new VoicemeeterRemote(snapshot.DllPath).Login())
                {
                    foreach (var strip in snapshot.Strips)
                    {
                        if (!remote.WaitForInputDevice(strip, DeviceSettleTimeout))
                        {
                            log?.Invoke("warn: Voicemeeter input did not reappear in time: " + strip.Name);
                            continue;
                        }
                        var restoredChoice = remote.SetStripDevice(strip);
                        if (restoredChoice != null)
                        {
                            restored++;
                            log?.Invoke($"restored Voicemeeter strip {strip.Index + 1} -> {restoredChoice.Driver.ToUpperInvariant()}: {restoredChoice.Name}");
                        }
                        else
                        {
                            log?.Invoke($"warn: failed to restore Voicemeeter strip {strip.Index + 1}: {strip.Name}");
                        }
                    }
                    if (restored > 0)
                    {
                        if (remote.RestartAudioEngine()) log?.Invoke("restarted Voicemeeter audio engine");
                        else log?.Invoke("warn: Voicemeeter audio engine restart request failed");
                    }
                }
            }
            catch (Exception exc)
            {
                log?.Invoke("warn: Voicemeeter input restore failed: " + exc.Message);
                return false;
            }

            return restored == snapshot.Strips.Count;
        }
    }
}
