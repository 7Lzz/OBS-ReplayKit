using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json.Linq;
using static ReplayKitHelper.Routes;

namespace ReplayKitHelper
{
    // the routes of the streamable account: sign in windows, the profile and its picture, log out and session import
    internal static class AccountRoutes
    {
        // answers the request when its path is one of these routes, with what DispatchRequest returns; null when it is not
        public static bool? Handle(RouteRequest r)
        {
            Stream stream = r.Stream;
            HttpRequest req = r.Req;
            string path = r.Path;
            bool QFlag(string key) => r.QFlag(key);


            if (path == "/signin-windows-status")
            {
                // returns visible sign-in popups owned by obss process family so the dock can update its status label. with overlay=1, the google oauth popup is moved onto the streamable login window so the flow presents as one window while preserving streamables popup-based oauth.
                if (req.Method != "GET") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "GET required"); return false; }
                uint ownerPid = ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0;
                string jsonArr = "[]";
                try
                {
                    bool overlayGoogle = QFlag("overlay");
                    jsonArr = Native.ListSignInWindows(ownerPid, false, overlayGoogle);
                }
                catch (Exception ex) { Log.Write("/signin-windows-status: " + ex.Message); }
                // give the sign-in popups our icon + themed title bar -- obs-browser doesnt icon its own window.open popups. dedup is title-keyed inside StyleSignInWindows so this poll-driven call only paints when a new page title shows up.
                try
                {
                    string iconPath = Constants.OBS_ICON_PATH;
                    string resolved = ReplaykitSettings.ResolveAppIconPath(ReplaykitSettings.Normalize(ReplaykitSettings.ReadSettings()));
                    if (!string.IsNullOrEmpty(resolved)) iconPath = resolved;
                    Native.StyleSignInWindows(iconPath);
                }
                catch (Exception ex) { Log.Write("/signin-windows-status style: " + ex.Message); }
                var h = HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "application/json" });
                HttpResponse.SendBytes(stream, 200, "OK", h, Encoding.UTF8.GetBytes(jsonArr));
                return false;
            }

            if (path == "/close-signin-windows")
            {
                // post to nuke the stuck sign-in popups via win32 wm_close. the dock js calls this after /import-session succeeds becuase cef refuses window.close() on cross-origin popups but windows-level wm_close works fine. we match only windows owned by the parent obs process, by title.
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                uint ownerPid = ParentWatchdog.ParentPid > 0 ? (uint)ParentWatchdog.ParentPid : 0;
                // specific titles only. we deliberately do not include a bare "streamable" since that would also match the clip-viewer popup (clips.html) and any other dock title that contains "streamable".
                string[] titles = { "about:blank", "Sign In - Google Accounts", "Sign in - Google Accounts", "Signing in to Streamable", "Dashboard - Streamable", "Log in - Streamable", "Sign in to Streamable - Streamable", "Streamable | " };
                int n = 0;
                try
                {
                    n = Native.CloseWindowsByTitle(titles, ownerPid);
                    Native.ResetSignInWindowStyleCache(); // next attempt restyles from scratch
                    Log.Write("/close-signin-windows: closed " + n + " window(s) under OBS pid=" + ownerPid);
                }
                catch (Exception ex) { Log.Write("/close-signin-windows: " + ex.Message); }
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["closed"] = n });
                return false;
            }

            if (path == "/me")
            {
                var a = Server.State.Auth;
                string masked = Constants.GetMaskedIdentity(a.Username);
                HttpResponse.SendJson(stream, 200, new JObject
                {
                    ["signedIn"] = a.SignedIn,
                    ["username"] = "",
                    ["displayName"] = a.SignedIn ? "Signed in" : "",
                    ["maskedUsername"] = a.SignedIn ? masked : "",
                    ["plan"] = a.Plan,
                    ["sizeCap"] = a.SizeCap,
                    ["maxUploadBytes"] = Constants.GetEffectiveUploadCap(),
                    ["maxDurationSec"] = Upload.SubjectToStreamableDurationLimit() ? Constants.STREAMABLE_FREE_MAX_DURATION_SEC : 0,
                    ["retentionDays"] = Constants.GetEffectiveRetentionDays(),
                    ["picture"] = ProfilePicture.Version(),
                });
                return false;
            }

            if (path == "/profile-picture")
            {
                byte[] picture = null;
                try { if (File.Exists(ProfilePicture.FilePath)) picture = File.ReadAllBytes(ProfilePicture.FilePath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Write("/profile-picture read: " + ex.Message); }
                if (picture == null) { HttpResponse.SendJson(stream, 404, new JObject { ["ok"] = false }); return false; }
                HttpResponse.SendBytes(stream, 200, "OK", HttpResponse.GetNoStoreHeaders(new Dictionary<string, string> { ["Content-Type"] = "image/png" }), picture);
                return false;
            }

            // the account menu picks a picture or removes it; the plugin is told so the circle in the title bar follows at once instead of on its next poll
            if (path == "/profile-picture/pick" || path == "/profile-picture/clear")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try
                {
                    if (path == "/profile-picture/clear") ProfilePicture.Clear();
                    else
                    {
                        string picked = FilePickers.PickPath(false, FilePickers.ImagesFilter, "");
                        if (string.IsNullOrEmpty(picked))
                        {
                            HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["changed"] = false, ["picture"] = ProfilePicture.Version() });
                            return false;
                        }
                        ProfilePicture.Save(picked);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.ExternalException)
                {
                    // gdi+ reports a file it cannot decode as out of memory or an argument error
                    Log.Write(path + ": " + ex.GetType().Name + ": " + ex.Message);
                    HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = false, ["message"] = ex is InvalidDataException ? ex.Message : "That file could not be used as a picture." });
                    return false;
                }
                PipeClient.SendAccountChanged();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true, ["changed"] = true, ["picture"] = ProfilePicture.Version() });
                return false;
            }

            if (path == "/logout")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                try { AuthCore.StreamableBackgroundLogout(); } catch (Exception ex) { Log.Write("Background logout failed: " + ex.Message); }
                AuthCore.ClearAuth();
                StreamableSignIn.ClearProfile();
                AppConfig.ClearClipsCache();
                HttpResponse.SendJson(stream, 200, new JObject { ["ok"] = true });
                return false;
            }

            if (path == "/import-session")
            {
                if (req.Method != "POST") { HttpResponse.SendText(stream, 405, "Method Not Allowed", "POST required"); return false; }
                var status = QFlag("start") ? StreamableSignIn.OpenOrGetStatus() : StreamableSignIn.GetStatus();
                if (status.Value<bool?>("signedIn") == true) AppConfig.ClearClipsCache();
                status["maxUploadBytes"] = Constants.GetEffectiveUploadCap();
                status["maxDurationSec"] = Upload.SubjectToStreamableDurationLimit() ? Constants.STREAMABLE_FREE_MAX_DURATION_SEC : 0;
                status["retentionDays"] = Constants.GetEffectiveRetentionDays();
                HttpResponse.SendJson(stream, status.Value<bool?>("ok") == true ? 200 : 500, status);
                return false;
            }

            return null;
        }
    }
}
