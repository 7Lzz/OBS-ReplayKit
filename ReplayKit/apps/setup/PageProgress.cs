using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // the IInstallProgress of the setup and uninstall windows: every callback goes to the page as a json message in the language of the window, and to the setup log in english. callbacks arrive on the worker thread; post hands them to the message thread
    internal sealed class PageProgress : IInstallProgress
    {
        private readonly Action<JObject> post;
        private int completed;

        public PageProgress(Action<JObject> post) { this.post = post; }

        public int TotalSteps { get; set; }
        public List<string> Issues { get; } = new List<string>();

        public void Render(int completed, SetupLocale.Text title, SetupLocale.Text detail, string state)
        {
            this.completed = completed;
            int total = Math.Max(TotalSteps, 1);
            double pct = 100.0 * Math.Min(completed, total) / total;
            post(new JObject { ["type"] = "step", ["percent"] = pct, ["state"] = state ?? "", ["title"] = title.Shown, ["detail"] = detail.Shown ?? "" });
            SetupLog.Line($"[{completed}/{total}] {(string.IsNullOrEmpty(state) ? "working" : state)}: {title.English}"
                          + (string.IsNullOrEmpty(detail.English) ? "" : " - " + detail.English));
        }

        public void LogLine(string message)
        {
            string text = (message ?? "").Trim();
            if (text.Length == 0) return;
            SetupLog.Line(text);
            post(new JObject { ["type"] = "log", ["line"] = text });
            if (Apply.LooksLikeIssue(text)) AddIssue(new SetupLocale.Text(text, text));
        }

        public void AddIssue(SetupLocale.Text message)
        {
            string cleaned = Clean(message.Shown);
            if (cleaned.Length == 0 || Issues.Contains(cleaned)) return;
            Issues.Add(cleaned);
            // what made the run incomplete is in the log, not only in the count at its end
            SetupLog.Line("issue: " + Clean(message.English));
        }

        public void SubProgress(double fraction, string detail)
        {
            int total = Math.Max(TotalSteps, 1);
            double frac = Math.Max(0.0, Math.Min(1.0, fraction));
            double pct = 100.0 * (completed / (double)total + frac / total);
            var o = new JObject { ["type"] = "step", ["percent"] = pct, ["state"] = "" };
            if (!string.IsNullOrEmpty(detail)) o["detail"] = detail;
            post(o);
        }

        private static string Clean(string message)
        {
            string cleaned = string.Join(" ", (message ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
            return cleaned.Length > 160 ? cleaned.Substring(0, 157) + "..." : cleaned;
        }
    }
}
