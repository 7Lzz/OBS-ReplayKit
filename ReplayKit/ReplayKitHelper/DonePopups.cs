using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // the popup that says a job is done (a gif exported, clips compressed, a driver to update): a form of notification of its own, apart from the bell. the plugin shows it on a card over the obs window from any view, and waits for the window to be back when it is minimized; what it shows is kept here only for the session, never in the list of the bell
    internal static class DonePopups
    {
        private const int MaxKept = 20;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, JObject> Items = new Dictionary<string, JObject>();
        private static readonly Queue<string> Order = new Queue<string>();

        // popup: icon ("check", "gif", "warning"), media (a helper path), text, big, detail, action { label, kind, file | url }. false when the plugin could not be asked (the pipe is closed), so a caller can leave the result somewhere else
        public static bool Show(string kind, string title, JObject popup)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            string id = Guid.NewGuid().ToString("N");
            var item = popup != null ? (JObject)popup.DeepClone() : new JObject();
            item["id"] = id;
            item["kind"] = kind ?? "done";
            item["title"] = title;
            lock (Gate)
            {
                Items[id] = item;
                Order.Enqueue(id);
                while (Order.Count > MaxKept) Items.Remove(Order.Dequeue());
            }
            return PipeClient.SendDonePopup(id);
        }

        public static JObject Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Gate) return Items.TryGetValue(id, out var item) ? (JObject)item.DeepClone() : null;
        }
    }
}
