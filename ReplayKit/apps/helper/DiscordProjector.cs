using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // discord "share preview" thru the share window (ShareWindow.cs), the replaykit process that owns the window discord streams and outlives obs: starts and parks it hidden-but-discoverable beside its monitor (so discords own screen picker lists it) when enabled, or shows it centered for a user-triggered visual check ("inspect"), or ends it when disabled, and while share preview is off no share window may run. every start/park/end sequence runs under ProjectorLock so Repark/Inspect/Disable/the keep-alive tick can never interleave with each other. ProjectorNative.cs is the win32 layer underneath.
    internal static class DiscordProjector
    {
        private static volatile bool disablePending;
        // 0 waits for OBS, 1 is queued or running, 2 has completed the startup attempt.
        private static int startupState;

        private sealed class ParkedWindow
        {
            public long Hwnd;
            public string Title;
            public ParkedRect Rect;
        }

        // the window the last successful park left in its corner, so the keep-alive can see between its ticks that it was minimized or moved; null when share preview is off, shown for inspection or a park did not finish
        private static volatile ParkedWindow parkedWindow;

        public static string GetProjectorTitle(JObject settings)
        {
            const string defaultTitle = "OBS ReplayKit Discord Share";
            if (settings == null) return defaultTitle;
            string value = (settings["discord_projector_title_hint"]?.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > ReplayKitShared.SettingLimits.DiscordProjectorTitleHintMaxLength || Regex.IsMatch(value, @"[\x00-\x1F]")) return defaultTitle;
            return value;
        }

        public static string GetProjectorShareTitle(JObject settings) => GetProjectorTitle(settings) + " (Projector - Desktop Audio)";

        private static int GetObjectInt(JToken obj, string key)
        {
            var value = obj?[key];
            if (value == null) return 0;
            return int.TryParse(value.ToString(), out int n) && n > 0 ? n : 0;
        }

        public sealed class SizeResult
        {
            public bool Ok;
            public int Width;
            public int Height;
            public string Source;
            public string Message;
        }

        public static SizeResult GetObsCanvasSize()
        {
            var videoSettings = ObsWebSocket.InvokeRequest("GetVideoSettings", null, 3000);
            if (!videoSettings.Ok) return new SizeResult { Ok = false, Message = Locales.Message("screenshare.obsVideoSettingsUnavailableError", "error", videoSettings.Message) };

            int baseWidth = GetObjectInt(videoSettings.Data, "baseWidth");
            int baseHeight = GetObjectInt(videoSettings.Data, "baseHeight");
            if (baseWidth > 0 && baseHeight > 0) return new SizeResult { Ok = true, Width = baseWidth, Height = baseHeight, Source = "obs_base_canvas" };

            int outputWidth = GetObjectInt(videoSettings.Data, "outputWidth");
            int outputHeight = GetObjectInt(videoSettings.Data, "outputHeight");
            if (outputWidth > 0 && outputHeight > 0) return new SizeResult { Ok = true, Width = outputWidth, Height = outputHeight, Source = "obs_output" };

            return new SizeResult { Ok = false, Message = Locales.Message("screenshare.obsVideoSettingsDidNotInclude") };
        }

        // the size from the settings, else the main monitor, else the canvas of obs when windows names no main monitor
        public static SizeResult ResolveProjectorWindowSize(JObject settings, MonitorWorkArea workArea)
        {
            int width = settings["discord_projector_width"]?.Value<int>() ?? 0;
            int height = settings["discord_projector_height"]?.Value<int>() ?? 0;
            if (width > 0 && height > 0) return new SizeResult { Ok = true, Width = width, Height = height, Source = "settings" };

            int sourceWidth = workArea.HasSourceBounds ? workArea.SourceBoundsWidth : 0;
            int sourceHeight = workArea.HasSourceBounds ? workArea.SourceBoundsHeight : 0;
            if (sourceWidth > 0 && sourceHeight > 0)
            {
                if (width <= 0) width = sourceWidth;
                if (height <= 0) height = sourceHeight;
                return new SizeResult { Ok = true, Width = width, Height = height, Source = "primary_source_monitor" };
            }

            var canvas = GetObsCanvasSize();
            if (canvas.Ok)
            {
                if (width <= 0) width = canvas.Width;
                if (height <= 0) height = canvas.Height;
                return new SizeResult { Ok = true, Width = width, Height = height, Source = canvas.Source };
            }
            return new SizeResult { Ok = false, Message = Locales.Message("screenshare.couldNotResolveAValidProjector") };
        }

        public sealed class ScreenBounds
        {
            public int Left, Top, Right, Bottom;
        }

        public sealed class MonitorWorkArea
        {
            public bool Ok;
            public string Message;
            public int Index, Count, SourceIndex;
            public int Left, Top, Right, Bottom;
            public int BoundsLeft, BoundsTop, BoundsRight, BoundsBottom, BoundsWidth, BoundsHeight;
            public bool HasSourceBounds;
            public int SourceBoundsLeft, SourceBoundsTop, SourceBoundsRight, SourceBoundsBottom, SourceBoundsWidth, SourceBoundsHeight;
            public List<ScreenBounds> AllScreenBounds = new List<ScreenBounds>();
        }

        public static MonitorWorkArea GetProjectorMonitorWorkArea(int monitorIndex)
        {
            var screens = ReplayKitShared.Monitors.List(out string listError);
            if (screens == null) return new MonitorWorkArea { Ok = false, Message = Locales.Message("screenshare.couldNotReadMonitorWorkAreas", "error", listError) };
            if (screens.Count < 1) return new MonitorWorkArea { Ok = false, Message = Locales.Message("screenshare.noRealMonitorWorkAreaWas") };

            // left on automatic, a shown projector goes to the last monitor that is not the main one, since games open fullscreen on the main one; with a single monitor that one is the only choice
            int idx;
            if (monitorIndex <= 0)
            {
                idx = screens.FindLastIndex(s => !s.Primary);
                if (idx < 0) idx = screens.Count - 1;
            }
            else idx = Math.Min(monitorIndex - 1, screens.Count - 1);
            var area = screens[idx].WorkArea;
            var bounds = screens[idx].Bounds;
            int sourceIdx = screens.FindIndex(s => s.Primary);
            System.Drawing.Rectangle? sourceBounds = null;
            if (sourceIdx >= 0) sourceBounds = screens[sourceIdx].Bounds;

            var result = new MonitorWorkArea
            {
                Ok = true,
                Index = idx + 1,
                Count = screens.Count,
                SourceIndex = sourceIdx + 1,
                Left = area.Left,
                Top = area.Top,
                Right = area.Right,
                Bottom = area.Bottom,
                BoundsLeft = bounds.Left,
                BoundsTop = bounds.Top,
                BoundsRight = bounds.Right,
                BoundsBottom = bounds.Bottom,
                BoundsWidth = bounds.Width,
                BoundsHeight = bounds.Height,
            };
            if (sourceBounds.HasValue)
            {
                result.HasSourceBounds = true;
                result.SourceBoundsLeft = sourceBounds.Value.Left;
                result.SourceBoundsTop = sourceBounds.Value.Top;
                result.SourceBoundsRight = sourceBounds.Value.Right;
                result.SourceBoundsBottom = sourceBounds.Value.Bottom;
                result.SourceBoundsWidth = sourceBounds.Value.Width;
                result.SourceBoundsHeight = sourceBounds.Value.Height;
            }
            foreach (var screen in screens)
            {
                var b = screen.Bounds;
                result.AllScreenBounds.Add(new ScreenBounds { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom });
            }
            return result;
        }

        private static List<ScreenBounds> GetScreenBoundsList(MonitorWorkArea workArea, int fallbackLeft, int fallbackTop, int fallbackRight, int fallbackBottom)
        {
            var list = new List<ScreenBounds>();
            foreach (var entry in workArea.AllScreenBounds)
            {
                if (entry.Right > entry.Left && entry.Bottom > entry.Top) list.Add(entry);
            }
            if (list.Count < 1) list.Add(new ScreenBounds { Left = fallbackLeft, Top = fallbackTop, Right = fallbackRight, Bottom = fallbackBottom });
            return list;
        }

        public sealed class ParkedRect
        {
            public int X, Y, Width, Height, VisiblePixels;
            public string Edge;
        }

        private static bool Overlaps(ScreenBounds a, ScreenBounds b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

        // parked outside the projector monitor on the side it sits from the main one, leaving only a visiblePixels square in that monitors outer corner (the bottom one, or the top one when it sits above): over a monitor the hidden window sits on whatever runs there, so a capture that copies the screen where the window is streams that instead of the projector, and covering the main monitor makes windows report a fullscreen app; wholly off the desktop, a discord share of it fell to a few frames a second on one pc while a share of the whole screen stayed smooth, so the square keeps it on a monitor, and a corner square instead of a strip along the edge since the window takes the clicks on what it covers; when the rest would cover another monitor it goes wholly past the desktop edge instead; with one monitor, or the main one picked, the edge setting gives the side
        public static ParkedRect GetDiscordProjectorParkedRect(MonitorWorkArea workArea, int windowWidth, int windowHeight, string edge, int visiblePixels)
        {
            if (workArea.Right <= workArea.Left || workArea.Bottom <= workArea.Top) throw new InvalidOperationException(Locales.Message("screenshare.monitorWorkAreaIsInvalid"));

            string edgeValue = (edge ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(ReplayKitShared.SettingLimits.DiscordProjectorEdges, edgeValue) < 0)
                throw new InvalidOperationException(Locales.Message("screenshare.theShareWindowSideIsNotValid"));

            int mainLeft = workArea.HasSourceBounds ? workArea.SourceBoundsLeft : workArea.BoundsLeft;
            int mainTop = workArea.HasSourceBounds ? workArea.SourceBoundsTop : workArea.BoundsTop;
            int sourceWidth = workArea.HasSourceBounds ? workArea.SourceBoundsWidth : workArea.BoundsWidth;
            int sourceHeight = workArea.HasSourceBounds ? workArea.SourceBoundsHeight : workArea.BoundsHeight;
            if (sourceWidth < 1 || sourceHeight < 1) throw new InvalidOperationException(Locales.Message("screenshare.projectorSourceMonitorBoundsAreInvalid"));
            int width = windowWidth > 0 ? windowWidth : sourceWidth;
            int height = windowHeight > 0 ? windowHeight : sourceHeight;

            // the side is the axis the centres differ most on, so a vertical monitor beside the main one counts as beside it even when it reaches higher or lower
            long dx = ((long)workArea.BoundsLeft + workArea.BoundsRight) - (2L * mainLeft + sourceWidth);
            long dy = ((long)workArea.BoundsTop + workArea.BoundsBottom) - (2L * mainTop + sourceHeight);
            string side = edgeValue;
            if (dx != 0 || dy != 0)
            {
                if (Math.Abs(dx) >= Math.Abs(dy)) side = dx > 0 ? "right" : "left";
                else side = dy > 0 ? "bottom" : "top";
            }

            var screens = GetScreenBoundsList(workArea, workArea.Left, workArea.Top, workArea.Right, workArea.Bottom);
            int visible = Math.Min(Math.Max(0, visiblePixels), Math.Min(width, height));
            if (visible > 0)
            {
                bool leftCorner = side == "left" || side == "top";
                int sx = leftCorner ? workArea.BoundsLeft - width + visible : workArea.BoundsRight - visible;
                int sy = side == "top" ? workArea.BoundsTop - height + visible : workArea.BoundsBottom - visible;
                var corner = new ScreenBounds { Left = sx, Top = sy, Right = sx + width, Bottom = sy + height };
                bool coversOther = screens.Any(b => !(b.Left == workArea.BoundsLeft && b.Top == workArea.BoundsTop && b.Right == workArea.BoundsRight && b.Bottom == workArea.BoundsBottom) && Overlaps(corner, b));
                if (!coversOther) return new ParkedRect { X = sx, Y = sy, Width = width, Height = height, Edge = side, VisiblePixels = visible };
            }

            int x = workArea.BoundsLeft, y = workArea.BoundsTop;
            if (side == "right") x = screens.Max(b => b.Right);
            else if (side == "left") x = screens.Min(b => b.Left) - width;
            else if (side == "top") y = screens.Min(b => b.Top) - height;
            else y = screens.Max(b => b.Bottom);
            return new ParkedRect { X = x, Y = y, Width = width, Height = height, Edge = side, VisiblePixels = 0 };
        }

        public sealed class InspectRect
        {
            public int X, Y, Width, Height;
        }

        public static InspectRect GetDiscordProjectorInspectRect(MonitorWorkArea workArea, int windowWidth, int windowHeight)
        {
            int left = workArea.Left, top = workArea.Top, right = workArea.Right, bottom = workArea.Bottom;
            if (right <= left || bottom <= top) throw new InvalidOperationException(Locales.Message("screenshare.monitorWorkAreaIsInvalid"));

            int workWidth = Math.Max(1, right - left);
            int workHeight = Math.Max(1, bottom - top);
            int maxWidth = Math.Max(320, (int)Math.Floor(workWidth * 0.72));
            int maxHeight = Math.Max(180, (int)Math.Floor(workHeight * 0.72));
            int width = Math.Min(Math.Max(320, windowWidth), maxWidth);
            int height = Math.Min(Math.Max(180, windowHeight), maxHeight);

            const double aspect = 16.0 / 9.0;
            if (width / (double)height > aspect) width = (int)Math.Round(height * aspect);
            else height = (int)Math.Round(width / aspect);

            int x = left + (int)Math.Floor((workWidth - width) / 2.0);
            int y = top + (int)Math.Floor((workHeight - height) / 2.0);
            return new InspectRect { X = x, Y = y, Width = width, Height = height };
        }

        public sealed class OpenProjectorResult
        {
            public bool Ok;
            public long Hwnd;
            public bool Opened;
            public string Message;
        }

        // the running share window, started when none runs; it starts see-through and off every desktop, and the park below shows it
        private static OpenProjectorResult EnsureWindow(JObject settings, string titleOverride = "")
        {
            string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
            var info = ShareWindow.EnsureRunning(title, ShareWindow.KeepSeconds(settings));
            if (!info.Ok || info.Hwnd == 0 || !ShareWindow.IsOwnWindow(info.Hwnd))
            {
                string message = info.Message ?? Locales.Message("screenshare.theShareWindowCouldNotStart");
                Log.Write("warn: " + message);
                return new OpenProjectorResult { Ok = false, Message = message };
            }
            ShareWindow.ApplyTheme(settings);
            return new OpenProjectorResult { Ok = true, Hwnd = info.Hwnd, Opened = info.Started };
        }

        private static JObject ParkedRectToJson(ParkedRect r) => new JObject
        {
            ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height, ["edge"] = r.Edge, ["visible_pixels"] = r.VisiblePixels,
        };

        private static JObject InspectRectToJson(InspectRect r) => new JObject { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };

        private static JObject WorkAreaToJson(MonitorWorkArea w)
        {
            var obj = new JObject
            {
                ["ok"] = w.Ok, ["index"] = w.Index, ["count"] = w.Count, ["source_index"] = w.SourceIndex,
                ["left"] = w.Left, ["top"] = w.Top, ["right"] = w.Right, ["bottom"] = w.Bottom,
                ["bounds_left"] = w.BoundsLeft, ["bounds_top"] = w.BoundsTop, ["bounds_right"] = w.BoundsRight, ["bounds_bottom"] = w.BoundsBottom,
                ["bounds_width"] = w.BoundsWidth, ["bounds_height"] = w.BoundsHeight,
            };
            if (w.HasSourceBounds)
            {
                obj["source_bounds_left"] = w.SourceBoundsLeft;
                obj["source_bounds_top"] = w.SourceBoundsTop;
                obj["source_bounds_right"] = w.SourceBoundsRight;
                obj["source_bounds_bottom"] = w.SourceBoundsBottom;
                obj["source_bounds_width"] = w.SourceBoundsWidth;
                obj["source_bounds_height"] = w.SourceBoundsHeight;
            }
            var allBounds = new JArray();
            foreach (var b in w.AllScreenBounds) allBounds.Add(new JObject { ["left"] = b.Left, ["top"] = b.Top, ["right"] = b.Right, ["bottom"] = b.Bottom });
            obj["all_screen_bounds"] = allBounds;
            return obj;
        }

        // starts the share window when none runs and parks it beside its monitor with nonzero alpha so discord can capture it during fullscreen gameplay, all under ProjectorLock so a helper restart and the keep-alive tick can never start or move it at the same time; Inspect and Disable take the same lock
        public static JObject Repark(JObject settings = null, string titleOverride = "")
        {
            lock (SettingsStore.Gate)
            lock (Server.State.ProjectorLock)
            {
                if (settings == null) settings = ReplaykitSettings.ReadSettings();
                string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
                if (mode != "projector")
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.discordProjectorIsNotTheActive"), ["warnings"] = new JArray() };
                if (!ReplaykitSettings.TestDiscordScreenshareEnabled(settings))
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.discordScreenshareSupportIsDisabled"), ["warnings"] = new JArray() };
                if (settings["discord_projector_enabled"]?.Value<bool>() != true)
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("shell.sharePreviewOff"), ["warnings"] = new JArray() };

                parkedWindow = null;
                disablePending = false;
                if (ProjectorNative.OperationPending())
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.thePreviousProjectorWindowChangeIs") };
                Server.State.ReplaykitDiscordProjectorInspectMode = false;

                var warnings = new List<string>();
                string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
                var open = EnsureWindow(settings, title);
                if (!open.Ok)
                {
                    warnings.Add(open.Message);
                    return new JObject { ["ok"] = false, ["message"] = open.Message, ["warnings"] = new JArray(warnings) };
                }

                var workArea = GetProjectorMonitorWorkArea(settings["discord_projector_monitor_index"]?.Value<int>() ?? 0);
                if (!workArea.Ok)
                {
                    warnings.Add(workArea.Message);
                    return new JObject { ["ok"] = false, ["message"] = workArea.Message, ["warnings"] = new JArray(warnings) };
                }

                var size = ResolveProjectorWindowSize(settings, workArea);
                if (!size.Ok)
                {
                    warnings.Add(size.Message);
                    return new JObject { ["ok"] = false, ["message"] = size.Message, ["warnings"] = new JArray(warnings) };
                }

                ParkedRect rect;
                try { rect = GetDiscordProjectorParkedRect(workArea, size.Width, size.Height, settings["discord_projector_edge"]?.Value<string>() ?? "", settings["discord_projector_visible_pixels"]?.Value<int>() ?? 0); }
                catch (InvalidOperationException ex)
                {
                    warnings.Add(ex.Message);
                    return new JObject { ["ok"] = false, ["message"] = ex.Message, ["warnings"] = new JArray(warnings) };
                }

                Log.Write(string.Format("Parking Discord projector on the {0} side, visible_pixels={1}, window=({2},{3},{4},{5}), mode=hidden",
                    rect.Edge, rect.VisiblePixels, rect.X, rect.Y, rect.Width, rect.Height));
                bool alreadyPlaced = !open.Opened && ProjectorNative.IsProjectorParked(open.Hwnd, title, rect.X, rect.Y, rect.Width, rect.Height, true);
                bool parked = alreadyPlaced || ProjectorNative.RestoreResizeTitleChromeGhosted(open.Hwnd, title, rect.X, rect.Y, rect.Width, rect.Height, true);
                if (!parked)
                {
                    string msg = Locales.Message("screenshare.theObsProjectorWindowChangeDid");
                    Log.Write("warn: " + msg);
                    warnings.Add(msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["warnings"] = new JArray(warnings) };
                }

                // explorer puts the button back whenever it builds a taskbar again (an explorer restart, a monitor that wakes or comes back), so the hide is applied on every park rather than once per window
                if (!ProjectorNative.SetProjectorTaskbarHidden(open.Hwnd, true))
                {
                    string msg = Locales.Message("screenshare.obsProjectorWindowWasMovedBut");
                    warnings.Add(msg);
                    Log.Write("warn: " + msg);
                }

                parkedWindow = new ParkedWindow { Hwnd = open.Hwnd, Title = title, Rect = rect };
                Log.Write("Discord projector ready");
                // a window just started has no card yet, and a theme or card change since the last park shows up here at the latest
                ShareCover.Apply(settings, open.Opened);
                return new JObject
                {
                    ["ok"] = true,
                    ["hwnd"] = open.Hwnd,
                    ["opened"] = open.Opened,
                    ["title"] = title,
                    ["rect"] = ParkedRectToJson(rect),
                    ["monitor"] = WorkAreaToJson(workArea),
                    ["size_source"] = size.Source,
                    ["applied"] = new JArray("Discord projector ready"),
                    ["warnings"] = new JArray(warnings),
                };
            }
        }

        // shows the projector centered and windowed, for a user-triggered visual check -- not hidden, not topmost.
        public static JObject Inspect(JObject settings = null, string titleOverride = "")
        {
            lock (SettingsStore.Gate)
            lock (Server.State.ProjectorLock)
            {
                if (settings == null) settings = ReplaykitSettings.ReadSettings();
                string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
                if (mode != "projector")
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.discordProjectorIsNotTheActive"), ["warnings"] = new JArray() };
                if (!ReplaykitSettings.TestDiscordScreenshareEnabled(settings))
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.discordScreenshareSupportIsDisabled"), ["warnings"] = new JArray() };
                if (settings["discord_projector_enabled"]?.Value<bool>() != true)
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("shell.sharePreviewOff"), ["warnings"] = new JArray() };

                parkedWindow = null;
                disablePending = false;
                if (ProjectorNative.OperationPending())
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.thePreviousProjectorWindowChangeIs") };
                var warnings = new List<string>();
                string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
                var open = EnsureWindow(settings, title);
                if (!open.Ok)
                {
                    warnings.Add(open.Message);
                    return new JObject { ["ok"] = false, ["message"] = open.Message, ["warnings"] = new JArray(warnings) };
                }

                var workArea = GetProjectorMonitorWorkArea(settings["discord_projector_monitor_index"]?.Value<int>() ?? 0);
                if (!workArea.Ok)
                {
                    warnings.Add(workArea.Message);
                    return new JObject { ["ok"] = false, ["message"] = workArea.Message, ["warnings"] = new JArray(warnings) };
                }

                var size = ResolveProjectorWindowSize(settings, workArea);
                if (!size.Ok)
                {
                    warnings.Add(size.Message);
                    return new JObject { ["ok"] = false, ["message"] = size.Message, ["warnings"] = new JArray(warnings) };
                }

                InspectRect rect;
                try { rect = GetDiscordProjectorInspectRect(workArea, size.Width, size.Height); }
                catch (InvalidOperationException ex)
                {
                    warnings.Add(ex.Message);
                    return new JObject { ["ok"] = false, ["message"] = ex.Message, ["warnings"] = new JArray(warnings) };
                }

                bool moved = ProjectorNative.RestoreResizeTitleAndChrome(open.Hwnd, title, rect.X, rect.Y, rect.Width, rect.Height, false);
                if (!moved)
                {
                    string msg = Locales.Message("screenshare.obsProjectorWindowWasFoundBut");
                    warnings.Add(msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["warnings"] = new JArray(warnings) };
                }

                ProjectorNative.SetProjectorTaskbarHidden(open.Hwnd, false);
                Server.State.ReplaykitDiscordProjectorInspectMode = true;
                return new JObject
                {
                    ["ok"] = true, ["hwnd"] = open.Hwnd, ["opened"] = open.Opened, ["title"] = title,
                    ["rect"] = InspectRectToJson(rect), ["monitor"] = WorkAreaToJson(workArea), ["size_source"] = size.Source,
                    ["applied"] = new JArray("Projector shown for inspection"), ["warnings"] = new JArray(warnings),
                };
            }
        }

        public static JObject Disable(JObject settings = null, string titleOverride = "")
        {
            parkedWindow = null;
            if (settings == null) settings = ReplaykitSettings.ReadSettings();
            var warnings = new List<string>();
            string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;

            lock (Server.State.ProjectorLock)
            {
                if (ProjectorNative.OperationPending())
                {
                    disablePending = true;
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.thePreviousProjectorWindowChangeIs") };
                }
                disablePending = false;
                Server.State.ReplaykitDiscordProjectorInspectMode = false;
                if (!ShareWindow.AnyRunning())
                    return new JObject { ["ok"] = true, ["title"] = title, ["applied"] = new JArray("Share preview already disabled"), ["warnings"] = new JArray(warnings) };
                // with share preview off no share window may run, so one that does not close when asked is ended
                if (ShareWindow.Quit(3000))
                    return new JObject { ["ok"] = true, ["title"] = title, ["applied"] = new JArray("Share preview disabled"), ["warnings"] = new JArray(warnings) };
                string msg = Locales.Message("screenshare.theShareWindowDidNotClose");
                warnings.Add(msg);
                return new JObject { ["ok"] = false, ["title"] = title, ["message"] = msg, ["warnings"] = new JArray(warnings) };
            }
        }

        // legacy share-bridge cleanup: an older discord-output mode (pre-dating projector-only) ran a seperate OBSReplayKitShareBridge.exe; stopped defensively in case one is still running from before an update.
        public static void StopShareBridge()
        {
            foreach (var proc in Process.GetProcessesByName("OBSReplayKitShareBridge"))
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        proc.CloseMainWindow();
                        if (proc.WaitForExit(1500)) { proc.Dispose(); continue; }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                try { proc.Kill(); } catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                proc.Dispose();
            }
        }

        public sealed class AudioDeviceResult
        {
            public bool Ok;
            public string Message;
            public string Id;
            public string Source;
            public string Name;
        }

        // takes a windows mixer mute of obs off the share cable, see ShareCableMute
        public static int ClearCableMute()
        {
            var cable = GetObsStreamAudioRenderDevice();
            return cable.Ok ? ShareCableMute.UnmuteObs(cable.Id) : -1;
        }

        public static AudioDeviceResult GetObsStreamAudioRenderDevice()
        {
            (string DeviceId, string Name)? cable;
            try { cable = ReplayKitShared.AudioEndpoints.FindShareCable(); }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return new AudioDeviceResult { Ok = false, Message = Locales.Message("screenshare.couldNotInspectObsStreamAudio", "error", ex.Message) };
            }
            if (cable == null)
                return new AudioDeviceResult { Ok = false, Message = Locales.Message("screenshare.obsStreamAudioPlaybackEndpointIs") };
            return new AudioDeviceResult { Ok = true, Id = cable.Value.DeviceId, Source = "obs_stream_audio", Name = cable.Value.Name };
        }

        // the keep-alive runs on the share-keep-alive thread and the startup park on a pool thread it queues, so a start of the share window or a window call stuck on another ui thread never holds up the http accept loop
        private static DateTime _keepAliveAt = DateTime.MinValue;
        private const int KeepAliveSeconds = 30;
        private static int _keepAliveFailures;
        // a start of the share window can wait seconds for it to answer while holding ProjectorLock, so one that keeps failing is retried every few minutes instead of every 30 s once it is clearly not a one-off miss
        private const int KeepAliveMaxBackoffSeconds = 300;
        // a minimized projector is 160x28 with no client area, which is all a capture of it gets, and a minimize request still reaches it with no minimize box; the check only reads window state, so it can run every second
        private const int ParkedCheckMilliseconds = 1000;
        private static DateTime _parkedCheckAt = DateTime.MinValue;
        // one early park per keep-alive interval, so a window something keeps moving cannot be fought over every second
        private static bool _earlyParkUsed;

        // keep obs and share-window ipc waits off the http accept loop
        public static void StartKeepAlive()
        {
            var worker = new Thread(() =>
            {
                while (!Server.State.Shutdown)
                {
                    try { KeepAlive(); }
                    catch (Exception ex) { Log.Write("warn: Discord projector keep-alive threw: " + ex.Message); }
                    Thread.Sleep(50);
                }
            }) { Name = "share-keep-alive", IsBackground = true };
            worker.Start();
        }

        private static void KeepAlive()
        {
            if (Volatile.Read(ref startupState) != 2)
            {
                StartAtStartup();
                return;
            }
            if (!Monitor.TryEnter(SettingsStore.Gate)) return;
            try { KeepProjectorAlive(); }
            finally { Monitor.Exit(SettingsStore.Gate); }
        }

        private static void KeepProjectorAlive()
        {
            // complete an explicit off request once the timed-out operation and fresh window information are available.
            if (disablePending)
            {
                var pendingSettings = ReplaykitSettings.ReadSettings();
                lock (Server.State.ProjectorLock)
                {
                    if (disablePending) Disable(pendingSettings);
                }
                return;
            }
            var now = DateTime.UtcNow;
            double intervalSeconds = KeepAliveSeconds;
            if (_keepAliveFailures > 1)
            {
                double backoff = KeepAliveSeconds * Math.Pow(2, _keepAliveFailures - 1);
                intervalSeconds = Math.Min(backoff, KeepAliveMaxBackoffSeconds);
            }
            bool leftItsPlace = false;
            var known = parkedWindow;
            if (known != null && !_earlyParkUsed && _keepAliveFailures == 0 && (now - _parkedCheckAt).TotalMilliseconds >= ParkedCheckMilliseconds)
            {
                _parkedCheckAt = now;
                leftItsPlace = !ProjectorNative.IsProjectorParked(known.Hwnd, known.Title, known.Rect.X, known.Rect.Y, known.Rect.Width, known.Rect.Height, true);
                // a park of a window already in place skips its topmost step, so a window that dropped below normal windows is raised here
                if (!leftItsPlace && ProjectorNative.IsProjectorBelowNormalWindow(known.Hwnd))
                {
                    Log.Write(ProjectorNative.RaiseProjectorToTopmost(known.Hwnd)
                        ? "Discord projector had dropped below normal windows; raised it back to topmost"
                        : "warn: Discord projector dropped below normal windows and could not be raised back");
                }
            }
            if (!leftItsPlace && (now - _keepAliveAt).TotalSeconds < intervalSeconds) return;
            _earlyParkUsed = leftItsPlace;
            _keepAliveAt = now;
            Log.Write(leftItsPlace ? "Discord projector was minimized or moved; parking it again now" : "Discord projector keep-alive tick");

            JObject settings;
            try { settings = ReplaykitSettings.ReadSettings(); }
            catch (Exception ex)
            {
                Log.Write("Discord projector keep-alive skipped: " + ex.Message);
                return;
            }
            // share preview off means no share window, whatever started one
            if (SettingsLiveApply.GetSharePreviewFlags()["enabled"]?.Value<bool>() != true)
            {
                if (ShareWindow.AnyRunning())
                {
                    Log.Write("A share window runs while share preview is off; ending it");
                    var off = Disable(settings);
                    if (off["ok"]?.Value<bool>() != true) Log.Write("warn: " + off["message"]?.Value<string>());
                }
                return;
            }
            ClearCableMute();
            if (Server.State.ReplaykitDiscordProjectorInspectMode)
            {
                Log.Write("Discord projector keep-alive skipped: inspect mode active");
                return;
            }
            StopShareBridge();
            var result = Repark(settings);
            if (!(result["ok"]?.Value<bool>() ?? false))
            {
                _keepAliveFailures++;
                Log.Write("warn: Discord projector keep-alive failed (" + _keepAliveFailures + " in a row, next retry in up to " + KeepAliveMaxBackoffSeconds + "s): " + result["message"]?.Value<string>());
            }
            else
            {
                _keepAliveFailures = 0;
            }
        }

        public static void StartAtStartup()
        {
            if (Server.State.Shutdown || Volatile.Read(ref startupState) != 0 || !WindowInfoReady()) return;
            if (Interlocked.CompareExchange(ref startupState, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool attempted = false;
                try
                {
                    if (!Server.State.Shutdown) attempted = ApplyStartup();
                }
                catch (Exception ex)
                {
                    attempted = true;
                    Log.Write("warn: Discord projector startup threw: " + ex.Message);
                }
                finally
                {
                    if (attempted) _keepAliveAt = DateTime.UtcNow;
                    Volatile.Write(ref startupState, attempted ? 2 : 0);
                }
            });
        }

        // once the plugin is connected and obs shows its window, obs runs far enough to hand frames to the share window
        private static bool WindowInfoReady()
        {
            lock (Server.State.IpcLock)
                return Server.State.IpcClientConnected && Server.State.ObsMainWindowHwnd != 0;
        }

        private static bool ApplyStartup()
        {
            lock (SettingsStore.Gate)
            lock (Server.State.ProjectorLock)
            {
                if (!WindowInfoReady() || ProjectorNative.OperationPending()) return false;
                if (Server.State.ReplaykitDiscordProjectorInspectMode) return true;
                Log.Write("OBS window information is ready; applying Discord projector startup.");
                ApplyStartupSettings();
                return true;
            }
        }

        private static void ApplyStartupSettings()
        {
            JObject settings;
            try { settings = ReplaykitSettings.ReadSettings(); }
            catch (Exception ex)
            {
                Log.Write("Discord projector startup skipped: " + ex.Message);
                return;
            }
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode == "projector" && (settings["discord_projector_enabled"]?.Value<bool>() ?? true))
            {
                StopShareBridge();
                Log.Write("Discord projector mode enabled");
                Log.Write("Skipping OBS Virtual Camera for Discord output");
                var result = Repark(settings);
                if (!(result["ok"]?.Value<bool>() ?? false)) Log.Write("warn: Discord projector startup failed: " + result["message"]?.Value<string>());
            }
            else if (mode == "projector")
            {
                StopShareBridge();
                var result = Disable(settings);
                if (!(result["ok"]?.Value<bool>() ?? false)) Log.Write("warn: Discord projector disable failed: " + result["message"]?.Value<string>());
            }
            else if (mode == "virtual_camera_legacy")
            {
                StopShareBridge();
                Log.Write("warn: Virtual Camera is legacy/deprecated for Discord output.");
            }
        }
    }
}
