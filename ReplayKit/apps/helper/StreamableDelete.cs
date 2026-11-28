using System;
using System.IO;
using System.Text;

namespace ReplayKitHelper
{
    internal static class StreamableDelete
    {
        public static (int Code, string Message) Delete(Clips.SafeClipPath clip, string expectedUrl)
        {
            string folder = Path.GetDirectoryName(clip.Full);
            bool deleted = false;
            string jar = "";
            try
            {
                using var reservation = JobCoordinator.TryReserve(UploadState.NewRequestId(), clip.Name, clip.Full, "delete-upload", out string busy);
                if (reservation == null) return (409, busy);
                if (!File.Exists(clip.Full)) return (404, Locales.Message("common.clipNotFound"));

                string url = Clips.GetLink(clip.Name, folder);
                if (!Upload.TryParseStreamableShortcode(url, out string shortcode) || !string.Equals(url, expectedUrl, StringComparison.Ordinal))
                    return (409, Locales.Message("clips.streamableLinkChanged"));

                string cookies;
                lock (Server.State.AuthLock)
                {
                    if (!Server.State.Auth.SignedIn) return (403, Locales.Message("common.signInToStreamable"));
                    cookies = AuthCore.CaptureCookies();
                }
                if (string.IsNullOrWhiteSpace(cookies)) return (403, Locales.Message("share.signedInStreamableSessionIsUnavailable"));

                // the requests run on a copy of the session, so a sign in, sign out or upload start does not wait on them
                Directory.CreateDirectory(Constants.SCRATCH_DIR);
                jar = Path.Combine(Constants.SCRATCH_DIR, "streamable_delete_" + Guid.NewGuid().ToString("N") + ".cookies.txt");
                File.WriteAllText(jar, cookies, Encoding.ASCII);

                string videoUrl = Constants.STREAMABLE_API + "/api/v1/videos/" + shortcode;
                var response = Curl.Run("-q", "-sS", "--max-time", "10", "--max-filesize", "4096", "-b", jar,
                    "-H", "Origin: https://streamable.com", "-H", "Referer: https://streamable.com/",
                    "-X", "DELETE", "-w", "\\n%{http_code}", videoUrl);

                // the dashboard requires a literal true body to confirm deletion.
                int separator = response.Stdout.LastIndexOf('\n');
                int status = 0;
                deleted = response.ExitCode == 0 && separator >= 0 &&
                    int.TryParse(response.Stdout.Substring(separator + 1).Trim(), out status) && status >= 200 && status < 300 &&
                    response.Stdout.Substring(0, separator).Trim() == "true";
                // a video that is already gone, such as one deleted on the site, cannot be deleted again, so a refused delete removes the link only when a lookup of the video answers 404, which streamable gives only for a video that no longer exists
                if (!deleted && status >= 400 && status < 500)
                {
                    var lookup = Curl.Run("-q", "-sS", "--max-time", "10", "-b", jar, "-o", "NUL", "-w", "%{http_code}", videoUrl);
                    deleted = lookup.ExitCode == 0 && lookup.Stdout.Trim() == "404";
                    if (deleted) Log.Write("Delete Streamable upload: the video was already gone, removing its link");
                }
                if (!deleted) return (502, Locales.Message("clips.streamableDeleteFailed"));

                Clips.RemoveLink(clip.Name, url, folder);
                return (200, null);
            }
            catch (Exception ex)
            {
                Log.Write("Delete Streamable upload: " + ex.GetType().Name);
                return (500, deleted ? Locales.Message("clips.streamableDeletedLinkRemains") : Locales.Message("clips.streamableDeleteFailed"));
            }
            finally
            {
                if (jar.Length > 0)
                    try { File.Delete(jar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
