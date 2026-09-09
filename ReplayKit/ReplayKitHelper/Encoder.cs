using System;
using System.Collections.Generic;
using System.Linq;

namespace ReplayKitHelper
{
    // pick the right obs recording encoder + settings for the users gpu. inputs: detected gpu (vendor+gen), codec preference (auto/h264/h265), preset cqp target, compression_mode (lower_gpu/balanced/smaller_files). outputs an EncoderChoice with the obs encoder id, encoder json, and human-readable label. fallback chain: user-preferred codec -> next-best hardware codec on this gpu -> software x264 (always available). av1 is deliberately not offered -- most iphones and plenty of android devices have no av1 decoder, and unlike a container-tag issue theres no fix for missing silicon. identical to ReplayKitSetup/Encoder.cs (the fresh-install path) -- copied rather than re-derived from obs_replaykit helper modules/62_replaykit_settings.ps1s New-ReplayKitEncoderSettings/Get-ReplayKitEncoderSpec so both agree on the same tuning tables. that ps code still has unreachable nvenc_av1/amf_av1/qsv_av1 branches left over from before av1 was dropped (Get-ReplayKitEncoderSpec never builds an "av1" candidate, so those cases are never selected) -- this port already omits them, matching how ReplayKitSetup was cleaned up.
    public enum CodecPreference
    {
        Auto,
        H264,
        H265,
    }

    public static class CodecPreferenceExt
    {
        // user-facing codec preference string -> enum. anything unrecognized falls back to auto.
        public static CodecPreference Parse(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "h264": return CodecPreference.H264;
                case "h265": return CodecPreference.H265;
                default: return CodecPreference.Auto;
            }
        }
    }

    // resolved encoder selection for this machine.
    public sealed class EncoderChoice
    {
        public string ObsEncoderId { get; set; }
        public Dictionary<string, object> Settings { get; set; }
        public string Codec { get; set; } // "h264" / "h265" -- for the menu
        public string Backend { get; set; } // "nvenc" / "amf" / "qsv" / "x264"
        public string Label { get; set; }
        public string Description { get; set; }

        public bool IsHardware => Backend != "x264";
    }

    public static class Encoder
    {
        // capability tables -- what each generation can actualy do. the encoder json we emit only references features the silicon supports; obs otherwise silently ignores unknown keys but the encoder may misbehave.
        private static readonly HashSet<NvidiaGen> NvidiaHevcOk = new HashSet<NvidiaGen> { NvidiaGen.Maxwell2, NvidiaGen.Pascal, NvidiaGen.Turing, NvidiaGen.Ampere, NvidiaGen.Ada, NvidiaGen.Blackwell };
        // nvenc hevc b-frames: turing+. pascal hevc cant use b-frames.
        private static readonly HashSet<NvidiaGen> NvidiaBfHevcOk = new HashSet<NvidiaGen> { NvidiaGen.Turing, NvidiaGen.Ampere, NvidiaGen.Ada, NvidiaGen.Blackwell };
        // lookahead: pascal+ (maxwell 2 doesnt implement it).
        private static readonly HashSet<NvidiaGen> NvidiaLookaheadOk = new HashSet<NvidiaGen> { NvidiaGen.Pascal, NvidiaGen.Turing, NvidiaGen.Ampere, NvidiaGen.Ada, NvidiaGen.Blackwell };
        // b-frames: pascal+ for nvenc h264.
        private static readonly HashSet<NvidiaGen> NvidiaBfH264Ok = new HashSet<NvidiaGen> { NvidiaGen.Pascal, NvidiaGen.Turing, NvidiaGen.Ampere, NvidiaGen.Ada, NvidiaGen.Blackwell };

        private static readonly HashSet<AmdGen> AmdHevcOk = new HashSet<AmdGen> { AmdGen.Polaris, AmdGen.Vega, AmdGen.Rdna1, AmdGen.Rdna2, AmdGen.Rdna3, AmdGen.Rdna4 };
        // amf hevc b-frames: vega+ (polaris hevc has no b-frame support).
        private static readonly HashSet<AmdGen> AmdBfOk = new HashSet<AmdGen> { AmdGen.Vega, AmdGen.Rdna1, AmdGen.Rdna2, AmdGen.Rdna3, AmdGen.Rdna4 };

        private static readonly HashSet<IntelGen> IntelHevcOk = new HashSet<IntelGen> { IntelGen.Skylake, IntelGen.KabyLake, IntelGen.IceLake, IntelGen.TigerLake, IntelGen.AlderLake, IntelGen.Arc };

        // compression-mode effort tables. balanced is the validated default (~10-11% gpu on a 3060 ti at 1080p60 hevc, vs the previous heavyweight config which sat near 30%). lower_gpu trades file size for less encoder work; smaller_files does the opposite. cqp stays the same across modes -- only encoder effort changes, so visual quality is held constant while gpu/filesize move opposite directions.
        private sealed class NvencEffort { public string Preset; public string Multipass; public bool Lookahead; public int Bf; }
        private static readonly Dictionary<string, NvencEffort> NvencEffortTable = new Dictionary<string, NvencEffort>
        {
            ["lower_gpu"] = new NvencEffort { Preset = "p1", Multipass = "disabled", Lookahead = false, Bf = 0 },
            ["balanced"] = new NvencEffort { Preset = "p2", Multipass = "disabled", Lookahead = false, Bf = 2 },
            ["smaller_files"] = new NvencEffort { Preset = "p5", Multipass = "qres", Lookahead = true, Bf = 3 },
        };

        private sealed class AmfEffort { public string Preset; public int Bf; }
        private static readonly Dictionary<string, AmfEffort> AmfEffortTable = new Dictionary<string, AmfEffort>
        {
            ["lower_gpu"] = new AmfEffort { Preset = "speed", Bf = 0 },
            ["balanced"] = new AmfEffort { Preset = "balanced", Bf = 2 },
            ["smaller_files"] = new AmfEffort { Preset = "quality", Bf = 3 },
        };

        private static readonly Dictionary<string, string> QsvEffortTable = new Dictionary<string, string>
        {
            ["lower_gpu"] = "veryfast",
            ["balanced"] = "balanced",
            ["smaller_files"] = "slower",
        };

        private static readonly Dictionary<string, string> X264EffortTable = new Dictionary<string, string>
        {
            ["lower_gpu"] = "superfast",
            ["balanced"] = "veryfast",
            ["smaller_files"] = "medium",
        };

        // look up the requested effort tier; fall back to balanced for unknown modes (forwards-compat with future values).
        private static T Effort<T>(Dictionary<string, T> table, string mode) => table.TryGetValue(mode, out var v) ? v : table["balanced"];

        // nvenc/amf cqp -> intel qsv icq (1-51, lower = higher quality). +1 offset becuase qsv hevc is a bit less efficient than nvenc hevc.
        private static int ToQsvIcq(int cqp) => Math.Max(1, Math.Min(51, cqp + 1));

        // nvenc cqp -> libx264 crf. software x264 is more bit-efficient than nvenc at the same numeric value, so shift down ~2 to keep visual quality comparable without blowing up file size.
        private static int ToX264Crf(int cqp) => Math.Max(0, Math.Min(51, cqp - 2));

        // nvenc h.264. preset/multipass/lookahead/bf come from the user-selected compression_mode.
        private static Dictionary<string, object> BuildNvencH264(int cqp, NvidiaGen gen, string mode)
        {
            var effort = Effort(NvencEffortTable, mode);
            var s = new Dictionary<string, object>
            {
                ["rate_control"] = "CQP",
                ["cqp"] = cqp,
                ["keyint_sec"] = 2,
                ["preset"] = effort.Preset,
                ["multipass"] = effort.Multipass,
                ["tune"] = "hq",
                ["profile"] = "high",
            };
            // b-frames: requested count, capped by generation support.
            s["bf"] = NvidiaBfH264Ok.Contains(gen) ? effort.Bf : 0;
            s["lookahead"] = effort.Lookahead && NvidiaLookaheadOk.Contains(gen);
            return s;
        }

        // nvenc hevc. profile=main keeps it 8-bit -- 10-bit hevc doubles encode cost and the dock cef preview doesnt decode hdr hevc reliably.
        private static Dictionary<string, object> BuildNvencHevc(int cqp, NvidiaGen gen, string mode)
        {
            var effort = Effort(NvencEffortTable, mode);
            var s = new Dictionary<string, object>
            {
                ["rate_control"] = "CQP",
                // hevc is ~30-50% more efficient than h.264 at the same perceived quality, so we use a slightly higher cqp for the same look.
                ["cqp"] = cqp + 2,
                ["keyint_sec"] = 2,
                ["preset"] = effort.Preset,
                ["multipass"] = effort.Multipass,
                ["tune"] = "hq",
                ["profile"] = "main",
            };
            s["bf"] = NvidiaBfHevcOk.Contains(gen) ? effort.Bf : 0;
            s["lookahead"] = effort.Lookahead && NvidiaLookaheadOk.Contains(gen);
            return s;
        }

        // amd amf h.264. filler_data off becuase for recording it just wastes disk.
        private static Dictionary<string, object> BuildAmfH264(int cqp, AmdGen gen, string mode)
        {
            var effort = Effort(AmfEffortTable, mode);
            return new Dictionary<string, object>
            {
                ["rate_control"] = "CQP",
                ["cqp"] = cqp,
                ["keyint_sec"] = 2,
                ["preset"] = effort.Preset,
                ["profile"] = "high",
                ["filler_data"] = false,
                ["bf"] = effort.Bf,
            };
        }

        // amf hevc (polaris+). rdna2+ adds b-frame support.
        private static Dictionary<string, object> BuildAmfHevc(int cqp, AmdGen gen, string mode)
        {
            var effort = Effort(AmfEffortTable, mode);
            var s = new Dictionary<string, object>
            {
                ["rate_control"] = "CQP",
                ["cqp"] = cqp + 2, // match nvenc hevc offset
                ["keyint_sec"] = 2,
                ["preset"] = effort.Preset,
                ["profile"] = "main",
                ["filler_data"] = false,
            };
            s["bf"] = AmdBfOk.Contains(gen) ? effort.Bf : 0;
            return s;
        }

        // quick sync h.264. icq ("intelligent constant quality") is qsvs closest equivalent to nvenc cqp -- bits track perceptual quality, not a hard bitrate.
        private static Dictionary<string, object> BuildQsvH264(int cqp, string mode)
        {
            return new Dictionary<string, object>
            {
                ["rate_control"] = "ICQ",
                ["icq_quality"] = ToQsvIcq(cqp),
                ["keyint_sec"] = 2,
                ["target_usage"] = Effort(QsvEffortTable, mode),
                ["profile"] = "high",
                ["async_depth"] = 4,
                ["low_power"] = false,
            };
        }

        private static Dictionary<string, object> BuildQsvHevc(int cqp, string mode)
        {
            return new Dictionary<string, object>
            {
                ["rate_control"] = "ICQ",
                ["icq_quality"] = ToQsvIcq(cqp + 2),
                ["keyint_sec"] = 2,
                ["target_usage"] = Effort(QsvEffortTable, mode),
                ["profile"] = "main",
                ["async_depth"] = 4,
                ["low_power"] = false,
            };
        }

        // libx264 software encoder. last-resort fallback.
        private static Dictionary<string, object> BuildX264(int cqp, string mode)
        {
            return new Dictionary<string, object>
            {
                ["rate_control"] = "CRF",
                ["crf"] = ToX264Crf(cqp),
                ["keyint_sec"] = 2,
                ["preset"] = Effort(X264EffortTable, mode),
                ["profile"] = "high",
                ["tune"] = "",
                ["x264opts"] = "",
            };
        }

        // one concrete encoder we could pick. resolved into an EncoderChoice by PickEncoder().
        private sealed class Candidate
        {
            public string ObsEncoderId;
            public string Codec;
            public string Backend;
            public string Label;
            public string Description;
            public Func<int, string, Dictionary<string, object>> Builder;
        }

        private static string GenLabel(string snakeGen)
        {
            var words = snakeGen.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1));
            return string.Join(" ", words);
        }

        private static Dictionary<string, Candidate> NvencCandidates(string genStr)
        {
            var gen = ParseNvidiaGen(genStr);
            var av = new Dictionary<string, Candidate>
            {
                ["h264"] = new Candidate
                {
                    ObsEncoderId = "obs_nvenc_h264_tex",
                    Codec = "h264",
                    Backend = "nvenc",
                    Label = "NVENC H.264",
                    Description = "NVIDIA hardware H.264 - plays anywhere, biggest files of these options.",
                    Builder = (cqp, mode) => BuildNvencH264(cqp, gen, mode),
                },
            };
            if (NvidiaHevcOk.Contains(gen))
            {
                av["h265"] = new Candidate
                {
                    ObsEncoderId = "obs_nvenc_hevc_tex",
                    Codec = "h265",
                    Backend = "nvenc",
                    Label = $"NVENC HEVC ({GenLabel(genStr)})",
                    Description = "NVIDIA hardware HEVC - ~half the file size of H.264 at the same quality.",
                    Builder = (cqp, mode) => BuildNvencHevc(cqp, gen, mode),
                };
            }
            return av;
        }

        private static Dictionary<string, Candidate> AmfCandidates(string genStr)
        {
            var gen = ParseAmdGen(genStr);
            var av = new Dictionary<string, Candidate>
            {
                ["h264"] = new Candidate
                {
                    ObsEncoderId = "h264_amf",
                    Codec = "h264",
                    Backend = "amf",
                    Label = "AMF H.264",
                    Description = "AMD hardware H.264 - plays anywhere, modest compression.",
                    Builder = (cqp, mode) => BuildAmfH264(cqp, gen, mode),
                },
            };
            if (AmdHevcOk.Contains(gen))
            {
                av["h265"] = new Candidate
                {
                    ObsEncoderId = "h265_amf",
                    Codec = "h265",
                    Backend = "amf",
                    Label = $"AMF HEVC ({genStr.ToUpperInvariant()})",
                    Description = "AMD hardware HEVC - significantly smaller files than H.264.",
                    Builder = (cqp, mode) => BuildAmfHevc(cqp, gen, mode),
                };
            }
            return av;
        }

        private static Dictionary<string, Candidate> QsvCandidates(string genStr)
        {
            var gen = ParseIntelGen(genStr);
            var av = new Dictionary<string, Candidate>
            {
                ["h264"] = new Candidate
                {
                    ObsEncoderId = "obs_qsv11_h264",
                    Codec = "h264",
                    Backend = "qsv",
                    Label = "Quick Sync H.264",
                    Description = "Intel iGPU H.264 - low CPU/GPU cost, larger files.",
                    Builder = (cqp, mode) => BuildQsvH264(cqp, mode),
                },
            };
            if (IntelHevcOk.Contains(gen))
            {
                av["h265"] = new Candidate
                {
                    ObsEncoderId = "obs_qsv11_hevc",
                    Codec = "h265",
                    Backend = "qsv",
                    Label = $"Quick Sync HEVC ({GenLabel(genStr)})",
                    Description = "Intel iGPU HEVC - smaller files, low overhead.",
                    Builder = (cqp, mode) => BuildQsvHevc(cqp, mode),
                };
            }
            return av;
        }

        private static Candidate SoftwareCandidate()
        {
            return new Candidate
            {
                ObsEncoderId = "obs_x264",
                Codec = "h264",
                Backend = "x264",
                Label = "x264 (software)",
                Description = "CPU-only H.264 - works on any machine, uses CPU not GPU.",
                Builder = (cqp, mode) => BuildX264(cqp, mode),
            };
        }

        private static NvidiaGen ParseNvidiaGen(string s) => Enum.TryParse<NvidiaGen>(ToPascal(s), out var v) ? v : NvidiaGen.PreMaxwell2;
        private static AmdGen ParseAmdGen(string s) => Enum.TryParse<AmdGen>(ToPascal(s), out var v) ? v : AmdGen.PrePolaris;
        private static IntelGen ParseIntelGen(string s) => Enum.TryParse<IntelGen>(ToPascal(s), out var v) ? v : IntelGen.PreSkylake;

        private static string ToPascal(string snake)
        {
            return string.Concat(snake.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        // codec slug -> usable encoder for this gpu. software x264 is always appended under "software" so callers can fall back.
        private static Dictionary<string, Candidate> AvailableCandidates(GpuInfo gpu)
        {
            if (gpu == null) return new Dictionary<string, Candidate> { ["software"] = SoftwareCandidate() };

            string gen = gpu.Generation ?? "";
            Dictionary<string, Candidate> candidates;
            switch (gpu.Vendor)
            {
                case Vendor.Nvidia: candidates = NvencCandidates(gen); break;
                case Vendor.Amd: candidates = AmfCandidates(gen); break;
                case Vendor.Intel: candidates = QsvCandidates(gen); break;
                default: candidates = new Dictionary<string, Candidate>(); break;
            }
            candidates["software"] = SoftwareCandidate();
            return candidates;
        }

        // preferred-codec fallback order.
        private static readonly Dictionary<CodecPreference, string[]> FallbackOrder = new Dictionary<CodecPreference, string[]>
        {
            [CodecPreference.H265] = new[] { "h265", "h264", "software" },
            [CodecPreference.H264] = new[] { "h264", "software" },
            [CodecPreference.Auto] = new[] { "h265", "h264", "software" },
        };

        // resolve codec preference + gpu + compression_mode into a concrete encoder choice. cqp is on the nvenc scale (0-51, lower = better); the chosen builder translates as needed (crf for x264, icq for qsv, etc.). compression_mode controls encoder effort (lower_gpu/balanced/smaller_files).
        public static EncoderChoice PickEncoder(GpuInfo gpu, string codecPreference, int cqp, string compressionMode = "balanced")
        {
            var pref = CodecPreferenceExt.Parse(codecPreference);
            var candidates = AvailableCandidates(gpu);

            foreach (var codec in FallbackOrder[pref])
            {
                if (!candidates.TryGetValue(codec, out var cand)) continue;
                return new EncoderChoice
                {
                    ObsEncoderId = cand.ObsEncoderId,
                    Settings = cand.Builder(cqp, compressionMode),
                    Codec = cand.Codec,
                    Backend = cand.Backend,
                    Label = cand.Label,
                    Description = cand.Description,
                };
            }

            // unreachable: fallback_order always ends in "software" and AvailableCandidates always includes it. defensive last-ditch so callers never get null.
            var sw = SoftwareCandidate();
            return new EncoderChoice
            {
                ObsEncoderId = sw.ObsEncoderId,
                Settings = sw.Builder(cqp, compressionMode),
                Codec = sw.Codec,
                Backend = sw.Backend,
                Label = sw.Label,
                Description = sw.Description,
            };
        }

        // codec slug -> short label for the codecs this gpu can actualy run. "auto" is always included.
        public static Dictionary<string, string> AvailableCodecs(GpuInfo gpu)
        {
            var cand = AvailableCandidates(gpu);
            var labels = new Dictionary<string, string> { ["auto"] = "Auto (pick best for this GPU)" };
            if (cand.TryGetValue("h264", out var h264)) labels["h264"] = h264.Label;
            if (cand.TryGetValue("h265", out var h265)) labels["h265"] = h265.Label;
            return labels;
        }
    }
}
