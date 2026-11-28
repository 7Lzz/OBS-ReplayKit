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
using static ReplayKitHelper.SettingsObsConfig;
using static ReplayKitHelper.SettingsRecording;
using static ReplayKitHelper.SettingsLiveApply;
using static ReplayKitHelper.SettingsHotkeys;
using static ReplayKitHelper.SettingsHttp;

namespace ReplayKitHelper
{
    // the overlay on a running obs: the opacity and colour filter through obs requests, which sources an overlay style uses, and the live preview the settings page drives while a slider moves
    internal static class SettingsOverlayLive
    {
        // the overlay opacity filter on a running obs: reading, creating and updating it through obs requests

        internal static JObject GetOverlayOpacityLiveFilterInfo(JArray filters)
        {
            foreach (var filter in filters ?? new JArray())
            {
                string name = filter["filterName"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name == OverlayScene.OpacityFilterName)
                {
                    return new JObject
                    {
                        ["found"] = true, ["name"] = name,
                        ["kind"] = filter["filterKind"]?.Value<string>() ?? "",
                        ["settings"] = filter["filterSettings"] as JObject ?? new JObject(),
                        ["enabled"] = filter["filterEnabled"]?.Value<bool>() ?? true,
                        ["managed"] = true,
                    };
                }
            }
            return new JObject { ["found"] = false, ["name"] = "", ["kind"] = "", ["settings"] = new JObject(), ["enabled"] = false, ["managed"] = false };
        }

        internal static int? GetOverlayOpacityPercentFromFilterSettings(JObject filterSettings)
        {
            var value = filterSettings?["opacity"];
            if (value == null || value.Type == JTokenType.Null) return null;
            try
            {
                double opacity = value.Value<double>();
                if (opacity > 1.0) return (int)Math.Round(Math.Min(opacity, 100.0));
                return (int)Math.Round(Math.Max(opacity, 0.0) * 100.0);
            }
            catch (FormatException) { return null; } catch (InvalidCastException) { return null; }
        }

        internal static JObject GetSourceFilterListLive(string sourceName)
        {
            var list = ObsRequest.Invoke("GetSourceFilterList", new JObject { ["sourceName"] = sourceName }, 3000);
            if (!list.Ok) return new JObject { ["ok"] = false, ["message"] = list.Message, ["filters"] = new JArray() };
            var filters = list.Data?["filters"] as JArray ?? list.Data?["sourceFilters"] as JArray ?? new JArray();
            return new JObject { ["ok"] = true, ["message"] = "", ["filters"] = filters };
        }

        internal static bool TestSourceExistsForFilters(string sourceName) => GetSourceFilterListLive(sourceName)["ok"]?.Value<bool>() == true;

        internal static JObject GetLatestSceneCollectionPath()
        {
            string root = Constants.OBS_SCENES_DIR;
            if (!Directory.Exists(root)) return null;
            var file = new DirectoryInfo(root).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return file != null ? new JObject { ["path"] = file.FullName } : null;
        }

        // last-resort readback of the opacity filter straight off disk, for the settings payload only (never a write target) -- used when a live filter lookup fails, so the settings dock still shows something sensible even if obs isnt reachable right now. picks the most-recently-written scene file under basic\scenes, a looser resolution than GetSceneCollectionPath (which asks obs which collection is actually active); that imprecision is fine here since this never drives a write.
        internal static JObject GetSceneFileOverlayOpacityPercent(JObject settings)
        {
            var pathToken = GetLatestSceneCollectionPath();
            string path = pathToken?["path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
                var data = JObject.Parse(json);
                var sources = data["sources"] as JArray ?? new JArray();
                foreach (var name in GetOverlayCandidateSourceNames(settings["overlayStyle"]?.Value<string>() ?? ""))
                {
                    foreach (var sourceToken in sources)
                    {
                        var source = (JObject)sourceToken;
                        if (source["name"]?.Value<string>() != name) continue;
                        var filters = source["filters"] as JArray ?? new JArray();
                        foreach (var filterToken in filters)
                        {
                            var filter = (JObject)filterToken;
                            if (!IsManagedOverlayOpacityJson(filter, "")) continue;
                            var filterSettings = filter["settings"] as JObject ?? new JObject();
                            var opacity = GetOverlayOpacityPercentFromFilterSettings(filterSettings);
                            if (opacity != null)
                                return new JObject { ["ok"] = true, ["opacity"] = opacity.Value, ["sourceName"] = name, ["filterName"] = filter["name"]?.Value<string>() ?? "" };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Get-ReplayKitSceneFileOverlayOpacityPercent failed: " + ex.Message);
            }
            return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
        }

        internal static JObject ApplyOverlayOpacityLive(string sourceName, int opacityPercent, JObject settings = null)
        {
            var list = GetSourceFilterListLive(sourceName);
            if (list["ok"]?.Value<bool>() != true) return new JObject { ["ok"] = false, ["message"] = list["message"] };
            var existingFilter = GetOverlayOpacityLiveFilterInfo(list["filters"] as JArray);
            string existingFilterName = existingFilter["found"]?.Value<bool>() == true ? existingFilter["name"].Value<string>() : "";
            bool hasColorAdjustments = HasOverlayColorAdjustments(settings);
            bool legacyPercent = false;
            if (existingFilter["found"]?.Value<bool>() == true)
                legacyPercent = OverlayOpacityFilterUsesLegacyPercent(existingFilter["kind"]?.Value<string>() ?? "", existingFilter["settings"] as JObject);

            if (opacityPercent >= SettingLimits.OverlayOpacityMax && !hasColorAdjustments)
            {
                if (string.IsNullOrWhiteSpace(existingFilterName)) return new JObject { ["ok"] = true, ["message"] = "" };
                var setFull = ObsRequest.Invoke("SetSourceFilterSettings", new JObject
                {
                    ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                    ["filterSettings"] = OverlayOpacityFilterSettingsLive(SettingLimits.OverlayOpacityMax, legacyPercent, settings), ["overlay"] = true,
                }, 3000);
                if (!setFull.Ok) return new JObject { ["ok"] = false, ["message"] = setFull.Message };
                var enableFull = ObsRequest.Invoke("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
                return enableFull.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = enableFull.Message };
            }

            var filterSettings = OverlayOpacityFilterSettingsLive(opacityPercent, legacyPercent, settings);
            if (!string.IsNullOrWhiteSpace(existingFilterName))
            {
                var set = ObsRequest.Invoke("SetSourceFilterSettings", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterSettings"] = filterSettings, ["overlay"] = true }, 3000);
                if (!set.Ok) return new JObject { ["ok"] = false, ["message"] = set.Message };
            }
            else
            {
                var create = ObsRequest.Invoke("CreateSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = OverlayScene.OpacityFilterName, ["filterKind"] = OverlayScene.OpacityFilterVersionedKind, ["filterSettings"] = filterSettings }, 3000);
                if (!create.Ok)
                {
                    var createFallback = ObsRequest.Invoke("CreateSourceFilter", new JObject
                    {
                        ["sourceName"] = sourceName, ["filterName"] = OverlayScene.OpacityFilterName, ["filterKind"] = OverlayScene.OpacityFilterKind,
                        ["filterSettings"] = OverlayOpacityFilterSettingsLive(opacityPercent, true, settings),
                    }, 3000);
                    if (!createFallback.Ok) return new JObject { ["ok"] = false, ["message"] = create.Message };
                    legacyPercent = true;
                }
                existingFilterName = OverlayScene.OpacityFilterName;
                var createdList = GetSourceFilterListLive(sourceName);
                if (createdList["ok"]?.Value<bool>() == true)
                {
                    var createdFilter = GetOverlayOpacityLiveFilterInfo(createdList["filters"] as JArray);
                    if (createdFilter["found"]?.Value<bool>() == true)
                    {
                        legacyPercent = OverlayOpacityFilterUsesLegacyPercent(createdFilter["kind"]?.Value<string>() ?? "", createdFilter["settings"] as JObject);
                        if (legacyPercent)
                        {
                            var setCreated = ObsRequest.Invoke("SetSourceFilterSettings", new JObject
                            {
                                ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                                ["filterSettings"] = OverlayOpacityFilterSettingsLive(opacityPercent, true, settings), ["overlay"] = true,
                            }, 3000);
                            if (!setCreated.Ok) return new JObject { ["ok"] = false, ["message"] = setCreated.Message };
                        }
                    }
                }
            }
            var enable = ObsRequest.Invoke("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
            return enable.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = enable.Message };
        }

        // the cursor of the display and game capture sources in a scene file follows the setting of its kind; the window capture source is written whole from the same settings elsewhere
        internal static void SetCaptureCursorSourceJson(JObject source, JObject settings)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string sourceId = source["id"]?.Value<string>() ?? "";
            string key;
            if (sourceName == OverlayScene.DisplayCaptureSourceName || sourceId == OverlayScene.DisplayCaptureSourceKind) key = "showCursorDisplayCapture";
            else if (sourceName == OverlayScene.GameCaptureSourceName || sourceId == OverlayScene.GameCaptureSourceKind) key = "showCursorGameCapture";
            else return;
            var sourceSettings = source["settings"] as JObject;
            if (sourceSettings == null) { sourceSettings = new JObject(); source["settings"] = sourceSettings; }
            sourceSettings["capture_cursor"] = CursorSetting(settings, key);
        }

        // the mouse cursor of every capture source on the running obs; a source that is not in the scene (code 600, not found) takes the setting when it is created
        internal static JObject ApplyCursorLive(JObject settings)
        {
            var applied = new List<string>();
            var warnings = new List<string>();
            foreach (var target in new[]
            {
                new[] { OverlayScene.DisplayCaptureSourceName, "capture_cursor", "showCursorDisplayCapture" },
                new[] { OverlayScene.GameCaptureSourceName, "capture_cursor", "showCursorGameCapture" },
                new[] { OverlayScene.WindowCaptureSourceName, "cursor", "showCursorWindowCapture" },
            })
            {
                var set = ObsRequest.Invoke("SetInputSettings", new JObject
                {
                    ["inputName"] = target[0],
                    ["inputSettings"] = new JObject { [target[1]] = CursorSetting(settings, target[2]) },
                    ["overlay"] = true,
                }, 3000);
                if (set.Ok) { if (!applied.Contains("Mouse cursor")) applied.Add("Mouse cursor"); }
                else if (set.Unavailable) { warnings.Add(Locales.Message("settings.mouseCursorWasSavedButObs", "error", set.Message)); break; }
                else if (set.Code != 600) warnings.Add(Locales.Message("settings.mouseCursorWasSavedButThe", "target", target[0], "error", set.Message));
            }
            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static JObject SetDisplayCaptureCursorLive(JObject settings)
        {
            var set = ObsRequest.Invoke("SetInputSettings", new JObject
            {
                ["inputName"] = OverlayScene.DisplayCaptureSourceName,
                ["inputSettings"] = new JObject { ["capture_cursor"] = CursorSetting(settings, "showCursorDisplayCapture") },
                ["overlay"] = true,
            }, 3000);
            return set.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = set.Message };
        }

        // game-window enumeration for the screenshare picker, with its own small p/invoke surface

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_CHILD = 0x40000000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int DWMWA_CLOAKED = 14;

        internal static string GetWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0) return "";
            if (length > 512) length = 512;
            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString().Trim();
        }

        internal static string GetWindowClass(IntPtr hwnd)
        {
            var builder = new StringBuilder(256);
            GetClassName(hwnd, builder, builder.Capacity);
            return builder.ToString().Trim();
        }

        internal static bool IsWindowCloaked(IntPtr hwnd)
        {
            try
            {
                int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4);
                return hr == 0 && cloaked != 0;
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        // enumerates visible top-level windows for the "Auto Game List" picker: filters out child/tool windows, cloaked (uwp-suspended) windows, and a hardcoded blocklist of system/obs/discord executables, then encodes each candidate as a title:class:exe token matching obss own Window Capture "window" setting format. capped at 80 candidates and sorted by label; a previously-saved selection that fell out of the live window list (app closed, etc.) is still surfaced as the first entry so the dropdown does not silently lose the users choice.
        public static List<JObject> GetGameWindowCandidates(string savedWindow = "")
        {
            var candidates = new List<JObject>();
            var seen = new HashSet<string>();
            var processCache = new Dictionary<int, string>();
            try
            {
                IntPtr shellWindow = GetShellWindow();
                EnumWindows((hwnd, _) =>
                {
                    if (hwnd == shellWindow) return true;
                    if (!IsWindowVisible(hwnd)) return true;
                    if (IsWindowCloaked(hwnd)) return true;

                    int style = GetWindowLong(hwnd, GWL_STYLE);
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((style & WS_CHILD) != 0) return true;
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                    string title = GetWindowTitle(hwnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;
                    string className = GetWindowClass(hwnd);
                    if (string.IsNullOrWhiteSpace(className)) return true;

                    GetWindowThreadProcessId(hwnd, out uint pidRaw);
                    int processId = (int)pidRaw;
                    if (processId <= 0) return true;
                    if (!processCache.TryGetValue(processId, out string exe))
                    {
                        try
                        {
                            using (var proc = Process.GetProcessById(processId))
                            {
                                exe = proc.ProcessName;
                                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
                            }
                        }
                        catch (ArgumentException) { exe = ""; }
                        catch (InvalidOperationException) { exe = ""; }
                        processCache[processId] = exe;
                    }
                    if (string.IsNullOrWhiteSpace(exe)) return true;
                    if (TestBlockedGameWindowExe(exe)) return true;

                    if (title.Length > GameWindowTitleMax) title = title.Substring(0, GameWindowTitleMax);
                    if (className.Length > GameWindowClassMax) className = className.Substring(0, GameWindowClassMax);
                    if (exe.Length > GameWindowExeMax) exe = exe.Substring(0, GameWindowExeMax);
                    string token = NewObsWindowToken(title, className, exe);
                    if (!seen.Add(token)) return true;

                    candidates.Add(new JObject
                    {
                        ["value"] = token, ["token"] = token, ["label"] = "[" + exe + "]: " + title,
                        ["title"] = title, ["className"] = className, ["exeName"] = exe,
                    });
                    return candidates.Count < 80;
                }, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Log.Write("Game window enumeration unavailable: " + ex.Message);
                return new List<JObject>();
            }

            var items = candidates.OrderBy(c => c["label"]?.Value<string>() ?? "", StringComparer.CurrentCulture).ToList();
            if (!string.IsNullOrWhiteSpace(savedWindow))
            {
                bool hasSaved = items.Any(item => item["value"]?.Value<string>() == savedWindow);
                // the saved window of Specific game is not open right now; it is marked so the Auto Game List picker, which adds running games, leaves it out
                if (!hasSaved)
                {
                    try
                    {
                        var saved = ConvertFromObsWindowToken(savedWindow);
                        items.Insert(0, new JObject
                        {
                            ["value"] = saved["token"], ["token"] = saved["token"], ["label"] = saved["label"],
                            ["blurb"] = Locales.Message("settings.savedSelection"), ["title"] = saved["title"], ["className"] = saved["className"], ["exeName"] = saved["exeName"], ["saved"] = true,
                        });
                    }
                    catch (InvalidOperationException)
                    {
                        items.Insert(0, new JObject { ["value"] = savedWindow, ["label"] = GetWindowLabelFromToken(savedWindow), ["blurb"] = Locales.Message("settings.savedSelection"), ["saved"] = true });
                    }
                }
            }
            return items;
        }

        // applies the selected screenshare capture mode (desktop/hybrid_auto/game_auto/game_window) live: ensures all three capture sources exist (creating Display/Game Capture if a user ever deleted one by hand or a scene got recreated bare -- do not regress this to a bare lookup-and-fail), toggles their scene-item visibility, and repositions Window Capture directly under Display Capture in scene-item order.
        internal static JObject ApplyScreenshareCaptureLive(JObject settings, JObject preset = null)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            string mode = settings["screenshareCaptureMode"]?.Value<string>() ?? "";
            bool useDesktop = mode == "desktop" || mode == "hybrid_auto";
            bool useGameCapture = mode == "game_auto";
            bool useWindowCapture = mode == "game_window";

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray(Locales.Message("settings.screenshareSceneLookupFailedError", "error", scene["message"]?.ToString())) };
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray(Locales.Message("settings.screenshareSceneItemsFailedError", "error", itemsResult["message"]?.ToString())) };
            var items = itemsResult["items"] as JArray;

            var gameInputSettings = GetGameCaptureInputSettings(settings);
            var displayEnsure = EnsureInputSceneItem(sceneName, OverlayScene.DisplayCaptureSourceName, OverlayScene.DisplayCaptureSourceKind, DisplayCaptureMethodSettings(), useDesktop);
            if (displayEnsure["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotPrepareDisplayCaptureError", "error", displayEnsure["message"]));
            var display = displayEnsure["ok"]?.Value<bool>() == true ? (JObject)displayEnsure["item"] : FindSceneItem(items, OverlayScene.DisplayCaptureSourceName);
            var gameEnsure = EnsureInputSceneItem(sceneName, OverlayScene.GameCaptureSourceName, OverlayScene.GameCaptureSourceKind, gameInputSettings, useGameCapture);
            if (gameEnsure["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotPrepareGameCaptureError", "error", gameEnsure["message"]));
            var game = gameEnsure["ok"]?.Value<bool>() == true ? (JObject)gameEnsure["item"] : FindSceneItem(items, OverlayScene.GameCaptureSourceName);

            var windowSettings = GetWindowCaptureInputSettings(settings);
            // in automatic mode the capture switch script picks the window; writing the specific game window here pointed the capture at whatever that setting last held while the script still believed it showed its game
            if (mode == "hybrid_auto") windowSettings.Remove("window");
            var windowEnsure = EnsureInputSceneItem(sceneName, OverlayScene.WindowCaptureSourceName, OverlayScene.WindowCaptureSourceKind, windowSettings, useWindowCapture);
            if (windowEnsure["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotPrepareWindowCaptureError", "error", windowEnsure["message"]));
            itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() == true) items = itemsResult["items"] as JArray;
            var window = FindSceneItem(items, OverlayScene.WindowCaptureSourceName);

            if (!useDesktop)
            {
                var hideDesktop = SetSceneItemEnabled(sceneName, display, false);
                if (hideDesktop["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotHideDisplayCaptureError", "error", hideDesktop["message"]));
            }

            var cursorResult = SetDisplayCaptureCursorLive(settings);
            if (cursorResult["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotUpdateTheDisplayCapture", "error", cursorResult["message"]));

            var setGame = ObsRequest.Invoke("SetInputSettings", new JObject { ["inputName"] = OverlayScene.GameCaptureSourceName, ["inputSettings"] = gameInputSettings, ["overlay"] = true }, 3000);
            if (!setGame.Ok) warnings.Add(Locales.Message("settings.couldNotUpdateGameCaptureSettings", "error", setGame.Message));

            var setWindow = ObsRequest.Invoke("SetInputSettings", new JObject { ["inputName"] = OverlayScene.WindowCaptureSourceName, ["inputSettings"] = windowSettings, ["overlay"] = true }, 3000);
            if (!setWindow.Ok) warnings.Add(Locales.Message("settings.couldNotUpdateWindowCaptureSettings", "error", setWindow.Message));

            if (useWindowCapture && string.IsNullOrWhiteSpace(settings["screenshareGameWindow"]?.Value<string>()))
                warnings.Add(Locales.Message("settings.specificGameCaptureWasSelectedWithout"));

            var enableGame = SetSceneItemEnabled(sceneName, game, useGameCapture);
            if (enableGame["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotToggleGameCaptureError", "error", enableGame["message"]));
            var enableWindow = SetSceneItemEnabled(sceneName, window, useWindowCapture);
            if (enableWindow["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotToggleWindowCaptureError", "error", enableWindow["message"]));
            if (useDesktop)
            {
                var enableDisplay = SetSceneItemEnabled(sceneName, display, true);
                if (enableDisplay["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotShowDisplayCaptureError", "error", enableDisplay["message"]));
            }

            var order = SetWindowCaptureSceneOrder(sceneName, items);
            if (order["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotPositionWindowCaptureUnder", "error", order["message"]));

            if (preset != null)
            {
                foreach (var captureName in new[] { OverlayScene.DisplayCaptureSourceName, OverlayScene.WindowCaptureSourceName, OverlayScene.GameCaptureSourceName })
                {
                    var capture = FindSceneItem(items, captureName);
                    var captureTransform = MainCaptureTransform(captureName, preset);
                    var captureResult = SetSceneItemTransform(sceneName, capture, captureTransform);
                    if (captureResult["ok"]?.Value<bool>() != true) warnings.Add(Locales.Message("settings.couldNotFitCapturenameToCanvas", "captureName", captureName, "error", captureResult["message"]));
                }
            }

            if (useDesktop) applied.Add("desktop capture source");
            else if (useWindowCapture) applied.Add("window capture source");
            else applied.Add("game capture source");
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static void RemoveJsonListWhere(JArray list, Func<JObject, bool> predicate)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (predicate((JObject)list[i])) list.RemoveAt(i);
            }
        }

        // full read-modify-write of the live scene collection file: locates or creates the Bongo Cat, WASD/Mouse, Group, and Window Capture sources, applies motion blur and cursor settings across all capture sources, then walks the scene-kind sources items array rewriting/creating/removing each scene item to match the selected overlay style. this is the live-file-edit twin of ReplayKitSetups Transform.ApplyScenesJson, except that function only ever handles its own known bundled-template shape once, while this must be defensive against whatever a real, possibly years-old, possibly hand-edited live scene collection currently contains -- matching sources by uuid OR by legacy name/id fallback throughout.
        internal static JObject SetOverlaySceneFile(JObject settings, JObject preset)
        {
            try
            {
                string path = GetSceneCollectionPath();
                if (!File.Exists(path)) return new JObject { ["ok"] = false, ["message"] = Locales.Message("settings.obsSceneCollectionFileWasNot") };
                var data = JObject.Parse(File.ReadAllText(path));
                var sources = data["sources"] as JArray;
                if (sources == null) throw new InvalidOperationException(Locales.Message("settings.obsSceneCollectionHasNoSources"));
                // do NOT self-assign data["sources"] / data["groups"] here -- in the bundled Newtonsoft that detaches the array from `data`, so every edit below lands on an orphan and the file is written from the untouched original (symptom: "switched overlay, scene still shows the old one"). they are re-attached once, right before serialize.
                var groups = data["groups"] as JArray ?? new JArray();
                RemoveBongoStandby(sources, groups);

                string overlayStyle = settings["overlayStyle"]?.Value<string>() ?? "";
                bool useInputOverlay = overlayStyle == "input_overlay";
                bool useBongo = overlayStyle == "bongo_cat";
                bool useMotionBlur = settings["motionBlurEnabled"]?.Value<bool>() ?? false;
                double motionBlurStrength = MotionBlurStrength(settings);
                var inputSources = new Dictionary<string, InputOverlaySourceRef>();
                var inputSourceNames = new[] { OverlayScene.WasdSourceName, OverlayScene.MouseSourceName };
                JObject inputGroup = null;
                foreach (var group in groups)
                {
                    if (group["name"]?.Value<string>() == OverlayScene.InputOverlayGroupName) { inputGroup = (JObject)group; break; }
                }
                string inputGroupUuid = inputGroup?["uuid"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(inputGroupUuid)) inputGroupUuid = Guid.NewGuid().ToString();

                var existingInputUuids = new HashSet<string>();
                foreach (var source in sources)
                {
                    string name = source["name"]?.Value<string>() ?? "";
                    if (inputSourceNames.Contains(name))
                    {
                        string uuid = source["uuid"]?.Value<string>() ?? "";
                        if (!string.IsNullOrWhiteSpace(uuid)) existingInputUuids.Add(uuid);
                    }
                }

                JObject bongoSource = null;
                foreach (var source in sources)
                {
                    if (source["name"]?.Value<string>() == OverlayScene.BongoSourceName) { bongoSource = (JObject)source; break; }
                }
                if (bongoSource == null)
                {
                    foreach (var source in sources)
                    {
                        if (IsBongoInputKind(source["id"]?.Value<string>())) { bongoSource = (JObject)source; break; }
                    }
                }
                string bongoUuid = bongoSource?["uuid"]?.Value<string>() ?? "";
                JObject windowCaptureSource = null;
                foreach (var source in sources)
                {
                    string name = source["name"]?.Value<string>() ?? "";
                    string uuid = source["uuid"]?.Value<string>() ?? "";
                    if (name == OverlayScene.WindowCaptureSourceName || uuid == OverlayScene.WindowCaptureSourceUuid) { windowCaptureSource = (JObject)source; break; }
                }
                string windowCaptureUuid = windowCaptureSource?["uuid"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(windowCaptureUuid)) windowCaptureUuid = OverlayScene.WindowCaptureSourceUuid;

                if (useInputOverlay)
                {
                    var keyboardFiles = InputOverlayMemberFiles(OverlayScene.WasdSourceName, settings);
                    var mouseFiles = InputOverlayMemberFiles(OverlayScene.MouseSourceName, settings);
                    var inputSpecs = new[]
                    {
                        new { name = OverlayScene.WasdSourceName, image = keyboardFiles?.Sheet ?? "", layout = keyboardFiles?.Json ?? "" },
                        new { name = OverlayScene.MouseSourceName, image = mouseFiles?.Sheet ?? "", layout = mouseFiles?.Json ?? "" },
                    };
                    foreach (var spec in inputSpecs)
                    {
                        JObject inputSource = null;
                        foreach (var source in sources)
                        {
                            if (source["name"]?.Value<string>() == spec.name) { inputSource = (JObject)source; break; }
                        }
                        string inputUuid = inputSource?["uuid"]?.Value<string>() ?? "";
                        if (string.IsNullOrWhiteSpace(inputUuid)) inputUuid = Guid.NewGuid().ToString();
                        if (inputSource == null)
                        {
                            inputSource = NewInputOverlaySourceJson(spec.name, inputUuid, spec.image, spec.layout);
                            sources.Add(inputSource);
                        }
                        else
                        {
                            SetInputOverlaySourceJson(inputSource, spec.name, inputUuid, spec.image, spec.layout);
                        }
                        SetOverlayOpacitySourceJson(inputSource, settings);
                        inputSources[spec.name] = new InputOverlaySourceRef { Uuid = inputUuid };
                    }
                }
                else
                {
                    RemoveJsonListWhere(sources, source => inputSourceNames.Contains(source["name"]?.Value<string>() ?? ""));
                    RemoveJsonListWhere(groups, group => group["name"]?.Value<string>() == OverlayScene.InputOverlayGroupName || group["uuid"]?.Value<string>() == inputGroupUuid);
                }

                if (useBongo && bongoSource == null)
                {
                    bongoSource = NewBongoSourceJson(Guid.NewGuid().ToString(), settings);
                    sources.Add(bongoSource);
                }
                if (useBongo)
                {
                    bongoUuid = bongoSource["uuid"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(bongoUuid)) bongoUuid = Guid.NewGuid().ToString();
                    SetBongoSourceJson(bongoSource, bongoUuid, settings);
                    SetOverlayOpacitySourceJson(bongoSource, settings);
                }
                else
                {
                    string bongoUuidCapture = bongoUuid;
                    RemoveJsonListWhere(sources, source =>
                        source["name"]?.Value<string>() == OverlayScene.BongoSourceName ||
                        IsBongoInputKind(source["id"]?.Value<string>()) ||
                        (!string.IsNullOrWhiteSpace(bongoUuidCapture) && source["uuid"]?.Value<string>() == bongoUuidCapture));
                }
                if (windowCaptureSource == null)
                {
                    windowCaptureSource = NewWindowCaptureSourceJson(windowCaptureUuid, settings);
                    sources.Add(windowCaptureSource);
                }
                else
                {
                    SetWindowCaptureSourceJson(windowCaptureSource, windowCaptureUuid, settings);
                }

                foreach (var sourceToken in sources)
                {
                    var source = (JObject)sourceToken;
                    string sourceName = source["name"]?.Value<string>() ?? "";
                    SetCaptureCursorSourceJson(source, settings);
                    if (string.IsNullOrWhiteSpace(MotionBlurFilterUuid(sourceName))) continue;
                    if (useMotionBlur) SetMotionBlurSourceJson(source, true, motionBlurStrength);
                    else RemoveMotionBlurSourceJson(source);
                }

                foreach (var sourceToken in sources)
                {
                    var source = (JObject)sourceToken;
                    if (source["id"]?.Value<string>() != "scene") continue;
                    var sceneSettings = source["settings"] as JObject;
                    if (sceneSettings == null) continue;
                    var items = sceneSettings["items"] as JArray;
                    if (items == null) continue;
                    // same detach hazard as data["sources"] above -- mutate `items` in place, re-attach below.
                    if (!useInputOverlay)
                    {
                        RemoveJsonListWhere(items, item =>
                        {
                            string name = item["name"]?.Value<string>() ?? "";
                            string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                            return inputSourceNames.Contains(name) || name == OverlayScene.InputOverlayGroupName || sourceUuid == inputGroupUuid || existingInputUuids.Contains(sourceUuid);
                        });
                    }
                    if (!useBongo)
                    {
                        string bongoUuidCapture = bongoUuid;
                        RemoveJsonListWhere(items, item =>
                        {
                            string name = item["name"]?.Value<string>() ?? "";
                            string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                            return name == OverlayScene.BongoSourceName || (!string.IsNullOrWhiteSpace(bongoUuidCapture) && sourceUuid == bongoUuidCapture);
                        });
                    }

                    bool foundBongoItem = false;
                    var foundInputItems = new Dictionary<string, bool> { [OverlayScene.WasdSourceName] = false, [OverlayScene.MouseSourceName] = false };
                    bool foundInputGroupItem = false;
                    bool foundWindowCaptureItem = false;
                    bool windowCaptureVisible = settings["screenshareCaptureMode"]?.Value<string>() == "game_window";
                    foreach (var itemToken in items)
                    {
                        var item = (JObject)itemToken;
                        string name = item["name"]?.Value<string>() ?? "";
                        string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                        if (name == OverlayScene.DisplayCaptureSourceName || name == OverlayScene.GameCaptureSourceName)
                        {
                            SetMainCaptureSceneItemJson(item, name, preset);
                        }
                        else if (name == OverlayScene.WindowCaptureSourceName || sourceUuid == windowCaptureUuid)
                        {
                            SetWindowCaptureSceneItemJson(item, windowCaptureUuid, preset, windowCaptureVisible);
                            foundWindowCaptureItem = true;
                        }
                        else if (name == OverlayScene.WasdSourceName || name == OverlayScene.MouseSourceName)
                        {
                            string inputUuid = "";
                            if (inputSources.TryGetValue(name, out var srcRef)) inputUuid = srcRef.Uuid;
                            if (string.IsNullOrWhiteSpace(inputUuid)) inputUuid = sourceUuid;
                            SetInputOverlaySceneItemJson(item, name, preset, useInputOverlay, inputUuid, true, settings);
                            foundInputItems[name] = true;
                            if (inputSources.TryGetValue(name, out var srcRef2)) srcRef2.Id = item["id"]?.Value<int>() ?? 0;
                        }
                        else if (name == OverlayScene.InputOverlayGroupName || sourceUuid == inputGroupUuid)
                        {
                            SetInputOverlayGroupSceneItemJson(item, inputGroupUuid, preset, useInputOverlay, settings);
                            foundInputGroupItem = true;
                        }
                        else if (name == OverlayScene.BongoSourceName || sourceUuid == bongoUuid)
                        {
                            SetBongoSceneItemJson(item, bongoUuid, preset, useBongo, settings);
                            foundBongoItem = true;
                        }
                    }

                    if (!foundWindowCaptureItem)
                    {
                        var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                        SetWindowCaptureSceneItemJson(newItem, windowCaptureUuid, preset, windowCaptureVisible);
                        int insertAt = 0;
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (items[i]["name"]?.Value<string>() == OverlayScene.DisplayCaptureSourceName) { insertAt = i + 1; break; }
                        }
                        items.Insert(insertAt, newItem);
                        sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                    }
                    MoveJsonSceneItemAfter(items, OverlayScene.WindowCaptureSourceName, OverlayScene.DisplayCaptureSourceName);
                    if (useBongo && !foundBongoItem)
                    {
                        var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                        SetBongoSceneItemJson(newItem, bongoUuid, preset, true, settings);
                        items.Add(newItem);
                        sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                    }
                    if (useInputOverlay)
                    {
                        foreach (var name in new[] { OverlayScene.WasdSourceName, OverlayScene.MouseSourceName })
                        {
                            if (foundInputItems[name]) continue;
                            if (!inputSources.TryGetValue(name, out var srcRef)) throw new InvalidOperationException("Input overlay source was not prepared for " + name + ".");
                            var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                            SetInputOverlaySceneItemJson(newItem, name, preset, true, srcRef.Uuid, true, settings);
                            srcRef.Id = newItem["id"]?.Value<int>() ?? 0;
                            items.Add(newItem);
                            sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                        }
                        if (inputGroup == null)
                        {
                            inputGroup = new JObject();
                            groups.Add(inputGroup);
                        }
                        SetInputOverlayGroupSourceJson(inputGroup, inputGroupUuid, inputSources, preset, settings);
                        if (!foundInputGroupItem)
                        {
                            var groupItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                            SetInputOverlayGroupSceneItemJson(groupItem, inputGroupUuid, preset, true, settings);
                            items.Add(groupItem);
                            sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                        }
                    }
                    sceneSettings["items"] = items;
                }

                data["sources"] = sources;
                data["groups"] = groups;
                string json = data.ToString(Formatting.None);
                AppConfig.WriteUtf8(path, json);
                // the restart flow gracefully closes OBS so it can save window geometry -- but that exit-save also writes OBSs stale in-memory scene collection back over the edit just made here. stage a copy with a non-.json extension (OBSs scene scan ignores it); ObsProcess.Relaunch moves it onto the real file after OBS is fully gone, right before the relaunch.
                try { AppConfig.WriteUtf8(path + Constants.PENDING_SCENE_SUFFIX, json); }
                catch (Exception ex) { Log.Write("SetOverlaySceneFile: could not stage pending scene: " + ex.Message); }
                return new JObject { ["ok"] = true, ["path"] = path };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // which live scene items a size or opacity change of the overlay style targets, preferring what is visible right now over what the style implies, so a change lands on the right source even mid-switch or in a scene in an unexpected state

        internal static string[] GetOverlaySourceNames(string overlayStyle)
        {
            if (overlayStyle == "bongo_cat") return new[] { OverlayScene.BongoSourceName };
            if (overlayStyle == "input_overlay") return new[] { OverlayScene.InputOverlayGroupName, OverlayScene.WasdSourceName, OverlayScene.MouseSourceName };
            return new string[0];
        }

        internal static List<string> GetOverlayCandidateSourceNames(string overlayStyle)
        {
            var names = new List<string>();
            foreach (var name in GetOverlaySourceNames(overlayStyle))
            {
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
            foreach (var style in new[] { "bongo_cat", "input_overlay" })
            {
                foreach (var name in GetOverlaySourceNames(style))
                {
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
                }
            }
            return names;
        }

        internal static void AddOverlayCandidateName(List<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!names.Contains(name)) names.Add(name);
        }

        internal static bool TestSceneItemVisible(JObject item)
        {
            if (item == null) return false;
            var enabled = item["sceneItemEnabled"];
            if (enabled == null || enabled.Type == JTokenType.Null) return true;
            return enabled.Value<bool>();
        }

        internal static List<string> GetOrderedOverlayCandidateSourceNames(string overlayStyle)
        {
            var candidates = GetOverlayCandidateSourceNames(overlayStyle);
            var ordered = new List<string>();
            JArray items = new JArray();

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() == true)
            {
                var itemsResult = GetSceneItems(scene["name"]?.Value<string>());
                if (itemsResult["ok"]?.Value<bool>() == true) items = itemsResult["items"] as JArray ?? new JArray();
            }

            if (items.Count > 0)
            {
                foreach (var name in candidates)
                {
                    var item = FindSceneItem(items, name);
                    if (item != null && TestSceneItemVisible(item)) AddOverlayCandidateName(ordered, name);
                }
                foreach (var name in candidates)
                {
                    if (FindSceneItem(items, name) != null) AddOverlayCandidateName(ordered, name);
                }
            }
            foreach (var name in candidates) AddOverlayCandidateName(ordered, name);
            return ordered;
        }

        internal static JObject GetLiveOverlayOpacityPercent(JObject settings)
        {
            foreach (var name in GetOrderedOverlayCandidateSourceNames(settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var filters = GetSourceFilterListLive(name);
                if (filters["ok"]?.Value<bool>() != true) continue;
                var match = GetOverlayOpacityLiveFilterInfo(filters["filters"] as JArray);
                if (match["found"]?.Value<bool>() != true) continue;
                var opacity = GetOverlayOpacityPercentFromFilterSettings(match["settings"] as JObject);
                if (opacity != null) return new JObject { ["ok"] = true, ["opacity"] = opacity.Value, ["sourceName"] = name, ["filterName"] = match["name"] };
            }
            return new JObject { ["ok"] = false, ["opacity"] = null, ["sourceName"] = "", ["filterName"] = "" };
        }

        internal static List<string> GetExistingInputOverlayScaleTargets(JArray items, bool visibleOnly)
        {
            var group = FindSceneItem(items, OverlayScene.InputOverlayGroupName);
            if (group != null && (!visibleOnly || TestSceneItemVisible(group))) return new List<string> { OverlayScene.InputOverlayGroupName };
            var targets = new List<string>();
            foreach (var name in new[] { OverlayScene.WasdSourceName, OverlayScene.MouseSourceName })
            {
                var item = FindSceneItem(items, name);
                if (item == null) continue;
                if (visibleOnly && !TestSceneItemVisible(item)) continue;
                targets.Add(name);
            }
            return targets;
        }

        internal static List<string> GetOverlayScaleSceneItemNames(JArray items, string overlayStyle)
        {
            if (overlayStyle == "bongo_cat")
            {
                if (FindSceneItem(items, OverlayScene.BongoSourceName) != null) return new List<string> { OverlayScene.BongoSourceName };
            }
            if (overlayStyle == "input_overlay")
            {
                var inputTargets = GetExistingInputOverlayScaleTargets(items, false);
                if (inputTargets.Count > 0) return inputTargets;
            }

            var bongo = FindSceneItem(items, OverlayScene.BongoSourceName);
            if (bongo != null && TestSceneItemVisible(bongo)) return new List<string> { OverlayScene.BongoSourceName };
            var visibleInputTargets = GetExistingInputOverlayScaleTargets(items, true);
            if (visibleInputTargets.Count > 0) return visibleInputTargets;
            if (bongo != null) return new List<string> { OverlayScene.BongoSourceName };
            return GetExistingInputOverlayScaleTargets(items, false);
        }

        // the scene json gives the wasd and mouse members a filter each and the group none, so every source of the style that carries one is a target, and a scene without any gets it on the members
        internal static List<string> GetOverlayOpacitySourceTargets(string overlayStyle)
        {
            var names = GetOrderedOverlayCandidateSourceNames(overlayStyle);
            if (names.Count == 0) return new List<string>();

            var styleNames = GetOverlaySourceNames(overlayStyle);
            var carrying = new List<string>();
            string firstCarrying = null;
            foreach (var name in names)
            {
                var filters = GetSourceFilterListLive(name);
                if (filters["ok"]?.Value<bool>() != true) continue;
                var match = GetOverlayOpacityLiveFilterInfo(filters["filters"] as JArray);
                if (match["found"]?.Value<bool>() != true) continue;
                if (firstCarrying == null) firstCarrying = name;
                if (styleNames.Contains(name)) carrying.Add(name);
            }
            if (carrying.Count > 0) return carrying;
            if (firstCarrying != null) return new List<string> { firstCarrying };

            if (overlayStyle == "input_overlay")
            {
                var fallback = new List<string>();
                foreach (var name in new[] { OverlayScene.WasdSourceName, OverlayScene.MouseSourceName })
                {
                    if (TestSourceExistsForFilters(name)) fallback.Add(name);
                }
                if (fallback.Count > 0) return fallback;
            }
            if (overlayStyle == "bongo_cat" && TestSourceExistsForFilters(OverlayScene.BongoSourceName)) return new List<string> { OverlayScene.BongoSourceName };
            foreach (var name in names)
            {
                if (TestSourceExistsForFilters(name)) return new List<string> { name };
            }
            return new List<string>();
        }

        internal static JObject ApplyOverlayScaleLive(JObject previous, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            double previousScale = OverlayScaleFactor(previous);
            double nextScale = OverlayScaleFactor(settings);
            if (previousScale <= 0.0 || Math.Abs(nextScale - previousScale) < 0.0001)
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray(Locales.Message("settings.overlaySizeSceneLookupFailedError", "error", scene["message"]?.ToString())) };
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray(Locales.Message("settings.overlaySizeSceneItemsFailedError", "error", itemsResult["message"]?.ToString())) };

            var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
            double scaleRatio = nextScale / previousScale;
            var items = itemsResult["items"] as JArray;
            foreach (var name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var item = FindSceneItem(items, name);
                if (item == null) continue;
                var r = SetSceneItemScaledFromCurrent(sceneName, item, scaleRatio, preset);
                if (r["ok"]?.Value<bool>() == true) { if (r["skipped"]?.Value<bool>() != true) applied.Add(name + " size"); }
                else warnings.Add(Locales.Message("settings.couldNotResizeNameError", "name", name, "error", r["message"]));
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // mirrors along the rotated local axis and keeps the same centre
        internal static JObject MirrorTransformInPlace(JObject current, string overlayStyle, JObject settings)
        {
            OverlayBoxSize(overlayStyle, settings, out double w, out double h);
            return OverlayRotation.Mirror(current, w, h);
        }

        // the unscaled box of the item of an overlay style: the 1280x768 bongo frame, or the group the picked wasd layouts make
        internal static void OverlayBoxSize(string overlayStyle, JObject settings, out double width, out double height)
        {
            if (overlayStyle == "bongo_cat") { width = OverlayScene.BongoSourceWidth; height = OverlayScene.BongoSourceHeight; return; }
            InputOverlayLayouts.GroupSize(settings, out width, out height);
        }

        internal static JObject ApplyOverlayRotationLive(JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true) return scene;
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true) return itemsResult;
            var items = itemsResult["items"] as JArray;
            foreach (string name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var item = FindSceneItem(items, name);
                var current = GetSceneItemTransformLive(sceneName, item);
                if (current["ok"]?.Value<bool>() != true || !(current["transform"] is JObject transform)) { warnings.Add(Locales.Message("settings.couldNotReadNameRotation", "name", name)); continue; }
                var result = SetSceneItemTransform(sceneName, item, OverlayRotation.Rotate(transform, settings["overlayRotation"]?.Value<int>() ?? 0));
                if (result["ok"]?.Value<bool>() == true) applied.Add(name + " rotation");
                else warnings.Add(Locales.Message("settings.couldNotRotateNameError", "name", name, "error", result["message"]));
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // toggle the horizontal flip live by mirroring the overlays CURRENT scene-item transform in place -- never repositions to the canonical corner, so a user who dragged the overlay somewhere keeps it there. Group for input_overlay (WASD/Mouse ride along inside it), Bongo Cat Overlay for bongo.
        internal static JObject ApplyOverlayFlipLive(JObject previous, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            if ((previous?["overlayFlipH"]?.Value<bool>() ?? false) == (settings?["overlayFlipH"]?.Value<bool>() ?? false))
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray(Locales.Message("settings.overlayFlipSceneLookupFailedError", "error", scene["message"]?.ToString())) };
            string sceneName = scene["name"]?.Value<string>();
            var items = GetSceneItems(sceneName)["items"] as JArray;
            string style = settings["overlayStyle"]?.Value<string>() ?? "";
            string itemName = OverlayItemNameForStyle(style);
            if (itemName == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            var item = FindSceneItem(items, itemName);
            if (item == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var cur = GetSceneItemTransformLive(sceneName, item);
            if (cur["ok"]?.Value<bool>() != true || cur["transform"] == null || cur["transform"].Type == JTokenType.Null)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray(Locales.Message("settings.overlayFlipPositionFailedError", "name", itemName, "error", cur["message"]?.ToString())) };
            var r = SetSceneItemTransform(sceneName, item, MirrorTransformInPlace(cur["transform"] as JObject, style, settings));
            if (r["ok"]?.Value<bool>() == true) applied.Add(itemName + " flip");
            else warnings.Add(Locales.Message("settings.couldNotFlipItemnameError", "itemName", itemName, "error", r["message"]));
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static JObject ApplyOverlayOpacityForStyleLive(JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            int opacity = OverlayOpacityValue(settings);
            foreach (var name in GetOverlayOpacitySourceTargets(settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var r = ApplyOverlayOpacityLive(name, opacity, settings);
                if (r["ok"]?.Value<bool>() == true) applied.Add(name + " opacity");
                else warnings.Add(Locales.Message("settings.couldNotSetNameOpacityError", "name", name, "error", r["message"]));
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static JObject ApplyOverlayVisualSettingsLive(JObject previous, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            if (previous["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>())
            {
                var scale = ApplyOverlayScaleLive(previous, settings);
                applied.AddRange((scale["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((scale["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            if ((previous["overlayFlipH"]?.Value<bool>() ?? false) != (settings["overlayFlipH"]?.Value<bool>() ?? false))
            {
                var flip = ApplyOverlayFlipLive(previous, settings);
                applied.AddRange((flip["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((flip["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            if (previous["overlayRotation"]?.Value<int>() != settings["overlayRotation"]?.Value<int>())
            {
                var rotation = ApplyOverlayRotationLive(settings);
                applied.AddRange((rotation["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((rotation["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                if (rotation["ok"]?.Value<bool>() != true && rotation["warnings"] == null) warnings.Add(Locales.Message("settings.couldNotRotateOverlayError", "error", rotation["message"]));
            }
            bool colorChanged = previous["overlayOpacity"]?.Value<int>() != settings["overlayOpacity"]?.Value<int>() ||
                previous["overlayHueShift"]?.Value<double>() != settings["overlayHueShift"]?.Value<double>() ||
                previous["overlayColorMultiply"]?.Value<string>() != settings["overlayColorMultiply"]?.Value<string>() ||
                previous["overlayColorAdd"]?.Value<string>() != settings["overlayColorAdd"]?.Value<string>();
            if (colorChanged)
            {
                var opacity = ApplyOverlayOpacityForStyleLive(settings);
                applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static bool TestOverlayColorRequest(JObject incoming) =>
            incoming != null && (incoming["overlayOpacity"] != null || incoming["overlayHueShift"] != null || incoming["overlayColorMultiply"] != null || incoming["overlayColorAdd"] != null);

        // the overlay item transforms before a save from the overlay preview, which the new size is worked out from
        internal static JObject CaptureOverlayBaseline(JObject settings)
        {
            var baseline = new JObject
            {
                ["settings"] = settings,
                ["preset"] = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings),
                ["sceneName"] = "",
                ["transforms"] = new JObject(),
            };
            var scene = GetSceneName();
            if (scene["ok"]?.Value<bool>() != true) return baseline;
            string sceneName = scene["name"]?.Value<string>();
            baseline["sceneName"] = sceneName;
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true) return baseline;
            var items = itemsResult["items"] as JArray;
            var transforms = (JObject)baseline["transforms"];
            foreach (var name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var item = FindSceneItem(items, name);
                if (item == null) continue;
                var current = GetSceneItemTransformLive(sceneName, item);
                if (current["ok"]?.Value<bool>() == true && current["transform"] != null && current["transform"].Type != JTokenType.Null)
                    transforms[name] = new JObject { ["item"] = item, ["transform"] = current["transform"] };
            }
            return baseline;
        }

        // size is relative to the baseline transforms; flip mirrors the item where it is now, so it never jumps back to the canonical corner; rotation keeps the centre
        internal static JObject ApplyOverlayGeometryLive(JObject baseline, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            var baseSettings = baseline["settings"] as JObject;
            string sceneName = baseline["sceneName"]?.Value<string>();
            var transforms = baseline["transforms"] as JObject ?? new JObject();
            if (string.IsNullOrWhiteSpace(sceneName))
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            bool scaleChanged = baseSettings["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>();
            bool flipChanged = (baseSettings["overlayFlipH"]?.Value<bool>() ?? false) != (settings["overlayFlipH"]?.Value<bool>() ?? false);
            bool rotationChanged = baseSettings["overlayRotation"]?.Value<int>() != settings["overlayRotation"]?.Value<int>();
            if (!scaleChanged && !flipChanged && !rotationChanged)
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            string style = settings["overlayStyle"]?.Value<string>() ?? "";
            if (scaleChanged)
            {
                double baseScale = OverlayScaleFactor(baseSettings);
                if (baseScale <= 0.0) baseScale = 1.0;
                double scaleRatio = OverlayScaleFactor(settings) / baseScale;
                var preset = baseline["preset"] as JObject ?? GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
                foreach (var prop in transforms.Properties().ToList())
                {
                    var entry = (JObject)prop.Value;
                    var rs = SetSceneItemTransform(sceneName, entry["item"] as JObject, GetScaledTransformFromCurrent(entry["transform"], scaleRatio, preset));
                    if (rs["ok"]?.Value<bool>() == true) { if (!flipChanged) applied.Add(prop.Name + " size"); }
                    else warnings.Add(Locales.Message("settings.couldNotPreviewNameSizeError", "name", prop.Name, "error", rs["message"]));
                }
            }

            if (flipChanged)
            {
                string itemName = OverlayItemNameForStyle(style);
                var item = itemName == null ? null : ((transforms[itemName] as JObject)?["item"] as JObject ?? FindSceneItem(GetSceneItems(sceneName)["items"] as JArray, itemName));
                if (item != null)
                {
                    var cur = GetSceneItemTransformLive(sceneName, item);
                    if (cur["ok"]?.Value<bool>() == true && cur["transform"] != null && cur["transform"].Type != JTokenType.Null)
                    {
                        var rf = SetSceneItemTransform(sceneName, item, MirrorTransformInPlace(cur["transform"] as JObject, style, settings));
                        if (rf["ok"]?.Value<bool>() == true) applied.Add(itemName + " flip");
                        else warnings.Add(Locales.Message("settings.couldNotPreviewItemnameFlipError", "itemName", itemName, "error", rf["message"]));
                    }
                    else warnings.Add(Locales.Message("settings.couldNotReadItemnameToFlip", "itemName", itemName, "error", cur["message"]));
                }
            }

            if (rotationChanged)
            {
                var rotation = ApplyOverlayRotationLive(settings);
                applied.AddRange((rotation["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((rotation["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                if (rotation["ok"]?.Value<bool>() != true && rotation["warnings"] == null) warnings.Add(Locales.Message("settings.couldNotPreviewOverlayRotationError", "error", rotation["message"]));
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        private static readonly HashSet<string> OverlayPreviewKeys = new HashSet<string> { "overlayOpacity", "overlayScale", "overlayRotation", "overlayFlipH", "overlayHueShift", "overlayColorMultiply", "overlayColorAdd" };

        // the save of the overlay preview on the settings page: the preview draws in the page only, so obs is changed once, from where its items are right before the save
        public static JObject OverlayPreviewFromRequest(string body)
        {
            lock (SettingsStore.Gate)
            {
                if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException(Locales.Message("settings.missingOverlayPreviewBody"));
                var incoming = JObject.Parse(body);
                if (incoming.Count < 1) throw new InvalidOperationException(Locales.Message("settings.missingOverlayPreviewSetting"));
                foreach (var prop in incoming.Properties())
                {
                    if (!OverlayPreviewKeys.Contains(prop.Name)) throw new InvalidOperationException(Locales.Message("settings.unknownSettingName", "name", prop.Name));
                }

                var currentJ = ReadSettings();
                var baseline = CaptureOverlayBaseline(Normalize(currentJ));
                foreach (var prop in incoming.Properties()) currentJ[prop.Name] = prop.Value;
                var settingsJ = Normalize(currentJ);
                WriteSettings(settingsJ);

                var warnings = new List<string>();
                var applied = new List<string>();
                var geometry = ApplyOverlayGeometryLive(baseline, settingsJ);
                applied.AddRange((geometry["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((geometry["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                if (TestOverlayColorRequest(incoming))
                {
                    var opacity = ApplyOverlayOpacityForStyleLive(settingsJ);
                    applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                }
                return new JObject
                {
                    ["ok"] = warnings.Count == 0, ["settings"] = settingsJ, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
        }

    }
}
