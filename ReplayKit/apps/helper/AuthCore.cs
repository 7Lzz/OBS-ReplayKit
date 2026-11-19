using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // auth storage (dpapi). saves the streamable session cookie + the user info json, encrypted with the current windows users dpapi master key -- so its tied to this account, never leaves disk readable, and survives reboots without us needing to store a password.
    internal static class AuthCore
    {
        private static string _cookies = "";
        private static int _sessionVersion;
        private static string LegacyCookieJar => Path.Combine(Constants.AUTH_DIR, ReplayKitShared.StateLayout.StreamableLegacyCookieFile);

        public static string CaptureCookies()
        {
            lock (Server.State.AuthLock) return _cookies;
        }

        private static bool DeleteAuthFile(string file)
        {
            try { File.Delete(file); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Write("Could not remove sign-in file " + Path.GetFileName(file) + ": " + ex.GetType().Name);
                return false;
            }
        }

        // persists a curl cookie jar (the session of the streamable sign-in window) encrypted with the current users dpapi key, so the session never lives on disk in plain text.
        public static void SaveAuthBlob(string cookieFileContent, JObject user)
        {
            Directory.CreateDirectory(Constants.AUTH_DIR);
            var payload = new JObject
            {
                ["cookies"] = cookieFileContent,
                ["user"] = user,
                ["savedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Constants.AUTH_FILE, encrypted);
        }

        public static JObject LoadAuthBlob()
        {
            if (!File.Exists(Constants.AUTH_FILE)) return null;
            try
            {
                byte[] enc = File.ReadAllBytes(Constants.AUTH_FILE);
                byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(dec);
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Log.Write("Load-AuthBlob: " + ex.Message);
                return null;
            }
        }

        // false when a saved sign-in file is still there, so a sign out can say it did not finish instead of signing back in on the next start
        public static bool ClearAuthBlob()
        {
            bool encryptedCleared = DeleteAuthFile(Constants.AUTH_FILE);
            return DeleteAuthFile(LegacyCookieJar) && encryptedCleared;
        }

        public static void StreamableBackgroundLogout()
        {
            string cookies = CaptureCookies();
            if (string.IsNullOrEmpty(cookies)) return;
            string jar = Path.Combine(Constants.SCRATCH_DIR, "streamable_logout_" + Guid.NewGuid().ToString("N") + ".cookies.txt");
            string respPath = Path.Combine(Constants.SCRATCH_DIR, "streamable_logout_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                string url = "https://streamable.com/logout?_cb=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                File.WriteAllText(jar, cookies, Encoding.ASCII);
                var r = Curl.Run("-s", "-S", "--max-time", "10", "-L", "-b", jar, "-o", respPath, "-w", "%{http_code}", url);
                string codeText = r.Stdout.Trim();
                Log.Write("Background Streamable logout http=" + codeText + " curlExit=" + r.ExitCode);
            }
            finally
            {
                DeleteAuthFile(jar);
                try { if (File.Exists(respPath)) File.Delete(respPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // translate a streamable user object into (sizecap, retentiondays). streamable hasnt published an exact byte-cap table for every plan, so we err on the generous side -- once a real user object is in the helper log these numbers can be sharpened.
        private static (long SizeCap, int RetentionDays, string PlanLabel) ResolvePlanLimits(JObject user)
        {
            string plan = "";
            if (user != null)
            {
                foreach (var k in new[] { "plan", "plan_name", "plan_slug", "user_type", "subscription_status" })
                {
                    string v = user[k]?.Value<string>();
                    if (!string.IsNullOrEmpty(v)) { plan = v.ToLowerInvariant(); break; }
                }
            }
            long size; int ret;
            if (Regex.IsMatch(plan, "^(premium|pro|enterprise|business)")) { size = 0; ret = 365; }
            else if (Regex.IsMatch(plan, "^(plus)")) { size = 5L * 1024 * 1024 * 1024; ret = 365; }
            else if (Regex.IsMatch(plan, "^(basic|paid)")) { size = 2L * 1024 * 1024 * 1024; ret = 365; }
            // a free account, or a plan not recognised above, uploads up to the same 1 GB ceiling as an anonymous upload
            else { size = Constants.HARD_UPLOAD_CAP; ret = Constants.SIGNED_IN_DEFAULT_RETENTION; }
            return (size, ret, plan);
        }

        // apply auth state into Server.State.Auth (under AuthLock).
        public static void ApplyAuth(JObject user, string cookieFile)
        {
            var limits = ResolvePlanLimits(user);
            string username = "";
            if (user != null)
            {
                // streamables actual /me response uses user_name. older snapshots used username. some flows populate email instead. check all of them.
                foreach (var k in new[] { "user_name", "username", "email", "name" })
                {
                    string v = user[k]?.Value<string>();
                    if (!string.IsNullOrEmpty(v)) { username = v; break; }
                }
            }
            lock (Server.State.AuthLock)
            {
                _cookies = cookieFile ?? "";
                _sessionVersion++;
                Server.State.Auth = new AuthState
                {
                    SignedIn = user != null,
                    Username = username,
                    Plan = limits.PlanLabel,
                    SizeCap = limits.SizeCap,
                    RetentionDays = limits.RetentionDays,
                };
            }
        }

        public static bool ClearAuth()
        {
            lock (Server.State.AuthLock)
            {
                _cookies = "";
                _sessionVersion++;
                Server.State.Auth = new AuthState
                {
                    SignedIn = false,
                    Username = "",
                    Plan = "",
                    SizeCap = 0,
                    RetentionDays = Constants.ANON_RETENTION_DAYS,
                };
                return ClearAuthBlob();
            }
        }

        public static (JObject User, bool Rejected) InvokeStreamableMe(string cookieFileContent)
        {
            string jar = Path.Combine(Constants.SCRATCH_DIR, "streamable_me_" + Guid.NewGuid().ToString("N") + ".cookies.txt");
            string respPath = Path.Combine(Constants.SCRATCH_DIR, "streamable_me_resp_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(jar, cookieFileContent, Encoding.ASCII);
                string url = Constants.STREAMABLE_API + "/api/v1/me";
                var r = Curl.Run(
                    "-s", "-S", "--max-time", "10",
                    "-b", jar,
                    "-H", "Origin: https://streamable.com",
                    "-H", "Referer: https://streamable.com/",
                    "-H", "Accept: application/json",
                    "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                    "-o", respPath,
                    "-w", "%{http_code}",
                    url);
                if (r.ExitCode != 0 || !int.TryParse(r.Stdout.Trim(), out int code)) return (null, false);
                if (code == 401 || code == 403) return (null, true);
                if (code < 200 || code >= 300 || !File.Exists(respPath)) return (null, false);
                string body = File.ReadAllText(respPath);
                try
                {
                    var u = JObject.Parse(body);
                    Log.Write("Streamable session validated.");
                    return (u, false);
                }
                catch (JsonException)
                {
                    Log.Write("Streamable /me: invalid JSON response.");
                    return (null, false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                return (null, false);
            }
            finally
            {
                try { if (File.Exists(jar)) File.Delete(jar); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                try { if (File.Exists(respPath)) File.Delete(respPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // restore the signed-in session from the dpapi cookie blob. called once at startup. if streamable rejects the cookies (session expired, signed out elsewhere), drop the stored auth quietly.
        public static void RestoreAuthAtStartup()
        {
            string cookies;
            int version;
            lock (Server.State.AuthLock)
            {
                DeleteAuthFile(LegacyCookieJar);
                if (_sessionVersion != 0) return;
                cookies = LoadAuthBlob()?["cookies"]?.Value<string>();
                version = _sessionVersion;
            }
            if (string.IsNullOrEmpty(cookies)) return;
            var live = InvokeStreamableMe(cookies);
            lock (Server.State.AuthLock)
            {
                if (version != _sessionVersion) return;
                if (live.User != null)
                {
                    ApplyAuth(live.User, cookies);
                    Log.Write("Restored signed-in session.");
                }
                else if (live.Rejected)
                {
                    Log.Write("Streamable rejected the saved session; clearing it.");
                    ClearAuthBlob();
                }
                else Log.Write("Streamable session could not be verified; keeping the encrypted sign-in.");
            }
        }
    }
}
