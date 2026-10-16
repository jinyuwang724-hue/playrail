using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ClawInput;

// Input layer engine without devices: trigger timing, output mixing, layout parsing, HidHide journal.
class InputLayerTests {
    static int failures;
    static void Check(bool result, string name) { if (!result) { failures++; Console.WriteLine("FAIL " + name); } else Console.WriteLine("PASS " + name); }

    const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, LB = 0x100;
    const ushort TAB = 0x09, CTRL = 0xA2, KEY_C = 0x43;

    static Command Pad(ushort bit) { return new Command { Kind = CommandKind.Pad, Pad = bit }; }
    static Command Keys(params ushort[] keys) { return new Command { Kind = CommandKind.Keys, Keys = keys, Extended = new bool[keys.Length] }; }
    static ActivatorDef Act(string type, Command c) { return new ActivatorDef { Type = type, Command = c }; }
    static Layout With(string source, params ActivatorDef[] acts) {
        var l = new Layout(); l.Buttons[Sources.Index(source)] = acts.ToList(); return l;
    }

    // Presses: (time ms, source names held from then on). Runs 1 ms ticks to the end time.
    sealed class Run {
        public readonly List<KeyValuePair<double, ushort>> Buttons = new List<KeyValuePair<double, ushort>>();
        public readonly List<KeyValuePair<double, OutputEvent>> Events = new List<KeyValuePair<double, OutputEvent>>();
        public readonly List<PadReport> Reports = new List<PadReport>();
        public Run(Layout layout, double end, params object[] steps) : this(new Translator(), layout, end, steps) { }
        public Run(Translator t, Layout layout, double end, params object[] steps) {
            var events = new List<OutputEvent>();
            t.SetLayout(layout, events);
            var frame = new InputFrame();
            ushort last = 0xFFFF;
            for (double now = 0; now <= end; now += 1) {
                for (int i = 0; i < steps.Length; i += 2)
                    if (Convert.ToDouble(steps[i]) == now) {
                        frame.Clear();
                        foreach (var name in ((string)steps[i + 1]).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                            frame.Pressed[Sources.Index(name)] = true;
                            if (name == "LT") frame.LT = 255;
                        }
                    }
                events.Clear();
                var r = t.Update(frame, now, events);
                Reports.Add(r);
                if (r.Buttons != last) { Buttons.Add(new KeyValuePair<double, ushort>(now, r.Buttons)); last = r.Buttons; }
                foreach (var e in events) Events.Add(new KeyValuePair<double, OutputEvent>(now, e));
            }
        }
        // First time the bit is on, or -1.
        public double On(ushort bit) { foreach (var b in Buttons) if ((b.Value & bit) != 0) return b.Key; return -1; }
        public double Off(ushort bit) { bool seen = false; foreach (var b in Buttons) { if ((b.Value & bit) != 0) seen = true; else if (seen) return b.Key; } return -1; }
        public int Rises(ushort bit) { int n = 0; bool was = false; foreach (var b in Buttons) { bool on = (b.Value & bit) != 0; if (on && !was) n++; was = on; } return n; }
        public string Keys() { return string.Join(" ", Events.Select(e => e.Key + ":" + e.Value)); }
    }

    static Layout LongAToB() {
        return With("A", new ActivatorDef { Type = "long", LongMs = 300, Command = Pad(B) }, Act("regular", Pad(A)));
    }
    static Layout DoubleXToTab() {
        return With("X", new ActivatorDef { Type = "double", DoubleMs = 250, Command = Keys(TAB) }, Act("regular", Pad(X)));
    }

    static void Triggers() {
        var r = new Run(new Layout(), 50, 0, "A", 20, "");
        Check(r.On(A) == 0 && r.Off(A) == 20, "an unbound button passes straight through, same tick");

        r = new Run(LongAToB(), 1000, 0, "A", 500, "");
        Check(r.On(B) == 300 && r.Off(B) == 500, "long press: B from the long press time until release");
        Check(r.On(A) == -1, "long press claims the press: the interruptible regular A never fires");

        r = new Run(LongAToB(), 1000, 0, "A", 100, "");
        Check(r.On(B) == -1, "short tap does not fire the long press");
        Check(r.On(A) == 100 && r.Off(A) == 140, "short tap fires regular A as a 40 ms tap on release");

        var eager = LongAToB(); eager.Buttons[Sources.Index("A")][1].Interruptible = false;
        r = new Run(eager, 1000, 0, "A", 500, "");
        Check(r.On(A) == 0 && r.Off(A) == 500 && r.On(B) == 300, "non-interruptible regular fires at once, long press on top");

        r = new Run(DoubleXToTab(), 1000, 0, "X", 80, "", 180, "X", 300, "");
        Check(r.Keys() == "180:KeyDown:9 300:KeyUp:9", "double press: Tab down on the second press, up on its release: " + r.Keys());
        Check(r.On(X) == -1, "double press claims both presses: no X");

        r = new Run(DoubleXToTab(), 1000, 0, "X", 80, "");
        Check(r.Events.Count == 0, "single tap sends no Tab");
        Check(r.On(X) == 331 && r.Off(X) == 371, "single tap fires X after the double-press window: " + r.On(X));

        r = new Run(DoubleXToTab(), 1000, 0, "X", 600, "");
        Check(r.On(X) == 250 && r.Off(X) == 600, "held press becomes regular X once the window passes");

        r = new Run(DoubleXToTab(), 1000, 0, "X", 80, "", 400, "X", 450, "");
        Check(r.Events.Count == 0 && r.Rises(X) == 2, "second press after the window is a new single press");

        r = new Run(With("A", Act("start", Pad(B)), Act("release", Pad(Y))), 1000, 0, "A", 300, "");
        Check(r.On(B) == 0 && r.Off(B) == 40, "start press: a tap when the button goes down");
        Check(r.On(Y) == 300 && r.Off(Y) == 340, "release press: a tap when the button comes up");

        var chord = With("A", new ActivatorDef { Type = "chord", Chord = Sources.Index("LB"), Command = Keys(CTRL) }, Act("regular", Pad(A)));
        r = new Run(chord, 500, 0, "LB", 50, "LB A", 150, "LB", 200, "");
        Check(r.Keys() == "50:KeyDown:162 150:KeyUp:162", "chord: LB held + A sends Ctrl: " + r.Keys());
        Check(r.On(A) == -1 && r.On(LB) == 0, "chord press belongs to the chord only; LB itself still passes through");
        r = new Run(chord, 500, 0, "A", 100, "");
        Check(r.On(A) == 0 && r.Events.Count == 0, "A alone stays A");

        r = new Run(With("A", new ActivatorDef { Type = "regular", Toggle = true, Command = Pad(B) }), 1000, 0, "A", 50, "", 300, "A", 350, "");
        Check(r.On(B) == 0 && r.Off(B) == 300, "toggle: first press latches B, second press releases it");

        r = new Run(With("A", new ActivatorDef { Type = "regular", RepeatHz = 10, Command = Pad(B) }), 1200, 0, "A", 1000, "");
        Check(r.Rises(B) == 10, "repeat at 10 Hz: 10 presses in one second, got " + r.Rises(B));

        r = new Run(With("A", new ActivatorDef { Type = "regular", StartDelayMs = 100, EndDelayMs = 50, Command = Pad(B) }), 1000, 0, "A", 200, "");
        Check(r.On(B) == 100 && r.Off(B) == 250, "start delay 100 ms, end delay 50 ms");

        r = new Run(With("A", new ActivatorDef { Type = "regular", StartDelayMs = 100, Command = Pad(B) }), 1000, 0, "A", 20, "");
        Check(r.On(B) == 100 && r.Off(B) == 140, "a press shorter than the start delay still fires after it, as a tap");
    }

    static void Mixing() {
        var r = new Run(With("A", Act("regular", Keys(CTRL, KEY_C))), 300, 0, "A", 100, "");
        Check(r.Keys() == "0:KeyDown:162 0:KeyDown:67 100:KeyUp:67 100:KeyUp:162", "key chord: Ctrl then C down, C then Ctrl up: " + r.Keys());

        var two = new Layout();
        two.Buttons[Sources.Index("A")] = new List<ActivatorDef> { Act("regular", Pad(Y)) };
        two.Buttons[Sources.Index("B")] = new List<ActivatorDef> { Act("regular", Pad(Y)) };
        r = new Run(two, 300, 0, "A", 50, "A B", 100, "B", 150, "");
        Check(r.On(Y) == 0 && r.Off(Y) == 150, "two buttons on one output: held until both are up");

        r = new Run(With("LT", Act("regular", Pad(B))), 200, 0, "LT", 100, "");
        Check(r.On(B) == 0 && r.Reports[50].LT == 0, "LT used as a button: digital press, no analog output");
        r = new Run(new Layout(), 100, 0, "LT");
        Check(r.Reports[50].LT == 255, "unbound LT stays analog");

        r = new Run(With("A", Act("regular", new Command { Kind = CommandKind.Axis, Value = 2 })), 100, 0, "A");
        Check(r.Reports[50].LY == 32767 && (r.Reports[50].Buttons & A) == 0, "A to left stick up");

        r = new Run(With("A", new ActivatorDef { Type = "regular", RepeatHz = 5, Command = new Command { Kind = CommandKind.Wheel, Value = 2 } }), 1100, 0, "A", 1000, "");
        Check(r.Events.Count(e => e.Value.Kind == OutputKind.Wheel) == 5, "wheel: one notch per repeat");

        var t = new Translator();
        r = new Run(t, With("A", Act("regular", Keys(TAB))), 100, 0, "A");
        var events = new List<OutputEvent>();
        t.SetLayout(Layout.Passthrough(), events);
        Check(events.Count == 1 && events[0].Kind == OutputKind.KeyUp && events[0].Key == TAB, "switching layouts releases held keys");

        var m = new Run(With("M1", Act("regular", Pad(A))), 100, 0, "M1", 50, "");
        Check(m.On(A) == 0 && m.Off(A) == 50, "M1 can drive an Xbox button");
        m = new Run(new Layout(), 100, 0, "M1");
        Check(m.On(0xFFFF) == -1, "unbound M1 / M2 send nothing");
    }

    static void Parsing() {
        var raw = "{\"name\":\"t\",\"tapMs\":30,\"ltThreshold\":100,\"rightStick\":\"none\",\"buttons\":{\"A\":[{\"type\":\"long\",\"longMs\":400,\"command\":{\"kind\":\"pad\",\"pad\":8192}},"
                + "{\"type\":\"regular\",\"command\":{\"kind\":\"pad\",\"pad\":4096}}],\"X\":[{\"type\":\"double\",\"doubleMs\":200,\"command\":{\"kind\":\"keys\",\"keys\":[9],\"extended\":[false]}}],"
                + "\"Y\":[],\"LB\":[{\"type\":\"chord\",\"chord\":\"RB\",\"repeatHz\":8,\"toggle\":true,\"interruptible\":false,\"command\":{\"kind\":\"action\",\"action\":\"keyboard\"}}]}}";
        var layout = LayoutJson.Parse(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw));
        var a = layout.Buttons[Sources.Index("A")];
        Check(a.Count == 2 && a[0].Type == "long" && a[0].LongMs == 400 && a[0].Command.Pad == B, "parse long press");
        Check(layout.Buttons[Sources.Index("X")][0].Command.Keys[0] == TAB, "parse keyboard command");
        Check(layout.Buttons[Sources.Index("Y")].Count == 0 && layout.Buttons[Sources.Index("B")] == null, "empty list disables, missing stays itself");
        var lb = layout.Buttons[Sources.Index("LB")][0];
        Check(lb.Chord == Sources.Index("RB") && lb.Toggle && !lb.Interruptible && lb.RepeatHz == 8, "parse chord settings");
        Check(layout.TapMs == 30 && layout.LtThreshold == 100 && layout.RightStick == StickOut.None, "parse layout settings");
        foreach (var bad in new[] { "{\"buttons\":{\"Q\":[]}}", "{\"buttons\":{\"A\":[{\"type\":\"triple\",\"command\":{\"kind\":\"none\"}}]}}",
                                    "{\"buttons\":{\"A\":[{\"type\":\"regular\",\"command\":{\"kind\":\"keys\",\"keys\":[]}}]}}",
                                    "{\"buttons\":{\"A\":[{\"type\":\"long\",\"longMs\":1,\"command\":{\"kind\":\"none\"}}]}}",
                                    "{\"buttons\":{\"A\":[{\"type\":\"regular\",\"command\":{\"kind\":\"action\",\"action\":\"shutdown\"}}]}}" }) {
            bool refused = false;
            try { LayoutJson.Parse(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(bad)); } catch (ArgumentException) { refused = true; }
            Check(refused, "refuse " + bad);
        }
    }

    sealed class FakeHidHide : IHidHide {
        public List<string> Black = new List<string>(), White = new List<string>();
        public bool Cloak;
        public int Writes;
        public List<string> Blacklist { get { return new List<string>(Black); } set { Black = new List<string>(value); Writes++; } }
        public List<string> Whitelist { get { return new List<string>(White); } set { White = new List<string>(value); Writes++; } }
        public bool Active { get { return Cloak; } set { Cloak = value; Writes++; } }
    }

    static void Journal() {
        string dir = Path.Combine(Path.GetTempPath(), "claw-input-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "hidhide-journal.json");
        var hh = new FakeHidHide();
        hh.White.Add(@"\Device\HarddiskVolume3\Tools\Other.exe");
        hh.Black.Add(@"HID\VID_1234&PID_0001\1");
        var pad = new List<string> { @"HID\VID_0DB0&PID_1901&IG_03\8&E3E6117&0&0000", @"USB\VID_0DB0&PID_1901&MI_00\6&2B02AE9F&1&0000" };
        var apps = new List<string> { @"\Device\HarddiskVolume3\App\ClawInput.exe", @"\Device\HarddiskVolume3\Tools\other.exe" };
        var j = Hiding.Apply(hh, path, 42, pad, apps);
        Check(hh.Cloak && hh.Black.Count == 3 && hh.White.Count == 2, "apply: hides the pad, keeps other entries, adds only missing apps");
        Check(j.AddedApps.Count == 1 && j.AddedDevices.Count == 2 && !j.PreviousCloak, "journal lists only what we added");
        var loaded = HideJournal.Load(path);
        Check(loaded != null && loaded.Active && loaded.Pid == 42 && loaded.AddedDevices.Count == 2, "journal is on disk before anything else");

        hh.Cloak = false; hh.Black.RemoveAt(2);
        Check(Hiding.Ensure(hh, path, j, pad, apps) == 2 && hh.Cloak && hh.Black.Count == 3, "ensure puts back a cloak / entry someone removed");
        hh.Writes = 0;
        Check(Hiding.Ensure(hh, path, j, pad, apps) == 0 && hh.Writes == 0, "ensure writes nothing when intact");

        Hiding.Restore(hh, path, HideJournal.Load(path), "test");
        Check(!hh.Cloak && hh.Black.Count == 1 && hh.White.Count == 1 && hh.White[0].EndsWith("Other.exe"), "restore: cloak off, only our entries removed");
        Check(!HideJournal.Load(path).Active && HideJournal.Load(path).RestoredBy == "test", "journal marked restored");
        Hiding.Restore(hh, path, HideJournal.Load(path), "again");
        Check(!hh.Cloak && hh.Black.Count == 1 && hh.White.Count == 1, "restore twice is harmless");

        hh.Cloak = true;
        j = Hiding.Apply(hh, path, 43, pad, apps);
        Hiding.Restore(hh, path, j, "test");
        Check(hh.Cloak && hh.Black.Count == 1, "someone else's cloak stays on after restore");
        Directory.Delete(dir, true);

        Check(MultiSz.Encode(new List<string> { "a", "b" }) == "a\0b\0\0" && MultiSz.Encode(new List<string>()) == "\0\0", "multi-string encoding");
        Check(string.Join("|", MultiSz.Decode("a\0b\0\0")) == "a|b", "multi-string decoding");
    }

    static int Main() {
        Triggers();
        Mixing();
        Parsing();
        Journal();
        Console.WriteLine(failures == 0 ? "input layer checks passed" : failures + " input layer check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
