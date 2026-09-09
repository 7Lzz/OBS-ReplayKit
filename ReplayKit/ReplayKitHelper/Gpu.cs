using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // detect the users gpu(s) and classify them for encoder selection. pulls win32_videocontroller via wmi and parses the adapter name to tag the architecture tier (registry/wmi doesnt expose the arch as a structured field, but marketing names follow stable patterns). prefers discrete over integrated when both are present. this is the exact same detection logic as ReplayKitSetup/Gpu.cs (the fresh-install path) -- copied rather than re-derived from obs_replaykit helper modules/62_replaykit_settings.ps1s Get-ReplayKitPrimaryGpu so both the installer and the live settings dock agree on the same classification, and so a bug fixed in one doesnt need re-fixing in the other by hand. only real change: QueryVideoControllers queries WMI directly instead of spawning powershell.exe, since this project already references System.Management (ReplayKitSetup didnt, historically).
    public enum Vendor { Nvidia, Amd, Intel, Unknown }

    // coarse nvenc capability tiers.
    public enum NvidiaGen { PreMaxwell2, Maxwell2, Pascal, Turing, Ampere, Ada, Blackwell }

    // coarse amf capability tiers.
    public enum AmdGen { PrePolaris, Polaris, Vega, Rdna1, Rdna2, Rdna3, Rdna4 }

    // coarse quick sync capability tiers.
    public enum IntelGen { PreSkylake, Skylake, KabyLake, IceLake, TigerLake, AlderLake, Arc }

    // one physical adapter detected on the system.
    public sealed class GpuInfo
    {
        public string Name { get; set; }
        public Vendor Vendor { get; set; }
        public string Generation { get; set; } // nvidiagen/amdgen/intelgen snake_case value, or null
        public bool IsDiscrete { get; set; }
        public long VramBytes { get; set; }
        public string Driver { get; set; }

        public string Label => Name;

        public string GenLabel
        {
            get
            {
                if (Generation == null) return "unknown";
                var words = Generation.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1));
                return string.Join(" ", words);
            }
        }
    }

    public static class Gpu
    {
        private static List<JObject> QueryVideoControllers()
        {
            var result = new List<JObject>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController"))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        long? ram = null;
                        try { if (row["AdapterRAM"] != null) ram = Convert.ToInt64(row["AdapterRAM"]); } catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException) { }
                        result.Add(new JObject
                        {
                            ["Name"] = row["Name"] as string,
                            ["AdapterRAM"] = ram,
                            ["DriverVersion"] = row["DriverVersion"] as string,
                        });
                    }
                }
            }
            catch (ManagementException) { }
            return result;
        }

        private static readonly (Regex Pattern, Vendor Vendor)[] VendorPatterns =
        {
            (new Regex(@"\bnvidia\b|\bgeforce\b|\bquadro\b|\brtx\b|\bgtx\b", RegexOptions.IgnoreCase), Vendor.Nvidia),
            (new Regex(@"\bamd\b|\bradeon\b|\brx\s*\d", RegexOptions.IgnoreCase), Vendor.Amd),
            (new Regex(@"\bintel\b|\biris\b|\barc\b|\buhd\b|\bhd\s*graphics\b", RegexOptions.IgnoreCase), Vendor.Intel),
        };

        // basic-display-adapter / indirect-display / remote-display drivers we want to ignore (no real encoder support, never the right answer for obs).
        private static readonly Regex[] IgnoreNamePatterns =
        {
            new Regex(@"\bbasic\s+display\s+adapter\b", RegexOptions.IgnoreCase),
            new Regex(@"\bidd\b|indirect\s+display", RegexOptions.IgnoreCase),
            new Regex(@"\bremote\s+display\b", RegexOptions.IgnoreCase),
            new Regex(@"\bvirtual\s+display\b", RegexOptions.IgnoreCase),
            new Regex(@"\bdisplay\s*link\b", RegexOptions.IgnoreCase),
        };

        private static Vendor DetectVendor(string name)
        {
            foreach (var (pattern, vendor) in VendorPatterns)
            {
                if (pattern.IsMatch(name)) return vendor;
            }
            return Vendor.Unknown;
        }

        // nvidia model -> arch. ordered most-specific first; first match wins.
        private static readonly (Regex Pattern, NvidiaGen Gen)[] NvidiaPatterns =
        {
            // blackwell -- rtx 50-series (2025+). 50xx covers desktop + mobile.
            (new Regex(@"\brtx\s*50\d{2}\b", RegexOptions.IgnoreCase), NvidiaGen.Blackwell),
            // ada lovelace -- rtx 40-series (2022-2024).
            (new Regex(@"\brtx\s*40\d{2}\b", RegexOptions.IgnoreCase), NvidiaGen.Ada),
            // ampere -- rtx 30-series.
            (new Regex(@"\brtx\s*30\d{2}\b", RegexOptions.IgnoreCase), NvidiaGen.Ampere),
            // turing -- rtx 20-series + gtx 16-series.
            (new Regex(@"\brtx\s*20\d{2}\b|\bgtx\s*16\d{2}\b|\btitan\s*rtx\b", RegexOptions.IgnoreCase), NvidiaGen.Turing),
            // pascal -- gtx 10-series + titan xp; pascal quadro variants match via the p prefix.
            (new Regex(@"\bgtx\s*10\d{2}\b|\btitan\s*xp\b|\bquadro\s*p\d", RegexOptions.IgnoreCase), NvidiaGen.Pascal),
            // maxwell 2nd gen -- gtx 9-series (gm204/206). gtx 750/750ti was maxwell 1 which didnt have hevc, so we deliberately dont claim it.
            (new Regex(@"\bgtx\s*9\d{2}\b|\btitan\s*x\b", RegexOptions.IgnoreCase), NvidiaGen.Maxwell2),
        };

        private static NvidiaGen DetectNvidiaGen(string name)
        {
            foreach (var (pattern, gen) in NvidiaPatterns)
            {
                if (pattern.IsMatch(name)) return gen;
            }
            return NvidiaGen.PreMaxwell2;
        }

        private static readonly (Regex Pattern, AmdGen Gen)[] AmdPatterns =
        {
            // rdna 4 -- rx 9000-series (forward-looking).
            (new Regex(@"\brx\s*9\d{3}\b", RegexOptions.IgnoreCase), AmdGen.Rdna4),
            // rdna 3 -- rx 7000-series.
            (new Regex(@"\brx\s*7\d{3}\b", RegexOptions.IgnoreCase), AmdGen.Rdna3),
            // rdna 2 -- rx 6000-series + rdna-2 integrated (rembrandt, ryzen 6000u/h).
            (new Regex(@"\brx\s*6\d{3}\b|\bradeon\s+graphics\s*\(rembrandt\)", RegexOptions.IgnoreCase), AmdGen.Rdna2),
            // rdna 1 -- rx 5000-series.
            (new Regex(@"\brx\s*5\d{3}\b", RegexOptions.IgnoreCase), AmdGen.Rdna1),
            // vega / radeon vii.
            (new Regex(@"\brx\s+vega\b|\bvega\s*\d|\bradeon\s+vii\b", RegexOptions.IgnoreCase), AmdGen.Vega),
            // polaris -- rx 4xx / 5xx (570/580/590 etc.). 5xx range conflicts with rdna1s 5000-series, so we match 3-digit 5xx only (not 4-digit 5xxx).
            (new Regex(@"\brx\s*4\d{2}\b|\brx\s*5\d{2}(?!\d)", RegexOptions.IgnoreCase), AmdGen.Polaris),
        };

        private static AmdGen DetectAmdGen(string name)
        {
            foreach (var (pattern, gen) in AmdPatterns)
            {
                if (pattern.IsMatch(name)) return gen;
            }
            return AmdGen.PrePolaris;
        }

        private static readonly (Regex Pattern, IntelGen Gen)[] IntelPatterns =
        {
            // arc discrete + xe-hpg. arc a310/a380/a580/a750/a770 all share the alchemist hevc encode block.
            (new Regex(@"\barc\s*a?\d{3}\b|\bxe-hpg\b", RegexOptions.IgnoreCase), IntelGen.Arc),
            // alder/raptor/meteor lake iris xe in 12th-14th gen.
            (new Regex(@"\b(12|13|14)\d{2}[a-z]?\b.*(uhd|iris)", RegexOptions.IgnoreCase), IntelGen.AlderLake),
            // tiger lake (11th gen) -- xe-lp.
            (new Regex(@"\b11\d{2}[a-z]?\b.*(iris|xe)|\bxe\s*graphics\b", RegexOptions.IgnoreCase), IntelGen.TigerLake),
            // ice lake (10th gen) -- first xe igpu.
            (new Regex(@"\b10\d{2}[a-z]?\b.*iris", RegexOptions.IgnoreCase), IntelGen.IceLake),
            // kaby lake (7th gen) hd 6xx. coffee lake (8th/9th) + comet lake (10th non-ice-lake) reuse the same igpu silicon, so they share this rule.
            (new Regex(@"\b(uhd|hd)\s*graphics\s*6\d{2}\b", RegexOptions.IgnoreCase), IntelGen.KabyLake),
            // skylake (6th gen) hd 5xx -- first with qsv hevc.
            (new Regex(@"\bhd\s*graphics\s*5\d{2}\b", RegexOptions.IgnoreCase), IntelGen.Skylake),
        };

        private static IntelGen DetectIntelGen(string name)
        {
            foreach (var (pattern, gen) in IntelPatterns)
            {
                if (pattern.IsMatch(name)) return gen;
            }
            return IntelGen.PreSkylake;
        }

        private static string SnakeCase(string enumName)
        {
            // PreMaxwell2 -> pre_maxwell2, Rdna1 -> rdna1, KabyLake -> kaby_lake
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (char.IsUpper(c) && i > 0 && !char.IsUpper(enumName[i - 1])) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // map a win32_videocontroller row to a GpuInfo, or null if it looks like a virtual/basic/fallback adapter we should ignore.
        private static GpuInfo Classify(JObject raw)
        {
            string name = (raw.Value<string>("Name") ?? "").Trim();
            if (name.Length == 0) return null;
            foreach (var skip in IgnoreNamePatterns)
            {
                if (skip.IsMatch(name)) return null;
            }

            Vendor vendor = DetectVendor(name);
            string gen = vendor == Vendor.Nvidia ? SnakeCase(DetectNvidiaGen(name).ToString())
                : vendor == Vendor.Amd ? SnakeCase(DetectAmdGen(name).ToString())
                : vendor == Vendor.Intel ? SnakeCase(DetectIntelGen(name).ToString())
                : null;

            long vram = raw.Value<long?>("AdapterRAM") ?? 0;
            // heuristic: igpus typically expose <2 gb adapterram (rest is shared/dynamic). discrete cards report full vram. 2 gb threshold catches apus/igpus that reserve up to 2 gb dedicated (ryzen) without losing genuinely small discrete cards from ~2014.
            bool isDiscrete = vram >= 2L * 1024 * 1024 * 1024 && vendor != Vendor.Intel;

            return new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                Generation = gen,
                IsDiscrete = isDiscrete,
                VramBytes = vram,
                Driver = (raw.Value<string>("DriverVersion") ?? "").Trim(),
            };
        }

        // every adapter we can usefully classify, discrete cards first.
        public static List<GpuInfo> ListGpus()
        {
            var rows = QueryVideoControllers();
            var result = rows.Select(Classify).Where(g => g != null).ToList();
            var vendorRank = new Dictionary<Vendor, int> { [Vendor.Nvidia] = 0, [Vendor.Amd] = 1, [Vendor.Intel] = 2, [Vendor.Unknown] = 3 };
            result = result.OrderBy(g => g.IsDiscrete ? 0 : 1).ThenBy(g => vendorRank[g.Vendor]).ToList();
            return result;
        }

        // best guess at which adapter obs will use for hardware encoding. null means callers should fall back to software encode.
        public static GpuInfo PrimaryGpu()
        {
            var gpus = ListGpus();
            return gpus.Count > 0 ? gpus[0] : null;
        }
    }
}
