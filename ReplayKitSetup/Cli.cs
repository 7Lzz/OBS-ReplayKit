using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayKitSetup
{
    // shared shape for a running install/cleanup progress screen -- Cli.InstallProgress (rich ANSI menu) and Update.CleanupProgress (headless --cleanup mode) both implement this so Cleanup.RunCleanup works from either caller.
    public interface IInstallProgress
    {
        int TotalSteps { get; set; }
        List<string> Issues { get; }
        void Render(int completed, string title, string detail, string state);
        void LogLine(string message);
        void AddIssue(string message);
    }

    // interactive terminal setup menu for OBS ReplayKit. ported from obs_replaykit/cli.py.
    public static class Cli
    {
        private static bool _colorEnabled = false;

        private static class C
        {
            public const string Reset = "\u001b[0m";
            public const string Title = "\u001b[1;97m";
            public const string Heading = "\u001b[1;36m";
            public const string Label = "\u001b[90m";
            public const string Value = "\u001b[97m";
            public const string Dim = "\u001b[90m";
            public const string Choice = "\u001b[96m";
            public const string Good = "\u001b[92m";
            public const string Warn = "\u001b[93m";
            public const string Bad = "\u001b[91m";
            public const string Accent = "\u001b[94m";
        }

        private static string Color(string text, string code) => _colorEnabled ? code + text + C.Reset : text;

        private static int ContentWidth()
        {
            int columns;
            try { columns = Console.WindowWidth; }
            catch (IOException) { columns = 124; }
            if (columns <= 0) columns = 124;
            return Math.Min(Math.Max(columns - 2, 96), 124);
        }

        private static readonly Dictionary<string, (string Title, string Summary, string Detail)> StorageInfoMap = new Dictionary<string, (string, string, string)>
        {
            ["lower_gpu"] = ("Lowest GPU use / larger clips", "Lowest recording impact. Files are larger.", "Baseline GPU use. Best if a game is sensitive to recording overhead."),
            ["balanced"] = ("Balanced GPU and clip size", "Recommended. Smaller clips with only a small encoder cost.", "Usually about +2-5% GPU over Lowest GPU use, with noticeably smaller files."),
            ["smaller_files"] = ("Smallest clips / more GPU use", "More encoder work to shrink clips for storage and uploads.", "Usually about +5-12% GPU over Lowest GPU use. Use this when disk size matters most."),
        };

        private static (string Title, string Summary, string Detail) StorageInfo(string mode) =>
            StorageInfoMap.TryGetValue(mode, out var info) ? info : StorageInfoMap["balanced"];

        // windows-only console setup. drops pythons non-windows branch as dead code for this target, matching Obs.cs.
        public static void ConfigureConsole()
        {
            try { Console.Title = "OBS ReplayKit Setup"; } catch (Exception) { }
            try
            {
                // mirrors "mode con: cols=124 lines=54" -- buffer must stay >= window size at every step or SetWindowSize/SetBufferSize throws.
                int safeWidth = Math.Max(124, Console.BufferWidth);
                int safeHeight = Math.Max(54, Console.BufferHeight);
                Console.SetBufferSize(safeWidth, safeHeight);
                Console.SetWindowSize(124, 54);
                Console.SetBufferSize(124, 54);
            }
            catch (Exception)
            {
                // best-effort terminal geometry cosmetics -- never fatal to setup, matching the blanket guard in the python original.
            }

            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                if (GetConsoleMode(handle, out uint mode))
                {
                    SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
                    _colorEnabled = true;
                    TrySetConsoleFont(handle);
                }
            }
            catch (Exception)
            {
                _colorEnabled = false;
            }
        }

        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ConsoleFontInfoEx
        {
            public uint cbSize;
            public uint nFont;
            public Coord dwFontSize;
            public uint FontFamily;
            public uint FontWeight;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FaceName;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref ConsoleFontInfoEx lpConsoleCurrentFontEx);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref ConsoleFontInfoEx lpConsoleCurrentFontEx);

        // use a modern readable console font when classic conhost allows it.
        private static void TrySetConsoleFont(IntPtr handle)
        {
            try
            {
                var info = new ConsoleFontInfoEx { cbSize = (uint)Marshal.SizeOf<ConsoleFontInfoEx>() };
                if (!GetCurrentConsoleFontEx(handle, false, ref info)) return;
                info.dwFontSize.Y = (short)Math.Max(info.dwFontSize.Y + 2, 18);
                info.FontWeight = 400;
                info.FaceName = "Cascadia Mono";
                SetCurrentConsoleFontEx(handle, false, ref info);
            }
            catch (Exception)
            {
            }
        }

        private static void Clear()
        {
            try { Console.Clear(); } catch (IOException) { }
        }

        private static void Pause(string message = "Press Enter to continue...")
        {
            Console.Write("\n" + message);
            Console.ReadLine();
        }

        private static string Prompt(string label = "Choose")
        {
            Console.Write("\n" + Color(label, C.Heading) + ": ");
            return (Console.ReadLine() ?? "").Trim();
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine(Color(title, C.Heading));
            Console.WriteLine(Color(new string('-', ContentWidth()), C.Dim));
        }

        private static void Row(string name, string value, string note = "")
        {
            int width = ContentWidth();
            string prefix = "  " + name.PadRight(26) + " ";
            string line = Color(prefix, C.Label) + Color(value, C.Value);
            if (string.IsNullOrEmpty(note))
            {
                Console.WriteLine(line);
                return;
            }
            // most notes fit right after the value on the same line; only wraps below for the few too long to fit.
            if (prefix.Length + value.Length + 2 + note.Length <= width)
            {
                Console.WriteLine(line + "  " + Color(note, C.Dim));
                return;
            }
            Console.WriteLine(line);
            string indent = new string(' ', prefix.Length);
            foreach (var wrapped in WrapText(note, Math.Max(40, width - prefix.Length)))
            {
                Console.WriteLine(Color(indent + wrapped, C.Dim));
            }
        }

        private static void MenuLine(string key, string name, string note, string keyColor = C.Choice)
        {
            Console.WriteLine($"  {Color("[" + key + "]", keyColor)} {Color(name.PadRight(25), C.Value)} {Color(note, C.Dim)}");
        }

        private static void OptionLine(string key, string title, string note = "", bool selected = false)
        {
            string marker = selected ? Color("*", C.Good) : " ";
            Console.WriteLine($"  {Color("[" + key + "]", C.Choice)} {marker} {Color(title, C.Value)}");
            if (!string.IsNullOrEmpty(note))
            {
                int width = ContentWidth();
                string indent = new string(' ', 8);
                foreach (var wrapped in WrapText(note, Math.Max(40, width - indent.Length)))
                {
                    Console.WriteLine(Color(indent + wrapped, C.Dim));
                }
            }
        }

        private static void CancelLine()
        {
            Console.WriteLine();
            Console.WriteLine($"  {Color("[Q]", C.Choice)} {Color("Cancel", C.Dim)}");
        }

        // plain greedy word-wrap -- note strings here are natural sentences with no single word wider than the wrap width, so textwrap.wraps long-word-breaking never triggers in practice and isnt needed.
        private static List<string> WrapText(string text, int width)
        {
            var result = new List<string>();
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var line = new StringBuilder();
            foreach (var word in words)
            {
                if (line.Length == 0) line.Append(word);
                else if (line.Length + 1 + word.Length <= width) line.Append(' ').Append(word);
                else
                {
                    result.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
            }
            if (line.Length > 0) result.Add(line.ToString());
            return result;
        }

        // mirrors pythons str.center(width, fillchar).
        private static string CenterPad(string s, int width, char fill)
        {
            int totalPad = width - s.Length;
            if (totalPad <= 0) return s;
            int left = totalPad / 2;
            int right = totalPad - left;
            return new string(fill, left) + s + new string(fill, right);
        }

        private static EncoderChoice CurrentEncoder(Preferences prefs)
        {
            var preset = Recording.GetPreset(prefs.RecordingPreset);
            return Encoder.PickEncoder(Gpu.PrimaryGpu(), prefs.CodecPreference, preset.CqpTarget, prefs.CompressionMode);
        }

        private static string DisplayText()
        {
            var display = Display.PrimaryDisplay();
            if (display == null) return "No display detected";
            return $"{display.Adapter}  {display.Width}x{display.Height}  ({display.EdidModel})";
        }

        private static string GpuText()
        {
            var gpu = Gpu.PrimaryGpu();
            if (gpu == null) return "No GPU detected - software encoder will be used";
            return $"{gpu.Label} ({gpu.GenLabel})";
        }

        private static string OverlayLabel(Preferences prefs)
        {
            if (prefs.OverlayStyle == "input_overlay") return "WASD/mouse";
            if (prefs.OverlayStyle == "bongo_cat") return "Bongo Cat";
            return "Off";
        }

        private static string StartupLabel(Preferences prefs) => prefs.ObsStartupEnabled ? "On" : "Off";
        private static string ClipNotificationLabel(Preferences prefs) => prefs.ClipNotificationEnabled ? "On" : "Off";
        private static string RecordingNotificationLabel(Preferences prefs) => prefs.RecordingNotificationEnabled ? "On" : "Off";
        private static string TrimPreciseLabel(Preferences prefs) => prefs.TrimPreciseDefault ? "Precise" : "Fast";
        private static string DiscordScreenshareLabel(Preferences prefs) => prefs.DiscordScreenshareEnabled ? "On" : "Off";
        private static string SleepOverrideLabel(Preferences prefs) => prefs.AllowSleepWhileActive ? "Allowed" : "Blocked by OBS";

        private static void RenderMenu(Preferences prefs)
        {
            Clear();
            var preset = Recording.GetPreset(prefs.RecordingPreset);
            var storage = StorageInfo(prefs.CompressionMode);
            var encoder = CurrentEncoder(prefs);
            string obsExe = Obs.FindObsExe();

            int width = ContentWidth();
            Console.WriteLine();
            Console.WriteLine(Color(CenterPad(" OBS REPLAYKIT SETUP ", width, '='), C.Title));
            Console.WriteLine(Color(CenterPad($" version {VersionInfo.Version} ", width, ' '), C.Dim));
            Console.WriteLine(Color(new string('=', width), C.Dim));
            Row("Windows user", Config.USERNAME);
            Row("OBS config", Config.OBS_CONFIG);
            Row("OBS install", !string.IsNullOrEmpty(obsExe) ? obsExe : "OBS was not found");
            Row("Clip keybind", Keybind.ComboToLabel(prefs.ClipKeybind));
            Row("Recording keybind", Keybind.ComboToLabel(prefs.RecordingKeybind));

            Section("Setup overview");
            Row("Recording quality", preset.Label, preset.Description);
            Row("GPU load vs file size", storage.Title, storage.Summary);
            Row("GPU detected", GpuText());
            Row("Recording encoder", encoder.Label, encoder.Description);
            Row("Display capture", DisplayText());
            Row("Clip length", $"{prefs.ReplayBufferSeconds} seconds");
            Row("Save folder", prefs.RecordingPath);
            Row("Microphone", prefs.MicrophoneName);
            Row("Overlay", OverlayLabel(prefs));
            Row("Windows startup", StartupLabel(prefs), "Start OBS automatically when you sign in.");
            Row("Clip saved popup", ClipNotificationLabel(prefs), "Show a small desktop popup after saving a clip.");
            Row("Recording popups", RecordingNotificationLabel(prefs), "Show a small desktop popup when recording starts or stops.");
            Row("Trim default", TrimPreciseLabel(prefs), "Default clip trimmer mode.");
            Row("Discord screenshare", DiscordScreenshareLabel(prefs), "Installs OBS Stream Audio for Discord Share Preview.");
            Row("Windows sleep", SleepOverrideLabel(prefs), "Let Windows sleep timers run while OBS or Share Preview is active.");

            Section("Change settings");
            MenuLine("1", "Recording quality", "resolution, FPS, and visual quality");
            MenuLine("2", "GPU load vs file size", "choose lower GPU use or smaller clips");
            MenuLine("3", "Recording codec", "auto, H.264, or HEVC");
            MenuLine("4", "Clip length", "how many seconds the hotkey saves");
            MenuLine("5", "Save folder", "where recordings and clips go");
            MenuLine("6", "Microphone", "system default or a specific device");
            MenuLine("7", "Clip keybind", "hotkey that saves a clip");
            MenuLine("8", "Recording keybind", "hotkey that starts and stops recording");
            MenuLine("9", "Overlay", "off, WASD/mouse, or Bongo Cat");
            MenuLine("10", "Windows startup", "turn automatic OBS launch on or off");
            MenuLine("11", "Clip saved popup", "show or hide the clip saved popup");
            MenuLine("12", "Recording popups", "show or hide recording started/stopped popups");
            MenuLine("13", "Trim default", "fast keyframe trim or precise re-encode trim");
            MenuLine("14", "Discord screenshare", "turn Share Preview audio support on or off");
            Console.WriteLine();
            MenuLine("A", "Apply and launch OBS", "write settings, install selected tools, start OBS", C.Good);
            MenuLine("R", "Clean reset", "remove ReplayKit OBS config and audio device changes", C.Bad);
            MenuLine("Q", "Quit", "close setup");
        }

        private static void ChooseRecordingQuality(Preferences prefs)
        {
            Clear();
            Section("Recording quality");
            for (int idx = 0; idx < Recording.PRESETS.Count; idx++)
            {
                var preset = Recording.PRESETS[idx];
                OptionLine((idx + 1).ToString(), preset.Label, preset.Description, preset.Name == prefs.RecordingPreset);
            }
            CancelLine();
            string choice = Prompt();
            if (choice.ToLowerInvariant() == "q") return;
            if (!int.TryParse(choice, out int n) || n < 1 || n > Recording.PRESETS.Count)
            {
                Pause("Invalid choice. Press Enter...");
                return;
            }
            var selected = Recording.PRESETS[n - 1];
            prefs.RecordingPreset = selected.Name;
            prefs.CompressionMode = Prefs.DefaultCompressionForPreset(selected.Name);
            prefs.Save();
        }

        private static void ChooseStorageMode(Preferences prefs)
        {
            Clear();
            Section("GPU load vs file size");
            Console.WriteLine(Color("This keeps visual quality the same. Pick lower GPU use or smaller clip files.", C.Dim));
            Console.WriteLine();
            var modes = Prefs.ALLOWED_COMPRESSION_MODES;
            for (int idx = 0; idx < modes.Length; idx++)
            {
                string mode = modes[idx];
                var info = StorageInfo(mode);
                OptionLine((idx + 1).ToString(), info.Title, $"{info.Summary} {info.Detail}", mode == prefs.CompressionMode);
            }
            CancelLine();
            string choice = Prompt();
            if (choice.ToLowerInvariant() == "q") return;
            if (!int.TryParse(choice, out int n) || n < 1 || n > modes.Length)
            {
                Pause("Invalid choice. Press Enter...");
                return;
            }
            prefs.CompressionMode = modes[n - 1];
            prefs.Save();
        }

        private static void ChooseCodec(Preferences prefs)
        {
            Clear();
            Section("Recording codec");
            var gpu = Gpu.PrimaryGpu();
            var codecs = Encoder.AvailableCodecs(gpu);
            var keys = Prefs.ALLOWED_CODEC_PREFERENCES.Where(k => codecs.ContainsKey(k)).ToList();
            for (int idx = 0; idx < keys.Count; idx++)
            {
                string key = keys[idx];
                OptionLine((idx + 1).ToString(), key.ToUpperInvariant(), codecs[key], key == prefs.CodecPreference);
            }
            CancelLine();
            string choice = Prompt();
            if (choice.ToLowerInvariant() == "q") return;
            if (!int.TryParse(choice, out int n) || n < 1 || n > keys.Count)
            {
                Pause("Invalid choice. Press Enter...");
                return;
            }
            prefs.CodecPreference = keys[n - 1];
            prefs.Save();
        }

        private static void ChooseReplayLength(Preferences prefs)
        {
            Clear();
            Section("Clip length");
            Row("Current", $"{prefs.ReplayBufferSeconds} seconds");
            Row("Allowed", $"{Prefs.REPLAY_BUFFER_MIN}-{Prefs.REPLAY_BUFFER_MAX} seconds");
            string value = Prompt("Seconds, or Q to cancel");
            if (value.ToLowerInvariant() == "q") return;
            if (!int.TryParse(value, out int seconds))
            {
                Pause("Clip length must be a number. Press Enter...");
                return;
            }
            if (seconds < Prefs.REPLAY_BUFFER_MIN || seconds > Prefs.REPLAY_BUFFER_MAX)
            {
                Pause($"Clip length must be between {Prefs.REPLAY_BUFFER_MIN} and {Prefs.REPLAY_BUFFER_MAX}. Press Enter...");
                return;
            }
            prefs.ReplayBufferSeconds = seconds;
            prefs.Save();
        }

        // mirrors pythons Path(path).expanduser().as_posix() -- expand a leading ~ then normalize to forward slashes without resolving . or ..
        private static string ExpandUserPosix(string path)
        {
            if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            {
                path = Config.USERPROFILE + path.Substring(1);
            }
            return path.Replace('\\', '/');
        }

        private static void ChooseSaveFolder(Preferences prefs)
        {
            Clear();
            Section("Save folder");
            Row("Current", prefs.RecordingPath);
            Console.WriteLine(Color("Paste a folder path. It will be created during Apply if it does not exist.", C.Dim));
            string value = Prompt("Folder path, or Q to cancel");
            if (value.ToLowerInvariant() == "q") return;
            string path = value.Trim().Trim('"');
            if (path.Length == 0 || path.Contains('\0'))
            {
                Pause("Invalid folder path. Press Enter...");
                return;
            }
            prefs.RecordingPath = ExpandUserPosix(path);
            prefs.Save();
        }

        private static void ChooseMicrophone(Preferences prefs)
        {
            Clear();
            Section("Microphone");
            var devices = new List<(string Name, string DeviceId)> { (Audio.DEFAULT_DEVICE_NAME, Audio.DEFAULT_DEVICE_ID) };
            devices.AddRange(Audio.ListMicrophones().Select(dev => (dev.Name, dev.DeviceId)));
            for (int idx = 0; idx < devices.Count; idx++)
            {
                OptionLine((idx + 1).ToString(), devices[idx].Name, "", devices[idx].DeviceId == prefs.MicrophoneDeviceId);
            }
            CancelLine();
            string choice = Prompt();
            if (choice.ToLowerInvariant() == "q") return;
            if (!int.TryParse(choice, out int n) || n < 1 || n > devices.Count)
            {
                Pause("Invalid choice. Press Enter...");
                return;
            }
            prefs.MicrophoneName = devices[n - 1].Name;
            prefs.MicrophoneDeviceId = devices[n - 1].DeviceId;
            prefs.Save();
        }

        private static void ChooseKeybind(Preferences prefs)
        {
            Clear();
            Section("Clip keybind");
            Row("Current", Keybind.ComboToLabel(prefs.ClipKeybind));
            Console.WriteLine(Color(@"Examples: shift+\, ctrl+f10, alt+s, f9", C.Dim));
            string value = Prompt("New keybind, or Q to cancel");
            if (value.ToLowerInvariant() == "q") return;
            var combo = Keybind.ParseCombo(value);
            if (combo == null)
            {
                Pause("Invalid keybind. Press Enter...");
                return;
            }
            prefs.ClipKeybind = combo;
            prefs.Save();
        }

        private static void ChooseRecordingKeybind(Preferences prefs)
        {
            Clear();
            Section("Recording keybind");
            Row("Current", Keybind.ComboToLabel(prefs.RecordingKeybind));
            Console.WriteLine(Color("This same key starts and stops OBS recording.", C.Dim));
            Console.WriteLine(Color("Examples: ctrl+f9, alt+r, f10. Type CLEAR to disable it.", C.Dim));
            string value = Prompt("New keybind, CLEAR, or Q to cancel");
            string low = value.ToLowerInvariant();
            if (low == "q") return;
            if (low == "clear")
            {
                prefs.RecordingKeybind = new Dictionary<string, object>();
                prefs.Save();
                return;
            }
            var combo = Keybind.ParseCombo(value);
            if (combo == null)
            {
                Pause("Invalid keybind. Press Enter...");
                return;
            }
            prefs.RecordingKeybind = combo;
            prefs.Save();
        }

        private static void InputOverlayMenu(Preferences prefs)
        {
            Clear();
            Section("Overlay");
            Console.WriteLine(Color("Choose the overlay ReplayKit installs and shows in OBS.", C.Dim));
            Console.WriteLine();
            Row("Current", OverlayLabel(prefs));
            Console.WriteLine();
            OptionLine("1", "WASD/mouse input overlay", "Shows keyboard and mouse presses.", prefs.OverlayStyle == "input_overlay");
            OptionLine("2", "Bongo Cat", "Animated keyboard/mouse overlay.", prefs.OverlayStyle == "bongo_cat");
            OptionLine("3", "Off", "No input overlay in the OBS scene.", prefs.OverlayStyle == "off");
            CancelLine();
            string choice = Prompt();
            if (choice == "1")
            {
                prefs.InputOverlayEnabled = true;
                prefs.OverlayStyle = "input_overlay";
                prefs.Save();
            }
            else if (choice == "2")
            {
                prefs.InputOverlayEnabled = true;
                prefs.OverlayStyle = "bongo_cat";
                prefs.Save();
            }
            else if (choice == "3")
            {
                prefs.InputOverlayEnabled = false;
                prefs.OverlayStyle = "off";
                prefs.Save();
            }
            else if (choice.ToLowerInvariant() == "q")
            {
                return;
            }
            else
            {
                Pause("Invalid choice. Press Enter...");
            }
        }

        private static void ChooseWindowsStartup(Preferences prefs)
        {
            Clear();
            Section("Windows startup");
            Console.WriteLine(Color("Choose whether Windows starts OBS automatically when you sign in.", C.Dim));
            Console.WriteLine();
            OptionLine("1", "On", "OBS opens on Windows sign-in. This is the default.", prefs.ObsStartupEnabled);
            OptionLine("2", "Off", "OBS only opens when you launch it yourself.", !prefs.ObsStartupEnabled);
            CancelLine();
            string choice = Prompt();
            if (choice == "1") { prefs.ObsStartupEnabled = true; prefs.Save(); }
            else if (choice == "2") { prefs.ObsStartupEnabled = false; prefs.Save(); }
            else if (choice.ToLowerInvariant() == "q") return;
            else Pause("Invalid choice. Press Enter...");
        }

        private static void ChooseClipNotification(Preferences prefs)
        {
            Clear();
            Section("Clip saved popup");
            Console.WriteLine(Color("Choose whether ReplayKit shows a small desktop popup after OBS saves a clip.", C.Dim));
            Console.WriteLine();
            OptionLine("1", "On", "Show a ReplayKit popup like: Saved the last 20s.", prefs.ClipNotificationEnabled);
            OptionLine("2", "Off", "Only use the save sound and OBS status.", !prefs.ClipNotificationEnabled);
            CancelLine();
            string choice = Prompt();
            if (choice == "1") { prefs.ClipNotificationEnabled = true; prefs.Save(); }
            else if (choice == "2") { prefs.ClipNotificationEnabled = false; prefs.Save(); }
            else if (choice.ToLowerInvariant() == "q") return;
            else Pause("Invalid choice. Press Enter...");
        }

        private static void ChooseRecordingNotification(Preferences prefs)
        {
            Clear();
            Section("Recording popups");
            Console.WriteLine(Color("Choose whether ReplayKit shows a small desktop popup when OBS recording starts or stops.", C.Dim));
            Console.WriteLine();
            OptionLine("1", "On", "Show Recording started and Recording stopped popups.", prefs.RecordingNotificationEnabled);
            OptionLine("2", "Off", "Only use OBS status for recording state.", !prefs.RecordingNotificationEnabled);
            CancelLine();
            string choice = Prompt();
            if (choice == "1") { prefs.RecordingNotificationEnabled = true; prefs.Save(); }
            else if (choice == "2") { prefs.RecordingNotificationEnabled = false; prefs.Save(); }
            else if (choice.ToLowerInvariant() == "q") return;
            else Pause("Invalid choice. Press Enter...");
        }

        private static void ChooseTrimDefault(Preferences prefs)
        {
            Clear();
            Section("Trim default");
            Console.WriteLine(Color("Choose the default mode for clip trimming.", C.Dim));
            Console.WriteLine();
            OptionLine("1", "Fast", "Trim at nearby keyframes without re-encoding.", !prefs.TrimPreciseDefault);
            OptionLine("2", "Precise", "Re-encode around the trim points for exact timing.", prefs.TrimPreciseDefault);
            CancelLine();
            string choice = Prompt();
            if (choice == "1") { prefs.TrimPreciseDefault = false; prefs.Save(); }
            else if (choice == "2") { prefs.TrimPreciseDefault = true; prefs.Save(); }
            else if (choice.ToLowerInvariant() == "q") return;
            else Pause("Invalid choice. Press Enter...");
        }

        private static void ChooseDiscordScreenshare(Preferences prefs)
        {
            Clear();
            Section("Discord screenshare");
            Console.WriteLine(Color("Choose whether ReplayKit installs and uses OBS Stream Audio for Discord Share Preview.", C.Dim));
            Console.WriteLine();
            OptionLine("1", "On", "Install the virtual audio cable and enable the Discord Share Preview path.", prefs.DiscordScreenshareEnabled);
            OptionLine("2", "Off", "Skip the virtual audio cable during setup and keep Share Preview disabled.", !prefs.DiscordScreenshareEnabled);
            CancelLine();
            string choice = Prompt();
            if (choice == "1") { prefs.DiscordScreenshareEnabled = true; prefs.Save(); }
            else if (choice == "2") { prefs.DiscordScreenshareEnabled = false; prefs.DiscordProjectorEnabled = false; prefs.Save(); }
            else if (choice.ToLowerInvariant() == "q") return;
            else Pause("Invalid choice. Press Enter...");
        }

        private static void CleanReset()
        {
            Clear();
            Section("Clean reset");
            Console.WriteLine(Color("This closes OBS, wipes OBS user config, removes ReplayKit OBS plugins,", C.Dim));
            Console.WriteLine(Color("and removes ReplayKit audio device changes.", C.Dim));
            Console.WriteLine();
            string confirm = Prompt("Press R again to clean reset, or Q to cancel");
            string key = confirm.ToLowerInvariant();
            if (key == "q") return;
            if (key != "r")
            {
                Pause("Reset cancelled. Press Enter...");
                return;
            }
            var progress = new InstallProgress(0, " OBS REPLAYKIT SETUP - CLEANING ");
            List<string> issues;
            try
            {
                issues = Cleanup.RunCleanup(progress);
            }
            catch (Exception)
            {
                Pause("Cleanup stopped. Press Enter to return to setup...");
                return;
            }
            Console.WriteLine();
            Console.WriteLine(Color("Cleanup complete.", C.Good));
            Console.WriteLine(Color("Your ReplayKit preferences were kept.", C.Dim));
            if (issues.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(Color("Some cleanup steps reported warnings. Re-run Clean reset if needed.", C.Warn));
            }
            Pause();
        }

        // rich ANSI progress screen for the interactive Apply/Clean reset flows. Update.CleanupProgress is the headless equivalent for --cleanup mode.
        public sealed class InstallProgress : IInstallProgress
        {
            private readonly string _title;
            private int _currentStep;
            private string _currentTitle = "";
            private string _currentDetail = "";

            public int TotalSteps { get; set; }
            public List<string> Issues { get; } = new List<string>();

            public InstallProgress(int totalSteps, string title = " OBS REPLAYKIT SETUP - APPLYING ")
            {
                TotalSteps = totalSteps;
                _title = title;
            }

            public void Render(int completed, string title, string detail, string state = "working")
            {
                _currentStep = completed;
                _currentTitle = title;
                _currentDetail = detail;
                Clear();
                int width = ContentWidth();
                Console.WriteLine();
                Console.WriteLine(Color(CenterPad(_title, width, '='), C.Title));
                Console.WriteLine(Color(new string('=', width), C.Dim));
                Console.WriteLine();

                int barWidth = Math.Min(58, Math.Max(32, width - 32));
                int filled = (int)(barWidth * completed / (double)Math.Max(1, TotalSteps));
                int empty = barWidth - filled;
                int percent = (int)(100 * completed / (double)Math.Max(1, TotalSteps));
                string bar = Color(new string('#', filled), C.Good) + Color(new string('-', empty), C.Dim);
                Console.WriteLine($"  {bar} {Color($"{percent,3}%", C.Value)}");
                Console.WriteLine();

                int stepNumber = state == "done" ? completed : completed + 1;
                string stepText = $"Step {Math.Min(Math.Max(stepNumber, 1), TotalSteps)} of {TotalSteps}";
                Console.WriteLine(Color("  " + stepText, C.Heading));
                string status = state == "failed" ? "Failed" : (state == "done" ? "Done" : "Working");
                string statusColor = state == "failed" ? C.Bad : (state == "done" ? C.Good : C.Warn);
                Console.WriteLine($"  {Color(status + ":", statusColor)} {Color(title, C.Value)}");
                if (!string.IsNullOrEmpty(detail))
                {
                    foreach (var wrapped in WrapText(detail, Math.Max(40, width - 4)))
                    {
                        Console.WriteLine(Color("  " + wrapped, C.Dim));
                    }
                }

                if (Issues.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine(Color("  Needs attention", C.Warn));
                    foreach (var issue in Issues.Skip(Math.Max(0, Issues.Count - 3)))
                    {
                        Console.WriteLine(Color("  - " + issue, C.Dim));
                    }
                }
            }

            private static readonly string[] IssueWords = { "warn", "failed", "missing", "not found", "skipped", "timed out", "permission denied" };

            public void LogLine(string message)
            {
                string text = (message ?? "").Trim();
                if (text.Length == 0) return;
                string lowered = text.ToLowerInvariant();
                if (lowered.StartsWith("downloading ") || lowered.StartsWith("downloaded "))
                {
                    _currentDetail = text;
                    Render(Math.Max(0, _currentStep), _currentTitle, _currentDetail, "working");
                }
                if (IssueWords.Any(word => lowered.Contains(word))) AddIssue(text);
            }

            public void AddIssue(string message)
            {
                string cleaned = string.Join(" ", (message ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
                if (cleaned.Length > 118) cleaned = cleaned.Substring(0, 115) + "...";
                if (cleaned.Length > 0 && !Issues.Contains(cleaned)) Issues.Add(cleaned);
            }
        }

        // shared step-runner for both the interactive Apply flow and Cleanup.RunCleanup (avoids duplicating the render/issue-tracking logic in two places -- c# has no import-cycle restriction stopping cleanup.cs from calling back into here, unlike the python original).
        public static void RunApplyStep(IInstallProgress progress, int index, string title, string detail, Func<object> action)
        {
            progress.Render(index - 1, title, detail, "working");
            object result;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                progress.AddIssue($"{title} failed: {ex.Message}");
                progress.Render(index - 1, title, detail, "failed");
                throw;
            }
            if (result is bool b && !b)
            {
                progress.AddIssue($"{title} did not complete. Re-run Apply after fixing the issue.");
            }
            progress.Render(index, title, detail, "done");
            System.Threading.Thread.Sleep(120);
        }

        private static List<string> RunApplyFlow(Preferences prefs)
        {
            var steps = new List<(string Title, string Detail, Func<object> Action)>();
            void Add(string title, string detail, Func<object> action) => steps.Add((title, detail, action));

            var progress = new InstallProgress(0);

            Add("Close OBS", "Stops OBS so settings can be written cleanly.", () => (object)Obs.CloseObs(progress.LogLine));
            Add("Back up current OBS settings", "Keeps a restore copy before ReplayKit writes the new setup.", () => { Installer.BackupExistingConfig(progress.LogLine); return (object)true; });
            Add("Prepare OBS settings folder", "Creates the OBS config folder and clears crash-recovery prompts.", () => { Directory.CreateDirectory(Config.OBS_CONFIG); Obs.CleanupCrashFlags(progress.LogLine); return (object)true; });
            Add("Build ReplayKit helper", "Makes sure the local Clips and controls helper is ready.", () => (object)Installer.EnsureLauncherBuilt(progress.LogLine));
            if (prefs.DiscordScreenshareEnabled)
                Add("Install OBS Stream Audio", "Installs and renames VB-Audio Cable for Discord share audio.", () => (object)VbCable.EnsureVbcable(progress.LogLine));
            else
                Add("Skip OBS Stream Audio", "Discord screenshare support is off, so no virtual audio cable driver is installed.", () => (object)true);
            Add("Write ReplayKit OBS profile", "Applies your quality, clip, encoder, microphone, and overlay choices.", () => (object)(Installer.InstallObsConfig(prefs, progress.LogLine) > 0));
            Add("Enable OBS WebSocket", "Lets the Clips and controls windows talk to OBS locally.", () => (object)Installer.ConfigureObsWebsocket(progress.LogLine));
            Add("Install Custom Controls and Clips", "Adds the ReplayKit dock and Clips browser files.", () => (object)(Installer.InstallObsCustomDock(progress.LogLine) > 0));
            Add("Install tray plugin", "Adds View Clips, Share Preview, and Restart OBS to the system tray menu.", () => (object)TrayPlugin.InstallReplaykitTrayPlugin(progress.LogLine));
            Add("Register launcher permission", "Avoids a UAC prompt every time OBS starts ReplayKit.", () => (object)Installer.InstallObsElevationTask(progress.LogLine));
            if (prefs.AllowSleepWhileActive)
                Add("Allow monitor and PC sleep", "Lets Windows sleep timers run even if OBS, Replay Buffer, or Share Preview is active.", () => (object)Installer.InstallObsSleepOverride(true, progress.LogLine));
            else
                Add("Restore OBS sleep blocking", "Removes ReplayKit's sleep override so OBS can keep active sessions awake.", () => (object)Installer.InstallObsSleepOverride(false, progress.LogLine));
            if (prefs.PinObsTrayIcon)
                Add("Pin OBS tray icon", "Keeps the OBS icon visible next to the clock instead of hidden behind the overflow arrow.", () => { TrayPin.PinObsTrayIcon(progress.LogLine); return (object)true; });
            else
                Add("Skip tray icon pinning", "Leaves the OBS tray icon at Windows' default overflow behavior.", () => { TrayPin.UnpinObsTrayIcon(progress.LogLine); return (object)true; });
            Add("Install video tools", "Downloads or verifies the trim/compress tools used by Clips.", () => (object)Installer.InstallObsFfmpeg(progress.LogLine));

            Add("Install WASD/mouse overlay", "Adds the keyboard and mouse overlay plugin and presets so Settings can switch to it later.",
                () => (object)(InputOverlay.InstallInputOverlayPlugin(progress.LogLine) && InputOverlay.InstallInputOverlayPresets(progress.LogLine)));
            Add("Install Bongo Cat overlay", "Adds the Bongo Cat keyboard and mouse overlay plugin so Settings can switch to it later.",
                () => (object)BongoCat.InstallBongoCatPlugin(progress.LogLine));
            Add("Install motion blur filter", "Adds the bundled OBS Shaderfilter plugin and removes the retired Composite Blur plugin.",
                () => (object)ShaderFilter.InstallReplaykitMotionBlurPlugin(progress.LogLine));

            Add("Install desktop audio capture", "Adds clean desktop/game audio capture for OBS.", () => (object)WinCapture.InstallWinCaptureAudio(progress.LogLine));
            Add("Prepare clip folders", "Creates the recording and clip folders if needed.", () => { Installer.EnsureRecordingDirs(prefs, progress.LogLine); return (object)true; });
            Add("Set Windows startup", "Adds or removes OBS from Windows startup based on your setup choice.", () => (object)Startup.ConfigureObsStartup(prefs.ObsStartupEnabled, progress.LogLine));
            Add("Launch OBS", "Starts OBS with the updated ReplayKit setup.", () => (object)Obs.LaunchObs(progress.LogLine));

            progress.TotalSteps = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                RunApplyStep(progress, i + 1, steps[i].Title, steps[i].Detail, steps[i].Action);
            }

            progress.Render(progress.TotalSteps, "Setup complete", "OBS ReplayKit is ready.", "done");
            return progress.Issues;
        }

        private static void ApplySettings(Preferences prefs)
        {
            Clear();
            prefs.Save();
            List<string> issues;
            try
            {
                issues = RunApplyFlow(prefs);
            }
            catch (Exception)
            {
                Pause("Apply stopped. Press Enter to return to setup...");
                return;
            }
            Console.WriteLine();
            Console.WriteLine(Color("Setup complete.", C.Good));
            if (prefs.DiscordScreenshareEnabled)
            {
                Console.WriteLine(Color("Discord share window: OBS ReplayKit Discord Share", C.Value));
                Console.WriteLine(Color("Select that Windowed Projector in Discord Go Live. ReplayKit keeps it parked automatically.", C.Dim));
            }
            else
            {
                Console.WriteLine(Color("Discord Share Preview is disabled. OBS Stream Audio was not installed.", C.Dim));
            }
            if (issues.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(Color("Some steps reported warnings. Re-run Apply if OBS does not behave as expected.", C.Warn));
            }
            Pause();
        }

        public static int RunCli()
        {
            ConfigureConsole();
            var prefs = Prefs.LoadPrefs();
            while (true)
            {
                RenderMenu(prefs);
                string choice = Prompt().ToLowerInvariant();
                switch (choice)
                {
                    case "1": ChooseRecordingQuality(prefs); break;
                    case "2": ChooseStorageMode(prefs); break;
                    case "3": ChooseCodec(prefs); break;
                    case "4": ChooseReplayLength(prefs); break;
                    case "5": ChooseSaveFolder(prefs); break;
                    case "6": ChooseMicrophone(prefs); break;
                    case "7": ChooseKeybind(prefs); break;
                    case "8": ChooseRecordingKeybind(prefs); break;
                    case "9": InputOverlayMenu(prefs); break;
                    case "10": ChooseWindowsStartup(prefs); break;
                    case "11": ChooseClipNotification(prefs); break;
                    case "12": ChooseRecordingNotification(prefs); break;
                    case "13": ChooseTrimDefault(prefs); break;
                    case "14": ChooseDiscordScreenshare(prefs); break;
                    case "a": ApplySettings(prefs); break;
                    case "r": CleanReset(); break;
                    case "q": FastExit.FastExitNow(0); break;
                    default: Pause("Invalid choice. Press Enter..."); break;
                }
            }
        }
    }
}
