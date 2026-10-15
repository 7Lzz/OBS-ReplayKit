using System;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace ReplayKitSetup
{
    internal static class BuiltinPage
    {
        internal static bool IsTrusted(string source) => string.Equals(source, "about:blank", StringComparison.Ordinal);

        internal static void Load(CoreWebView2 web, string html)
        {
            // NavigateToString can report its encoded HTML here even though the document source is about:blank.
            string initialUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            bool initial = true;
            web.NavigationStarting += (sender, e) =>
            {
                bool allowed = initial && (IsTrusted(e.Uri) || string.Equals(e.Uri, initialUri, StringComparison.Ordinal));
                e.Cancel = !allowed;
                if (allowed) { initial = false; initialUri = null; }
            };
            web.FrameNavigationStarting += (sender, e) => { e.Cancel = true; };
            web.NewWindowRequested += (sender, e) => { e.Handled = true; };
            web.PermissionRequested += (sender, e) => { e.State = CoreWebView2PermissionState.Deny; };
            web.NavigateToString(html);
        }
    }
}
