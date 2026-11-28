using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    public sealed class ObsResult
    {
        public bool Ok;
        public bool Unavailable;
        public string Message;
        public string RequestType;
        public int Code;
        public JToken Data;
    }

    // asks obs through the plugin, which answers in the request and response shapes of obs-websocket v5: the request goes over the pipe and the answer comes back on /obs/reply, matched by a random id so a page cannot answer in place of obs
    internal static class ObsRequest
    {
        private sealed class Waiting
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Body;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Waiting> Pending = new Dictionary<string, Waiting>(StringComparer.Ordinal);

        // set while obs is asked to close for a restart or exit, when a request could only wait out its timeout
        private static volatile bool obsClosing;

        internal static void MarkObsClosing() => obsClosing = true;

        // obs stayed open after all
        internal static void AllowRequests() => obsClosing = false;

        private static string NewId()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static ObsResult Invoke(string requestType, JToken requestData = null, int timeoutMs = 3000)
        {
            // with the clip buffer on disk the replay buffer requests are answered by the plugin, which owns that buffer, and obs never starts its own
            var routed = ClipBuffer.TryInvoke(requestType);
            if (routed != null) return routed;
            if (obsClosing) return new ObsResult { Ok = false, Unavailable = true, RequestType = requestType, Message = Locales.Message("helper.obsIsClosing") };

            string id = NewId();
            var waiting = new Waiting();
            lock (Gate) Pending[id] = waiting;
            try
            {
                string json = (requestData as JObject ?? new JObject()).ToString(Formatting.None);
                if (!PipeClient.SendObsRequest(id, requestType, json))
                    return new ObsResult { Ok = false, Unavailable = true, RequestType = requestType, Message = Locales.Message("helper.thePluginIsNotConnected") };
                if (!waiting.Done.Wait(Math.Max(1000, timeoutMs)))
                    return new ObsResult { Ok = false, Unavailable = true, RequestType = requestType, Message = Locales.Message("helper.obsDidNotAnswer") };
            }
            finally
            {
                lock (Gate) Pending.Remove(id);
            }

            JObject reply;
            try { reply = JObject.Parse(waiting.Body ?? ""); }
            catch (JsonException) { return new ObsResult { Ok = false, Unavailable = true, RequestType = requestType, Message = Locales.Message("helper.obsDidNotAnswer") }; }
            if (reply["ok"]?.Value<bool>() == true)
                return new ObsResult { Ok = true, RequestType = requestType, Code = 100, Data = reply["data"] };
            string comment = reply["comment"]?.Value<string>();
            return new ObsResult
            {
                Ok = false,
                Unavailable = reply["unavailable"]?.Value<bool>() == true,
                RequestType = requestType,
                Code = reply["code"]?.Value<int>() ?? 0,
                Message = string.IsNullOrEmpty(comment) ? requestType + " failed." : comment,
            };
        }

        // the answer of the plugin to a request still waiting; false when no request waits under that id
        internal static bool Answer(string id, string body)
        {
            Waiting waiting;
            lock (Gate) Pending.TryGetValue(id ?? "", out waiting);
            if (waiting == null) return false;
            waiting.Body = body;
            waiting.Done.Set();
            return true;
        }

        public static ObsResult SaveReplayBuffer() => Invoke("SaveReplayBuffer", null, 3000);
    }
}
