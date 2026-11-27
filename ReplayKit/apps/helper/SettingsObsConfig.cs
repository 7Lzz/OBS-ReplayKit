using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReplayKitShared;
using Encoder = ReplayKitShared.Encoder;
using static ReplayKitHelper.ReplaykitSettings;
using static ReplayKitHelper.SettingsSceneJson;
using static ReplayKitHelper.SettingsOverlayLive;
using static ReplayKitHelper.SettingsRecording;
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHotkeys;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // the obs config files the settings reach: the close warning, sleep and startup shortcut in the obs ini files, the profile parameters of basic.ini and the hotkey json of basic.ini
    internal static class SettingsObsConfig
    {
        // -- obs ini config: close-warning + sleep override + windows startup shortcut --

        internal static string GetObsConfigRoot()
        {
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData)) return Path.Combine(appData, "obs-studio");
            return Path.Combine(AppConfig.GetUserProfile(), "AppData", "Roaming", "obs-studio");
        }

        // the start minimized switch of obs as its user.ini holds it, or null when the file or the key is not there
        internal static bool? ReadObsStartMinimized()
        {
            try
            {
                string path = Path.Combine(GetObsConfigRoot(), "user.ini");
                if (!File.Exists(path)) return null;
                string value = ObsIni.Get(File.ReadAllText(path), "BasicWindow", "SysTrayWhenStarted");
                if (value != null) return string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
            }
            return null;
        }

        internal static JObject SetObsCloseWarningConfig(bool disabled)
        {
            try
            {
                string root = GetObsConfigRoot();
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                string resolvedRoot = Path.GetFullPath(root);
                string appData = Environment.GetEnvironmentVariable("APPDATA");
                string expectedRoot = !string.IsNullOrEmpty(appData)
                    ? Path.GetFullPath(Path.Combine(appData, "obs-studio"))
                    : Path.GetFullPath(Path.Combine(AppConfig.GetUserProfile(), "AppData", "Roaming", "obs-studio"));
                if (!string.Equals(resolvedRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Locales.Message("settings.obsConfigRootDidNotResolve"));

                string confirmOnExit = disabled ? "false" : "true";
                var changed = new List<string>();
                var paths = new[] { Path.Combine(resolvedRoot, "user.ini"), Path.Combine(resolvedRoot, "global.ini") };
                foreach (var path in paths)
                {
                    string text = File.Exists(path) ? File.ReadAllText(path) : "";
                    string next = ObsIni.Set(text, "General", "ConfirmOnExit", confirmOnExit);
                    if (next != text)
                    {
                        AppConfig.WriteUtf8(path, next);
                        changed.Add(Path.GetFileName(path));
                    }
                }
                return new JObject { ["ok"] = true, ["confirmOnExit"] = confirmOnExit, ["changed"] = new JArray(changed) };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // requestsoverride only covers obs64.exe and never changes the power plan, just whether that one process may hold off sleep and display-off; the override stays until powercfg clears it
        internal static JObject InvokePowerCfg(string[] arguments)
        {
            var allowed = new HashSet<string>(SleepOverrideRequest.Requests) { "/requestsoverride", "PROCESS", SleepOverrideRequest.Process };
            foreach (var arg in arguments)
            {
                if (!allowed.Contains(arg)) return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.invalidPowerConfigurationRequest") };
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SystemPrograms.Powercfg,
                    Arguments = string.Join(" ", arguments),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.powercfgCouldNotStart") };
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.powercfgTimedOut") };
                }
                string output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
                if (process.ExitCode != 0)
                {
                    string line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    if (string.IsNullOrWhiteSpace(line)) line = "powercfg exited " + process.ExitCode + ".";
                    return new JObject { ["ok"] = false, ["message"] = line, ["exitCode"] = process.ExitCode };
                }
                return new JObject { ["ok"] = true, ["message"] = output, ["exitCode"] = 0 };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        internal static JObject SetSleepOverrideSetting(bool allowSleep)
        {
            var args = new List<string> { "/requestsoverride", "PROCESS", SleepOverrideRequest.Process };
            if (allowSleep) args.AddRange(SleepOverrideRequest.Requests);
            var result = InvokePowerCfg(args.ToArray());
            if (result["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["message"] = result["message"] };
            string mode = allowSleep ? "allow_sleep" : "obs_default";
            return new JObject { ["ok"] = true, ["mode"] = mode };
        }

        // powercfg only takes a request override from an administrator, so when windows refuses it the same command is run again through the administrator prompt, which the user can allow or decline
        internal static JObject ApplySleepOverrideSetting(bool allowSleep)
        {
            var result = SetSleepOverrideSetting(allowSleep);
            if (result["ok"]?.Value<bool>() != true) result = SetSleepOverrideElevated(allowSleep);
            if (result["ok"]?.Value<bool>() == true) return new JObject { ["applied"] = new JArray("Windows sleep"), ["warnings"] = new JArray() };
            return new JObject { ["applied"] = new JArray(), ["warnings"] = new JArray(Locales.Message("settings.sleepNotAppliedError", "error", result["message"]?.ToString())) };
        }

        internal static JObject SetSleepOverrideElevated(bool allowSleep)
        {
            var args = new List<string> { "/requestsoverride", "PROCESS", SleepOverrideRequest.Process };
            if (allowSleep) args.AddRange(SleepOverrideRequest.Requests);
            try
            {
                var psi = new ProcessStartInfo { FileName = SystemPrograms.Powercfg, Arguments = string.Join(" ", args), UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using var process = Process.Start(psi);
                if (process == null) return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.powercfgCouldNotStart") };
                if (!process.WaitForExit(120000)) return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.powercfgDidNotFinish") };
                return process.ExitCode == 0 ? new JObject { ["ok"] = true } : new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.powercfgExitedCode", "code", process.ExitCode) };
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.adminPromptDeclined") };
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        internal static string GetStartupShortcutPath()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(folder))
            {
                string appData = Environment.GetEnvironmentVariable("APPDATA");
                if (!string.IsNullOrEmpty(appData)) folder = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            }
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException(Locales.Message("settings.windowsStartupFolderWasNotFound"));
            return Path.Combine(folder, "OBS ReplayKit.lnk");
        }

        internal static string GetObsStartupTarget()
        {
            string obs = Constants.ResolveObsExe();
            if (string.IsNullOrEmpty(obs) || !File.Exists(obs)) throw new InvalidOperationException(Locales.Message("settings.obsExecutableWasNotFound"));
            return obs;
        }

        internal static void RemoveLegacyObsRunValue()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                if (key?.GetValue("OBS ReplayKit") != null) key.DeleteValue("OBS ReplayKit", throwOnMissingValue: false);
            }
        }

        internal static void NewObsStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            string obsPath = GetObsStartupTarget();
            string shortcutDir = Path.GetDirectoryName(shortcutPath);
            if (!Directory.Exists(shortcutDir)) Directory.CreateDirectory(shortcutDir);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = obsPath;
                shortcut.Arguments = ObsLaunch.Arguments;
                shortcut.WorkingDirectory = Path.GetDirectoryName(obsPath);
                shortcut.IconLocation = obsPath + ",0";
                shortcut.Description = Locales.Message("settings.startObsReplaykitWhenWindowsSigns");
                shortcut.Save();
            }
            finally
            {
                Marshal.ReleaseComObject(shortcut);
                Marshal.ReleaseComObject(shell);
            }
        }

        internal static void RemoveObsStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }

        internal static JObject SetObsStartupSetting(bool enabled)
        {
            try
            {
                RemoveLegacyObsRunValue();
                if (enabled) NewObsStartupShortcut();
                else RemoveObsStartupShortcut();
                RemoveLegacyObsRunValue();
                return new JObject { ["ok"] = true };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // persists a dock-picked clip dir into the shared helper config json (the same file the lua bootstrap and this process both read) -- psHelper is not written here since the c# port has no separate worker script for it to point at.
        internal static void UpdateHelperConfigClipDir(string clipDir)
        {
            AppConfig.LoadConfig();
            Server.State.Config["clipDir"] = clipDir;
            var obj = new JObject
            {
                ["scriptDir"] = AppConfig.GetScriptDir(),
                ["clipDir"] = clipDir,
                ["loggingEnabled"] = Server.State.LogEnabled,
            };
            AppConfig.WriteUtf8(Server.State.ConfigPath, obj.ToString(Formatting.Indented));
            Server.State.ConfigMTime = DateTime.MinValue;
            AppConfig.LoadConfig();
        }

        // -- obs profile parameters (basic.ini) + monitoring device --

        internal static ObsWebSocketResult SetObsProfileParameterSafe(string category, string name, string value) =>
            ObsWebSocket.InvokeRequest("SetProfileParameter", new JObject { ["parameterCategory"] = category, ["parameterName"] = name, ["parameterValue"] = value }, 3000);

        internal static ObsWebSocketResult GetObsProfileParameterSafe(string category, string name) =>
            ObsWebSocket.InvokeRequest("GetProfileParameter", new JObject { ["parameterCategory"] = category, ["parameterName"] = name }, 3000);

        internal static JObject GetObsProfileParameterValue(string category, string name)
        {
            var result = GetObsProfileParameterSafe(category, name);
            if (!result.Ok) return new JObject { ["ok"] = false, ["value"] = "", ["message"] = result.Message };
            var value = result.Data?["parameterValue"];
            if (value == null || value.Type == JTokenType.Null) return new JObject { ["ok"] = false, ["value"] = "", ["message"] = Locales.Message("settings.obsDidNotReturnCategoryName", "category", category, "name", name) };
            return new JObject { ["ok"] = true, ["value"] = value.Value<string>(), ["message"] = "" };
        }

        internal static JObject SetObsMonitoringDevice(string deviceId, string deviceName)
        {
            deviceId = (deviceId ?? "").Trim();
            deviceName = (deviceName ?? "").Trim();
            if (deviceId.Length == 0 || deviceName.Length == 0)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = Locales.Message("settings.obsMonitoringDeviceIdNameIs") };
            if (deviceId.Length > 512 || deviceName.Length > 256 || Regex.IsMatch(deviceId, @"[\x00-\x1F]") || Regex.IsMatch(deviceName, @"[\x00-\x1F]"))
                return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = Locales.Message("settings.obsMonitoringDeviceIdNameIs2") };

            var request = new JObject { ["id"] = deviceId, ["name"] = deviceName };
            if (!PipeClient.ApplyObsConfig("MONITORDEVICE", request.ToString(Newtonsoft.Json.Formatting.None), 3000))
                return new JObject { ["ok"] = false, ["applied"] = new JArray(), ["message"] = Locales.Message("settings.monitoringDeviceNotConfirmed", "device", deviceName) };
            return new JObject { ["ok"] = true, ["applied"] = new JArray("OBS monitoring device verified"), ["message"] = "" };
        }
        // -- keybind <-> basic.ini hotkey json --

        internal static JObject ConvertKeybindToObsBinding(JObject combo)
        {
            if (combo == null || string.IsNullOrWhiteSpace(combo["key"]?.Value<string>())) return null;
            var bind = new JObject();
            foreach (var mod in HotkeyModifiers.Order)
            {
                if (combo[mod]?.Value<bool>() == true) bind[mod] = true;
            }
            bind["key"] = combo["key"].Value<string>();
            return bind;
        }

        // obs keeps every binding of a hotkey in one array, so the extra binding is just the second entry; the same combo twice is written once
        internal static JArray ConvertKeybindsToObsBindings(JObject combo, JObject extra)
        {
            var list = new JArray();
            var bind = ConvertKeybindToObsBinding(combo);
            var extraBind = ConvertKeybindToObsBinding(extra);
            if (bind != null) list.Add(bind);
            if (extraBind != null && (bind == null || !JToken.DeepEquals(bind, extraBind))) list.Add(extraBind);
            return list;
        }

        internal static string ConvertClipKeybindToBasicIni(JObject combo, JObject extra) =>
            new JObject { ["ReplayBuffer.Save"] = ConvertKeybindsToObsBindings(combo, extra) }.ToString(Formatting.None);

        internal static string ConvertRecordingKeybindToBasicIni(JObject combo, JObject extra) =>
            new JObject { ["bindings"] = ConvertKeybindsToObsBindings(combo, extra) }.ToString(Formatting.None);

        internal static JObject ConvertRecordingBasicIniToKeybind(string json, int index = 0)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                var data = JObject.Parse(json);
                var bindings = data["bindings"] as JArray;
                if (bindings == null || bindings.Count <= index || bindings[index] == null || bindings[index].Type == JTokenType.Null) return new JObject();
                return index == 0 ? NormalizeRecordingKeybind(bindings[index]) : NormalizeExtraKeybind(bindings[index], "extra recording keybind");
            }
            catch (JsonException) { return new JObject(); }
        }

        internal static JObject ConvertClipBasicIniToKeybind(string json, int index = 0)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                var data = JObject.Parse(json);
                var bindings = (data["bindings"] ?? data["ReplayBuffer.Save"]) as JArray;
                if (bindings == null || bindings.Count <= index || bindings[index] == null || bindings[index].Type == JTokenType.Null) return new JObject();
                return index == 0 ? NormalizeClipKeybind(bindings[index]) : NormalizeExtraKeybind(bindings[index], "extra clip keybind");
            }
            catch (JsonException) { return new JObject(); }
        }

        internal static ObsWebSocketResult GetReplayBufferHotkeyJson()
        {
            var result = ObsWebSocket.InvokeRequest("GetVersion");
            if (!result.Ok) return result;
            if (!Version.TryParse(result.Data?["obsVersion"]?.Value<string>(), out var version))
                return new ObsWebSocketResult { Ok = false, Message = Locales.Message("settings.obsVersionIsInvalid") };
            return GetObsProfileParameterSafe("Hotkeys", version.Major >= 33 ? "OBSBasic.SaveReplayBuffer" : "ReplayBuffer");
        }

        // keep both formats current so switching obs versions preserves the saved clip bindings
        internal static ObsWebSocketResult SetReplayBufferHotkeyJson(JObject combo, JObject extra)
        {
            var result = SetObsProfileParameterSafe("Hotkeys", "ReplayBuffer", ConvertClipKeybindToBasicIni(combo, extra));
            if (!result.Ok) return result;
            return SetObsProfileParameterSafe("Hotkeys", "OBSBasic.SaveReplayBuffer", ConvertRecordingKeybindToBasicIni(combo, extra));
        }

        internal static JObject SetRecordingHotkeyPairJson(string startJson, string stopJson)
        {
            var errors = new List<string>();
            var names = new[] { "OBSBasic.StartRecording", "OBSBasic.StopRecording" };
            var jsons = new[] { startJson, stopJson };
            for (int i = 0; i < names.Length; i++)
            {
                var r = SetObsProfileParameterSafe("Hotkeys", names[i], jsons[i]);
                if (!r.Ok) errors.Add(names[i] + ": " + r.Message);
            }
            if (errors.Count > 0) return new JObject { ["ok"] = false, ["message"] = string.Join("; ", errors) };
            return new JObject { ["ok"] = true };
        }

    }
}
