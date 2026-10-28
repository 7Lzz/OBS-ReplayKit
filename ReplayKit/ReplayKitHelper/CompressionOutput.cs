using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ReplayKitHelper
{
    internal sealed class CompressionOutput
    {
        public string Text;
        public bool Exceeded;
        public bool Truncated;

        public static async Task<CompressionOutput> Read(StreamReader reader, Process process)
        {
            const int retained = 65536;
            const int maximum = 4 * 1024 * 1024;
            var text = new StringBuilder();
            var buffer = new char[4096];
            int total = 0;
            bool exceeded = false;
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (total <= maximum) total += count;
                if (total > maximum && !exceeded)
                {
                    exceeded = true;
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                text.Append(buffer, 0, count);
                if (text.Length > retained) text.Remove(0, text.Length - retained);
            }
            return new CompressionOutput { Text = text.ToString(), Exceeded = exceeded, Truncated = total > retained };
        }
    }
}
