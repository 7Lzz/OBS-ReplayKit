using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using ReplayKitShared;

namespace ReplayKitSetup
{
    // writes a hotkey combo into the qt-flavored json obs persists for hotkeys (OBS_KEY_<x> + shift/control/alt/command booleans). the combo shape (lowercase shift/control/alt/command/key fields, in that order) is a contract with the helpers settings json
    public static class Keybind
    {
        // the field order obs expects when serializing.
        private static readonly string[] ModOrder = HotkeyModifiers.Order;

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
            if (!(combo["key"] is string key)) throw new ArgumentException(SetupLocale.Message("setup.hotkeyKeyMustBeAString"));
            sb.Append("\"key\":").Append(JsonConvert.ToString(key));
            sb.Append('}');
            return sb.ToString();
        }

        // the json obs writes for ReplayBuffer and for a frontend hotkey such as OBSBasic.StartRecording, with the extra binding of the settings as a second entry, the way the helper writes them; the same combo twice is written once, and only true-valued modifiers are emitted
        public static string ToBasicIniValue(Dictionary<string, object> combo, Dictionary<string, object> extra) =>
            "{\"ReplayBuffer.Save\":[" + BindingsJson(combo, extra) + "]}";

        public static string ToObsHotkeyValue(Dictionary<string, object> combo, Dictionary<string, object> extra) =>
            "{\"bindings\":[" + BindingsJson(combo, extra) + "]}";

        private static string BindingsJson(Dictionary<string, object> combo, Dictionary<string, object> extra)
        {
            var list = new List<string>();
            if (combo != null && combo.ContainsKey("key")) list.Add(OrderedComboJson(combo));
            if (extra != null && extra.ContainsKey("key"))
            {
                string second = OrderedComboJson(extra);
                if (!list.Contains(second)) list.Add(second);
            }
            return string.Join(",", list);
        }

        // the clip keybind the bundled basic.ini ships with.
        public static Dictionary<string, object> DefaultCombo()
        {
            return new Dictionary<string, object> { ["key"] = SettingDefaults.ClipKey };
        }
    }
}
