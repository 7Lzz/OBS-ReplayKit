using System;
using System.Collections.Generic;
using System.IO;

namespace ReplayKitHelper
{
    // the pieces of speech the clip audio was cut into and where each one sits in the joined audio the engine listened to, with silence between them so no word is heard across a pause
    internal sealed class SpeechTimings
    {
        private sealed class Range
        {
            public double SourceStart, SourceEnd, ProcessedStart, ProcessedEnd;
        }

        private readonly List<Range> ranges = new List<Range>();

        public int Count => ranges.Count;

        public void Add(double sourceStart, double sourceEnd, double processedStart, double processedEnd)
        {
            if (!(sourceEnd > sourceStart) || !(processedEnd > processedStart) ||
                (ranges.Count > 0 && (sourceStart < ranges[ranges.Count - 1].SourceEnd || processedStart < ranges[ranges.Count - 1].ProcessedEnd)))
                throw new InvalidDataException("The speech detector returned inconsistent speech ranges.");
            if (ranges.Count >= 20000) throw new InvalidDataException("The speech map is too large.");
            ranges.Add(new Range { SourceStart = sourceStart, SourceEnd = sourceEnd, ProcessedStart = processedStart, ProcessedEnd = processedEnd });
        }

        // the piece a word belongs to, by where the engine heard the middle of it, the way faster-whisper restores its speech chunks; a word heard in the silence between pieces goes to the nearer one
        public int PieceAt(double processed)
        {
            if (ranges.Count == 0 || double.IsNaN(processed) || double.IsInfinity(processed))
                throw new InvalidDataException("Speech-word timings could not be aligned with the original audio.");
            int low = 0, high = ranges.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (ranges[middle].ProcessedStart <= processed) low = middle + 1;
                else high = middle;
            }
            int index = Math.Max(0, low - 1);
            if (processed > ranges[index].ProcessedEnd && index + 1 < ranges.Count && ranges[index + 1].ProcessedStart - processed < processed - ranges[index].ProcessedEnd) index++;
            return index;
        }

        // a time of the joined audio as a time of the clip audio, kept within its piece
        public double ToSource(int index, double processed)
        {
            var range = ranges[index];
            return range.SourceStart + Math.Max(0, Math.Min(range.ProcessedEnd - range.ProcessedStart, processed - range.ProcessedStart));
        }

        // the stretch of the clip audio a piece covers, in seconds of what was listened to
        public void SourceRange(int index, out double start, out double end)
        {
            if (index < 0 || index >= ranges.Count) throw new InvalidDataException("Unknown speech range.");
            start = ranges[index].SourceStart;
            end = ranges[index].SourceEnd;
        }
    }
}
