using System.Text;

namespace ReplayKitHelper
{
    // CommandLineToArgvW-compliant argument quoting; net48 has no ProcessStartInfo.ArgumentList, so every child process command line is built here
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
