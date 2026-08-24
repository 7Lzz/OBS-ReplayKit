using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // discord "share preview" via an obs windowed projector: finds/opens/parks it hidden-but-discoverable in a corner (so discord's own screen picker lists it) when enabled, or shows it centered for a user-triggered visual check ("inspect"), or closes it when disabled. every find/open/park/close sequence runs under ProjectorLock so Repark/Inspect/Disable/the keep-alive tick can never interleave with each other -- see the comment on Repark below, which documents a real concurrency bug this fixed. ported from obs_replaykit helper modules/64_discord_projector.ps1 (the orchestration half; ProjectorNative.cs is the embedded win32 layer from the same file).
    internal static class DiscordProjector
    {
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
            if (!videoSettings.Ok) return new SizeResult { Ok = false, Message = "OBS video settings unavailable: " + videoSettings.Message };

            int baseWidth = GetObjectInt(videoSettings.Data, "baseWidth");
            int baseHeight = GetObjectInt(videoSettings.Data, "baseHeight");
            if (baseWidth > 0 && baseHeight > 0) return new SizeResult { Ok = true, Width = baseWidth, Height = baseHeight, Source = "obs_base_canvas" };

            int outputWidth = GetObjectInt(videoSettings.Data, "outputWidth");
            int outputHeight = GetObjectInt(videoSettings.Data, "outputHeight");
            if (outputWidth > 0 && outputHeight > 0) return new SizeResult { Ok = true, Width = outputWidth, Height = outputHeight, Source = "obs_output" };

            return new SizeResult { Ok = false, Message = "OBS video settings did not include a valid canvas or output size." };
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
            if (width < 1 || height < 1) return new SizeResult { Ok = false, Message = "Could not resolve a valid projector window size." };
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
            System.Windows.Forms.Screen[] screens;
            try { screens = System.Windows.Forms.Screen.AllScreens; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ExternalException) { return new MonitorWorkArea { Ok = false, Message = "Could not read monitor work areas: " + ex.Message }; }
            if (screens.Length < 1) return new MonitorWorkArea { Ok = false, Message = "No real monitor work area was reported by Windows." };

            int idx = monitorIndex <= 0 ? screens.Length - 1 : Math.Max(1, monitorIndex) - 1;
            if (idx >= screens.Length) idx = screens.Length - 1;
            var area = screens[idx].WorkingArea;
            var bounds = screens[idx].Bounds;
            var sourceScreen = System.Windows.Forms.Screen.PrimaryScreen;
            int sourceIdx = -1;
            System.Drawing.Rectangle? sourceBounds = null;
            if (sourceScreen != null)
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    if (screens[i].DeviceName == sourceScreen.DeviceName) { sourceIdx = i; break; }
                }
                if (sourceIdx >= 0) sourceBounds = sourceScreen.Bounds;
            }

            var result = new MonitorWorkArea
            {
                Ok = true,
                Index = idx + 1,
                Count = screens.Length,
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

        private static long GetRectIntersectionArea(ScreenBounds a, ScreenBounds b)
        {
            int left = Math.Max(a.Left, b.Left);
            int top = Math.Max(a.Top, b.Top);
            int right = Math.Min(a.Right, b.Right);
            int bottom = Math.Min(a.Bottom, b.Bottom);
            if (right <= left || bottom <= top) return 0;
            return (long)(right - left) * (bottom - top);
        }

        private static int GetCornerPreference(string edge, string corner)
        {
            string edgeValue = (edge ?? "").Trim().ToLowerInvariant();
            var orders = new Dictionary<string, string[]>
            {
                ["right"] = new[] { "top_right", "bottom_right", "top_left", "bottom_left" },
                ["left"] = new[] { "top_left", "bottom_left", "top_right", "bottom_right" },
                ["top"] = new[] { "top_right", "top_left", "bottom_right", "bottom_left" },
                ["bottom"] = new[] { "bottom_right", "bottom_left", "top_right", "top_left" },
            };
            var order = orders.TryGetValue(edgeValue, out var o) ? o : orders["bottom"];
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] == corner) return i;
            }
            return 99;
        }

        public sealed class ParkedRect
        {
            public int X, Y, FallbackX, FallbackY, Width, Height, ClipX, ClipY, ClipWidth, ClipHeight;
            public string Edge, Corner;
            public int VisiblePixels;
            public long VisibleArea, WorkVisibleArea;
        }

        private sealed class CornerCandidate
        {
            public string Corner;
            public int X, Y;
            public long MonitorArea, WorkArea;
            public int Preference;
        }

        public static ParkedRect GetDiscordProjectorParkedRect(MonitorWorkArea workArea, int windowWidth, int windowHeight, string edge, int visiblePixels)
        {
            int left = workArea.Left, top = workArea.Top, right = workArea.Right, bottom = workArea.Bottom;
            if (right <= left || bottom <= top) throw new InvalidOperationException("Monitor work area is invalid.");

            string edgeValue = (edge ?? "").Trim().ToLowerInvariant();
            if (edgeValue != "right" && edgeValue != "left" && edgeValue != "top" && edgeValue != "bottom")
                throw new InvalidOperationException("Invalid projector edge: " + edge);

            int workWidth = right - left;
            int workHeight = bottom - top;
            int screenLeft = workArea.BoundsLeft;
            int screenTop = workArea.BoundsTop;
            int screenWidth = workArea.BoundsWidth;
            int screenHeight = workArea.BoundsHeight;
            int screenRight = workArea.BoundsRight;
            int screenBottom = workArea.BoundsBottom;
            if (screenRight <= screenLeft || screenBottom <= screenTop)
            {
                screenLeft = left; screenTop = top; screenRight = right; screenBottom = bottom;
                screenWidth = workWidth; screenHeight = workHeight;
            }
            int sourceWidth = workArea.HasSourceBounds ? workArea.SourceBoundsWidth : screenWidth;
            int sourceHeight = workArea.HasSourceBounds ? workArea.SourceBoundsHeight : screenHeight;
            if (sourceWidth < 1 || sourceHeight < 1) throw new InvalidOperationException("Projector source monitor bounds are invalid.");
            int autoWidth = Math.Max(1, sourceWidth);
            int autoHeight = Math.Max(1, sourceHeight);
            int width = windowWidth <= 0 ? Math.Max(1, autoWidth) : Math.Max(1, windowWidth);
            int height = windowHeight <= 0 ? Math.Max(1, autoHeight) : Math.Max(1, windowHeight);

            int visible = Math.Min(Math.Max(1, visiblePixels), Math.Min(width, height));
            var workRect = new ScreenBounds { Left = left, Top = top, Right = right, Bottom = bottom };
            var screenBounds = GetScreenBoundsList(workArea, screenLeft, screenTop, screenRight, screenBottom);
            var candidates = new[]
            {
                new { Corner = "top_left", X = left - width + visible, Y = top - height + visible },
                new { Corner = "top_right", X = right - visible, Y = top - height + visible },
                new { Corner = "bottom_left", X = left - width + visible, Y = bottom - visible },
                new { Corner = "bottom_right", X = right - visible, Y = bottom - visible },
            };
            var scored = new List<CornerCandidate>();
            foreach (var candidate in candidates)
            {
                var rect = new ScreenBounds { Left = candidate.X, Top = candidate.Y, Right = candidate.X + width, Bottom = candidate.Y + height };
                long monitorArea = 0;
                foreach (var bounds in screenBounds) monitorArea += GetRectIntersectionArea(rect, bounds);
                scored.Add(new CornerCandidate
                {
                    Corner = candidate.Corner,
                    X = candidate.X,
                    Y = candidate.Y,
                    MonitorArea = monitorArea,
                    WorkArea = GetRectIntersectionArea(rect, workRect),
                    Preference = GetCornerPreference(edgeValue, candidate.Corner),
                });
            }
            var best = scored
                .OrderBy(c => c.MonitorArea)
                .ThenBy(c => Math.Abs(c.WorkArea - (long)visible * visible))
                .ThenBy(c => c.Preference)
                .ThenBy(c => c.Corner, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best == null) throw new InvalidOperationException("Could not resolve a safe projector parking corner.");

            int hostX = screenLeft;
            int hostY = screenTop;
            if (width <= screenWidth && best.Corner.EndsWith("_right")) hostX = screenRight - width;
            if (height <= screenHeight && best.Corner.StartsWith("bottom_")) hostY = screenBottom - height;

            int clipX = best.Corner.EndsWith("_right") ? right - hostX - visible : left - hostX;
            int clipY = best.Corner.StartsWith("bottom_") ? bottom - hostY - visible : top - hostY;
            clipX = Math.Min(Math.Max(0, clipX), Math.Max(0, width - visible));
            clipY = Math.Min(Math.Max(0, clipY), Math.Max(0, height - visible));

            return new ParkedRect
            {
                X = hostX, Y = hostY, FallbackX = best.X, FallbackY = best.Y,
                Width = width, Height = height,
                ClipX = clipX, ClipY = clipY, ClipWidth = visible, ClipHeight = visible,
                Edge = edgeValue, Corner = best.Corner,
                VisiblePixels = visible, VisibleArea = best.MonitorArea, WorkVisibleArea = best.WorkArea,
            };
        }

        public sealed class InspectRect
        {
            public int X, Y, Width, Height;
        }

        public static InspectRect GetDiscordProjectorInspectRect(MonitorWorkArea workArea, int windowWidth, int windowHeight)
        {
            int left = workArea.Left, top = workArea.Top, right = workArea.Right, bottom = workArea.Bottom;
            if (right <= left || bottom <= top) throw new InvalidOperationException("Monitor work area is invalid.");

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

        private static readonly string ProjectorHandoffPath = Path.Combine(Constants.SCRATCH_DIR, "obsreplaykit_projector_windows.txt");
        private const int ProjectorHandoffMaxAgeSeconds = 2;

        // reads the tray plugins (replaykit-tray.cpp) published list of hwnds obs itself has marked with isOBSProjectorWindow -- the actual authoritative signal obs uses internally, republished every 250ms via an atomic write so this never sees a half-written file. returns null (not an empty list) when the file is missing or older than a couple publish cycles, so the caller can tell "confirmed no projectors exist" apart from "the tray plugin isnt running or obs is mid-shutdown, this genuinely doesnt know" and fall back accordingly -- an empty list here would silently collapse those two very different situations into one.
        private static List<long> GetProjectorHwndsFromTrayPlugin()
        {
            FileInfo info;
            try
            {
                info = new FileInfo(ProjectorHandoffPath);
                if (!info.Exists) return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds > ProjectorHandoffMaxAgeSeconds) return null;

            var hwnds = new List<long>();
            try
            {
                foreach (var line in File.ReadAllLines(ProjectorHandoffPath))
                {
                    if (long.TryParse(line.Trim(), out long n) && n != 0) hwnds.Add(n);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            return hwnds;
        }

        // the tray-plugin signal above is authoritative when available -- prefer it. FindAllObsWindows (window class + no-owner heuristic) is the fallback for an older bundle without the tray plugin update, or the brief window before the tray plugins first publish after obs startup.
        private static List<long> GetObsWindowCandidates()
        {
            var fromTray = GetProjectorHwndsFromTrayPlugin();
            if (fromTray != null) return fromTray;
            return new List<long>(ProjectorNative.FindAllObsWindows());
        }

        // true only for a hwnd that appeared after baseline was captured -- a window already present in baseline is off limits no matter what it looks like, becuase it could be a projector the user opened themselves from OBSs own menu (same title pattern, no way to tell them apart except by "did it exist before a projector was asked for").
        private static bool IsNewProjectorWindow(long hwnd, List<long> baseline) => !baseline.Contains(hwnd);

        private static long GetFirstNewProjectorWindow(List<long> baseline)
        {
            foreach (var candidate in GetObsWindowCandidates())
            {
                if (IsNewProjectorWindow(candidate, baseline)) return candidate;
            }
            return 0;
        }

        private static void CloseStrayProjectorWindows(List<long> baseline, long keepHwnd)
        {
            foreach (var candidate in GetObsWindowCandidates())
            {
                if (candidate == keepHwnd) continue;
                if (!IsNewProjectorWindow(candidate, baseline)) continue;
                ProjectorNative.CloseWindow(candidate);
                Log.Write("Closed stray Discord projector window hwnd=" + candidate + " left over from an earlier slow-to-appear attempt");
            }
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
                Server.State.ReplaykitDiscordProjectorPendingBaseline = null;
                return new OpenProjectorResult { Ok = true, Hwnd = hwnd, Opened = false };
            }

            // an earlier attempt may still be mid-create on obss side even though this gave up waiting for it last time -- if one is already pending, reuse that same baseline instead of re-snapshotting now, or its own not-yet-appeared window would be wrongly treated as "pre-existing, not ours" the moment it finally shows up. only take a fresh baseline when nothing is pending.
            if (Server.State.ReplaykitDiscordProjectorPendingBaseline == null)
                Server.State.ReplaykitDiscordProjectorPendingBaseline = GetObsWindowCandidates();
            var baseline = Server.State.ReplaykitDiscordProjectorPendingBaseline;

            Log.Write("Opening OBS Windowed Projector for title '" + title + "'");
            var open = ObsWebSocket.InvokeRequest("OpenVideoMixProjector", new JObject { ["videoMixType"] = "OBS_WEBSOCKET_VIDEO_MIX_TYPE_PROGRAM", ["monitorIndex"] = -1 }, 5000);
            if (!open.Ok)
            {
                Log.Write("warn: OpenVideoMixProjector request failed: " + open.Message);
                return new OpenProjectorResult { Ok = false, Hwnd = 0, Opened = false, Message = "Opening OBS Windowed Projector failed: " + open.Message };
            }

            // 24x250ms=6s -- a slow pc that misses a shorter window would report "not found" while obs was still mid-create, leaving that projector un-renamed and un-hidden until some later cycle happened to trip over it. matching against baseline (not title text) so this works regardless of obss ui language -- a non-english install localizes the windows own title, but never localizes what obs process it belongs to or when it appeared, which is all this actually checks now.
            for (int i = 0; i < 24; i++)
            {
                Thread.Sleep(250);
                long newHwnd = GetFirstNewProjectorWindow(baseline);
                if (newHwnd != 0)
                {
                    ProjectorNative.PreParkProjectorWindow(newHwnd, title);
                    ProjectorNative.SetProjectorTaskbarHidden(newHwnd, true);
                    int extra = ProjectorNative.CloseDuplicateProjectorWindows(title, newHwnd);
                    if (extra > 0) Log.Write("Closed " + extra + " leaked Discord projector window(s)");
                    CloseStrayProjectorWindows(baseline, newHwnd);
                    Server.State.ReplaykitDiscordProjectorPendingBaseline = null;
                    Log.Write("Opened OBS projector: hwnd=" + newHwnd + " after " + (i + 1) + " poll(s) (" + ((i + 1) * 250) + "ms)");
                    return new OpenProjectorResult { Ok = true, Hwnd = newHwnd, Opened = true };
                }
            }
            Log.Write("warn: OBS Windowed Projector opened but never appeared for title '" + title + "' after 6s (still watching -- next check will catch it late instead of opening another)");
            return new OpenProjectorResult { Ok = false, Hwnd = 0, Opened = true, Message = "OBS Windowed Projector was opened, but its window could not be found." };
        }

        private static JObject ParkedRectToJson(ParkedRect r) => new JObject
        {
            ["x"] = r.X, ["y"] = r.Y, ["fallback_x"] = r.FallbackX, ["fallback_y"] = r.FallbackY,
            ["width"] = r.Width, ["height"] = r.Height,
            ["clip_x"] = r.ClipX, ["clip_y"] = r.ClipY, ["clip_width"] = r.ClipWidth, ["clip_height"] = r.ClipHeight,
            ["edge"] = r.Edge, ["corner"] = r.Corner,
            ["visible_pixels"] = r.VisiblePixels, ["visible_area"] = r.VisibleArea, ["work_visible_area"] = r.WorkVisibleArea,
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

        // finds/opens/parks the projector hidden in a screen corner (topmost + alpha-0 layered, so its click-thru and invisible but still discoverable in discords screen picker). runs the whole find/open/park sequence under ProjectorLock -- confirmed via a real user log (in the ps original) that without this, a helper restart firing this function at the same moment as the keep-alive tick could see no projector from both, and both would open one. Inspect and Disable take the same lock for their own find/open/close sequences so none of the three can interleave with each other either.
        public static JObject Repark(JObject settings = null, string titleOverride = "", bool force = false)
        {
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
                try
                {
                    int visiblePixels = settings["discord_projector_visible_pixels"]?.Value<int>() ?? 0;
                    rect = GetDiscordProjectorParkedRect(workArea, size.Width, size.Height, settings["discord_projector_edge"]?.Value<string>() ?? "", visiblePixels);
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add(ex.Message);
                    return new JObject { ["ok"] = false, ["message"] = ex.Message, ["warnings"] = new JArray(warnings) };
                }

                Log.Write(string.Format("Parking Discord projector at corner={0}, visible_pixels={1}, visible_area={2}, window=({3},{4},{5},{6}), mode=hidden",
                    rect.Corner, rect.VisiblePixels, rect.VisibleArea, rect.X, rect.Y, rect.Width, rect.Height));
                bool alreadyPlaced = !open.Opened && ProjectorNative.IsWindowAtRectAndTitle(open.Hwnd, title, rect.X, rect.Y, rect.Width, rect.Height);
                bool parked = ProjectorNative.RestoreResizeTitleChromeGhosted(open.Hwnd, title, rect.X, rect.Y, rect.Width, rect.Height, true);
                if (!parked)
                {
                    ProjectorNative.PreParkProjectorWindow(open.Hwnd, title);
                    string msg = "OBS projector window was found, but Windows rejected the ghosted park request.";
                    warnings.Add(msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["warnings"] = new JArray(warnings) };
                }

                const bool hideProjectorTaskbar = true;
                if (hideProjectorTaskbar)
                {
                    if (alreadyPlaced && Server.State.ReplaykitDiscordProjectorTaskbarHiddenApplied)
                    {
                        Server.State.ReplaykitDiscordProjectorTaskbarHiddenApplied = true;
                    }
                    else
                    {
                        bool hidden = ProjectorNative.SetProjectorTaskbarHidden(open.Hwnd, true);
                        if (!hidden)
                        {
                            Server.State.ReplaykitDiscordProjectorTaskbarHiddenApplied = false;
                            string msg = "OBS projector window was moved, but Windows rejected the taskbar-hide request.";
                            warnings.Add(msg);
                            Log.Write("warn: " + msg);
                        }
                        else
                        {
                            Server.State.ReplaykitDiscordProjectorTaskbarHiddenApplied = true;
                        }
                    }
                }

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
            if (settings == null) settings = ReplaykitSettings.ReadSettings();
            string mode = settings["discord_output_mode"]?.Value<string>() ?? "";
            if (mode != "projector") return new JObject { ["ok"] = false, ["message"] = "Discord projector is not the active Share Preview mode.", ["warnings"] = new JArray() };
            if (!(settings["discord_screenshare_enabled"]?.Value<bool>() ?? true))
                return new JObject { ["ok"] = false, ["message"] = "Discord screenshare support is disabled.", ["warnings"] = new JArray() };

            lock (Server.State.ProjectorLock)
            {
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
                    string msg = "OBS projector window was found, but Windows rejected the move/resize request.";
                    warnings.Add(msg);
                    return new JObject { ["ok"] = false, ["message"] = msg, ["warnings"] = new JArray(warnings) };
                }

                ProjectorNative.SetProjectorTaskbarHidden(open.Hwnd, false);
                Server.State.ReplaykitDiscordProjectorTaskbarHiddenApplied = false;
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
            if (settings == null) settings = ReplaykitSettings.ReadSettings();
            var warnings = new List<string>();
            string title = string.IsNullOrWhiteSpace(titleOverride) ? GetProjectorShareTitle(settings) : titleOverride;

            lock (Server.State.ProjectorLock)
            {
                Server.State.ReplaykitDiscordProjectorInspectMode = false;
                // disabling ends any open attempt still being tracked -- a stale baseline from before this disable shouldnt carry into whatever happens the next time its turned back on.
                Server.State.ReplaykitDiscordProjectorPendingBaseline = null;
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

                string msg = "OBS projector window was found, but it did not close after repeated attempts.";
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
                return new AudioDeviceResult { Ok = false, Message = "Could not inspect OBS Stream Audio playback endpoint: " + ex.Message };
            }

            var selected = candidates.OrderBy(c => c.Rank).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
            if (selected.Id == null)
                return new AudioDeviceResult { Ok = false, Message = "OBS Stream Audio playback endpoint is not active. Install or enable VB-Audio Cable, then re-run Share Preview." };
            return new AudioDeviceResult { Ok = true, Id = selected.Id, Source = "obs_stream_audio", Name = selected.Name };
        }

        private static DateTime _keepAliveAt = DateTime.MinValue;
        // this tick runs on the main accept-loop thread (see Runtime.cs), never a pool thread, and must stay that way -- the win32 reposition call underneath it can block waiting on obss own ui thread if obs is busy, which on the pooled model only briefly delays the next new connection being accepted, not any in-flight request.
        private const int KeepAliveSeconds = 30;
        private static int _keepAliveFailures;
        // open-if-missing has no upper bound tighter than a 5s websocket call plus a 6s find-poll, and that whole sequence runs on this same main thread while holding ProjectorLock -- confirmed via a real user log (in the ps original) that on a machine where the projector genuinely fails to appear, every single 30s tick re-attempts and re-blocks the entire helper for the full open+poll duration, starving every other connection. capping the backoff here so a persistently-broken projector gets retried every few minutes instead of every 30s forever, once its clearly not a one-off miss.
        private const int KeepAliveMaxBackoffSeconds = 300;

        public static void KeepAlive()
        {
            var now = DateTime.UtcNow;
            double intervalSeconds = KeepAliveSeconds;
            if (_keepAliveFailures > 1)
            {
                double backoff = KeepAliveSeconds * Math.Pow(2, _keepAliveFailures - 1);
                intervalSeconds = Math.Min(backoff, KeepAliveMaxBackoffSeconds);
            }
            if ((now - _keepAliveAt).TotalSeconds < intervalSeconds) return;
            _keepAliveAt = now;
            Log.Write("Discord projector keep-alive tick");

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
