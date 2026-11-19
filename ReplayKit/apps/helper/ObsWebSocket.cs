using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    public sealed class ObsWebSocketResult
    {
        public bool Ok;
        public bool Unavailable;
        public string Message;
        public string RequestType;
        public int Code;
        public JToken Data;
    }

    // thin obs-websocket v5 json-rpc client: connects (or reuses the cached connection), sends one request, and matches the response by requestId
    internal static class ObsWebSocket
    {
        private sealed class SettingsResult
        {
            public bool Ok;
            public bool Unavailable;
            public string Message;
            public int Port;
            public string Password;
        }

        private static SettingsResult GetObsWebSocketSettings()
        {
            string path = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "plugin_config", "obs-websocket", "config.json");
            if (!File.Exists(path)) return new SettingsResult { Ok = false, Unavailable = true, Message = Locales.Message("helper.obsWebsocketConfigNotFound") };
            JObject cfg;
            try { cfg = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return new SettingsResult { Ok = false, Unavailable = true, Message = Locales.Message("helper.obsWebsocketConfigIsNotValid") };
            }

            if (!(cfg["server_enabled"]?.Value<bool>() ?? false))
                return new SettingsResult { Ok = false, Unavailable = true, Message = Locales.Message("helper.obsWebsocketServerIsDisabled") };
            string password = cfg["server_password"]?.Value<string>() ?? "";
            if ((cfg["auth_required"]?.Value<bool>() ?? false) && string.IsNullOrEmpty(password))
                return new SettingsResult { Ok = false, Unavailable = true, Message = Locales.Message("helper.obsWebsocketPasswordIsMissing") };

            int port = 6455;
            var portToken = cfg["server_port"];
            if (portToken != null) int.TryParse(portToken.ToString(), out port);
            if (port <= 0 || port > 65535)
                return new SettingsResult { Ok = false, Unavailable = true, Message = Locales.Message("helper.obsWebsocketPortIsInvalid") };
            return new SettingsResult { Ok = true, Port = port, Password = password };
        }

        internal static string CreateAuthentication(string password, string salt, string challenge)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(challenge))
                throw new InvalidOperationException(Locales.Message("helper.obsWebsocketAuthenticationDataIsMissing"));
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        private static JObject ReceiveMessage(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            var segment = new ArraySegment<byte>(buffer);
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = socket.ReceiveAsync(segment, token).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException(Locales.Message("helper.obsWebsocketClosedTheConnection"));
                    if (result.MessageType != WebSocketMessageType.Text || ms.Length + result.Count > 16 * 1024 * 1024)
                        throw new InvalidDataException(Locales.Message("helper.obsWebsocketMessageIsInvalidOr"));
                    if (result.Count > 0) ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                return JObject.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        private static void SendMessage(ClientWebSocket socket, JObject payload, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).GetAwaiter().GetResult();
        }

        // set while obs is asked to close for a restart or exit: the cached socket is only read during a request, so obs never got an answer to its close, waited out its 5 s handshake and then kept its websocket thread, and with it its exit, waiting on the open connection until the restart stopped it at 15 s
        private static volatile bool obsClosing;

        internal static void DisconnectForObsClose()
        {
            lock (Server.State.ObsWebSocketLock)
            {
                obsClosing = true;
                CloseCached();
            }
        }

        // obs stayed open after all
        internal static void AllowConnect() => obsClosing = false;

        // drops the cached connection (if any) and closes it. only ever called with ObsWebSocketLock already held.
        private static void CloseCached()
        {
            var socket = Server.State.ObsWebSocket;
            if (socket == null) return;
            Server.State.ObsWebSocket = null;
            Server.State.ObsWebSocketPort = 0;
            // cleanup must not wait for a peers close handshake while holding the request lock.
            socket.Abort();
            socket.Dispose();
        }

        // returns an open, identified socket -- reuses Server.State.ObsWebSocket when its still marked open and pointed at the current port, otherwise connects fresh. only ever called with ObsWebSocketLock already held.
        private static ClientWebSocket GetConnected(SettingsResult settings, CancellationToken token)
        {
            var cached = Server.State.ObsWebSocket;
            if (cached != null && cached.State == WebSocketState.Open && Server.State.ObsWebSocketPort == settings.Port)
                return cached;
            CloseCached();

            // ClientWebSocket.ConnectAsync goes thru the same http stack as HttpWebRequest on .net framework 4.x (proxy detection and all), which can cost multiple seconds against a port nothing is listening on instead of failing fast -- a raw tcp probe answers "is anything even listening" in well under a second, so a disabled/broken obs-websocket fails fast here instead of costing the caller close to the full timeout. only paid on a fresh connect, not on every reused request -- which is most of them once obs is up, since the socket stays open between calls.
            using (var probe = new TcpClient())
            {
                var ar = probe.BeginConnect("127.0.0.1", settings.Port, null, null);
                using (var wait = ar.AsyncWaitHandle)
                {
                    if (WaitHandle.WaitAny(new[] { wait, token.WaitHandle }, 500) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException(Locales.Message("helper.obsWebsocketIsNotReachable"));
                    }
                }
                probe.EndConnect(ar);
            }

            var socket = new ClientWebSocket();
            try
            {
                var uri = new Uri("ws://127.0.0.1:" + settings.Port);
                socket.ConnectAsync(uri, token).GetAwaiter().GetResult();

                var hello = ReceiveMessage(socket, token);
                if (hello["op"]?.Value<int>() != 0) throw new InvalidOperationException(Locales.Message("helper.obsWebsocketDidNotSendHello"));
                var identify = new JObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
                if (hello["d"]?["authentication"] is JObject auth)
                    identify["authentication"] = CreateAuthentication(settings.Password, auth["salt"]?.Value<string>(), auth["challenge"]?.Value<string>());
                SendMessage(socket, new JObject { ["op"] = 1, ["d"] = identify }, token);

                var identified = ReceiveMessage(socket, token);
                if (identified["op"]?.Value<int>() != 2) throw new InvalidOperationException(Locales.Message("helper.obsWebsocketDidNotIdentifyThis"));
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            Server.State.ObsWebSocket = socket;
            Server.State.ObsWebSocketPort = settings.Port;
            return socket;
        }

        public static ObsWebSocketResult InvokeRequest(string requestType, JToken requestData = null, int timeoutMs = 3000)
        {
            var clock = Stopwatch.StartNew();
            int budget = Math.Max(1000, timeoutMs);
            // with the clip buffer on disk the replay buffer requests are answered by the plugin, which owns that buffer, and obs never starts its own
            var routed = ClipBuffer.TryInvoke(requestType);
            if (routed != null) return routed;
            var settings = GetObsWebSocketSettings();

            // the whole connect-reuse-or-fresh + send + receive cycle runs under one lock -- the request/response matching below has no way to tell two concurrent callers requests apart on a shared socket, so at most one request can be in flight on it at a time regardless of how many callers fire one.
            var gate = Server.State.ObsWebSocketLock;
            int remaining = (int)Math.Max(0, budget - clock.ElapsedMilliseconds);
            if (remaining == 0 || !Monitor.TryEnter(gate, remaining))
                return new ObsWebSocketResult { Ok = false, Unavailable = true, RequestType = requestType, Message = Locales.Message("helper.obsWebsocketIsNotReachable") };
            try
            {
                if (obsClosing) return new ObsWebSocketResult { Ok = false, Unavailable = true, Message = Locales.Message("helper.obsIsClosing") };
                if (!settings.Ok)
                {
                    CloseCached();
                    return new ObsWebSocketResult { Ok = false, Unavailable = settings.Unavailable, Message = settings.Message };
                }

                using (var cts = new CancellationTokenSource())
                {
                    remaining = (int)Math.Max(0, budget - clock.ElapsedMilliseconds);
                    if (remaining == 0)
                        return new ObsWebSocketResult { Ok = false, Unavailable = true, RequestType = requestType, Message = Locales.Message("helper.obsWebsocketIsNotReachable") };
                    cts.CancelAfter(remaining);
                    try
                    {
                        var socket = GetConnected(settings, cts.Token);

                        string requestId = Guid.NewGuid().ToString("N");
                        var data = new JObject { ["requestType"] = requestType, ["requestId"] = requestId };
                        if (requestData != null) data["requestData"] = requestData;
                        SendMessage(socket, new JObject { ["op"] = 6, ["d"] = data }, cts.Token);

                        while (true)
                        {
                            var response = ReceiveMessage(socket, cts.Token);
                            if (response["op"]?.Value<int>() != 7) continue;
                            if (response["d"]?["requestId"]?.Value<string>() != requestId) continue;
                            var status = response["d"]?["requestStatus"];
                            if (status != null && (status["result"]?.Value<bool>() ?? false))
                                return new ObsWebSocketResult { Ok = true, RequestType = requestType, Data = response["d"]?["responseData"] };
                            string comment = status?["comment"]?.Value<string>();
                            if (string.IsNullOrEmpty(comment)) comment = requestType + " failed.";
                            return new ObsWebSocketResult { Ok = false, RequestType = requestType, Code = status?["code"]?.Value<int>() ?? 0, Message = comment };
                        }
                    }
                    catch (Exception ex)
                    {
                        // anything thrown above (connect, hello/identify, send, receive, cancellation) leaves the socket in an unknown state -- drop it so the next call reconnects fresh instead of reusing something possibly half-broken. a request obs itself answered with a failure result never reaches here, it returns from inside the try above.
                        CloseCached();
                        return new ObsWebSocketResult { Ok = false, Unavailable = true, Message = ex.Message };
                    }
                }
            }
            finally { Monitor.Exit(gate); }
        }

        public static ObsWebSocketResult SaveReplayBuffer() => InvokeRequest("SaveReplayBuffer", null, 3000);
    }
}
