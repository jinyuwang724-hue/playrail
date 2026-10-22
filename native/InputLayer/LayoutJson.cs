using System;
using System.Collections;
using System.Collections.Generic;

// The engine form of a layout, compiled by the toolbox agent (plugin/claw_input_layer.py) from the
// stored layout: key names are already virtual-key codes, buttons are XUSB bits. The agent validates
// everything; this side only refuses what it cannot run.
namespace ClawInput {
    public static class LayoutJson {
        static object Get(IDictionary d, string key) { return d != null && d.Contains(key) ? d[key] : null; }
        static double Number(IDictionary d, string key, double fallback, double low, double high) {
            object v = Get(d, key);
            if (v == null) return fallback;
            double x = Convert.ToDouble(v);
            if (double.IsNaN(x) || x < low || x > high) throw new ArgumentException(key + " 超出范围");
            return x;
        }
        static int Source(object name) {
            int i = Sources.Index(name as string);
            if (i < 0) throw new ArgumentException("不认识的键 " + name);
            return i;
        }
        static StickOut Stick(object v, StickOut fallback) {
            string s = v as string;
            if (s == null) return fallback;
            if (s == "left") return StickOut.Left;
            if (s == "right") return StickOut.Right;
            if (s == "none") return StickOut.None;
            throw new ArgumentException("摇杆输出只能是 left / right / none");
        }

        static StickMode Mode(object v) {
            string s = v as string;
            if (s == null || s == "stick") return StickMode.Stick;
            if (s == "mouse") return StickMode.Mouse;
            if (s == "dpad") return StickMode.Dpad;
            throw new ArgumentException("摇杆行为只能是 stick / mouse / dpad");
        }

        public static Layout Parse(IDictionary d) {
            var layout = new Layout();
            layout.Name = Get(d, "name") as string ?? "";
            layout.TapMs = Number(d, "tapMs", 40, 10, 500);
            layout.LtThreshold = (byte)Number(d, "ltThreshold", 128, 1, 254);
            layout.RtThreshold = (byte)Number(d, "rtThreshold", 128, 1, 254);
            layout.LeftStick = Stick(Get(d, "leftStick"), StickOut.Left);
            layout.RightStick = Stick(Get(d, "rightStick"), StickOut.Right);
            layout.LeftMode = Mode(Get(d, "leftMode"));
            layout.RightMode = Mode(Get(d, "rightMode"));
            layout.LeftSpeed = Number(d, "leftSpeed", 1500, 50, 10000);
            layout.RightSpeed = Number(d, "rightSpeed", 1500, 50, 10000);
            var buttons = Get(d, "buttons") as IDictionary;
            if (buttons != null)
                foreach (DictionaryEntry entry in buttons) {
                    int source = Source(entry.Key);
                    var list = new List<ActivatorDef>();
                    var items = entry.Value as IList;
                    if (items == null) throw new ArgumentException("按键 " + entry.Key + " 的触发方式要是列表");
                    foreach (var item in items) list.Add(Activator(item as IDictionary));
                    layout.Buttons[source] = list;
                }
            return layout;
        }

        static ActivatorDef Activator(IDictionary d) {
            if (d == null) throw new ArgumentException("触发方式格式错误");
            var a = new ActivatorDef();
            a.Type = Get(d, "type") as string;
            if (Array.IndexOf(new[] { "regular", "long", "double", "start", "release", "chord" }, a.Type) < 0) throw new ArgumentException("不认识的触发方式 " + a.Type);
            a.LongMs = Number(d, "longMs", 300, 50, 5000);
            a.DoubleMs = Number(d, "doubleMs", 250, 50, 2000);
            a.StartDelayMs = Number(d, "startDelayMs", 0, 0, 5000);
            a.EndDelayMs = Number(d, "endDelayMs", 0, 0, 5000);
            a.RepeatHz = Number(d, "repeatHz", 0, 0, 60);
            a.Toggle = Equals(Get(d, "toggle"), true);
            a.Interruptible = !Equals(Get(d, "interruptible"), false);
            if (a.Type == "chord") a.Chord = Source(Get(d, "chord"));
            a.Command = Command(Get(d, "command") as IDictionary);
            return a;
        }

        static Command Command(IDictionary d) {
            var c = new Command();
            string kind = Get(d, "kind") as string;
            switch (kind) {
                case "none": c.Kind = CommandKind.None; break;
                case "pad": c.Kind = CommandKind.Pad; c.Pad = (ushort)Number(d, "pad", 0, 1, 0xFFFF); break;
                case "axis": c.Kind = CommandKind.Axis; c.Value = (int)Number(d, "value", 0, 0, 9); break;
                case "mouse": c.Kind = CommandKind.Mouse; c.Value = (int)Number(d, "value", 1, 1, 5); break;
                case "wheel": c.Kind = CommandKind.Wheel; c.Value = (int)Number(d, "value", 1, 1, 4); break;
                case "action":
                    c.Kind = CommandKind.Action; c.Action = Get(d, "action") as string;
                    if (Array.IndexOf(new[] { "keyboard", "gamebar", "library", "controlCenter", "steamMainMenu", "steamQuickAccess", "steamLibrary", "magpieToggle" },c.Action)<0)
                        throw new ArgumentException("不认识的系统动作 " + c.Action);
                    break;
                case "keys": {
                    c.Kind = CommandKind.Keys;
                    var keys = Get(d, "keys") as IList;
                    var ext = Get(d, "extended") as IList;
                    if (keys == null || keys.Count < 1 || keys.Count > 4) throw new ArgumentException("键盘指令要 1 到 4 个键");
                    c.Keys = new ushort[keys.Count]; c.Extended = new bool[keys.Count];
                    for (int i = 0; i < keys.Count; i++) {
                        int vk = Convert.ToInt32(keys[i]);
                        if (vk < 1 || vk > 254) throw new ArgumentException("键码超出范围");
                        c.Keys[i] = (ushort)vk; c.Extended[i] = ext != null && i < ext.Count && Equals(ext[i], true);
                    }
                    break;
                }
                default: throw new ArgumentException("不认识的指令 " + kind);
            }
            return c;
        }
    }
}
