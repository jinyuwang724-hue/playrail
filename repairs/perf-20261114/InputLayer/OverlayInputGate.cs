namespace ClawInput {
    // Keep output neutral through the close animation and until the dismissing
    // button/stick has been released. The physical pad remains readable by UI.
    public sealed class OverlayInputGate {
        bool waiting;
        public bool Waiting { get { return waiting; } }
        public bool Block(bool held, InputFrame frame) {
            if (held) waiting = true;
            else if (waiting && Neutral(frame)) waiting = false;
            return held || waiting;
        }
        static bool Neutral(InputFrame frame) {
            foreach (bool down in frame.Pressed) if (down) return false;
            return frame.LT < 30 && frame.RT < 30
                && System.Math.Abs((int)frame.LX) < 8000 && System.Math.Abs((int)frame.LY) < 8000
                && System.Math.Abs((int)frame.RX) < 8000 && System.Math.Abs((int)frame.RY) < 8000;
        }
    }
}
