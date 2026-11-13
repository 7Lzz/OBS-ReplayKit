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
    // the overlay on a running obs: the opacity and colour filter over obs-websocket, which sources an overlay style uses, and the live preview the settings page drives while a slider moves
    internal static class SettingsOverlayLive
    {
        // -- overlay opacity filter: live-websocket half (create/read/update the filter on a running obs) --

        internal static JObject GetOverlayOpacityLiveFilterInfo(JArray filters)
        {
            foreach (var filter in filters ?? new JArray())
            {
                string name = filter["filterName"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name == "ReplayKit Overlay Opacity")
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

        internal static string GetOverlayOpacityLiveFilterName(JArray filters)
        {
            var match = GetOverlayOpacityLiveFilterInfo(filters);
            return match["found"]?.Value<bool>() == true ? match["name"].Value<string>() : "";
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
            var list = ObsWebSocket.InvokeRequest("GetSourceFilterList", new JObject { ["sourceName"] = sourceName }, 3000);
            if (!list.Ok) return new JObject { ["ok"] = false, ["message"] = list.Message, ["filters"] = new JArray() };
            var filters = list.Data?["filters"] as JArray ?? list.Data?["sourceFilters"] as JArray ?? new JArray();
            return new JObject { ["ok"] = true, ["message"] = "", ["filters"] = filters };
        }

        internal static bool TestSourceExistsForFilters(string sourceName) => GetSourceFilterListLive(sourceName)["ok"]?.Value<bool>() == true;

        internal static JObject GetLatestSceneCollectionPath()
        {
            string root = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "basic", "scenes");
            if (!Directory.Exists(root)) return null;
            var file = new DirectoryInfo(root).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return file != null ? new JObject { ["path"] = file.FullName } : null;
        }

        // last-resort readback of the opacity filter straight off disk, for the settings payload only (never a write target) -- used when a live websocket filter lookup fails, so the settings dock still shows something sensible even if obs isnt reachable right now. picks the most-recently-written scene file under basic\scenes, a looser resolution than GetSceneCollectionPath (which asks obs which collection is actually active); that imprecision is fine here since this never drives a write.
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

            if (opacityPercent >= 100 && !hasColorAdjustments)
            {
                if (string.IsNullOrWhiteSpace(existingFilterName)) return new JObject { ["ok"] = true, ["message"] = "" };
                var setFull = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                {
                    ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                    ["filterSettings"] = OverlayOpacityFilterSettingsLive(100, legacyPercent, settings), ["overlay"] = true,
                }, 3000);
                if (!setFull.Ok) return new JObject { ["ok"] = false, ["message"] = setFull.Message };
                var enableFull = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
                return enableFull.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = enableFull.Message };
            }

            var filterSettings = OverlayOpacityFilterSettingsLive(opacityPercent, legacyPercent, settings);
            if (!string.IsNullOrWhiteSpace(existingFilterName))
            {
                var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterSettings"] = filterSettings, ["overlay"] = true }, 3000);
                if (!set.Ok) return new JObject { ["ok"] = false, ["message"] = set.Message };
            }
            else
            {
                var create = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = "ReplayKit Overlay Opacity", ["filterKind"] = "color_filter_v2", ["filterSettings"] = filterSettings }, 3000);
                if (!create.Ok)
                {
                    var createFallback = ObsWebSocket.InvokeRequest("CreateSourceFilter", new JObject
                    {
                        ["sourceName"] = sourceName, ["filterName"] = "ReplayKit Overlay Opacity", ["filterKind"] = "color_filter",
                        ["filterSettings"] = OverlayOpacityFilterSettingsLive(opacityPercent, true, settings),
                    }, 3000);
                    if (!createFallback.Ok) return new JObject { ["ok"] = false, ["message"] = create.Message };
                    legacyPercent = true;
                }
                existingFilterName = "ReplayKit Overlay Opacity";
                var createdList = GetSourceFilterListLive(sourceName);
                if (createdList["ok"]?.Value<bool>() == true)
                {
                    var createdFilter = GetOverlayOpacityLiveFilterInfo(createdList["filters"] as JArray);
                    if (createdFilter["found"]?.Value<bool>() == true)
                    {
                        legacyPercent = OverlayOpacityFilterUsesLegacyPercent(createdFilter["kind"]?.Value<string>() ?? "", createdFilter["settings"] as JObject);
                        if (legacyPercent)
                        {
                            var setCreated = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                            {
                                ["sourceName"] = sourceName, ["filterName"] = existingFilterName,
                                ["filterSettings"] = OverlayOpacityFilterSettingsLive(opacityPercent, true, settings), ["overlay"] = true,
                            }, 3000);
                            if (!setCreated.Ok) return new JObject { ["ok"] = false, ["message"] = setCreated.Message };
                        }
                    }
                }
            }
            var enable = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject { ["sourceName"] = sourceName, ["filterName"] = existingFilterName, ["filterEnabled"] = true }, 3000);
            return enable.Ok ? new JObject { ["ok"] = true, ["message"] = "" } : new JObject { ["ok"] = false, ["message"] = enable.Message };
        }

        // the cursor of the display and game capture sources in a scene file follows the setting of its kind; the window capture source is written whole from the same settings elsewhere
        internal static void SetCaptureCursorSourceJson(JObject source, JObject settings)
        {
            string sourceName = source["name"]?.Value<string>() ?? "";
            string sourceId = source["id"]?.Value<string>() ?? "";
            string key;
            if (sourceName == "Display Capture" || sourceId == "monitor_capture") key = "showCursorDisplayCapture";
            else if (sourceName == "Game Capture" || sourceId == "game_capture") key = "showCursorGameCapture";
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
                new[] { "Display Capture", "capture_cursor", "showCursorDisplayCapture" },
                new[] { "Game Capture", "capture_cursor", "showCursorGameCapture" },
                new[] { "Window Capture", "cursor", "showCursorWindowCapture" },
            })
            {
                var set = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject
                {
                    ["inputName"] = target[0],
                    ["inputSettings"] = new JObject { [target[1]] = CursorSetting(settings, target[2]) },
                    ["overlay"] = true,
                }, 3000);
                if (set.Ok) { if (!applied.Contains("Mouse cursor")) applied.Add("Mouse cursor"); }
                else if (set.Unavailable) { warnings.Add("Mouse cursor was saved, but OBS could not be reached: " + set.Message); break; }
                else if (set.Code != 600) warnings.Add("Mouse cursor was saved, but the " + target[0] + " could not be updated: " + set.Message);
            }
            return new JObject { ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static JObject SetDisplayCaptureCursorLive(JObject settings)
        {
            var set = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject
            {
                ["inputName"] = "Display Capture",
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

                    if (title.Length > 160) title = title.Substring(0, 160);
                    if (className.Length > 120) className = className.Substring(0, 120);
                    if (exe.Length > 96) exe = exe.Substring(0, 96);
                    string token = NewObsWindowToken(title, className, exe);
                    if (!seen.Add(token)) return true;

                    candidates.Add(new JObject
                    {
                        ["value"] = token, ["token"] = token, ["label"] = "[" + exe + "]: " + title,
                        ["blurb"] = className, ["title"] = title, ["className"] = className, ["exeName"] = exe,
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
                if (!hasSaved)
                {
                    try
                    {
                        var saved = ConvertFromObsWindowToken(savedWindow);
                        items.Insert(0, new JObject
                        {
                            ["value"] = saved["token"], ["token"] = saved["token"], ["label"] = saved["label"],
                            ["blurb"] = "Saved selection", ["title"] = saved["title"], ["className"] = saved["className"], ["exeName"] = saved["exeName"],
                        });
                    }
                    catch (InvalidOperationException)
                    {
                        items.Insert(0, new JObject { ["value"] = savedWindow, ["label"] = GetWindowLabelFromToken(savedWindow), ["blurb"] = "Saved selection" });
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
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Screenshare capture was saved, but OBS scene lookup failed: " + scene["message"]) };
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Screenshare capture was saved, but OBS scene items could not be read: " + itemsResult["message"]) };
            var items = itemsResult["items"] as JArray;

            var gameInputSettings = GetGameCaptureInputSettings(settings);
            var displayEnsure = EnsureInputSceneItem(sceneName, "Display Capture", "monitor_capture", DisplayCaptureMethodSettings(), useDesktop);
            if (displayEnsure["ok"]?.Value<bool>() != true) warnings.Add("Could not prepare Display Capture: " + displayEnsure["message"]);
            var display = displayEnsure["ok"]?.Value<bool>() == true ? (JObject)displayEnsure["item"] : FindSceneItem(items, "Display Capture");
            var gameEnsure = EnsureInputSceneItem(sceneName, "Game Capture", "game_capture", gameInputSettings, useGameCapture);
            if (gameEnsure["ok"]?.Value<bool>() != true) warnings.Add("Could not prepare Game Capture: " + gameEnsure["message"]);
            var game = gameEnsure["ok"]?.Value<bool>() == true ? (JObject)gameEnsure["item"] : FindSceneItem(items, "Game Capture");

            var windowSettings = GetWindowCaptureInputSettings(settings);
            // in automatic mode the capture switch script picks the window; writing the specific game window here pointed the capture at whatever that setting last held while the script still believed it showed its game
            if (mode == "hybrid_auto") windowSettings.Remove("window");
            var windowEnsure = EnsureInputSceneItem(sceneName, "Window Capture", "window_capture", windowSettings, useWindowCapture);
            if (windowEnsure["ok"]?.Value<bool>() != true) warnings.Add("Could not prepare Window Capture: " + windowEnsure["message"]);
            itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() == true) items = itemsResult["items"] as JArray;
            var window = FindSceneItem(items, "Window Capture");

            if (!useDesktop)
            {
                var hideDesktop = SetSceneItemEnabled(sceneName, display, false);
                if (hideDesktop["ok"]?.Value<bool>() != true) warnings.Add("Could not hide Display Capture: " + hideDesktop["message"]);
            }

            var cursorResult = SetDisplayCaptureCursorLive(settings);
            if (cursorResult["ok"]?.Value<bool>() != true) warnings.Add("Could not update the Display Capture cursor: " + cursorResult["message"]);

            var setGame = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = "Game Capture", ["inputSettings"] = gameInputSettings, ["overlay"] = true }, 3000);
            if (!setGame.Ok) warnings.Add("Could not update Game Capture settings: " + setGame.Message);

            var setWindow = ObsWebSocket.InvokeRequest("SetInputSettings", new JObject { ["inputName"] = "Window Capture", ["inputSettings"] = windowSettings, ["overlay"] = true }, 3000);
            if (!setWindow.Ok) warnings.Add("Could not update Window Capture settings: " + setWindow.Message);

            if (useWindowCapture && string.IsNullOrWhiteSpace(settings["screenshareGameWindow"]?.Value<string>()))
                warnings.Add("Specific game capture was selected without a game window.");

            var enableGame = SetSceneItemEnabled(sceneName, game, useGameCapture);
            if (enableGame["ok"]?.Value<bool>() != true) warnings.Add("Could not toggle Game Capture: " + enableGame["message"]);
            var enableWindow = SetSceneItemEnabled(sceneName, window, useWindowCapture);
            if (enableWindow["ok"]?.Value<bool>() != true) warnings.Add("Could not toggle Window Capture: " + enableWindow["message"]);
            if (useDesktop)
            {
                var enableDisplay = SetSceneItemEnabled(sceneName, display, true);
                if (enableDisplay["ok"]?.Value<bool>() != true) warnings.Add("Could not show Display Capture: " + enableDisplay["message"]);
            }

            var order = SetWindowCaptureSceneOrder(sceneName, items);
            if (order["ok"]?.Value<bool>() != true) warnings.Add("Could not position Window Capture under overlays: " + order["message"]);

            if (preset != null)
            {
                foreach (var captureName in new[] { "Display Capture", "Window Capture", "Game Capture" })
                {
                    var capture = FindSceneItem(items, captureName);
                    var captureTransform = MainCaptureTransform(captureName, preset);
                    var captureResult = SetSceneItemTransform(sceneName, capture, captureTransform);
                    if (captureResult["ok"]?.Value<bool>() != true) warnings.Add("Could not fit " + captureName + " to canvas: " + captureResult["message"]);
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
                if (!File.Exists(path)) return new JObject { ["ok"] = false, ["message"] = "OBS scene collection file was not found." };
                var data = JObject.Parse(File.ReadAllText(path));
                var sources = data["sources"] as JArray;
                if (sources == null) throw new InvalidOperationException("OBS scene collection has no sources list.");
                // do NOT self-assign data["sources"] / data["groups"] here -- in the bundled Newtonsoft that detaches the array from `data`, so every edit below lands on an orphan and the file is written from the untouched original (symptom: "switched overlay, scene still shows the old one"). they are re-attached once, right before serialize.
                var groups = data["groups"] as JArray ?? new JArray();
                RemoveBongoStandby(sources, groups);

                string overlayStyle = settings["overlayStyle"]?.Value<string>() ?? "";
                bool useInputOverlay = overlayStyle == "input_overlay";
                bool useBongo = overlayStyle == "bongo_cat";
                bool useMotionBlur = settings["motionBlurEnabled"]?.Value<bool>() ?? false;
                double motionBlurStrength = MotionBlurStrength(settings);
                var inputSources = new Dictionary<string, InputOverlaySourceRef>();
                var inputSourceNames = new[] { "WASD Overlay", "Mouse Overlay" };
                JObject inputGroup = null;
                foreach (var group in groups)
                {
                    if (group["name"]?.Value<string>() == "Group") { inputGroup = (JObject)group; break; }
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
                    if (source["name"]?.Value<string>() == "Bongo Cat Overlay") { bongoSource = (JObject)source; break; }
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
                    if (name == "Window Capture" || uuid == "edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c") { windowCaptureSource = (JObject)source; break; }
                }
                string windowCaptureUuid = windowCaptureSource?["uuid"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(windowCaptureUuid)) windowCaptureUuid = "edb2d9d4-7b53-4f3a-a760-61cd03ce9b6c";

                if (useInputOverlay)
                {
                    var keyboardFiles = InputOverlayMemberFiles("WASD Overlay", settings);
                    var mouseFiles = InputOverlayMemberFiles("Mouse Overlay", settings);
                    var inputSpecs = new[]
                    {
                        new { name = "WASD Overlay", image = keyboardFiles?.Sheet ?? "", layout = keyboardFiles?.Json ?? "" },
                        new { name = "Mouse Overlay", image = mouseFiles?.Sheet ?? "", layout = mouseFiles?.Json ?? "" },
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
                    RemoveJsonListWhere(groups, group => group["name"]?.Value<string>() == "Group" || group["uuid"]?.Value<string>() == inputGroupUuid);
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
                        source["name"]?.Value<string>() == "Bongo Cat Overlay" ||
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
                            return inputSourceNames.Contains(name) || name == "Group" || sourceUuid == inputGroupUuid || existingInputUuids.Contains(sourceUuid);
                        });
                    }
                    if (!useBongo)
                    {
                        string bongoUuidCapture = bongoUuid;
                        RemoveJsonListWhere(items, item =>
                        {
                            string name = item["name"]?.Value<string>() ?? "";
                            string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                            return name == "Bongo Cat Overlay" || (!string.IsNullOrWhiteSpace(bongoUuidCapture) && sourceUuid == bongoUuidCapture);
                        });
                    }

                    bool foundBongoItem = false;
                    var foundInputItems = new Dictionary<string, bool> { ["WASD Overlay"] = false, ["Mouse Overlay"] = false };
                    bool foundInputGroupItem = false;
                    bool foundWindowCaptureItem = false;
                    bool windowCaptureVisible = settings["screenshareCaptureMode"]?.Value<string>() == "game_window";
                    foreach (var itemToken in items)
                    {
                        var item = (JObject)itemToken;
                        string name = item["name"]?.Value<string>() ?? "";
                        string sourceUuid = item["source_uuid"]?.Value<string>() ?? "";
                        if (name == "Display Capture" || name == "Game Capture")
                        {
                            SetMainCaptureSceneItemJson(item, name, preset);
                        }
                        else if (name == "Window Capture" || sourceUuid == windowCaptureUuid)
                        {
                            SetWindowCaptureSceneItemJson(item, windowCaptureUuid, preset, windowCaptureVisible);
                            foundWindowCaptureItem = true;
                        }
                        else if (name == "WASD Overlay" || name == "Mouse Overlay")
                        {
                            string inputUuid = "";
                            if (inputSources.TryGetValue(name, out var srcRef)) inputUuid = srcRef.Uuid;
                            if (string.IsNullOrWhiteSpace(inputUuid)) inputUuid = sourceUuid;
                            SetInputOverlaySceneItemJson(item, name, preset, useInputOverlay, inputUuid, true, settings);
                            foundInputItems[name] = true;
                            if (inputSources.TryGetValue(name, out var srcRef2)) srcRef2.Id = item["id"]?.Value<int>() ?? 0;
                        }
                        else if (name == "Group" || sourceUuid == inputGroupUuid)
                        {
                            SetInputOverlayGroupSceneItemJson(item, inputGroupUuid, preset, useInputOverlay, settings);
                            foundInputGroupItem = true;
                        }
                        else if (name == "Bongo Cat Overlay" || sourceUuid == bongoUuid)
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
                            if (items[i]["name"]?.Value<string>() == "Display Capture") { insertAt = i + 1; break; }
                        }
                        items.Insert(insertAt, newItem);
                        sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                    }
                    MoveJsonSceneItemAfter(items, "Window Capture", "Display Capture");
                    if (useBongo && !foundBongoItem)
                    {
                        var newItem = new JObject { ["id"] = GetNextJsonSceneItemId(items) };
                        SetBongoSceneItemJson(newItem, bongoUuid, preset, true, settings);
                        items.Add(newItem);
                        sceneSettings["id_counter"] = GetNextJsonSceneItemId(items);
                    }
                    if (useInputOverlay)
                    {
                        foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
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
                try { AppConfig.WriteUtf8(path + ".replaykit-pending", json); }
                catch (Exception ex) { Log.Write("SetOverlaySceneFile: could not stage pending scene: " + ex.Message); }
                return new JObject { ["ok"] = true, ["path"] = path };
            }
            catch (Exception ex)
            {
                return new JObject { ["ok"] = false, ["message"] = ex.Message };
            }
        }

        // -- overlay candidate-name resolution + scale/opacity targeting: given the currently-selected overlay style, works out which live scene item(s) a scale or opacity change should target -- preferring whatever is actually visible right now over what the style nominally implies, so a live tweak lands on the right source even mid-transition or if the scene is in an unexpected state.

        internal static string[] GetOverlaySourceNames(string overlayStyle)
        {
            if (overlayStyle == "bongo_cat") return new[] { "Bongo Cat Overlay" };
            if (overlayStyle == "input_overlay") return new[] { "Group", "WASD Overlay", "Mouse Overlay" };
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
            var group = FindSceneItem(items, "Group");
            if (group != null && (!visibleOnly || TestSceneItemVisible(group))) return new List<string> { "Group" };
            var targets = new List<string>();
            foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
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
                if (FindSceneItem(items, "Bongo Cat Overlay") != null) return new List<string> { "Bongo Cat Overlay" };
            }
            if (overlayStyle == "input_overlay")
            {
                var inputTargets = GetExistingInputOverlayScaleTargets(items, false);
                if (inputTargets.Count > 0) return inputTargets;
            }

            var bongo = FindSceneItem(items, "Bongo Cat Overlay");
            if (bongo != null && TestSceneItemVisible(bongo)) return new List<string> { "Bongo Cat Overlay" };
            var visibleInputTargets = GetExistingInputOverlayScaleTargets(items, true);
            if (visibleInputTargets.Count > 0) return visibleInputTargets;
            if (bongo != null) return new List<string> { "Bongo Cat Overlay" };
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
                foreach (var name in new[] { "WASD Overlay", "Mouse Overlay" })
                {
                    if (TestSourceExistsForFilters(name)) fallback.Add(name);
                }
                if (fallback.Count > 0) return fallback;
            }
            if (overlayStyle == "bongo_cat" && TestSourceExistsForFilters("Bongo Cat Overlay")) return new List<string> { "Bongo Cat Overlay" };
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
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay size was saved, but OBS scene lookup failed: " + scene["message"]) };
            string sceneName = scene["name"]?.Value<string>();
            var itemsResult = GetSceneItems(sceneName);
            if (itemsResult["ok"]?.Value<bool>() != true)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay size was saved, but OBS scene items could not be read: " + itemsResult["message"]) };

            var preset = GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
            double scaleRatio = nextScale / previousScale;
            var items = itemsResult["items"] as JArray;
            foreach (var name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
            {
                var item = FindSceneItem(items, name);
                if (item == null) continue;
                var r = SetSceneItemScaledFromCurrent(sceneName, item, scaleRatio, preset);
                if (r["ok"]?.Value<bool>() == true) { if (r["skipped"]?.Value<bool>() != true) applied.Add(name + " size"); }
                else warnings.Add("Could not resize " + name + ": " + r["message"]);
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        // Mirror along the rotated local axis while keeping the same center.
        internal static JObject MirrorTransformInPlace(JObject current, string overlayStyle, JObject settings)
        {
            OverlayBoxSize(overlayStyle, settings, out double w, out double h);
            return OverlayRotation.Mirror(current, w, h);
        }

        // the unscaled box of the item of an overlay style: the 1280x768 bongo frame, or the group the picked wasd layouts make
        internal static void OverlayBoxSize(string overlayStyle, JObject settings, out double width, out double height)
        {
            if (overlayStyle == "bongo_cat") { width = 1280; height = 768; return; }
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
                if (current["ok"]?.Value<bool>() != true || !(current["transform"] is JObject transform)) { warnings.Add("Could not read " + name + " rotation."); continue; }
                var result = SetSceneItemTransform(sceneName, item, OverlayRotation.Rotate(transform, settings["overlayRotation"]?.Value<int>() ?? 0));
                if (result["ok"]?.Value<bool>() == true) applied.Add(name + " rotation");
                else warnings.Add("Could not rotate " + name + ": " + result["message"]);
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
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay flip was saved, but OBS scene lookup failed: " + scene["message"]) };
            string sceneName = scene["name"]?.Value<string>();
            var items = GetSceneItems(sceneName)["items"] as JArray;
            string style = settings["overlayStyle"]?.Value<string>() ?? "";
            string itemName = style == "bongo_cat" ? "Bongo Cat Overlay" : style == "input_overlay" ? "Group" : null;
            if (itemName == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            var item = FindSceneItem(items, itemName);
            if (item == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            var cur = GetSceneItemTransformLive(sceneName, item);
            if (cur["ok"]?.Value<bool>() != true || cur["transform"] == null || cur["transform"].Type == JTokenType.Null)
                return new JObject { ["ok"] = false, ["applied"] = new JArray(applied), ["warnings"] = new JArray("Overlay flip was saved, but " + itemName + "'s current position could not be read: " + cur["message"]) };
            var r = SetSceneItemTransform(sceneName, item, MirrorTransformInPlace(cur["transform"] as JObject, style, settings));
            if (r["ok"]?.Value<bool>() == true) applied.Add(itemName + " flip");
            else warnings.Add("Could not flip " + itemName + ": " + r["message"]);
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
                else warnings.Add("Could not set " + name + " opacity: " + r["message"]);
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
                if (rotation["ok"]?.Value<bool>() != true && rotation["warnings"] == null) warnings.Add("Could not rotate overlay: " + rotation["message"]);
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

        // -- overlay live-preview: lets the settings dock preview overlay scale/opacity/color changes live (e.g. while dragging a slider) without committing them to disk until the user releases or confirms, by snapshotting the pre-preview transform and filter state once per session and restoring it on cancel.

        internal static bool TestOverlayColorRequest(JObject incoming) =>
            incoming != null && (incoming["overlayOpacity"] != null || incoming["overlayHueShift"] != null || incoming["overlayColorMultiply"] != null || incoming["overlayColorAdd"] != null);

        // the in-memory preview snapshot mirrored to disk, so a session that dies mid-preview (obs crash / end-task before commit or cancel) can still be reverted on the next helper start instead of leaking the un-applied geometry into obss saved scene.
        internal static string OverlayPreviewBaselinePath => Path.Combine(Constants.SCRATCH_DIR, "overlay_preview_baseline.json");

        internal static void TryDeleteOverlayPreviewBaseline()
        {
            try { File.Delete(OverlayPreviewBaselinePath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // holds OverlayPreviewLock across the whole capture, including the obs-websocket round trips, not just the null-check -- this only runs once per preview session, and a lost race here would mean two callers each capture their own "baseline", whichever stores last silently winning as the revert target. worth the extra lock-hold time given how rarely this is actually contended.
        internal static JObject GetOverlayPreviewState(JObject settings)
        {
            if (Server.State.ReplaykitOverlayPreviewState != null) return Server.State.ReplaykitOverlayPreviewState;

            lock (Server.State.OverlayPreviewLock)
            {
                if (Server.State.ReplaykitOverlayPreviewState != null) return Server.State.ReplaykitOverlayPreviewState;

                var baselineSettings = (JObject)settings.DeepClone();
                var liveOpacity = GetLiveOverlayOpacityPercent(baselineSettings);
                if (liveOpacity["ok"]?.Value<bool>() == true && liveOpacity["opacity"]?.Type != JTokenType.Null)
                    baselineSettings["overlayOpacity"] = liveOpacity["opacity"];

                var state = new JObject
                {
                    ["settings"] = baselineSettings,
                    ["preset"] = GetPresetSpec(baselineSettings["recordingPreset"]?.Value<string>() ?? "", baselineSettings),
                    ["sceneName"] = "",
                    ["transforms"] = new JObject(),
                    ["opacityFilters"] = new JArray(),
                };

                var scene = GetSceneName();
                if (scene["ok"]?.Value<bool>() == true)
                {
                    state["sceneName"] = scene["name"];
                    var itemsResult = GetSceneItems(scene["name"]?.Value<string>());
                    if (itemsResult["ok"]?.Value<bool>() == true)
                    {
                        var items = itemsResult["items"] as JArray;
                        var transforms = (JObject)state["transforms"];
                        foreach (var name in GetOverlayScaleSceneItemNames(items, settings["overlayStyle"]?.Value<string>() ?? ""))
                        {
                            var item = FindSceneItem(items, name);
                            if (item == null) continue;
                            var current = GetSceneItemTransformLive(scene["name"]?.Value<string>(), item);
                            if (current["ok"]?.Value<bool>() == true && current["transform"] != null && current["transform"].Type != JTokenType.Null)
                                transforms[name] = new JObject { ["item"] = item, ["transform"] = current["transform"] };
                        }
                    }
                }

                var opacityFilters = new JArray();
                foreach (var name in GetOverlayOpacitySourceTargets(baselineSettings["overlayStyle"]?.Value<string>() ?? ""))
                {
                    var filters = GetSourceFilterListLive(name);
                    if (filters["ok"]?.Value<bool>() != true) continue;
                    var match = GetOverlayOpacityLiveFilterInfo(filters["filters"] as JArray);
                    if (match["found"]?.Value<bool>() == true)
                    {
                        opacityFilters.Add(new JObject
                        {
                            ["sourceName"] = name, ["found"] = true, ["filterName"] = match["name"],
                            ["settings"] = ((JObject)(match["settings"] ?? new JObject())).DeepClone(), ["enabled"] = match["enabled"],
                        });
                    }
                    else
                    {
                        opacityFilters.Add(new JObject { ["sourceName"] = name, ["found"] = false, ["filterName"] = "", ["settings"] = new JObject(), ["enabled"] = false });
                    }
                }
                state["opacityFilters"] = opacityFilters;

                Server.State.ReplaykitOverlayPreviewState = state;
                try { AppConfig.WriteUtf8(OverlayPreviewBaselinePath, state.ToString(Formatting.None)); }
                catch (Exception ex) { Log.Write("GetOverlayPreviewState: could not persist baseline: " + ex.Message); }
                return state;
            }
        }

        internal static void ClearOverlayPreviewState()
        {
            lock (Server.State.OverlayPreviewLock) { Server.State.ReplaykitOverlayPreviewState = null; }
            TryDeleteOverlayPreviewBaseline();
        }

        internal static long? GetOverlayPreviewRevision(JObject incoming)
        {
            var raw = incoming?["overlayPreviewRevision"];
            if (raw == null) return null;
            incoming.Remove("overlayPreviewRevision");
            long revision;
            try { revision = raw.Value<long>(); }
            catch (FormatException) { throw new InvalidOperationException("Invalid overlay preview revision."); }
            catch (InvalidCastException) { throw new InvalidOperationException("Invalid overlay preview revision."); }
            if (revision < 1) throw new InvalidOperationException("Invalid overlay preview revision.");
            return revision;
        }

        // Size is relative to the snapshot; flip and rotation preserve the current center. The snapshot remains the cancel target.
        internal static JObject ApplyOverlayGeometryPreviewLive(JObject preview, JObject settings)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            var previewSettings = preview["settings"] as JObject;
            string sceneName = preview["sceneName"]?.Value<string>();
            var transforms = preview["transforms"] as JObject ?? new JObject();
            if (string.IsNullOrWhiteSpace(sceneName))
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            bool scaleChanged = previewSettings["overlayScale"]?.Value<int>() != settings["overlayScale"]?.Value<int>();
            bool scaleTouched = preview["scaleTouched"]?.Value<bool>() ?? false;
            if (scaleChanged) { preview["scaleTouched"] = true; scaleTouched = true; }

            bool wantMirror = (previewSettings["overlayFlipH"]?.Value<bool>() ?? false) != (settings["overlayFlipH"]?.Value<bool>() ?? false);
            bool mirrorApplied = preview["mirrorApplied"]?.Value<bool>() ?? false;
            bool flipTransition = wantMirror != mirrorApplied;
            bool rotationTouched = preview["rotationTouched"]?.Value<bool>() ?? false;
            if (previewSettings["overlayRotation"]?.Value<int>() != settings["overlayRotation"]?.Value<int>())
            {
                preview["rotationTouched"] = true;
                rotationTouched = true;
            }

            if (!scaleTouched && !flipTransition && !rotationTouched)
                return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };

            double baseScale = OverlayScaleFactor(previewSettings);
            double nextScale = OverlayScaleFactor(settings);
            if (baseScale <= 0.0) baseScale = 1.0;
            double scaleRatio = nextScale / baseScale;
            var preset = preview["preset"] as JObject ?? GetPresetSpec(settings["recordingPreset"]?.Value<string>() ?? "", settings);
            string style = settings["overlayStyle"]?.Value<string>() ?? "";

            // size: snapshot-relative, over whatever items the snapshot captured
            if (scaleTouched)
            {
                foreach (var prop in transforms.Properties().ToList())
                {
                    var entry = (JObject)prop.Value;
                    var scaled = GetScaledTransformFromCurrent(entry["transform"], scaleRatio, preset);
                    if (mirrorApplied) // keep the current mirror while resizing; the flip step below toggles it if needed
                    {
                        OverlayBoxSize(style, settings, out double boxW, out double boxH);
                        double sw = GetDoubleValue(entry["transform"], "sourceWidth", 0.0);
                        if (sw <= 0.0) sw = boxW;
                        var basis = (JObject)entry["transform"].DeepClone();
                        basis.Merge(scaled);
                        var size = OverlayRotation.Rectangle((JObject)entry["transform"]);
                        basis["width"] = size["w"].Value<double>() * scaleRatio;
                        basis["height"] = size["h"].Value<double>() * scaleRatio;
                        scaled = OverlayRotation.Mirror(basis, sw, boxH);
                    }
                    var rs = SetSceneItemTransform(sceneName, entry["item"] as JObject, scaled);
                    if (rs["ok"]?.Value<bool>() == true) { if (!flipTransition) applied.Add(prop.Name + " size"); }
                    else warnings.Add("Could not preview " + prop.Name + " size: " + rs["message"]);
                }
            }

            // flip: mirror the flip targets CURRENT transform in place -- looked up by style, independent of the snapshots transforms map (which can miss the item), so it never repositions to the canonical corner
            if (flipTransition)
            {
                string itemName = style == "bongo_cat" ? "Bongo Cat Overlay" : style == "input_overlay" ? "Group" : null;
                var item = itemName == null ? null : ((transforms[itemName] as JObject)?["item"] as JObject ?? FindSceneItem(GetSceneItems(sceneName)["items"] as JArray, itemName));
                if (item != null)
                {
                    var cur = GetSceneItemTransformLive(sceneName, item);
                    if (cur["ok"]?.Value<bool>() == true && cur["transform"] != null && cur["transform"].Type != JTokenType.Null)
                    {
                        // make sure the un-mirrored transform is the snapshots revert target (adopt a manual reposition since the snapshot; add the entry if the initial capture missed it) so cancel un-flips cleanly.
                            if (!mirrorApplied && !scaleTouched && !rotationTouched)
                        {
                            if (transforms[itemName] is JObject snapEntry) snapEntry["transform"] = cur["transform"];
                            else transforms[itemName] = new JObject { ["item"] = item, ["transform"] = cur["transform"] };
                        }
                        var rf = SetSceneItemTransform(sceneName, item, MirrorTransformInPlace(cur["transform"] as JObject, style, settings));
                        if (rf["ok"]?.Value<bool>() == true) applied.Add(itemName + " flip");
                        else warnings.Add("Could not preview " + itemName + " flip: " + rf["message"]);
                    }
                    else warnings.Add("Could not read " + itemName + " to flip: " + cur["message"]);
                }
                preview["mirrorApplied"] = wantMirror;
            }
            if (rotationTouched)
            {
                var rotation = ApplyOverlayRotationLive(settings);
                applied.AddRange((rotation["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((rotation["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                if (rotation["ok"]?.Value<bool>() != true && rotation["warnings"] == null) warnings.Add("Could not preview overlay rotation: " + rotation["message"]);
            }
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        internal static JObject RestoreOverlayPreviewLive(JObject preview)
        {
            var warnings = new List<string>();
            var applied = new List<string>();
            if (preview == null) return new JObject { ["ok"] = true, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
            string sceneName = preview["sceneName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                var transforms = preview["transforms"] as JObject ?? new JObject();
                foreach (var prop in transforms.Properties().ToList())
                {
                    var entry = (JObject)prop.Value;
                    var r = SetSceneItemTransform(sceneName, entry["item"] as JObject, entry["transform"] as JObject);
                    if (r["ok"]?.Value<bool>() == true) applied.Add(prop.Name + " size restored");
                    else warnings.Add("Could not restore " + prop.Name + ": " + r["message"]);
                }
            }
            bool restoredExactOpacity = false;
            var opacityFilters = preview["opacityFilters"] as JArray ?? new JArray();
            foreach (var entryToken in opacityFilters)
            {
                var entry = (JObject)entryToken;
                string sourceName = entry["sourceName"]?.Value<string>() ?? "";
                if (string.IsNullOrWhiteSpace(sourceName)) continue;
                if (entry["found"]?.Value<bool>() == true)
                {
                    string filterName = entry["filterName"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(filterName)) continue;
                    var set = ObsWebSocket.InvokeRequest("SetSourceFilterSettings", new JObject
                    {
                        ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterSettings"] = entry["settings"], ["overlay"] = true,
                    }, 3000);
                    if (set.Ok)
                    {
                        var enable = ObsWebSocket.InvokeRequest("SetSourceFilterEnabled", new JObject
                        {
                            ["sourceName"] = sourceName, ["filterName"] = filterName, ["filterEnabled"] = entry["enabled"]?.Value<bool>() ?? false,
                        }, 3000);
                        if (enable.Ok) { applied.Add(sourceName + " opacity restored"); restoredExactOpacity = true; }
                        else warnings.Add("Could not restore " + sourceName + " opacity filter enabled state: " + enable.Message);
                    }
                    else
                    {
                        warnings.Add("Could not restore " + sourceName + " opacity filter settings: " + set.Message);
                    }
                }
                else
                {
                    var filters = GetSourceFilterListLive(sourceName);
                    if (filters["ok"]?.Value<bool>() != true) continue;
                    foreach (var filterToken in filters["filters"] as JArray ?? new JArray())
                    {
                        if (filterToken["filterName"]?.Value<string>() != "ReplayKit Overlay Opacity") continue;
                        var remove = ObsWebSocket.InvokeRequest("RemoveSourceFilter", new JObject { ["sourceName"] = sourceName, ["filterName"] = "ReplayKit Overlay Opacity" }, 3000);
                        if (remove.Ok) { applied.Add(sourceName + " opacity restored"); restoredExactOpacity = true; }
                        else warnings.Add("Could not remove preview opacity filter from " + sourceName + ": " + remove.Message);
                        break;
                    }
                }
            }
            if (!restoredExactOpacity)
            {
                var opacity = ApplyOverlayOpacityForStyleLive(preview["settings"] as JObject);
                applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
            }
            ClearOverlayPreviewState();
            return new JObject { ["ok"] = warnings.Count == 0, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings) };
        }

        public static JObject OverlayPreviewFromRequest(string body, string mode = "preview")
        {
            lock (SettingsStore.Gate)
            {
                if (mode == "cancel")
                {
                    // read the snapshot under a brief lock, then release before RestoreOverlayPreviewLive does its own (possibly several) obs-websocket round trips -- a cancel and a fresh preview tick landing in the same tight window can still race on which baseline is "current", but holding the lock across the whole restore would block every other overlay-preview op for that entire duration, which is worse than this narrow, self-correcting edge case.
                    JObject previewSnapshot;
                    lock (Server.State.OverlayPreviewLock) { previewSnapshot = Server.State.ReplaykitOverlayPreviewState; }
                    var live = RestoreOverlayPreviewLive(previewSnapshot);
                    return new JObject
                    {
                        ["ok"] = true, ["settings"] = ReadSettings(), ["applied"] = live["applied"], ["warnings"] = live["warnings"],
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }
                if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("Missing overlay preview body.");
                var incoming = JObject.Parse(body);
                long? previewRevision = GetOverlayPreviewRevision(incoming);
                if (incoming.Count < 1) throw new InvalidOperationException("Missing overlay preview setting.");
                var allowedKeys = new HashSet<string> { "overlayOpacity", "overlayScale", "overlayRotation", "overlayFlipH", "overlayHueShift", "overlayColorMultiply", "overlayColorAdd" };
                foreach (var prop in incoming.Properties())
                {
                    if (!allowedKeys.Contains(prop.Name)) throw new InvalidOperationException("Unknown overlay preview setting: " + prop.Name);
                }
                if (mode == "preview" && previewRevision != null)
                {
                    // read-compare-write on the revision has to be one atomic op -- otherwise two preview ticks racing here could both pass the check and both think they own the latest revision.
                    bool stale = false;
                    lock (Server.State.OverlayPreviewLock)
                    {
                        if (Server.State.ReplaykitOverlayPreviewRevision >= previewRevision.Value) stale = true;
                        else Server.State.ReplaykitOverlayPreviewRevision = previewRevision.Value;
                    }
                    if (stale)
                    {
                        return new JObject
                        {
                            ["ok"] = true, ["settings"] = Normalize(ReadSettings()), ["applied"] = new JArray(), ["warnings"] = new JArray(),
                            ["skipped"] = true, ["restartRequired"] = false, ["restartReason"] = "",
                        };
                    }
                }

                var currentJ = ReadSettings();
                var previous = Normalize(currentJ);
                var preview = GetOverlayPreviewState(previous);
                foreach (var prop in incoming.Properties()) currentJ[prop.Name] = prop.Value;
                var settingsJ = Normalize(currentJ);
                bool forceColorSync = TestOverlayColorRequest(incoming);
                var previewSettings = preview["settings"] as JObject;

                if (mode == "commit")
                {
                    WriteSettings(settingsJ);
                    var warnings = new List<string>();
                    var applied = new List<string>();
                    var geometryCommit = ApplyOverlayGeometryPreviewLive(preview, settingsJ);
                    applied.AddRange((geometryCommit["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    warnings.AddRange((geometryCommit["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    bool colorChangedCommit = previewSettings["overlayOpacity"]?.Value<int>() != settingsJ["overlayOpacity"]?.Value<int>() ||
                        previewSettings["overlayHueShift"]?.Value<double>() != settingsJ["overlayHueShift"]?.Value<double>() ||
                        previewSettings["overlayColorMultiply"]?.Value<string>() != settingsJ["overlayColorMultiply"]?.Value<string>() ||
                        previewSettings["overlayColorAdd"]?.Value<string>() != settingsJ["overlayColorAdd"]?.Value<string>();
                    if (colorChangedCommit || forceColorSync)
                    {
                        var opacity = ApplyOverlayOpacityForStyleLive(settingsJ);
                        applied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                        warnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    }
                    ClearOverlayPreviewState();
                    return new JObject
                    {
                        ["ok"] = warnings.Count == 0, ["settings"] = settingsJ, ["applied"] = new JArray(applied), ["warnings"] = new JArray(warnings),
                        ["restartRequired"] = false, ["restartReason"] = "",
                    };
                }

                var previewWarnings = new List<string>();
                var previewApplied = new List<string>();
                var geometryPreview = ApplyOverlayGeometryPreviewLive(preview, settingsJ);
                previewApplied.AddRange((geometryPreview["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                previewWarnings.AddRange((geometryPreview["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                bool colorChanged = previewSettings["overlayOpacity"]?.Value<int>() != settingsJ["overlayOpacity"]?.Value<int>() ||
                    previewSettings["overlayHueShift"]?.Value<double>() != settingsJ["overlayHueShift"]?.Value<double>() ||
                    previewSettings["overlayColorMultiply"]?.Value<string>() != settingsJ["overlayColorMultiply"]?.Value<string>() ||
                    previewSettings["overlayColorAdd"]?.Value<string>() != settingsJ["overlayColorAdd"]?.Value<string>();
                if (colorChanged || forceColorSync)
                {
                    var opacity = ApplyOverlayOpacityForStyleLive(settingsJ);
                    previewApplied.AddRange((opacity["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                    previewWarnings.AddRange((opacity["warnings"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>());
                }

                return new JObject
                {
                    ["ok"] = true, ["settings"] = settingsJ, ["applied"] = new JArray(previewApplied), ["warnings"] = new JArray(previewWarnings),
                    ["restartRequired"] = false, ["restartReason"] = "",
                };
            }
        }

        // consumes a persisted preview baseline left by a session that died before commit or cancel -- once obs is answering again, push the pre-preview transforms + opacity filters back so an un-applied live preview cant survive into obss saved scene. runs before the http accept loop, so nothing can be mid-preview yet.
        public static void RevertAbandonedOverlayPreviewAtStartup()
        {
            if (!File.Exists(OverlayPreviewBaselinePath)) return;

            string raw;
            try { raw = File.ReadAllText(OverlayPreviewBaselinePath); }
            catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: cannot read baseline, leaving it: " + ex.Message); return; }

            // delete the file before acting on it -- if we cant delete it we must NOT run the revert, otherwise a baseline that keeps coming back (locked/read-only scratch dir) would re-stomp the users applied overlay on every single launch. strictly one shot.
            try { File.Delete(OverlayPreviewBaselinePath); }
            catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: baseline undeletable, skipping revert: " + ex.Message); return; }

            JObject baseline;
            try { baseline = JObject.Parse(raw); }
            catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: unparseable baseline discarded: " + ex.Message); return; }

            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 30; i++)
                    {
                        // a real preview session started while we were waiting -- it owns the overlay now, dont clobber it with a stale baseline.
                        if (Server.State.ReplaykitOverlayPreviewState != null) return;
                        if (GetSceneName()["ok"]?.Value<bool>() == true)
                        {
                            var live = RestoreOverlayPreviewLive(baseline);
                            Log.Write("RevertAbandonedOverlayPreview: " + string.Join(", ", (live["applied"] as JArray)?.Select(t => t.Value<string>()) ?? Enumerable.Empty<string>()));
                            return;
                        }
                        Thread.Sleep(2000);
                    }
                    Log.Write("RevertAbandonedOverlayPreview: OBS not reachable in time; abandoned-preview revert skipped.");
                }
                catch (Exception ex) { Log.Write("RevertAbandonedOverlayPreview: " + ex.Message); }
            });
        }

    }
}
