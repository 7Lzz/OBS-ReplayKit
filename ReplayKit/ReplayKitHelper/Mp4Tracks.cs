using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReplayKitHelper
{
    // reads the track boxes of an mp4 without touching the media data, and puts the audio tracks into the one state every player understands: the first audio track enabled and the others disabled, all in the same alternate group, so a player plays the mix and lists the rest as alternatives instead of playing them all at once
    internal static class Mp4Tracks
    {
        internal sealed class Track
        {
            public uint Id;
            public bool Audio;
            public bool Video;
            // the 24 bit flags of the tkhd box: 1 enabled, 2 in the movie, 4 in the preview
            public uint Flags;
            public ushort AlternateGroup;
            // where the version and flags word and the alternate group of the tkhd box sit in the file, so they can be rewritten in place
            public long FlagsOffset;
            public long GroupOffset;
            public int Version;
            // the time scale of the media header, which for an audio track is its sample rate
            public uint Timescale;
            // the name of the first sample entry of the track, which is the codec: mp4a for aac
            public string SampleEntry = "";
        }

        private const uint TrackEnabled = 1;
        private const ushort AudioAlternateGroup = 1;

        // reads one big endian value; returns false when the file ends first
        private static bool ReadBytes(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static uint ReadU32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

        private static ulong ReadU64(byte[] b, int at) => ((ulong)ReadU32(b, at) << 32) | ReadU32(b, at + 4);

        private struct Box
        {
            public string Type;
            public long Start;
            public long HeaderSize;
            public long Size;
            public long PayloadStart => Start + HeaderSize;
            public long End => Start + Size;
        }

        // the boxes lying directly between start and end; stops at the first box that does not fit, which is what a file still being written looks like
        private static IEnumerable<Box> BoxesIn(Stream s, long start, long end)
        {
            var head = new byte[16];
            long pos = start;
            while (pos + 8 <= end)
            {
                s.Seek(pos, SeekOrigin.Begin);
                if (!ReadBytes(s, head, 8)) yield break;
                long size = ReadU32(head, 0);
                long header = 8;
                if (size == 1)
                {
                    if (!ReadBytes(s, head, 8)) yield break;
                    size = (long)ReadU64(head, 0);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }
                if (size < header || pos + size > end) yield break;
                s.Seek(pos + 4, SeekOrigin.Begin);
                var type = new byte[4];
                if (!ReadBytes(s, type, 4)) yield break;
                yield return new Box { Type = System.Text.Encoding.ASCII.GetString(type), Start = pos, HeaderSize = header, Size = size };
                pos += size;
            }
        }

        private static bool TryFind(Stream s, long start, long end, string type, out Box found)
        {
            foreach (var box in BoxesIn(s, start, end))
            {
                if (box.Type != type) continue;
                found = box;
                return true;
            }
            found = default(Box);
            return false;
        }

        // the tracks of the file in the order of its moov box, or null when it is not an mp4 with a readable moov box
        public static List<Track> Read(Stream s)
        {
            if (!TryFind(s, 0, s.Length, "moov", out Box moov)) return null;
            var tracks = new List<Track>();
            var buf = new byte[64];
            foreach (var trak in BoxesIn(s, moov.PayloadStart, moov.End))
            {
                if (trak.Type != "trak") continue;
                if (!TryFind(s, trak.PayloadStart, trak.End, "tkhd", out Box tkhd)) continue;
                if (!TryFind(s, trak.PayloadStart, trak.End, "mdia", out Box mdia)) continue;
                if (!TryFind(s, mdia.PayloadStart, mdia.End, "hdlr", out Box hdlr)) continue;

                s.Seek(hdlr.PayloadStart + 8, SeekOrigin.Begin);
                if (!ReadBytes(s, buf, 4)) continue;
                string handler = System.Text.Encoding.ASCII.GetString(buf, 0, 4);

                uint timescale = 0;
                if (TryFind(s, mdia.PayloadStart, mdia.End, "mdhd", out Box mdhd))
                {
                    s.Seek(mdhd.PayloadStart, SeekOrigin.Begin);
                    if (ReadBytes(s, buf, 1))
                    {
                        s.Seek(mdhd.PayloadStart + (buf[0] == 1 ? 20 : 12), SeekOrigin.Begin);
                        if (ReadBytes(s, buf, 4)) timescale = ReadU32(buf, 0);
                    }
                }

                string sampleEntry = "";
                if (TryFind(s, mdia.PayloadStart, mdia.End, "minf", out Box minf) && TryFind(s, minf.PayloadStart, minf.End, "stbl", out Box stbl) && TryFind(s, stbl.PayloadStart, stbl.End, "stsd", out Box stsd))
                {
                    // the box starts with its version and flags and the number of entries, then the first entry with its size and its name
                    s.Seek(stsd.PayloadStart + 12, SeekOrigin.Begin);
                    if (ReadBytes(s, buf, 4)) sampleEntry = System.Text.Encoding.ASCII.GetString(buf, 0, 4);
                }

                s.Seek(tkhd.PayloadStart, SeekOrigin.Begin);
                if (!ReadBytes(s, buf, 4)) continue;
                int version = buf[0];
                if (version != 0 && version != 1) continue;
                uint flags = ReadU32(buf, 0) & 0xFFFFFF;
                long idOffset = tkhd.PayloadStart + (version == 0 ? 12 : 20);
                long groupOffset = tkhd.PayloadStart + (version == 0 ? 34 : 46);
                if (groupOffset + 2 > tkhd.End) continue;
                s.Seek(idOffset, SeekOrigin.Begin);
                if (!ReadBytes(s, buf, 4)) continue;
                uint id = ReadU32(buf, 0);
                s.Seek(groupOffset, SeekOrigin.Begin);
                if (!ReadBytes(s, buf, 2)) continue;

                tracks.Add(new Track
                {
                    Id = id,
                    Audio = handler == "soun",
                    Video = handler == "vide",
                    Flags = flags,
                    AlternateGroup = (ushort)((buf[0] << 8) | buf[1]),
                    FlagsOffset = tkhd.PayloadStart,
                    GroupOffset = groupOffset,
                    Version = version,
                    Timescale = timescale,
                    SampleEntry = sampleEntry,
                });
            }
            return tracks;
        }

        // how many audio tracks a file has, 0 when it cannot be read as an mp4 (a file being written, another format)
        public static int CountAudio(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var tracks = Read(fs);
                    return tracks == null ? 0 : tracks.Count(t => t.Audio);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        // whether the file is an mp4 whose track boxes can be read; for one that is not, a count of no audio tracks says nothing, since the file may hold audio that is not seen
        public static bool IsReadable(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return Read(fs) != null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // the sample rate of every audio track in file order, empty when the file cannot be read as an mp4
        public static List<int> AudioSampleRates(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var tracks = Read(fs);
                    return tracks == null ? new List<int>() : tracks.Where(t => t.Audio).Select(t => t.Timescale > 0 ? (int)t.Timescale : 48000).ToList();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<int>();
            }
        }

        // the codec of every audio track in file order, mp4a for aac, empty when the file cannot be read as an mp4
        public static List<string> AudioSampleEntries(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var tracks = Read(fs);
                    return tracks == null ? new List<string>() : tracks.Where(t => t.Audio).Select(t => t.SampleEntry).ToList();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        public enum Fix { Nothing, Changed, Busy, Failed }

        // the flags every audio track should carry, by position: only the first one enabled
        private static uint WantedFlags(Track track, int position) => position == 0 ? (track.Flags | TrackEnabled) : (track.Flags & ~TrackEnabled);

        // makes the first audio track the enabled one and puts all audio tracks into one alternate group. only two small fields of the tkhd boxes are rewritten in place, so no offset or size of the file moves, and what was there is written back when anything goes wrong
        public static Fix NormalizeAudio(string path)
        {
            // looked at read only first, so a clip that needs nothing is never opened for writing
            List<Track> tracks;
            try
            {
                using (var ro = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    tracks = Read(ro);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Fix.Nothing;
            }
            if (tracks == null) return Fix.Nothing;
            var audio = tracks.Where(t => t.Audio).ToList();
            if (audio.Count < 2) return Fix.Nothing;
            bool needed = false;
            for (int i = 0; i < audio.Count; i++)
                needed |= WantedFlags(audio[i], i) != audio[i].Flags || audio[i].AlternateGroup != AudioAlternateGroup;
            if (!needed) return Fix.Nothing;

            // the time the clip was last changed stays what it was, so a corrected clip keeps its place in the list
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    var written = new List<Track>();
                    try
                    {
                        for (int i = 0; i < audio.Count; i++)
                        {
                            WriteTrack(fs, audio[i], WantedFlags(audio[i], i), AudioAlternateGroup);
                            written.Add(audio[i]);
                        }
                        fs.Flush();
                        var again = Read(fs);
                        var after = again == null ? null : again.Where(t => t.Audio).ToList();
                        bool ok = after != null && after.Count == audio.Count;
                        for (int i = 0; ok && i < after.Count; i++)
                            ok = after[i].Flags == WantedFlags(audio[i], i) && after[i].AlternateGroup == AudioAlternateGroup;
                        if (!ok) throw new IOException("the track flags did not read back as written");
                    }
                    catch (IOException ex)
                    {
                        foreach (var t in written) WriteTrack(fs, t, t.Flags, t.AlternateGroup);
                        fs.Flush();
                        Log.Write("Mp4Tracks: " + Path.GetFileName(path) + " left as it was: " + ex.Message);
                        return Fix.Failed;
                    }
                }
                File.SetLastWriteTimeUtc(path, stamp);
                return Fix.Changed;
            }
            catch (UnauthorizedAccessException)
            {
                // a read only file cannot be corrected, and that is not a reason to look at it again
                return Fix.Nothing;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32 || (ex.HResult & 0xFFFF) == 33)
            {
                // a sharing or lock violation: obs still holds the file open for writing, so the caller looks again later
                return Fix.Busy;
            }
            catch (IOException ex)
            {
                Log.Write("Mp4Tracks: " + Path.GetFileName(path) + " not corrected: " + ex.Message);
                return Fix.Failed;
            }
        }

        private static void WriteTrack(Stream s, Track t, uint flags, ushort group)
        {
            var word = new byte[4];
            word[0] = (byte)t.Version;
            word[1] = (byte)(flags >> 16);
            word[2] = (byte)(flags >> 8);
            word[3] = (byte)flags;
            s.Seek(t.FlagsOffset, SeekOrigin.Begin);
            s.Write(word, 0, 4);
            s.Seek(t.GroupOffset, SeekOrigin.Begin);
            s.Write(new[] { (byte)(group >> 8), (byte)group }, 0, 2);
        }
    }
}
