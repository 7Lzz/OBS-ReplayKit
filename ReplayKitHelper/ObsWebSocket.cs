using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
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

    // thin obs-websocket v5 json-rpc client: connects (or reuses a cached connection), sends one request, matches the response by requestId. ported from obs_replaykit helper modules/61_obs_websocket.ps1 -- a near 1:1 translation, since PS 5.1 on .NET Framework 4.x already had direct access to System.Net.WebSockets.ClientWebSocket.
    internal static class ObsWebSocket
    {
        private sealed class SettingsResult
        {
            public bool Ok;
            public bool Unavailable;
            public string Message;
            public int Port;
        }

        private static SettingsResult GetObsWebSocketSettings()
        {
            string path = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "obs-studio", "plugin_config", "obs-websocket", "config.json");
            if (!File.Exists(path)) return new SettingsResult { Ok = false, Unavailable = true, Message = "OBS websocket config not found." };
            JObject cfg;
            try { cfg = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return new SettingsResult { Ok = false, Unavailable = true, Message = "OBS websocket config is not valid JSON." };
            }

            if (!(cfg["server_enabled"]?.Value<bool>() ?? false))
                return new SettingsResult { Ok = false, Unavailable = true, Message = "OBS websocket server is disabled." };
            if (cfg["auth_required"]?.Value<bool>() ?? false)
                return new SettingsResult { Ok = false, Unavailable = true, Message = "OBS websocket authentication is enabled." };

            int port = 6455;
            var portToken = cfg["server_port"];
            if (portToken != null) int.TryParse(portToken.ToString(), out port);
            if (port <= 0 || port > 65535)
                return new SettingsResult { Ok = false, Unavailable = true, Message = "OBS websocket port is invalid." };
            return new SettingsResult { Ok = true, Port = port };
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
                    if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("OBS websocket closed the connection.");
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

        // drops the cached connection (if any) and closes it. only ever called with ObsWebSocketLock already held.
        private static void CloseCached()
        {
            var socket = Server.State.ObsWebSocket;
            if (socket == null) return;
            Server.State.ObsWebSocket = null;
            Server.State.ObsWebSocketPort = 0;
            try
            {
                if (socket.State == WebSocketState.Open)
                    socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException || ex is ObjectDisposedException) { }
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
                if (!ar.AsyncWaitHandle.WaitOne(500)) throw new InvalidOperationException("OBS websocket is not reachable.");
                probe.EndConnect(ar);
            }

            var socket = new ClientWebSocket();
            try
            {
                var uri = new Uri("ws://127.0.0.1:" + settings.Port);
                socket.ConnectAsync(uri, token).GetAwaiter().GetResult();

                var hello = ReceiveMessage(socket, token);
                if (hello["op"]?.Value<int>() != 0) throw new InvalidOperationException("OBS websocket did not send Hello.");
                if (hello["d"]?["authentication"] != null) throw new InvalidOperationException("OBS websocket requires authentication.");

                int rpcVersion = hello["d"]?["rpcVersion"]?.Value<int>() ?? 1;
                SendMessage(socket, new JObject { ["op"] = 1, ["d"] = new JObject { ["rpcVersion"] = rpcVersion } }, token);

                var identified = ReceiveMessage(socket, token);
                if (identified["op"]?.Value<int>() != 2) throw new InvalidOperationException("OBS websocket did not identify this client.");
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
            var settings = GetObsWebSocketSettings();

            // the whole connect-reuse-or-fresh + send + receive cycle runs under one lock -- the request/response matching below has no way to tell two concurrent callers requests apart on a shared socket, so at most one request can be in flight on it at a time regardless of how many callers fire one.
            lock (Server.State.ObsWebSocketLock)
            {
                if (!settings.Ok)
                {
                    CloseCached();
                    return new ObsWebSocketResult { Ok = false, Unavailable = settings.Unavailable, Message = settings.Message };
                }

                using (var cts = new CancellationTokenSource())
                {
                    cts.CancelAfter(Math.Max(1000, timeoutMs));
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
        }

        public static ObsWebSocketResult SaveReplayBuffer() => InvokeRequest("SaveReplayBuffer", null, 3000);
    }
}
