using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace ReplayKitSetup
{
    internal static class Download
    {
        internal static void Save(HttpClient client, string url, string path, long limit, TimeSpan timeout,
            long expectedLength = 0, Action<long, long> progress = null, ISet<string> allowedHosts = null)
        {
            ValidateUri(new Uri(url, UriKind.Absolute), allowedHosts);
            using var deadline = new CancellationTokenSource(timeout);
            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            ValidateUri(response.RequestMessage.RequestUri, allowedHosts);
            long announced = response.Content.Headers.ContentLength ?? 0;
            if (limit <= 0 || expectedLength < 0 || announced > limit || expectedLength > limit)
                throw new InvalidDataException("Download exceeds the size limit.");
            if (announced > 0 && expectedLength > 0 && announced != expectedLength)
                throw new InvalidDataException("Download size does not match the release metadata.");
            long expected = expectedLength > 0 ? expectedLength : announced;
            bool created = false;
            bool complete = false;
            try
            {
                using var source = response.Content.ReadAsStreamAsync(deadline.Token).GetAwaiter().GetResult();
                using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.ReadAsync(buffer, 0, buffer.Length, deadline.Token).GetAwaiter().GetResult()) > 0)
                {
                    total += read;
                    if (total > limit || (expected > 0 && total > expected))
                        throw new InvalidDataException("Download exceeds the expected size.");
                    destination.Write(buffer, 0, read);
                    progress?.Invoke(total, expected);
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (total == 0 || (expected > 0 && total != expected))
                    throw new InvalidDataException("Download was incomplete.");
                destination.Flush(true);
                complete = true;
            }
            finally
            {
                if (created && !complete) File.Delete(path);
            }
        }

        private static void ValidateUri(Uri uri, ISet<string> allowedHosts)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
                (allowedHosts != null && !allowedHosts.Contains(uri.Host)))
                throw new InvalidDataException("Download URL is not trusted.");
        }
    }
}
