using System.Text;

namespace ReplayKitHelper
{
    // win32 CommandLineToArgvW-compliant argument quoting. net48s ProcessStartInfo.Arguments is a single string (no ArgumentList until .NET Core 2.1+), so every child process invocation (curl.exe, ffmpeg.exe, explorer.exe, a relaunch of ourselves) needs this to pass arguments safely. ported from the identical Quote-Arg implementations duplicated across obs_replaykit helper upload_worker.ps1 and modules/71_routes.ps1 -- one shared copy here instead of two that could drift apart.
    internal static class ProcessArgs
    {
        public static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            bool needsQuote = false;
            foreach (char c in arg)
            {
                if (char.IsWhiteSpace(c) || c == '"') { needsQuote = true; break; }
            }
            if (!needsQuote) return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    slashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1);
                    sb.Append('"');
                    slashes = 0;
                }
                else
                {
                    sb.Append('\\', slashes);
                    sb.Append(c);
                    slashes = 0;
                }
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string Join(params string[] args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Quote(args[i]));
            }
            return sb.ToString();
        }
    }
}
