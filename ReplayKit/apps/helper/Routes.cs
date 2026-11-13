using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the gate every helper request passes (host and origin) and the shared helpers of the route classes, which hold the routes themselves, one feature each
    internal static class Routes
    {
        internal static readonly string[] AppIconExtensions = { ".ico", ".png", ".jpg", ".jpeg", ".bmp" };
        internal static readonly object RenameGate = new object();

        // "40-a1b2c3d4.ico" -> "40" -- drop the source-hash suffix ImportCustomIcon appends so the grid shows a clean label.
        internal static string UserIconLabel(string fileName) =>
            Regex.Replace(Path.GetFileNameWithoutExtension(fileName), "-[0-9a-f]{8}$", "");

        // the colours the dock draws a theme tile from.
        internal static JObject ThemeSwatch(Themes.Tokens t) => new JObject
        {
            ["bg"] = t.Bg, ["panel"] = t.Panel, ["accent"] = t.Accent, ["text"] = t.Text,
            ["background"] = Themes.BackgroundCss(t),
        };

        // the body of a failed action; a problem with the setup file also carries its code, which the settings page keys on
        internal static JObject FailureJson(Exception ex)
        {
            var body = new JObject { ["ok"] = false, ["message"] = ex.Message };
            if (ex is SetupExeException setup) body["code"] = setup.Code;
            return body;
        }

        internal static bool TestSettingsOrigin(HttpRequest req)
        {
            if (req.Headers.TryGetValue("sec-fetch-site", out string site) &&
                !string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(site, "same-site", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(site, "none", StringComparison.OrdinalIgnoreCase)) return false;
            if (!req.Headers.TryGetValue("origin", out string origin))
            {
                if (!req.Headers.TryGetValue("referer", out string referer)) return true;
                return Uri.TryCreate(referer, UriKind.Absolute, out var uri) && TrustedOrigin(uri.GetLeftPart(UriPartial.Authority));
            }
            return TrustedOrigin(origin);
        }

        internal static bool TrustedOrigin(string origin)
        {
            int port = ReplayKitShared.HelperEndpoint.Port;
            return string.Equals(origin, "http://" + Constants.HOST_ADDR + ":" + port, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(origin, "http://localhost:" + port, StringComparison.OrdinalIgnoreCase);
        }

        internal static JObject TrimResultToJson(TrimResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (!r.Ok) { obj["message"] = r.Message; if (r.Cancelled) obj["cancelled"] = true; return obj; }
            if (r.Name != null) obj["name"] = r.Name;
            if (r.SourceName != null)
            {
                obj["sourceName"] = r.SourceName;
                obj["mode"] = r.Mode;
                obj["precise"] = r.Precise;
                obj["removeAudio"] = r.RemoveAudio;
                obj["startSec"] = r.StartSec;
                obj["endSec"] = r.EndSec;
                obj["durationSec"] = r.DurationSec;
            }
            return obj;
        }

        internal static JObject KeyframeResultToJson(KeyframeScanResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Pending) obj["pending"] = true;
            obj["message"] = r.Message ?? "";
            obj["keyframes"] = new JArray(r.Keyframes);
            if (r.Ok)
            {
                obj["name"] = r.Name;
                obj["count"] = r.Count;
                obj["cached"] = r.Cached;
                obj["probeMs"] = r.ProbeMs;
            }
            if (r.RetryMs != 0) obj["retryMs"] = r.RetryMs;
            return obj;
        }

        internal static JObject ObsResultToJson(ObsWebSocketResult r)
        {
            var obj = new JObject { ["ok"] = r.Ok };
            if (r.Unavailable) obj["unavailable"] = true;
            if (r.RequestType != null) obj["requestType"] = r.RequestType;
            if (!r.Ok) { obj["message"] = r.Message; if (r.Code != 0) obj["code"] = r.Code; }
            else if (r.Data != null) obj["data"] = r.Data;
            return obj;
        }

        public static bool DispatchRequest(Stream stream, HttpRequest req)
        {
            AppConfig.LoadConfig();

            int port = ReplayKitShared.HelperEndpoint.Port;
            // every route is gated here on host and origin, so no route checks them again
            if (!req.Headers.TryGetValue("host", out string host) ||
                !(string.Equals(host, Constants.HOST_ADDR + ":" + port, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, "localhost:" + port, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, Constants.HOST_ADDR, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) ||
                !TestSettingsOrigin(req))
            {
                HttpResponse.SendText(stream, 403, "Forbidden", "Untrusted request origin or host.");
                return false;
            }

            if (req.Method == "OPTIONS")
            {
                HttpResponse.SendBytes(stream, 204, "No Content", HttpResponse.GetNoStoreHeaders(), new byte[0]);
                return false;
            }

            var request = new RouteRequest(stream, req);
            foreach (var handle in Handlers)
            {
                bool? answered = handle(request);
                if (answered != null) return answered.Value;
            }
            HttpResponse.SendText(stream, 404, "Not Found", "Not found");
            return false;
        }

        // the route classes in the order they are asked; no two of them answer the same path
        internal static readonly Func<RouteRequest, bool?>[] Handlers =
        {
            GameRoutes.Handle,
            PageRoutes.Handle,
            RecordingRoutes.Handle,
            SettingsRoutes.Handle,
            SetupRoutes.Handle,
            OverlayRoutes.Handle,
            AudioRoutes.Handle,
            DiscordRoutes.Handle,
            WindowRoutes.Handle,
            NotificationRoutes.Handle,
            EditorRoutes.Handle,
            UpdateRoutes.Handle,
            AccountRoutes.Handle,
            AppearanceRoutes.Handle,
            StorageRoutes.Handle,
            AppRoutes.Handle,
            ClipRoutes.Handle,
            ShareRoutes.Handle,
            SdkRoutes.Handle,
        };
    }
}
