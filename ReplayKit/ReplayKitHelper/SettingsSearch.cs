using System.Text;

namespace ReplayKitHelper
{
    // the query typed into a settings search box. the search box of the replaykit settings and the one of the obs settings are two views of this one string, so both lists of tabs answer the same question; it lives in memory only and starts empty with the helper
    internal static class SettingsSearch
    {
        private const int MaxLength = 120;
        private static readonly object Gate = new object();
        private static string _query = "";

        public static string Get()
        {
            lock (Gate) return _query;
        }

        public static void Set(string query)
        {
            var clean = new StringBuilder();
            foreach (char c in query ?? "")
            {
                if (!char.IsControl(c)) clean.Append(c);
                if (clean.Length >= MaxLength) break;
            }
            lock (Gate) _query = clean.ToString();
        }
    }
}
