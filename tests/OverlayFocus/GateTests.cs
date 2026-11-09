using System;
using ClawInput;
class GateTests {
    static int checks;
    static void Check(bool value) { if (!value) throw new Exception("Gate check " + (checks + 1)); checks++; }
    static void Main() {
        var gate = new OverlayInputGate(); var frame = new InputFrame();
        Check(!gate.Block(false, frame));
        frame.Pressed[0] = true;
        Check(gate.Block(true, frame));
        Check(gate.Block(false, frame)); // dismissing button never reaches game
        frame.Clear(); Check(!gate.Block(false, frame));
        Check(gate.Block(true, frame)); // no input still holds while overlay is up
        frame.LX = 30000; Check(gate.Block(false, frame));
        frame.LX = 1500; Check(!gate.Block(false, frame)); // tolerate stick drift
        gate.Block(true, frame); frame.RT = 255; Check(gate.Block(false, frame));
        frame.RT = 0; Check(!gate.Block(false, frame));
        Console.WriteLine(checks + " overlay input gate checks passed");
    }
}
