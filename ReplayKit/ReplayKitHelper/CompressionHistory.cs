using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ReplayKitShared;

namespace ReplayKitHelper
{
    internal static class CompressionHistory
    {
        private const int MaxEntries = 50;
        private const int MaxBytes = 64 * 1024;
        private static string StorePath() => Path.Combine(AppConfig.GetReplayKitRoot(), StateLayout.CompressionHistoryFile);

        internal sealed class Estimate
        {
            public double SavedFraction = 0.5;
            public int Samples;
        }

        private static long Integer(JToken value)
        {
            if (value?.Type != JTokenType.Integer || !long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                throw new InvalidDataException("Invalid compression history number.");
            return number;
        }

        private static void Validate(string id, string mode, long before, long after, long scale)
        {
            if (!Guid.TryParseExact(id, "N", out _) || (mode != "fast" && mode != "smaller") ||
                before <= 0 || after <= 0 || after >= before || (scale != 0 && (scale < 120 || scale > 4320)))
                throw new InvalidDataException("Invalid compression history result.");
        }

        private static JArray Entries(JObject db)
        {
            if (db.Count == 0) return new JArray();
            if (db.Count != 2 || Integer(db["version"]) != 1 || !(db["entries"] is JArray entries) || entries.Count > MaxEntries)
                throw new InvalidDataException("Invalid compression history file.");
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in entries)
            {
                if (!(token is JObject entry) || entry.Count != 6 || entry["id"]?.Type != JTokenType.String || entry["mode"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Invalid compression history entry.");
                string id = entry.Value<string>("id");
                Validate(id, entry.Value<string>("mode"), Integer(entry["beforeBytes"]), Integer(entry["afterBytes"]), Integer(entry["scaleHeight"]));
                if (!ids.Add(id) || Integer(entry["completedAt"]) <= 0)
                    throw new InvalidDataException("Invalid compression history entry.");
            }
            return entries;
        }

        public static void Prepare()
        {
            ClipStore.Update(StorePath(), db =>
            {
                var entries = Entries(db);
                db["version"] = 1;
                db["entries"] = entries;
                return true;
            }, MaxBytes);
        }

        public static void Record(string id, string mode, long before, long after, int scaleHeight)
        {
            Validate(id, mode, before, after, scaleHeight);
            ClipStore.Update(StorePath(), db =>
            {
                var entries = Entries(db);
                var existing = entries.OfType<JObject>().FirstOrDefault(e => string.Equals(e.Value<string>("id"), id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (existing.Value<string>("mode") != mode || Integer(existing["beforeBytes"]) != before ||
                        Integer(existing["afterBytes"]) != after || Integer(existing["scaleHeight"]) != scaleHeight)
                        throw new InvalidDataException("The compression result was already recorded with different values.");
                    return false;
                }
                entries.Add(new JObject
                {
                    ["id"] = id, ["mode"] = mode, ["beforeBytes"] = before, ["afterBytes"] = after,
                    ["scaleHeight"] = scaleHeight, ["completedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                while (entries.Count > MaxEntries) entries.RemoveAt(0);
                db["version"] = 1;
                db["entries"] = entries;
                return true;
            }, MaxBytes);
        }

        public static Estimate GetEstimate(string mode)
        {
            if (mode != "fast" && mode != "smaller") throw new ArgumentException("Invalid compression type.", nameof(mode));
            return ClipStore.Read(StorePath(), db =>
            {
                var result = new Estimate();
                decimal before = 0, saved = 0;
                foreach (var entry in Entries(db).OfType<JObject>())
                {
                    // Resizing changes savings, so full-size Storage estimates use full-size results.
                    if (entry.Value<string>("mode") != mode || Integer(entry["scaleHeight"]) != 0) continue;
                    long size = Integer(entry["beforeBytes"]);
                    before += size;
                    saved += size - Integer(entry["afterBytes"]);
                    result.Samples++;
                }
                if (before > 0) result.SavedFraction = (double)(saved / before);
                return result;
            }, MaxBytes);
        }
    }
}
