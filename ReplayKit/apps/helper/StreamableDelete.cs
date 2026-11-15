using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ReplayKitHelper
{
    internal static class StreamableDelete
    {
        public static (int Code, string Message) Delete(Clips.SafeClipPath clip, string expectedUrl)
        {
            string folder = Path.GetDirectoryName(clip.Full);
            bool deleted = false;
            try
            {
                using var reservation = JobCoordinator.TryReserve(UploadState.NewRequestId(), clip.Name, clip.Full, "delete-upload", out string busy);
                if (reservation == null) return (409, busy);
                if (!File.Exists(clip.Full)) return (404, Locales.Message("common.clipNotFound"));

                string url = Clips.GetLink(clip.Name, folder);
                var match = Regex.Match(url ?? "", @"\Ahttps://streamable\.com/([A-Za-z0-9_-]{1,64})\z");
                if (!match.Success || !string.Equals(url, expectedUrl, StringComparison.Ordinal))
                    return (409, Locales.Message("clips.streamableLinkChanged"));

                Curl.Result response;
                lock (Server.State.AuthLock)
                {
                    if (!Server.State.Auth.SignedIn) return (403, Locales.Message("common.signInToStreamable"));
                    string jar = AuthCore.GetAuthCookieJarPath();
                    if (!File.Exists(jar) || new FileInfo(jar).Length == 0)
                        return (403, Locales.Message("share.signedInStreamableSessionIsUnavailable"));

                    response = Curl.Run("-q", "-sS", "--max-time", "10", "--max-filesize", "4096", "-b", jar,
                        "-H", "Origin: https://streamable.com", "-H", "Referer: https://streamable.com/",
                        "-X", "DELETE", "-w", "\\n%{http_code}",
                        Constants.STREAMABLE_API + "/api/v1/videos/" + match.Groups[1].Value);
                }

                // the dashboard requires a literal true body to confirm deletion.
                int separator = response.Stdout.LastIndexOf('\n');
                deleted = response.ExitCode == 0 && separator >= 0 &&
                    int.TryParse(response.Stdout.Substring(separator + 1), out int status) && status >= 200 && status < 300 &&
                    response.Stdout.Substring(0, separator).Trim() == "true";
                if (!deleted) return (502, Locales.Message("clips.streamableDeleteFailed"));

                Clips.RemoveLink(clip.Name, url, folder);
                return (200, null);
            }
            catch (Exception ex)
            {
                Log.Write("Delete Streamable upload: " + ex.GetType().Name);
                return (500, deleted ? Locales.Message("clips.streamableDeletedLinkRemains") : Locales.Message("clips.streamableDeleteFailed"));
            }
        }
    }
}
