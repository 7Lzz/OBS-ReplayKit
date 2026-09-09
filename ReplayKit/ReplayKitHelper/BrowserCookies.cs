using System;
using System.Security;
using System.Security.Principal;

namespace ReplayKitHelper
{
    // Compatibility wrapper for callers that only need the helper's integrity level.
    // Streamable sessions are now owned by StreamableSignIn, never harvested from browsers.
    internal static class BrowserCookies
    {
        public static bool TestIsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
