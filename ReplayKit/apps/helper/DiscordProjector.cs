using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // discord "share preview" via an obs windowed projector: finds/opens/parks it hidden-but-discoverable off the desktop (so discords own screen picker lists it) when enabled, or shows it centered for a user-triggered visual check ("inspect"), or closes it when disabled. every find/open/park/close sequence runs under ProjectorLock so Repark/Inspect/Disable/the keep-alive tick can never interleave with each other -- see the comment on Repark below, which documents a real concurrency bug this fixed. ProjectorNative.cs is the win32 layer underneath.
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
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || Regex.IsMatch(value, @"[\x00-\x1F]")) return defaultTitle;
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
            public List<string> Warnings = new List<string>();
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

        public static SizeResult ResolveProjectorWindowSize(JObject settings, MonitorWorkArea workArea, bool preferSourceMonitor)
        {
            int width = settings["discord_projector_width"]?.Value<int>() ?? 0;
            int height = settings["discord_projector_height"]?.Value<int>() ?? 0;
            if (width > 0 && height > 0) return new SizeResult { Ok = true, Width = width, Height = height, Source = "settings" };

            int sourceWidth = workArea.HasSourceBounds ? workArea.SourceBoundsWidth : 0;
            int sourceHeight = workArea.HasSourceBounds ? workArea.SourceBoundsHeight : 0;
            if (preferSourceMonitor && sourceWidth > 0 && sourceHeight > 0)
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

            string fallbackWarning = canvas.Message + " Falling back to Windows primary monitor size.";
            if (width <= 0) width = sourceWidth;
            if (height <= 0) height = sourceHeight;
            if (width < 1 || height < 1) return new SizeResult { Ok = false, Message = Locales.Message("screenshare.couldNotResolveAValidProjector") };
            return new SizeResult { Ok = true, Width = width, Height = height, Source = "primary_monitor_fallback", Warnings = new List<string> { fallbackWarning } };
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
            public int X, Y, Width, Height;
            public string Edge;
        }

        // parked wholly past the edge of the desktop on the side the projector monitor sits from the main one, lined up with that monitor so it stays the nearest one and the window keeps away from the game: on a monitor the hidden window sits over whatever runs there, so a capture that copies the screen where the window is (discord switches to one when it leaves windows graphics capture, which some pcs do while a game is fullscreen) streams that game without the overlay, and covering the main monitor makes windows report a fullscreen app; windows graphics capture and printwindow still read a window off the desktop, and discord still lists it; with one monitor, or the main one picked, the edge setting gives the side
        public static ParkedRect GetDiscordProjectorParkedRect(MonitorWorkArea workArea, int windowWidth, int windowHeight, string edge)
        {
            if (workArea.Right <= workArea.Left || workArea.Bottom <= workArea.Top) throw new InvalidOperationException(Locales.Message("screenshare.monitorWorkAreaIsInvalid"));

            string edgeValue = (edge ?? "").Trim().ToLowerInvariant();
            if (edgeValue != "right" && edgeValue != "left" && edgeValue != "top" && edgeValue != "bottom")
                throw new InvalidOperationException("Invalid projector edge: " + edge);

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
            int x = workArea.BoundsLeft, y = workArea.BoundsTop;
            if (side == "right") x = screens.Max(b => b.Right);
            else if (side == "left") x = screens.Min(b => b.Left) - width;
            else if (side == "top") y = screens.Min(b => b.Top) - height;
            else y = screens.Max(b => b.Bottom);
            return new ParkedRect { X = x, Y = y, Width = width, Height = height, Edge = side };
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

        public static long FindProjectorWindow(JObject settings, string titleOverride = "")
        {
            string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
            long hwnd = ProjectorNative.FindProjectorWindow(title);
            if (hwnd == 0) Log.Write("Find projector: no match for title '" + title + "'");
            else Log.Write("Find projector: matched hwnd=" + hwnd + " for title '" + title + "'");
            return hwnd;
        }

        private const int ProjectorHandoffMaxAgeSeconds = 2;

        // the native plugin (core/ipc_pipe.cpp) streams the hwnds obs itself marked with isOBSProjectorWindow over the OBSReplayKitIpc pipe every 250ms -- the actual authoritative signal obs uses internally. PipeClient caches the latest snapshot in Server.State. returns null (not an empty list) when the pipe isnt connected or no snapshot has landed in the last couple publish cycles, so the caller can tell "confirmed no projectors exist" apart from "the plugin isnt running or obs is mid-shutdown, this genuinely doesnt know" and fall back to the window-heuristic scan -- an empty list here would silently collapse those two very different situations into one.
        private static List<long> GetProjectorHwndsFromPlugin()
        {
            lock (Server.State.IpcLock)
            {
                if (!Server.State.IpcClientConnected || Server.State.ProjectorHwnds == null) return null;
                if ((DateTime.UtcNow - Server.State.ProjectorHwndsAtUtc).TotalSeconds > ProjectorHandoffMaxAgeSeconds) return null;
                return new List<long>(Server.State.ProjectorHwnds);
            }
        }

        // Only OBS-confirmed projectors can be moved or hidden; the main window can also have "Projector" in its title.
        private static List<long> GetObsWindowCandidates()
        {
            var fromPlugin = GetProjectorHwndsFromPlugin();
            return fromPlugin == null ? null : fromPlugin.Where(ProjectorNative.IsProjectorWindow).ToList();
        }

        public sealed class OpenProjectorResult
        {
            public bool Ok;
            public long Hwnd;
            public bool Opened;
            public string Message;
        }

        private static OpenProjectorResult OpenIfMissing(JObject settings, string titleOverride = "")
        {
            string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
            long hwnd = FindProjectorWindow(settings, title);
            if (hwnd != 0)
            {
                int extra = ProjectorNative.CloseDuplicateProjectorWindows(title, hwnd);
                if (extra > 0) Log.Write("Closed " + extra + " leaked Discord projector window(s)");
                Server.State.ReplaykitDiscordProjectorPendingHwnd = 0;
                return new OpenProjectorResult { Ok = true, Hwnd = hwnd, Opened = false };
            }

            // a projector an earlier attempt opened but gave up waiting for is taken now instead of opening another, once; if it fails again the next attempt opens a fresh one
            long opened = Server.State.ReplaykitDiscordProjectorPendingHwnd;
            Server.State.ReplaykitDiscordProjectorPendingHwnd = 0;
            bool fresh = opened == 0;
            if (fresh)
            {
                // the plugin opens it, since only from inside obs can it be made see-through before obs first shows it, and it answers with the exact window it opened, so a projector the user opens in the meantime (a source projector of Game Capture shows the game without the overlay) is never taken for it
                Log.Write("Opening OBS Windowed Projector for title '" + title + "'");
                if (!PipeClient.OpenShareProjectorAndWait(5000, out opened))
                {
                    Log.Write("warn: the native plugin did not answer the share projector open");
                    return new OpenProjectorResult { Ok = false, Hwnd = 0, Opened = false, Message = Locales.Message("screenshare.openingObsWindowedProjectorFailedError", "error", Locales.Message("helper.thePluginDidNotAnswerIn")) };
                }
                if (opened == 0) return new OpenProjectorResult { Ok = false, Hwnd = 0, Opened = true, Message = Locales.Message("screenshare.obsWindowedProjectorWasOpenedBut") };
            }

            // the plugin streams the projectors obs marked every 250 ms and a window is only handled once it is in that list; 24x250ms=6s for a slow pc
            for (int i = 0; i < 24; i++)
            {
                if (ProjectorNative.IsProjectorWindow(opened))
                {
                    if (!ProjectorNative.PreParkProjectorWindow(opened, title))
                    {
                        ProjectorNative.CloseWindow(opened);
                        return new OpenProjectorResult { Ok = false, Message = Locales.Message("screenshare.theObsProjectorWindowCouldNot") };
                    }
                    int extra = ProjectorNative.CloseDuplicateProjectorWindows(title, opened);
                    if (extra > 0) Log.Write("Closed " + extra + " leaked Discord projector window(s)");
                    Log.Write("Opened OBS projector: hwnd=" + opened + " after " + (i * 250) + "ms");
                    return new OpenProjectorResult { Ok = true, Hwnd = opened, Opened = true };
                }
                Thread.Sleep(250);
            }
            if (fresh) Server.State.ReplaykitDiscordProjectorPendingHwnd = opened;
            Log.Write("warn: OBS Windowed Projector hwnd=" + opened + " was opened but never reached the projector list after 6s" + (fresh ? " (the next check takes it instead of opening another)" : ""));
            return new OpenProjectorResult { Ok = false, Hwnd = 0, Opened = true, Message = Locales.Message("screenshare.obsWindowedProjectorWasOpenedBut") };
        }

        private static JObject ParkedRectToJson(ParkedRect r) => new JObject
        {
            ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height, ["edge"] = r.Edge,
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

        // finds/opens/parks the layered projector off the desktop with nonzero alpha so discord can capture it during fullscreen gameplay. runs the whole find/open/park sequence under ProjectorLock -- without it, a helper restart firing this function at the same moment as the keep-alive tick could see no projector from both, and both would open one. Inspect and Disable take the same lock for their own find/open/close sequences so none of the three can interleave with each other either.
        public static JObject Repark(JObject settings = null, string titleOverride = "", bool force = false)
        {
            parkedWindow = null;
            if (settings == null) settings = ReplaykitSettings.ReadSettings();
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode != "projector")
            {
                Log.Write("Discord projector repark skipped: output mode is '" + mode + "'");
                return new JObject { ["ok"] = true, ["applied"] = new JArray("Discord projector skipped: legacy output mode selected."), ["warnings"] = new JArray() };
            }
            if (!(settings["discord_screenshare_enabled"]?.Value<bool>() ?? true))
            {
                Log.Write("Discord projector repark skipped: screenshare disabled in settings");
                return new JObject { ["ok"] = true, ["applied"] = new JArray("Discord screenshare support disabled."), ["warnings"] = new JArray() };
            }
            if (!(settings["discord_projector_enabled"]?.Value<bool>() ?? true) && !force)
            {
                Log.Write("Discord projector repark skipped: projector disabled in settings");
                return new JObject { ["ok"] = true, ["applied"] = new JArray("Discord projector disabled."), ["warnings"] = new JArray() };
            }

            lock (Server.State.ProjectorLock)
            {
                disablePending = false;
                if (ProjectorNative.OperationPending())
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.thePreviousProjectorWindowChangeIs") };
                Server.State.ReplaykitDiscordProjectorInspectMode = false;

                var warnings = new List<string>();
                string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
                var open = OpenIfMissing(settings, title);
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

                var size = ResolveProjectorWindowSize(settings, workArea, true);
                if (!size.Ok)
                {
                    warnings.Add(size.Message);
                    return new JObject { ["ok"] = false, ["message"] = size.Message, ["warnings"] = new JArray(warnings) };
                }
                warnings.AddRange(size.Warnings);

                ParkedRect rect;
                try { rect = GetDiscordProjectorParkedRect(workArea, size.Width, size.Height, settings["discord_projector_edge"]?.Value<string>() ?? ""); }
                catch (InvalidOperationException ex)
                {
                    warnings.Add(ex.Message);
                    return new JObject { ["ok"] = false, ["message"] = ex.Message, ["warnings"] = new JArray(warnings) };
                }

                Log.Write(string.Format("Parking Discord projector past the {0} edge of the desktop, window=({1},{2},{3},{4}), mode=hidden",
                    rect.Edge, rect.X, rect.Y, rect.Width, rect.Height));
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
                const bool hideProjectorTaskbar = true;
                if (hideProjectorTaskbar && !ProjectorNative.SetProjectorTaskbarHidden(open.Hwnd, true))
                {
                    string msg = Locales.Message("screenshare.obsProjectorWindowWasMovedBut");
                    warnings.Add(msg);
                    Log.Write("warn: " + msg);
                }

                parkedWindow = new ParkedWindow { Hwnd = open.Hwnd, Title = title, Rect = rect };
                Log.Write("Discord projector ready");
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
            parkedWindow = null;
            if (settings == null) settings = ReplaykitSettings.ReadSettings();
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode != "projector") return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.discordProjectorIsNotTheActive"), ["warnings"] = new JArray() };
            if (!(settings["discord_screenshare_enabled"]?.Value<bool>() ?? true))
                return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.discordScreenshareSupportIsDisabled"), ["warnings"] = new JArray() };

            lock (Server.State.ProjectorLock)
            {
                disablePending = false;
                if (ProjectorNative.OperationPending())
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.thePreviousProjectorWindowChangeIs") };
                var warnings = new List<string>();
                string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;
                var open = OpenIfMissing(settings, title);
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

                var size = ResolveProjectorWindowSize(settings, workArea, true);
                if (!size.Ok)
                {
                    warnings.Add(size.Message);
                    return new JObject { ["ok"] = false, ["message"] = size.Message, ["warnings"] = new JArray(warnings) };
                }
                warnings.AddRange(size.Warnings);

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
                if (GetObsWindowCandidates() == null)
                {
                    disablePending = true;
                    return new JObject { ["ok"] = false, ["message"] = Locales.Message("screenshare.obsProjectorWindowInformationIsUnavailable") };
                }
                disablePending = false;
                Server.State.ReplaykitDiscordProjectorInspectMode = false;
                // disabling ends any open attempt still being tracked, and its window is closed too since it never got the share title the sweep below closes by
                long pending = Server.State.ReplaykitDiscordProjectorPendingHwnd;
                Server.State.ReplaykitDiscordProjectorPendingHwnd = 0;
                if (pending != 0) ProjectorNative.CloseWindow(pending);
                long hwnd = FindProjectorWindow(settings, title);
                if (hwnd == 0)
                {
                    return new JObject { ["ok"] = true, ["title"] = title, ["applied"] = new JArray("Share preview already disabled"), ["warnings"] = new JArray(warnings) };
                }

                // posting wm_close doesnt guarantee obs actually destroyed the projector -- verify it really went away instead of trusting the post, since a parked/ghosted projector left alive here keeps playing its own program audio mix to the default device long after this "succeeds". sweeps every exact-title match, not just the one Find picked as best, so a leaked duplicate from the keep-alive bug cant survive a disable.
                for (int attempt = 1; attempt <= 6; attempt++)
                {
                    ProjectorNative.CloseWindow(hwnd);
                    ProjectorNative.CloseDuplicateProjectorWindows(title, 0);
                    Thread.Sleep(Math.Min(500, 100 * attempt));
                    hwnd = FindProjectorWindow(settings, title);
                    if (hwnd == 0)
                    {
                        return new JObject { ["ok"] = true, ["title"] = title, ["applied"] = new JArray("Share preview disabled"), ["warnings"] = new JArray(warnings) };
                    }
                }

                string msg = Locales.Message("screenshare.obsProjectorWindowWasFoundBut2");
                warnings.Add(msg);
                return new JObject { ["ok"] = false, ["hwnd"] = hwnd, ["title"] = title, ["message"] = msg, ["warnings"] = new JArray(warnings) };
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
            const string renderPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
            const string friendlyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
            const int activeFlag = 0x1;
            var candidates = new List<(string Id, string Name, int Rank)>();
            try
            {
                using (var renderKey = Registry.LocalMachine.OpenSubKey(renderPath))
                {
                    if (renderKey == null) throw new InvalidOperationException("registry key not found");
                    foreach (var childName in renderKey.GetSubKeyNames())
                    {
                        using (var child = renderKey.OpenSubKey(childName))
                        {
                            if (child == null) continue;
                            var state = child.GetValue("DeviceState");
                            if (state == null || (Convert.ToInt32(state) & activeFlag) == 0) continue;
                            using (var props = child.OpenSubKey("Properties"))
                            {
                                var name = props?.GetValue(friendlyKey) as string;
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                string displayName = name.Trim();
                                string canonical = Regex.Replace(displayName, @"^\s*\d+\s*-\s*", "").Trim();
                                string lower = canonical.ToLowerInvariant();
                                if (Regex.IsMatch(lower, "surround|16ch|loopback|do not select")) continue;

                                int rank = 999;
                                if (lower == "obs stream audio") rank = 0;
                                else if (lower.StartsWith("obs stream audio")) rank = 1;
                                else if (lower.StartsWith("cable input")) rank = 2;
                                if (rank == 999) continue;

                                candidates.Add(("{0.0.0.00000000}." + childName, displayName, rank));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is IOException || ex is InvalidOperationException)
            {
                return new AudioDeviceResult { Ok = false, Message = Locales.Message("screenshare.couldNotInspectObsStreamAudio", "error", ex.Message) };
            }

            var selected = candidates.OrderBy(c => c.Rank).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
            if (selected.Id == null)
                return new AudioDeviceResult { Ok = false, Message = Locales.Message("screenshare.obsStreamAudioPlaybackEndpointIs") };
            return new AudioDeviceResult { Ok = true, Id = selected.Id, Source = "obs_stream_audio", Name = selected.Name };
        }

        private static DateTime _keepAliveAt = DateTime.MinValue;
        // this tick runs on the main accept-loop thread (see Runtime.cs), never a pool thread, and must stay that way -- the win32 reposition call underneath it can block waiting on obss own ui thread if obs is busy, which on the pooled model only briefly delays the next new connection being accepted, not any in-flight request.
        private const int KeepAliveSeconds = 30;
        private static int _keepAliveFailures;
        // open-if-missing has no upper bound tighter than a 5s websocket call plus a 6s find-poll, and that whole sequence runs on this same main thread while holding ProjectorLock -- on a machine where the projector genuinely fails to appear, every single 30s tick re-attempts and re-blocks the entire helper for the full open+poll duration, starving every other connection. capping the backoff here so a persistently-broken projector gets retried every few minutes instead of every 30s forever, once its clearly not a one-off miss.
        private const int KeepAliveMaxBackoffSeconds = 300;
        // a minimized projector is 160x28 with no client area, which is all a capture of it gets, and a minimize request still reaches it with no minimize box; the check only reads window state, so it can run every second
        private const int ParkedCheckMilliseconds = 1000;
        private static DateTime _parkedCheckAt = DateTime.MinValue;
        // one early park per keep-alive interval, so a window something keeps moving cannot be fought over every second
        private static bool _earlyParkUsed;

        public static void KeepAlive()
        {
            if (Volatile.Read(ref startupState) != 2)
            {
                StartAtStartup();
                return;
            }
            // Complete an explicit off request once the timed-out operation and fresh window information are available.
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
            if (!(settings["discord_projector_enabled"]?.Value<bool>() ?? true))
            {
                Log.Write("Discord projector keep-alive skipped: projector disabled in settings");
                return;
            }
            ClearCableMute();
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode != "projector")
            {
                Log.Write("Discord projector keep-alive skipped: output mode is '" + mode + "'");
                return;
            }
            if (!(settings["discord_screenshare_enabled"]?.Value<bool>() ?? true))
            {
                Log.Write("Discord projector keep-alive skipped: screenshare disabled in settings");
                return;
            }
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

        private static bool WindowInfoReady()
        {
            lock (Server.State.IpcLock)
                return Server.State.IpcClientConnected && Server.State.ObsMainWindowHwnd != 0 &&
                    Server.State.ProjectorHwnds != null &&
                    (DateTime.UtcNow - Server.State.ProjectorHwndsAtUtc).TotalSeconds <= ProjectorHandoffMaxAgeSeconds;
        }

        private static bool ApplyStartup()
        {
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
