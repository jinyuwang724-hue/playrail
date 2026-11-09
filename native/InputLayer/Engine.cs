using System;
using System.Collections.Generic;

// Translation core of the software input layer: real pad frame + layout -> virtual pad report
// plus keyboard / mouse / system events. Pure and clock-driven (times in ms), so the trigger
// behaviour is unit-tested without devices (tests/InputLayerTests.cs).
//
// Trigger semantics follow Steam Input's activators (docs/input/gamepad-input-layer.md):
//   regular  pressed while the button is held
//   long     after the button is held longPressMs, until release
//   double   on the second press within doublePressMs of the first release, until that release
//   start    a tap when the button goes down
//   release  a tap when the button comes up
//   chord    like regular, but only when the press starts while chordWith is held; such a press
//            belongs to the chord activators only
// An interruptible regular press on a button that also has long / double activators waits until
// the press is known to be neither (Steam delays it the same way): a short tap fires as a tap on
// release (or when the double window ends), a long hold fires the long activator instead.
namespace ClawInput {
    public static class Sources {
        public static readonly string[] Names = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Back", "Start", "Guide",
                                                  "LS", "RS", "Up", "Down", "Left", "Right", "M1", "M2" };
        public const int Count = 19, LT = 6, RT = 7, M1 = 17, M2 = 18;
        // XUSB button bit of each source (0: none, LT / RT are analog, M1 / M2 have no Xbox button).
        public static readonly ushort[] PadBit = { 0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0, 0, 0x0020, 0x0010, 0x0400,
                                                   0x0040, 0x0080, 0x0001, 0x0002, 0x0004, 0x0008, 0, 0 };
        public static int Index(string name) { return Array.IndexOf(Names, name); }
    }

    public struct PadReport {
        public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY;
        public bool Same(PadReport o) { return Buttons == o.Buttons && LT == o.LT && RT == o.RT && LX == o.LX && LY == o.LY && RX == o.RX && RY == o.RY; }
    }

    public sealed class InputFrame {
        public readonly bool[] Pressed = new bool[Sources.Count];
        public byte LT, RT; public short LX, LY, RX, RY;
        public void Clear() { Array.Clear(Pressed, 0, Pressed.Length); LT = RT = 0; LX = LY = RX = RY = 0; }
        public void CopyFrom(InputFrame o) { Array.Copy(o.Pressed, Pressed, Pressed.Length); LT = o.LT; RT = o.RT; LX = o.LX; LY = o.LY; RX = o.RX; RY = o.RY; }
    }

    public enum CommandKind { None, Pad, Axis, Keys, Mouse, Wheel, Action }
    // Axis: 0 LT, 1 RT, 2 LStickUp, 3 LStickDown, 4 LStickLeft, 5 LStickRight, 6-9 the same for the right stick.
    // Mouse: 1 left, 2 right, 3 middle, 4 back (X1), 5 forward (X2). Wheel: 1 up, 2 down, 3 left, 4 right.
    public sealed class Command {
        public CommandKind Kind; public ushort Pad; public int Value; public ushort[] Keys; public bool[] Extended; public string Action;
    }

    public sealed class ActivatorDef {
        public string Type = "regular";
        public double LongMs = 300, DoubleMs = 250, StartDelayMs, EndDelayMs, RepeatHz;
        public int Chord = -1;
        public bool Toggle, Interruptible = true;
        public Command Command = new Command();
    }

    public enum StickOut { Left, Right, None }

    public sealed class Layout {
        public string Name = "";
        public readonly List<ActivatorDef>[] Buttons = new List<ActivatorDef>[Sources.Count]; // null: the button stays itself
        public byte LtThreshold = 128, RtThreshold = 128;
        public StickOut LeftStick = StickOut.Left, RightStick = StickOut.Right;
        public double TapMs = 40;
        public static Layout Passthrough() { return new Layout { Name = "passthrough" }; }
        public bool Uses(int source) { return Buttons[source] != null; }
    }

    public enum OutputKind { KeyDown, KeyUp, MouseDown, MouseUp, Wheel, Action }
    public struct OutputEvent {
        public OutputKind Kind; public ushort Key; public bool Extended; public int Value; public string Action;
        public override string ToString() { return Kind + (Kind == OutputKind.Action ? ":" + Action : ":" + (Kind == OutputKind.KeyDown || Kind == OutputKind.KeyUp ? Key : Value)); }
    }

    // One activator's command over time: start / end delays, toggle, repeat (turbo), minimum tap length.
    public sealed class Driver {
        public readonly ActivatorDef Def;
        readonly double tapMs;
        bool running, latched;
        double start, end, minEnd;
        public bool Pressed, WasPressed;
        public Driver(ActivatorDef def, double tapMs) { Def = def; this.tapMs = tapMs; }
        public bool Engaged { get { return running; } }

        public void On(double t) {
            if (Def.Toggle) { latched = !latched; if (latched) Begin(t, tapMs); else Finish(t); return; }
            Begin(t, 0);
        }
        public void Off(double t) { if (!Def.Toggle) Finish(t); }
        public void Tap(double t) {
            if (Def.Toggle) { On(t); return; }
            Begin(t, tapMs); Finish(t);
        }
        public void Reset() { running = latched = false; Pressed = WasPressed = false; }
        void Begin(double t, double minMs) { running = true; start = t + Def.StartDelayMs; minEnd = start + minMs; end = double.PositiveInfinity; }
        // Released before a start delay ran out: the command still fires, as a tap.
        void Finish(double t) { if (running && double.IsPositiveInfinity(end)) end = Math.Max(Math.Max(t, minEnd), t < start ? start + tapMs : 0) + Def.EndDelayMs; }

        public void Evaluate(double t) {
            WasPressed = Pressed;
            if (running && t >= end) running = false;
            bool active = running && t >= start;
            if (active && Def.RepeatHz > 0) {
                double period = 1000.0 / Def.RepeatHz;
                active = (t - start) % period < period / 2;
            }
            Pressed = active;
        }
    }

    // Press timeline of one physical button, dispatched to its activators.
    public sealed class ButtonProcessor {
        enum Phase { Idle, Pressed, AwaitSecond, Second }
        readonly List<Driver> regular = new List<Driver>(), deferred = new List<Driver>(), longs = new List<Driver>(),
            doubles = new List<Driver>(), starts = new List<Driver>(), releases = new List<Driver>(), chords = new List<Driver>();
        public readonly List<Driver> All = new List<Driver>();
        readonly bool[] longDone;
        readonly double doubleMs;
        Phase phase;
        bool down, chordSession, pending, pendingHeld, pendingTap, longFired;
        double pressAt, releaseAt;

        public ButtonProcessor(List<ActivatorDef> defs, double tapMs) {
            bool hasLong = false, hasDouble = false;
            foreach (var d in defs) { if (d.Type == "long") hasLong = true; if (d.Type == "double") hasDouble = true; }
            doubleMs = 250;
            foreach (var d in defs) {
                var driver = new Driver(d, tapMs);
                All.Add(driver);
                switch (d.Type) {
                    case "regular": ((d.Interruptible && (hasLong || hasDouble)) ? deferred : regular).Add(driver); break;
                    case "long": longs.Add(driver); break;
                    case "double": if (doubles.Count == 0) doubleMs = d.DoubleMs; doubles.Add(driver); break;
                    case "start": starts.Add(driver); break;
                    case "release": releases.Add(driver); break;
                    case "chord": chords.Add(driver); break;
                }
            }
            longDone = new bool[longs.Count];
        }

        public void Update(bool pressed, bool[] held, double t) {
            if (pressed && !down) Press(held, t);
            else if (!pressed && down) Release(t);
            down = pressed;
            Tick(t);
        }

        public void Reset() {
            foreach (var d in All) d.Reset();
            phase = Phase.Idle; down = chordSession = pending = pendingHeld = pendingTap = longFired = false;
        }

        void Press(bool[] held, double t) {
            if (phase == Phase.AwaitSecond) {
                if (t - releaseAt <= doubleMs && !ChordHeld(held)) {
                    phase = Phase.Second;
                    pendingTap = false;
                    foreach (var d in doubles) d.On(t);
                    foreach (var d in regular) d.On(t);
                    foreach (var d in starts) if (!d.Def.Interruptible) d.Tap(t);
                    return;
                }
                ResolveTap(t);
            }
            phase = Phase.Pressed; pressAt = t; longFired = pending = pendingHeld = pendingTap = false;
            Array.Clear(longDone, 0, longDone.Length);
            chordSession = false;
            foreach (var d in chords) if (held[d.Def.Chord]) { d.On(t); chordSession = true; }
            if (chordSession) return;
            foreach (var d in starts) d.Tap(t);
            foreach (var d in regular) d.On(t);
            pending = deferred.Count > 0;
        }

        void Release(double t) {
            if (phase == Phase.Second) {
                foreach (var d in doubles) d.Off(t);
                foreach (var d in regular) d.Off(t);
                foreach (var d in releases) if (!d.Def.Interruptible) d.Tap(t);
                phase = Phase.Idle;
                return;
            }
            if (chordSession) {
                foreach (var d in chords) d.Off(t);
                chordSession = false; phase = Phase.Idle;
                return;
            }
            foreach (var d in regular) d.Off(t);
            foreach (var d in longs) d.Off(t);
            foreach (var d in releases) if (!(d.Def.Interruptible && longFired)) d.Tap(t);
            bool shortPress = doubles.Count > 0 && !longFired && t - pressAt < doubleMs;
            if (pendingHeld) { foreach (var d in deferred) d.Off(t); pendingHeld = false; phase = Phase.Idle; }
            else if (pending) {
                pending = false;
                if (shortPress) { phase = Phase.AwaitSecond; releaseAt = t; pendingTap = true; }
                else { foreach (var d in deferred) d.Tap(t); phase = Phase.Idle; }
            } else if (shortPress) { phase = Phase.AwaitSecond; releaseAt = t; pendingTap = false; }
            else phase = Phase.Idle;
        }

        void Tick(double t) {
            if (phase == Phase.Pressed && !chordSession) {
                for (int i = 0; i < longs.Count; i++)
                    if (!longDone[i] && t - pressAt >= longs[i].Def.LongMs) {
                        longDone[i] = true; longFired = true; longs[i].On(t);
                        pending = false; // the long press claims this press: the deferred regular never fires
                    }
                if (pending && longs.Count == 0 && t - pressAt >= doubleMs) {
                    pending = false; pendingHeld = true;
                    foreach (var d in deferred) d.On(t);
                }
            }
            if (phase == Phase.AwaitSecond && t - releaseAt > doubleMs) ResolveTap(t);
            foreach (var d in All) d.Evaluate(t);
        }

        void ResolveTap(double t) {
            phase = Phase.Idle;
            if (pendingTap) { pendingTap = false; foreach (var d in deferred) d.Tap(t); }
        }

        bool ChordHeld(bool[] held) { foreach (var d in chords) if (held[d.Def.Chord]) return true; return false; }
    }

    // Real frame -> virtual report + events. Unbound buttons pass straight through.
    public sealed class Translator {
        Layout layout = Layout.Passthrough();
        readonly ButtonProcessor[] processors = new ButtonProcessor[Sources.Count];
        readonly List<ushort> keysDown = new List<ushort>();
        readonly Dictionary<ushort, bool> extended = new Dictionary<ushort, bool>();
        readonly bool[] digital = new bool[Sources.Count];
        int mouseDown;
        bool ltDigital, rtDigital;
        public Layout Current { get { return layout; } }

        public void SetLayout(Layout next, List<OutputEvent> events) {
            ReleaseAll(events);
            layout = next ?? Layout.Passthrough();
            for (int i = 0; i < Sources.Count; i++)
                processors[i] = layout.Buttons[i] == null ? null : new ButtonProcessor(layout.Buttons[i], layout.TapMs);
            ltDigital = rtDigital = false;
        }

        // Everything up: used on layout switches, pad loss and shutdown.
        public void ReleaseAll(List<OutputEvent> events) {
            for (int i = keysDown.Count - 1; i >= 0; i--) events.Add(new OutputEvent { Kind = OutputKind.KeyUp, Key = keysDown[i], Extended = Extended(keysDown[i]) });
            keysDown.Clear();
            for (int b = 1; b <= 5; b++) if ((mouseDown & (1 << b)) != 0) events.Add(new OutputEvent { Kind = OutputKind.MouseUp, Value = b });
            mouseDown = 0;
            foreach (var p in processors) if (p != null) p.Reset();
        }

        bool Extended(ushort key) { bool value; return extended.TryGetValue(key, out value) && value; }

        static bool Trigger(byte value, byte threshold, bool was) {
            int release = Math.Max(1, threshold - 10);
            return was ? value >= release : value > threshold;
        }

        public PadReport Update(InputFrame f, double t, List<OutputEvent> events) {
            Array.Copy(f.Pressed, digital, Sources.Count);
            ltDigital = Trigger(f.LT, layout.LtThreshold, ltDigital);
            rtDigital = Trigger(f.RT, layout.RtThreshold, rtDigital);
            digital[Sources.LT] = ltDigital; digital[Sources.RT] = rtDigital;

            var report = new PadReport();
            for (int i = 0; i < Sources.Count; i++) {
                if (processors[i] != null) processors[i].Update(digital[i], digital, t);
                else if (digital[i]) report.Buttons |= Sources.PadBit[i];
            }
            report.LT = layout.Uses(Sources.LT) ? (byte)0 : f.LT;
            report.RT = layout.Uses(Sources.RT) ? (byte)0 : f.RT;
            Stick(layout.LeftStick, f.LX, f.LY, ref report);
            Stick(layout.RightStick, f.RX, f.RY, ref report);

            var wantKeys = new List<ushort>();
            int wantMouse = 0;
            int axes = 0;
            foreach (var p in processors) {
                if (p == null) continue;
                foreach (var d in p.All) {
                    var c = d.Def.Command;
                    bool rising = d.Pressed && !d.WasPressed;
                    switch (c.Kind) {
                        case CommandKind.Pad: if (d.Pressed) report.Buttons |= c.Pad; break;
                        case CommandKind.Axis: if (d.Pressed) axes |= 1 << c.Value; break;
                        case CommandKind.Keys:
                            if (d.Pressed) for (int k = 0; k < c.Keys.Length; k++) {
                                if (!wantKeys.Contains(c.Keys[k])) wantKeys.Add(c.Keys[k]);
                                extended[c.Keys[k]] = c.Extended != null && c.Extended[k];
                            }
                            break;
                        case CommandKind.Mouse: if (d.Pressed) wantMouse |= 1 << c.Value; break;
                        case CommandKind.Wheel: if (rising) events.Add(new OutputEvent { Kind = OutputKind.Wheel, Value = c.Value }); break;
                        case CommandKind.Action: if (rising) events.Add(new OutputEvent { Kind = OutputKind.Action, Action = c.Action }); break;
                    }
                }
            }
            if ((axes & 1) != 0) report.LT = 255;
            if ((axes & 2) != 0) report.RT = 255;
            Digital(axes >> 2, ref report.LX, ref report.LY);
            Digital(axes >> 6, ref report.RX, ref report.RY);

            // Keys: releases in reverse press order, then new presses in command order (Ctrl before C).
            for (int i = keysDown.Count - 1; i >= 0; i--)
                if (!wantKeys.Contains(keysDown[i])) { events.Add(new OutputEvent { Kind = OutputKind.KeyUp, Key = keysDown[i], Extended = Extended(keysDown[i]) }); keysDown.RemoveAt(i); }
            foreach (var key in wantKeys)
                if (!keysDown.Contains(key)) { keysDown.Add(key); events.Add(new OutputEvent { Kind = OutputKind.KeyDown, Key = key, Extended = Extended(key) }); }
            for (int b = 1; b <= 5; b++) {
                int bit = 1 << b;
                if ((wantMouse & bit) != 0 && (mouseDown & bit) == 0) events.Add(new OutputEvent { Kind = OutputKind.MouseDown, Value = b });
                if ((wantMouse & bit) == 0 && (mouseDown & bit) != 0) events.Add(new OutputEvent { Kind = OutputKind.MouseUp, Value = b });
            }
            mouseDown = wantMouse;
            return report;
        }

        static void Stick(StickOut output, short x, short y, ref PadReport r) {
            if (output == StickOut.Left) { r.LX = x; r.LY = y; }
            else if (output == StickOut.Right) { r.RX = x; r.RY = y; }
        }

        // Bits: up, down, left, right. A held direction pins its axis to the edge; opposite directions cancel.
        static void Digital(int bits, ref short x, ref short y) {
            bits &= 15;
            if (bits == 0) return;
            int up = bits & 1, down = (bits >> 1) & 1, left = (bits >> 2) & 1, right = (bits >> 3) & 1;
            if (up != 0 || down != 0) y = (short)((up - down) * 32767);
            if (left != 0 || right != 0) x = (short)((right - left) * 32767);
        }
    }
}
