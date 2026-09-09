using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReplayKitSetup
{
    // rewrite hardcoded C:\Users\<name> paths to the current user. ported from obs_replaykit/pathrewrite.py.
    public static class PathRewrite
    {
        // shared/system accounts we never want to rewrite.
        private static readonly HashSet<string> SharedUserFolders = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "public", "default", "all users", "default user",
        };

        // matches c:\users\<x>, c:/users/<x>, or escaped c:\\users\\<x>; captures the prefix + username separately.
        private static readonly Regex UserPathRe = new Regex(@"(C:[\\/]+Users[\\/]+)([^\\/\s""']+)", RegexOptions.IgnoreCase);

        // replace every c:\users\<x> with c:\users\<new_user>. preserves the separator style (single backslash, escaped backslash, or forward slash) and skips shared accounts.
        public static string RewriteUserPaths(string text, string newUser)
        {
            return UserPathRe.Replace(text, match =>
            {
                string prefix = match.Groups[1].Value;
                string found = match.Groups[2].Value;
                if (SharedUserFolders.Contains(found)) return match.Value;
                return prefix + newUser;
            });
        }
    }
}
