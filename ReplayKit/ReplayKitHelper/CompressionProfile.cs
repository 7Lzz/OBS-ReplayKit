using System;
using System.Runtime.InteropServices;

namespace ReplayKitHelper
{
    internal static class CompressionProfile
    {
        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

        internal sealed class Resources
        {
            public int Workers, DecoderThreads, FrameThreads;
        }

        public static Resources Limits(int processors, long availableMemory, bool hevc)
        {
            int count = Math.Max(1, Math.Min(1024, processors));
            int workers = hevc ? count - Math.Max(1, count / 4) : count / 2;
            workers = Math.Max(1, Math.Min(16, workers));
            int frames = 0;
            if (availableMemory <= 0)
            {
                workers = Math.Min(workers, Math.Max(1, count / 2));
                frames = 2;
            }
            else if (availableMemory < 2L * 1024 * 1024 * 1024)
            {
                workers = Math.Min(workers, 2);
                frames = 1;
            }
            else if (availableMemory < 4L * 1024 * 1024 * 1024)
            {
                workers = Math.Min(workers, 4);
                frames = 2;
            }
            if (count <= 2) frames = 1;
            return new Resources { Workers = workers, DecoderThreads = Math.Max(1, Math.Min(4, workers)), FrameThreads = frames };
        }

        public static Resources Limits(bool hevc)
        {
            var memory = new MemoryStatus();
            long available = GlobalMemoryStatusEx(memory) ? (long)Math.Min(memory.AvailablePhysical, (ulong)long.MaxValue) : 0;
            return Limits(Environment.ProcessorCount, available, hevc);
        }

        public static bool Software(string encoder) => encoder == "libx264" || encoder == "libx265";

        public static string[] Arguments(string encoder, string mode, int workers = 2, int frameThreads = 0)
        {
            switch (encoder)
            {
                case "hevc_nvenc":
                    return new[] { "-c:v", encoder, "-preset", "p4", "-tune", "hq", "-rc", "constqp", "-qp", "28", "-bf", "2", "-spatial-aq", "1", "-rc-lookahead", "16", "-multipass", "disabled", "-profile:v", "main", "-tag:v", "hvc1", "-pix_fmt", "yuv420p" };
                case "h264_nvenc":
                    return new[] { "-c:v", encoder, "-preset", "p4", "-tune", "hq", "-rc", "constqp", "-qp", "28", "-bf", "2", "-spatial-aq", "1", "-rc-lookahead", "16", "-multipass", "disabled", "-profile:v", "high", "-pix_fmt", "yuv420p" };
                case "hevc_amf":
                    return new[] { "-c:v", encoder, "-usage", "transcoding", "-quality", "quality", "-rc", "cqp", "-qp_i", "28", "-qp_p", "28", "-profile:v", "main", "-tag:v", "hvc1" };
                case "h264_amf":
                    return new[] { "-c:v", encoder, "-usage", "transcoding", "-quality", "quality", "-rc", "cqp", "-qp_i", "28", "-qp_p", "28", "-qp_b", "28", "-profile:v", "high" };
                case "hevc_qsv":
                    return new[] { "-c:v", encoder, "-preset", "medium", "-global_quality", "26", "-profile:v", "main", "-tag:v", "hvc1" };
                case "h264_qsv":
                    return new[] { "-c:v", encoder, "-preset", "medium", "-global_quality", "25", "-look_ahead", "0", "-profile:v", "high" };
                case "libx265":
                    return new[] { "-c:v", encoder, "-preset", "medium", "-crf", "25", "-tag:v", "hvc1", "-x265-params", "pools=" + Math.Max(1, workers) + ":frame-threads=" + Math.Max(0, frameThreads) + ":info=0:log-level=error" };
                case "libx264":
                    return new[] { "-c:v", encoder, "-preset", mode == "fast" ? "veryfast" : "medium", "-crf", mode == "fast" ? "23" : "21", "-threads", Math.Max(1, workers).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                default:
                    throw new InvalidOperationException("No compatible compression encoder is available.");
            }
        }
    }
}
