using System;
using System.Security;
using System.Security.Principal;

namespace ReplayKitHelper
{
    // whether this helper runs elevated, which follows how obs itself was launched
    internal static class Elevation
    {
        public static bool IsAdmin()
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
