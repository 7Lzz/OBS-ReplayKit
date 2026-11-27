using System.IO;
using System.Text;
using ReplayKitShared;

namespace ReplayKitHelper
{
    // reads and writes single keys of the user.ini of obs (%APPDATA%\obs-studio\user.ini) through ObsIni, keeping every other line as obs wrote it
    internal static class ObsUserIni
    {
        public static string Path => System.IO.Path.Combine(Constants.OBS_CONFIG_DIR, "user.ini");

        // the value of section/key, or null when the file, the section or the key is missing
        public static string Read(string section, string key)
        {
            string ini = Path;
            return File.Exists(ini) ? ObsIni.Get(File.ReadAllText(ini), section, key) : null;
        }

        // sets section/key, or removes the key when value is empty; a missing section is added at the end
        public static void Set(string section, string key, string value)
        {
            string ini = Path;
            string text = File.Exists(ini) ? File.ReadAllText(ini) : "";
            string next = string.IsNullOrEmpty(value) ? ObsIni.Remove(text, section, key) : ObsIni.Set(text, section, key, value);
            if (next != text) File.WriteAllText(ini, next, new UTF8Encoding(false));
        }
    }
}
