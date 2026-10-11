using System.Text;

namespace ReplayKitSetup
{
    // builds a CommandLineToArgvW-compliant command line for ProcessStartInfo.Arguments
    public static class Win32Args
    {
        public static string Build(params string[] args)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                AppendQuoted(sb, args[i] ?? "");
            }
            return sb.ToString();
        }

        // follows the same backslash/quote escaping rules CommandLineToArgvW expects, matching how msvcrt-based programs (powershell.exe, schtasks.exe, wscript.exe, cmd.exe) parse their own argv.
        private static void AppendQuoted(StringBuilder sb, string arg)
        {
            bool needsQuotes = arg.Length == 0;
            foreach (char c in arg)
            {
                if (c == ' ' || c == '\t' || c == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }
            if (!needsQuotes)
            {
                sb.Append(arg);
                return;
            }

            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }
    }
}
