using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ReplayKitSetup
{
    // parse human-typed key combos ("shift+\\", "ctrl+f10") into the qt-flavored json obs persists for hotkeys (OBS_KEY_<x> + shift/control/alt/command booleans). ported from obs_replaykit/keybind.py. the combo shape (lowercase shift/control/alt/command/key fields) is a cross-language contract with the powershell helpers settings json -- keep field names and json field order exactly matched to the python original, not just the parsed meaning.
    public static class Keybind
    {
        // OBS_KEY_ identifiers from libobs/obs-hotkey.h. only the subset a human is likely to type for a clip keybind: letters, digits, function keys, common symbol keys on a us layout, navigation, numpad.
        private static readonly Dictionary<string, string> NamedKeys = new Dictionary<string, string>
        {
            ["\\"] = "OBS_KEY_BACKSLASH",
            ["/"] = "OBS_KEY_SLASH",
            [","] = "OBS_KEY_COMMA",
            ["."] = "OBS_KEY_PERIOD",
            [";"] = "OBS_KEY_SEMICOLON",
            ["'"] = "OBS_KEY_APOSTROPHE",
            ["`"] = "OBS_KEY_QUOTELEFT",
            ["-"] = "OBS_KEY_MINUS",
            ["="] = "OBS_KEY_EQUAL",
            ["["] = "OBS_KEY_BRACKETLEFT",
            ["]"] = "OBS_KEY_BRACKETRIGHT",
            ["space"] = "OBS_KEY_SPACE",
            ["tab"] = "OBS_KEY_TAB",
            ["enter"] = "OBS_KEY_RETURN",
            ["return"] = "OBS_KEY_RETURN",
            ["escape"] = "OBS_KEY_ESCAPE",
            ["esc"] = "OBS_KEY_ESCAPE",
            ["backspace"] = "OBS_KEY_BACKSPACE",
            ["insert"] = "OBS_KEY_INSERT",
            ["delete"] = "OBS_KEY_DELETE",
            ["del"] = "OBS_KEY_DELETE",
            ["home"] = "OBS_KEY_HOME",
            ["end"] = "OBS_KEY_END",
            ["pageup"] = "OBS_KEY_PAGEUP",
            ["pagedown"] = "OBS_KEY_PAGEDOWN",
            ["up"] = "OBS_KEY_UP",
            ["down"] = "OBS_KEY_DOWN",
            ["left"] = "OBS_KEY_LEFT",
            ["right"] = "OBS_KEY_RIGHT",
            ["capslock"] = "OBS_KEY_CAPSLOCK",
            ["printscreen"] = "OBS_KEY_PRINT",
            ["scrolllock"] = "OBS_KEY_SCROLLLOCK",
            ["pause"] = "OBS_KEY_PAUSE",
        };

        // pretty-print labels in the obs hotkey dialog style.
        private static readonly Dictionary<string, string> NamedKeyLabels = new Dictionary<string, string>
        {
            ["OBS_KEY_BACKSLASH"] = "\\",
            ["OBS_KEY_SLASH"] = "/",
            ["OBS_KEY_COMMA"] = ",",
            ["OBS_KEY_PERIOD"] = ".",
            ["OBS_KEY_SEMICOLON"] = ";",
            ["OBS_KEY_APOSTROPHE"] = "'",
            ["OBS_KEY_QUOTELEFT"] = "`",
            ["OBS_KEY_MINUS"] = "-",
            ["OBS_KEY_EQUAL"] = "=",
            ["OBS_KEY_BRACKETLEFT"] = "[",
            ["OBS_KEY_BRACKETRIGHT"] = "]",
            ["OBS_KEY_SPACE"] = "Space",
            ["OBS_KEY_TAB"] = "Tab",
            ["OBS_KEY_RETURN"] = "Enter",
            ["OBS_KEY_ESCAPE"] = "Esc",
            ["OBS_KEY_BACKSPACE"] = "Backspace",
            ["OBS_KEY_INSERT"] = "Insert",
            ["OBS_KEY_DELETE"] = "Delete",
            ["OBS_KEY_HOME"] = "Home",
            ["OBS_KEY_END"] = "End",
            ["OBS_KEY_PAGEUP"] = "PageUp",
            ["OBS_KEY_PAGEDOWN"] = "PageDown",
            ["OBS_KEY_UP"] = "Up",
            ["OBS_KEY_DOWN"] = "Down",
            ["OBS_KEY_LEFT"] = "Left",
            ["OBS_KEY_RIGHT"] = "Right",
            ["OBS_KEY_CAPSLOCK"] = "CapsLock",
            ["OBS_KEY_PRINT"] = "PrintScreen",
            ["OBS_KEY_SCROLLLOCK"] = "ScrollLock",
            ["OBS_KEY_PAUSE"] = "Pause",
        };

        // modifier synonyms -> the json field obs expects.
        private static readonly Dictionary<string, string> ModAliases = new Dictionary<string, string>
        {
            ["shift"] = "shift",
            ["ctrl"] = "control",
            ["control"] = "control",
            ["alt"] = "alt",
            ["opt"] = "alt",
            ["option"] = "alt",
            ["win"] = "command",
            ["cmd"] = "command",
            ["meta"] = "command",
            ["command"] = "command",
        };

        // pretty modifier order for display labels, and the field order obs expects when serializing.
        private static readonly string[] ModOrder = { "control", "alt", "shift", "command" };
        private static readonly Dictionary<string, string> ModLabel = new Dictionary<string, string>
        {
            ["control"] = "Ctrl",
            ["alt"] = "Alt",
            ["shift"] = "Shift",
            ["command"] = "Win",
        };

        // map a single token (the non-modifier part of a combo) to an OBS_KEY_.
        private static string ObsKeyForToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (NamedKeys.TryGetValue(token, out var direct)) return direct;
            string low = token.ToLowerInvariant();
            if (NamedKeys.TryGetValue(low, out var lowMatch)) return lowMatch;
            if (token.Length == 1 && char.IsLetter(token[0])) return "OBS_KEY_" + token.ToUpperInvariant();
            if (token.Length == 1 && char.IsDigit(token[0])) return "OBS_KEY_" + token;
            if (low.StartsWith("f") && low.Length > 1 && low.Substring(1).All(char.IsDigit))
            {
                int n = int.Parse(low.Substring(1));
                if (n >= 1 && n <= 24) return "OBS_KEY_F" + n;
            }
            if (low.StartsWith("numpad") && low.Length > 6 && low.Substring(6).All(char.IsDigit))
            {
                return "OBS_KEY_NUM" + low.Substring(6);
            }
            return null;
        }

        // parse "shift+\\" / "ctrl+f10" / "f9" into an obs keybind dict. null if no key was identified or input is modifier-only (obs wont fire on a bare shift).
        public static Dictionary<string, object> ParseCombo(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string cleaned = text.Trim().Trim('\'', '"');
            cleaned = cleaned.Replace("-", "+").Replace(" ", "");
            if (cleaned.Length == 0) return null;

            var parts = new List<string>();
            var buf = new StringBuilder();
            foreach (char ch in cleaned)
            {
                if (ch == '+' && buf.Length > 0)
                {
                    parts.Add(buf.ToString());
                    buf.Clear();
                }
                else
                {
                    buf.Append(ch);
                }
            }
            if (buf.Length > 0) parts.Add(buf.ToString());
            if (parts.Count == 0) return null;

            var combo = new Dictionary<string, object>();
            string keyToken = null;
            foreach (var raw in parts)
            {
                string low = raw.ToLowerInvariant();
                if (ModAliases.TryGetValue(low, out var modField) && raw != keyToken)
                {
                    combo[modField] = true;
                    continue;
                }
                if (keyToken == null) keyToken = raw;
            }

            if (keyToken == null) return null;
            string obsKey = ObsKeyForToken(keyToken);
            if (obsKey == null) return null;
            combo["key"] = obsKey;
            return combo;
        }

        // format an obs keybind dict like "Shift + \\" for the cli.
        public static string ComboToLabel(Dictionary<string, object> combo)
        {
            if (combo == null || !combo.ContainsKey("key")) return "(none)";
            var parts = new List<string>();
            foreach (var mod in ModOrder)
            {
                if (combo.TryGetValue(mod, out var v) && v is bool b && b) parts.Add(ModLabel[mod]);
            }
            string obsKey = Convert.ToString(combo["key"]);
            if (NamedKeyLabels.TryGetValue(obsKey, out var label))
            {
                parts.Add(label);
            }
            else if (obsKey.StartsWith("OBS_KEY_F") && obsKey.Substring("OBS_KEY_F".Length).All(char.IsDigit))
            {
                parts.Add(obsKey.Substring("OBS_KEY_".Length));
            }
            else if (obsKey.StartsWith("OBS_KEY_NUM"))
            {
                parts.Add("Numpad " + obsKey.Substring("OBS_KEY_NUM".Length));
            }
            else if (obsKey.StartsWith("OBS_KEY_"))
            {
                parts.Add(TitleCase(obsKey.Substring("OBS_KEY_".Length)));
            }
            else
            {
                parts.Add(obsKey);
            }
            return string.Join(" + ", parts);
        }

        private static string TitleCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        }

        // builds {mod:true, ..., key:"..."} in obs field order, no whitespace -- shared by both basic.ini and hotkey-frontend json serialization below.
        private static string OrderedComboJson(Dictionary<string, object> combo)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var mod in ModOrder)
            {
                if (combo.TryGetValue(mod, out var v) && v is bool b && b)
                {
                    if (!first) sb.Append(',');
                    sb.Append('"').Append(mod).Append("\":true");
                    first = false;
                }
            }
            if (!first) sb.Append(',');
            sb.Append("\"key\":\"").Append(combo["key"]).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        // serialise the combo into the exact json obs writes for ReplayBuffer. compact form: no whitespace, modifiers before key, only true-valued modifiers emitted.
        public static string ToBasicIniValue(Dictionary<string, object> combo)
        {
            if (combo == null || !combo.ContainsKey("key")) return "{\"ReplayBuffer.Save\":[]}";
            return "{\"ReplayBuffer.Save\":[" + OrderedComboJson(combo) + "]}";
        }

        // serialise a frontend obs hotkey value such as OBSBasic.StartRecording.
        public static string ToObsHotkeyValue(Dictionary<string, object> combo)
        {
            if (combo == null || !combo.ContainsKey("key")) return "{\"bindings\":[]}";
            return "{\"bindings\":[" + OrderedComboJson(combo) + "]}";
        }

        // parse the json obs persists under [Hotkeys] ReplayBuffer=. returns the first ReplayBuffer.Save keybind dict, or null if malformed.
        public static Dictionary<string, object> FromBasicIniValue(string text)
        {
            object data;
            try
            {
                data = new JavaScriptSerializer().DeserializeObject(text);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (!(data is Dictionary<string, object> dict)) return null;
            if (!dict.TryGetValue("ReplayBuffer.Save", out var bindsObj)) return null;
            if (!(bindsObj is object[] binds) || binds.Length == 0) return null;
            if (!(binds[0] is Dictionary<string, object> first) || !first.ContainsKey("key")) return null;
            return first;
        }

        // the shift+\\ keybind the bundled basic.ini ships with.
        public static Dictionary<string, object> DefaultCombo()
        {
            return new Dictionary<string, object> { ["shift"] = true, ["key"] = "OBS_KEY_BACKSLASH" };
        }
    }
}
