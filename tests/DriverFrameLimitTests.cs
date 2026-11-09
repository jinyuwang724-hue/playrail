using System;
using System.Collections.Generic;

class DriverFrameLimitTests {
    sealed class Fake : IntelFrameLimit.IBackend {
        public IntelFrameLimit.Setting State = new IntelFrameLimit.Setting(false, 60);
        public bool RejectReadback;
        public int Writes;
        public IntelFrameLimit.Capabilities Caps { get { return new IntelFrameLimit.Capabilities { Min = 30, Max = 300, Step = 1, Default = 60, LiveChange = true }; } }
        public IntelFrameLimit.Setting Get() { return State; }
        public void Put(IntelFrameLimit.Setting setting) { Writes++; State = setting; if (RejectReadback) { State.Value++; RejectReadback = false; } }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static Dictionary<string, object> Journal(int fps) { return new Dictionary<string, object> {
        { "written", fps }, { "previousEnabled", false }, { "previousValue", 60 }
    }; }
    static void Main() {
        var fake = new Fake(); var limiter = new IntelFrameLimit.Controller(fake);
        Assert((int)limiter.Read()["fpsLimit"] == 0 && fake.Writes == 0, "Reading must never change configuration");
        foreach (int fps in new[] { -1, 1, 29, 301, 360 }) {
            try { limiter.Set(fps); throw new Exception("Accepted unsupported rate"); } catch (ArgumentException) { }
        }
        Assert(fake.Writes == 0, "Unsupported values must be rejected before writes");
        limiter.Set(60); Assert(fake.State.Enabled && fake.State.Value == 60, "Enable arbitrary driver rate");
        limiter.Set(0); Assert(!fake.State.Enabled && fake.State.Value == 60, "Disable preserves the stored driver value");
        limiter.Session("driver_game_set", Journal(40));
        limiter.Session("driver_game_restore", Journal(40));
        Assert(!fake.State.Enabled && fake.State.Value == 60, "Game exit restores both enable and value");
        limiter.Session("driver_game_set", Journal(40)); fake.State.Value = 90;
        Assert((bool)limiter.Session("driver_game_restore", Journal(40))["conflict"] && fake.State.Value == 90, "Restore preserves external changes");
        fake.State = new IntelFrameLimit.Setting(false, 60); fake.RejectReadback = true;
        try { limiter.Set(40); throw new Exception("Accepted a mismatched readback"); } catch (InvalidOperationException) { }
        Assert(!fake.State.Enabled && fake.State.Value == 60, "Failed verification restores original state");
        Console.WriteLine("PASS driver limiter: read-only, capability bounds, disable, session recovery, conflicts, rollback");
    }
}
