using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal sealed class SpeechTranscript
    {
        private sealed class Word
        {
            public string Text;
            // where the engine heard the first and the last sounding token, in seconds of the joined audio
            public double First, Last;
            public double Start;
            public double End;
            public int Stretch = -1;
            public double LogSum;
            public int LogCount;
            public bool Unsure;
        }

        // the average log probability under which whisper itself counts a decode as failed (its logprob_threshold)
        private const double UnsureLogProb = -1.0;
        // a word runs until the next one starts, but never longer than this, so the last word before a pause does not hang on through it
        private const double MaxWordLength = 1.5;

        public JArray Words = new JArray();
        public string Warning;
        public int Unsure;
        // the text of every piece of speech the engine was unsure of, by the number of the piece
        public readonly SortedDictionary<int, string> UnsureText = new SortedDictionary<int, string>();
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex TextWords = new Regex(@"\S+", RegexOptions.Compiled);
        private static readonly Regex Sound = new Regex(@"^(\[.*\]|\(.*\)|<\|.*\|>)$", RegexOptions.Compiled);
        private static readonly Regex Terms = new Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

        private static double Number(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return double.NaN;
            double number = value.Value<double>();
            return double.IsNaN(number) || double.IsInfinity(number) ? double.NaN : number;
        }

        private static bool Repeated(string text)
        {
            var terms = Terms.Matches(text).Cast<Match>().Select(match => match.Value.ToLowerInvariant()).ToArray();
            if (terms.Length < 32 || text.Length < 120) return false;
            for (int size = 1; size <= 4; size++)
            {
                int same = 0;
                for (int i = size; i < terms.Length; i++) if (terms[i] == terms[i - size]) same++;
                if (same >= (terms.Length - size) * 0.85) return true;
            }
            return false;
        }

        // the words of a segment from its tokens, timed by the dtw alignment of the engine (t_dtw, in centiseconds of the joined audio) held within the segment -- the edges of a segment come from the timestamp tokens whisper predicts and are sound at pauses, while the alignment alone can drift seconds into the silence before a lone word; the token offsets whisper.cpp estimates instead put the first word after a pause on the wrong side of it
        private static List<Word> TimedWords(JArray tokens, string text, double from, double to)
        {
            if (tokens == null || tokens.Count == 0) return null;
            var words = new List<Word>();
            Word current = null;
            foreach (var token in tokens)
            {
                double id = Number(token["id"]);
                if (double.IsNaN(id) || id >= 50257) continue;
                string piece = token["text"]?.Type == JTokenType.String ? token.Value<string>("text") : "";
                if (piece.Length == 0) continue;
                if (string.IsNullOrWhiteSpace(piece)) { current = null; continue; }
                if (TextWords.Matches(piece).Count > 1) return null;
                // punctuation makes no sound, so it joins the word before it and takes no part in its timing
                bool silent = !Terms.IsMatch(piece);
                double heard = Number(token["t_dtw"]) / 100;
                if (!silent && (double.IsNaN(heard) || heard < 0)) return null;
                heard = Math.Max(from, Math.Min(to, heard));
                foreach (Match match in TextWords.Matches(piece))
                {
                    if (current == null || match.Index > 0)
                    {
                        current = new Word { Text = "", First = double.NaN, Last = double.NaN };
                        words.Add(current);
                    }
                    current.Text += match.Value;
                    if (silent) continue;
                    if (double.IsNaN(current.First)) current.First = heard;
                    current.Last = Math.Max(heard, double.IsNaN(current.Last) ? heard : current.Last);
                    double p = Number(token["p"]);
                    if (p > 0 && p <= 1) { current.LogSum += Math.Log(p); current.LogCount++; }
                }
                if (char.IsWhiteSpace(piece[piece.Length - 1])) current = null;
            }
            if (Whitespace.Replace(string.Concat(words.Select(word => word.Text)), "") != Whitespace.Replace(text, "")) return null;
            // a lone mark with no word before it has nothing to be timed by
            words.RemoveAll(word => double.IsNaN(word.First));
            return words;
        }

        // start and end are the part of the clip that was listened to, in seconds of the clip; the pieces of timings are in seconds of what was listened to
        public static SpeechTranscript Parse(JObject data, double start, double end, SpeechTimings timings)
        {
            if (double.IsNaN(start) || double.IsNaN(end) || double.IsInfinity(start) || double.IsInfinity(end) || start < 0 || end <= start)
                throw new InvalidDataException("Invalid subtitle range.");
            if (timings == null || timings.Count == 0) throw new InvalidDataException("There is no speech map to place the words with.");
            if (!(data["transcription"] is JArray segments)) throw new InvalidDataException("The speech engine returned an invalid transcript.");
            var result = new SpeechTranscript();
            var words = new List<Word>();
            int rejected = 0;
            foreach (var segment in segments)
            {
                if (!(segment is JObject seg)) throw new InvalidDataException("The speech engine returned an invalid segment.");
                if (seg["text"]?.Type != JTokenType.String) throw new InvalidDataException("The speech engine returned invalid subtitle text.");
                string text = seg.Value<string>("text").Trim();
                if (text.Length == 0 || Sound.IsMatch(text)) continue;
                if (Repeated(text)) { rejected++; continue; }
                double from = Number(seg["offsets"]?["from"]) / 1000;
                double to = Number(seg["offsets"]?["to"]) / 1000;
                if (double.IsNaN(from) || double.IsNaN(to) || from < 0 || to < from) throw new InvalidDataException("The speech engine returned invalid segment timings.");
                var timed = TimedWords(seg["tokens"] as JArray, text, from, to);
                if (timed == null) throw new InvalidDataException("The speech engine did not provide word timings that could be aligned with the audio.");
                foreach (var word in timed)
                {
                    // the piece is the one holding the middle of the word, and the word starts where its first sound was heard, inside that piece
                    word.Stretch = timings.PieceAt((word.First + word.Last) / 2);
                    word.Start = Math.Max(start, Math.Min(end, start + timings.ToSource(word.Stretch, word.First)));
                    words.Add(word);
                    if (words.Count > 20000) throw new InvalidDataException("The transcript has too many words. Select a shorter section.");
                }
            }
            words = words.OrderBy(word => word.Start).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                timings.SourceRange(word.Stretch, out double pieceStart, out double pieceEnd);
                double until = Math.Min(end, start + pieceEnd);
                if (i + 1 < words.Count && words[i + 1].Stretch == word.Stretch) until = Math.Min(until, words[i + 1].Start);
                word.End = Math.Max(word.Start, Math.Min(until, word.Start + MaxWordLength));
            }
            // a piece of speech the engine was this unsure of overall is a guess, most often one word said alone over noise; it is kept but marked so the editor can ask for it to be checked
            foreach (var stretch in words.GroupBy(word => word.Stretch))
            {
                int count = stretch.Sum(word => word.LogCount);
                if (count > 0 && stretch.Sum(word => word.LogSum) / count < UnsureLogProb)
                    foreach (var word in stretch) word.Unsure = true;
            }
            int unsure = 0;
            foreach (var word in words)
            {
                var item = new JArray(Math.Round(word.Start, 3), Math.Round(word.End, 3), word.Text);
                // an unsure word carries the number of its piece plus one, so the editor can keep each piece in a subtitle of its own and offer what else it might have been
                if (word.Unsure)
                {
                    item.Add(word.Stretch + 1);
                    unsure++;
                    result.UnsureText.TryGetValue(word.Stretch, out string text);
                    result.UnsureText[word.Stretch] = text == null ? word.Text : text + " " + word.Text;
                }
                result.Words.Add(item);
            }
            result.Unsure = unsure;
            if (rejected > 0 && words.Count == 0) throw new InvalidDataException("The speech engine returned repeated text instead of reliable speech. Choose the spoken language or an isolated voice track.");
            if (rejected > 0) result.Warning = "Repeated or unreliable passages were skipped; review the subtitles.";
            return result;
        }
    }
}
