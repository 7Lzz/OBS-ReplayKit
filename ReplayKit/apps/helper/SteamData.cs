using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class SteamData
    {
        private sealed class TextReader
        {
            private readonly string text;
            private int at, tokens;
            private bool structural;
            internal TextReader(string value) { text = value; }
            private string Token()
            {
                structural = false;
                if (++tokens > 100000) throw new InvalidDataException(Locales.Message("clips.steamMetadataIsTooComplex"));
                while (at < text.Length)
                {
                    if (char.IsWhiteSpace(text[at])) { at++; continue; }
                    if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '/') { while (at < text.Length && text[at] != '\n') at++; continue; }
                    break;
                }
                if (at == text.Length) return null;
                char first = text[at++];
                if (first == '{' || first == '}') { structural = true; return first.ToString(); }
                if (first != '"') throw new InvalidDataException("Unsupported Steam text token.");
                var value = new StringBuilder();
                while (at < text.Length && value.Length <= 4096)
                {
                    char next = text[at++];
                    if (next == '"') return value.ToString();
                    if (next == '\\' && at < text.Length && (text[at] == '\\' || text[at] == '"')) next = text[at++];
                    value.Append(next);
                }
                throw new InvalidDataException(Locales.Message("clips.invalidSteamTextString"));
            }
            internal JObject Object(int depth = 0)
            {
                if (depth > 32) throw new InvalidDataException(Locales.Message("clips.steamMetadataIsTooDeep"));
                var result = new JObject();
                while (true)
                {
                    string key = Token();
                    if (key == null || structural && key == "}")
                    {
                        if ((depth == 0) != (key == null)) throw new InvalidDataException(Locales.Message("clips.invalidSteamTextObject"));
                        return result;
                    }
                    if (structural || result.Property(key, StringComparison.OrdinalIgnoreCase) != null) throw new InvalidDataException(Locales.Message("clips.invalidSteamTextField"));
                    string value = Token();
                    if (value == null || structural && value == "}") throw new InvalidDataException(Locales.Message("clips.invalidSteamTextField"));
                    result[key] = structural ? (JToken)Object(depth + 1) : new JValue(value);
                }
            }
        }

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static JObject Text(string file)
        {
            using (var input = Open(file, 1024 * 1024))
            using (var reader = new StreamReader(input, StrictUtf8, true))
            {
                var buffer = new char[1024 * 1024 + 1];
                int length = reader.ReadBlock(buffer, 0, buffer.Length);
                if (length == buffer.Length) throw new InvalidDataException(Locales.Message("clips.steamTextMetadataIsTooLarge"));
                return new TextReader(new string(buffer, 0, length)).Object();
            }
        }

        private static FileStream Open(string file, int limit)
        {
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (stream.Length <= limit && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) return stream;
                throw new InvalidDataException(Locales.Message("clips.steamMetadataIsTooLargeOr"));
            }
            catch { stream.Dispose(); throw; }
        }

        private static string String(BinaryReader reader)
        {
            var bytes = new List<byte>();
            for (int i = 0; i <= 4096; i++)
            {
                byte next = reader.ReadByte();
                if (next == 0) return StrictUtf8.GetString(bytes.ToArray());
                bytes.Add(next);
            }
            throw new InvalidDataException(Locales.Message("clips.steamMetadataStringIsTooLong"));
        }

        private static JObject Binary(BinaryReader reader, string[] strings, ref int tokens, int depth = 0)
        {
            if (depth > 32) throw new InvalidDataException(Locales.Message("clips.steamMetadataIsTooDeep"));
            var result = new JObject();
            while (++tokens <= 100000)
            {
                byte type = reader.ReadByte();
                if (type == 8) return result;
                string key;
                if (strings == null) key = String(reader);
                else { uint index = reader.ReadUInt32(); if (index >= strings.Length) throw new InvalidDataException(Locales.Message("clips.invalidSteamStringIndex")); key = strings[index]; }
                JToken value;
                if (type == 0) value = Binary(reader, strings, ref tokens, depth + 1);
                else if (type == 1) value = new JValue(String(reader));
                else if (type == 2 || type == 3 || type == 4 || type == 6) value = new JValue(reader.ReadUInt32());
                else if (type == 7 || type == 10) value = new JValue(reader.ReadUInt64());
                else throw new InvalidDataException(Locales.Message("clips.unsupportedSteamBinaryType"));
                if (result.Property(key, StringComparison.OrdinalIgnoreCase) != null) throw new InvalidDataException(Locales.Message("clips.duplicateSteamMetadataField"));
                result[key] = value;
            }
            throw new InvalidDataException(Locales.Message("clips.steamMetadataIsTooComplex"));
        }

        internal static Dictionary<uint, JObject> Apps(string file, HashSet<uint> wanted)
        {
            var result = new Dictionary<uint, JObject>();
            // the cache grows with the games an account owns; only the records of installed games are read and the rest are jumped over, so a large one costs little
            using (var input = Open(file, 256 * 1024 * 1024))
            using (var reader = new BinaryReader(input))
            {
                uint magic = reader.ReadUInt32(), version = magic & 255;
                if ((magic >> 8) != 0x075644 || version < 39 || version > 41 || reader.ReadUInt32() != 1) throw new InvalidDataException(Locales.Message("clips.unsupportedSteamAppCache"));
                string[] strings = null;
                long limit = input.Length;
                if (version == 41)
                {
                    long table = reader.ReadInt64();
                    if (table < 16 || table > input.Length - 4) throw new InvalidDataException(Locales.Message("clips.invalidSteamStringTable"));
                    input.Position = table;
                    uint count = reader.ReadUInt32();
                    if (count > 200000) throw new InvalidDataException(Locales.Message("clips.steamStringTableIsTooLarge"));
                    strings = new string[count];
                    for (int i = 0; i < strings.Length; i++) strings[i] = String(reader);
                    input.Position = 16;
                    limit = table;
                }
                for (int records = 0; records < 1000000 && input.Position <= limit - 4; records++)
                {
                    uint id = reader.ReadUInt32();
                    if (id == 0) return result;
                    uint size = reader.ReadUInt32();
                    long end = input.Position + size;
                    int header = version >= 40 ? 60 : 40;
                    if (size < header || end > limit) throw new InvalidDataException(Locales.Message("clips.invalidSteamAppRecord"));
                    if (wanted.Contains(id))
                    {
                        if (size > 2 * 1024 * 1024) throw new InvalidDataException(Locales.Message("clips.steamAppRecordIsTooLarge"));
                        input.Position += header;
                        byte[] bytes = reader.ReadBytes((int)size - header);
                        using (var data = new MemoryStream(bytes))
                        using (var fields = new BinaryReader(data))
                        {
                            int tokens = 0;
                            var app = Binary(fields, strings, ref tokens);
                            if (data.Position != data.Length) throw new InvalidDataException(Locales.Message("clips.invalidSteamAppPayload"));
                            result[id] = app["appinfo"] as JObject ?? app;
                        }
                    }
                    input.Position = end;
                }
                throw new InvalidDataException(Locales.Message("clips.invalidSteamCacheFooter"));
            }
        }
    }
}
