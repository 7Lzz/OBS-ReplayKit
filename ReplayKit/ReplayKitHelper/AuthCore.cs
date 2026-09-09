using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // auth storage (dpapi). saves the streamable session cookie + the user info json, encrypted with the current windows users dpapi master key -- so its tied to this account, never leaves disk readable, and survives reboots without us needing to store a password. ported from obs_replaykit helper modules/10_auth_core.ps1.
    internal static class AuthCore
    {
        // path to the curl cookie jar handed to the in-process upload orchestration (Upload.cs) whenever the user is signed in. ApplyAuth rewrites it from the decrypted blob, so the cookies live on disk in plaintext only while a session is active.
        public static string GetAuthCookieJarPath() => Path.Combine(Constants.AUTH_DIR, "auth_cookies.txt");

        // persists a curl cookie jar (imported from a chromium browsers streamable.com session) encrypted with the current users dpapi key, so the session never lives on disk in plain text.
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

        public static void ClearAuthBlob()
        {
            try { if (File.Exists(Constants.AUTH_FILE)) File.Delete(Constants.AUTH_FILE); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            string jar = GetAuthCookieJarPath();
            try { if (File.Exists(jar)) File.Delete(jar); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        public static void StreamableBackgroundLogout()
        {
            string jar = GetAuthCookieJarPath();
            if (!File.Exists(jar)) return;
            string respPath = Path.Combine(Constants.SCRATCH_DIR, "streamable_logout_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                string url = "https://streamable.com/logout?_cb=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var r = Curl.Run("-s", "-S", "--max-time", "10", "-L", "-b", jar, "-c", jar, "-o", respPath, "-w", "%{http_code}", url);
                string codeText = (r.Stdout + r.Stderr).Trim();
                Log.Write("Background Streamable logout http=" + codeText + " curlExit=" + r.ExitCode);
            }
            finally
            {
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
            else { size = Constants.ANON_SIZE_CAP; ret = Constants.SIGNED_IN_DEFAULT_RETENTION; }
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
                Server.State.Auth = new AuthState
                {
                    SignedIn = user != null,
                    Username = username,
                    Plan = limits.PlanLabel,
                    SizeCap = limits.SizeCap,
                    RetentionDays = limits.RetentionDays,
                };
            }
            // write the plaintext cookie jar to disk for the in-process upload orchestration.
            if (!string.IsNullOrEmpty(cookieFile))
            {
                Directory.CreateDirectory(Constants.AUTH_DIR);
                File.WriteAllText(GetAuthCookieJarPath(), cookieFile, Encoding.ASCII);
            }
        }

        public static void ClearAuth()
        {
            lock (Server.State.AuthLock)
            {
                Server.State.Auth = new AuthState
                {
                    SignedIn = false,
                    Username = "",
                    Plan = "",
                    SizeCap = Constants.ANON_SIZE_CAP,
                    RetentionDays = Constants.ANON_RETENTION_DAYS,
                };
            }
            ClearAuthBlob();
        }

        // validate an existing session by hitting /api/v1/me. returns the user object on success, null otherwise. called at startup from the dpapi-stored blob; if streamables session has expired the stored auth gets dropped.
        public static JObject InvokeStreamableMe(string cookieFileContent)
        {
            string jar = Path.Combine(Constants.SCRATCH_DIR, "streamable_me_" + Guid.NewGuid().ToString("N") + ".txt");
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
                if (!int.TryParse(r.Stdout.Trim(), out int code) || code < 200 || code >= 300) return null;
                if (!File.Exists(respPath)) return null;
                string body = File.ReadAllText(respPath);
                try
                {
                    var u = JObject.Parse(body);
                    Log.Write("Streamable session validated.");
                    return u;
                }
                catch (JsonException)
                {
                    Log.Write("Streamable /me: invalid JSON response.");
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                return null;
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
            var blob = LoadAuthBlob();
            string cookies = blob?["cookies"]?.Value<string>();
            if (blob == null || string.IsNullOrEmpty(cookies)) return;
            var live = InvokeStreamableMe(cookies);
            if (live != null)
            {
                ApplyAuth(live, cookies);
                Log.Write("Restored signed-in session.");
                return;
            }
            Log.Write("Stored auth blob exists but Streamable rejected the session -- clearing.");
            ClearAuthBlob();
        }
    }
}
