using System;
using System.Collections.Generic;
using System.Linq;

class FanServiceTests {
    sealed class Hardware : IFanHardware {
        public double Temp = 65;
        public bool FailApply, BadReadback, KeepThermalBoost;
        public int TemperatureFailures;
        public int Writes, Releases;
        public Dictionary<string, object> State = new Dictionary<string, object> {
            { "cpu", new[] {60,64,68,74,80,84} }, { "gpu", new[] {60,64,68,74,80,84} },
            { "software", false }, { "fullSpeed", false }, { "rpm1", 3000 },
            { "duty1", 60 }, { "duty2", 60 }, { "ecTemperature", 65 }
        };
        public double Temperature() { if (TemperatureFailures-- > 0) throw new Exception("transient sensor failure"); return Temp; }
        public Dictionary<string, object> Read() { return State; }
        public Dictionary<string, object> Apply(int duty, bool full) {
            if (FailApply) { FailApply = false; throw new Exception("apply failed"); }
            Writes++; State = new Dictionary<string, object>(State);
            State["cpu"] = Enumerable.Repeat(BadReadback ? duty + 1 : duty, 6).ToArray();
            State["gpu"] = Enumerable.Repeat(duty, 6).ToArray();
            State["duty1"] = State["duty2"] = KeepThermalBoost ? 94 : duty;
            State["software"] = true; State["fullSpeed"] = full; return State;
        }
        public Dictionary<string, object> Auto(int[] cpu, int[] gpu) {
            Releases++; State = new Dictionary<string, object>(State);
            State["cpu"] = cpu; State["gpu"] = gpu; State["software"] = false; State["fullSpeed"] = false; return State;
        }
    }
    sealed class Store : IFanStore {
        public bool Fail;
        public FanConfig Saved;
        public void Save(FanConfig value) { if (Fail) throw new Exception("disk full"); Saved = FanJson.Copy(value); }
    }
    static void Assert(bool ok, string label) { if (!ok) throw new Exception(label); }
    static void Reject(Action action, string label) { try { action(); } catch { return; } throw new Exception("not rejected: " + label); }
    static FanConfig Config() { return new FanConfig {
        schema = 1, revision = 0, ownerSid = "S-1-5-18",
        factoryCpu = new[] {60,64,68,74,80,84}, factoryGpu = new[] {60,64,68,74,80,84},
        fan = new FanSettings { mode = "balanced", full = false, control = "custom", curves = new Dictionary<string,int[]> {
            {"quiet",new[]{20,25,40,50,63,76}}, {"balanced",new[]{20,30,50,63,76,76}}, {"performance",new[]{30,45,63,76,88,100}}
        }}
    }; }
    static FanSettings Settings(FanEngine engine) { return (FanSettings)engine.Snapshot()["settings"]; }
    public static int Main() {
        var hw = new Hardware(); var store = new Store(); var engine = new FanEngine(Config(), hw, store);
        engine.Tick(0); Assert((int)engine.Snapshot()["target"] == 40, "boot applies saved balanced curve");
        var edit = Settings(engine); edit.mode = "performance";
        engine.Update(edit, 0, 1); Assert((int)engine.Snapshot()["target"] == 54 && store.Saved.revision == 1, "switch immediate and persisted");
        edit = Settings(engine); edit.curves["performance"] = new[] {60,60,60,60,60,60};
        engine.Update(edit, 1, 2); Assert((int)engine.Snapshot()["target"] == 60, "active curve edit immediate");
        int before = hw.Writes; edit = Settings(engine); edit.curves["quiet"][0] = 21;
        engine.Update(edit, 2, 3); Assert(hw.Writes == before && store.Saved.fan.curves["quiet"][0] == 21, "inactive edit saves without changing output");
        Reject(() => engine.Update(edit, 2, 4), "stale revision");
        edit = Settings(engine); edit.curves["performance"][0] = 0;
        Reject(() => engine.Update(edit, 3, 4), "zero duty");
        Assert((long)engine.Snapshot()["revision"] == 3, "invalid edit not persisted");
        edit = Settings(engine); edit.mode = "quiet"; hw.FailApply = true;
        Reject(() => engine.Update(edit, 3, 4), "write failure");
        Assert(Settings(engine).mode == "performance" && (int)engine.Snapshot()["target"] == 60, "old mode survives failed write");
        store.Fail = true;
        Reject(() => engine.Update(edit, 3, 4), "persistence failure"); store.Fail = false;
        Assert(Settings(engine).mode == "performance" && (long)engine.Snapshot()["revision"] == 3 && (int)engine.Snapshot()["target"] == 60, "disk failure rolls output back");
        hw.BadReadback = true;
        Reject(() => engine.Update(edit, 3, 4), "bad readback"); hw.BadReadback = false;
        Assert(!true.Equals(hw.State["software"]), "failed rollback releases Auto");
        engine.Tick(5); Assert(true.Equals(hw.State["software"]), "recovers after write fault");
        hw.Temp = double.NaN; engine.Tick(6);
        Assert(!true.Equals(hw.State["software"]) && ((string)engine.Snapshot()["error"]).Length > 0, "sensor fault releases Auto");
        hw.Temp = 65; engine.Tick(7); Assert((string)engine.Snapshot()["error"] == "", "sensor recovery");
        hw.State["software"] = false; engine.Tick(8); Assert(true.Equals(hw.State["software"]), "EC reset repaired");
        engine.Suspend(); Assert(!true.Equals(hw.State["software"]), "suspend releases Auto");
        engine.Resume(); engine.Tick(9); Assert(true.Equals(hw.State["software"]), "resume reapplies saved mode");
        var restarted = new FanEngine(store.Saved, hw, store); restarted.Tick(10);
        Assert((int)restarted.Snapshot()["target"] == 60 && Settings(restarted).curves["quiet"][0] == 21, "restart uses committed settings");
        edit = Settings(restarted); edit.control = "auto"; restarted.Update(edit, 3, 11);
        Assert(!true.Equals(hw.State["software"]), "manual Auto immediately releases");
        var autoRestart = new FanEngine(store.Saved, hw, store); autoRestart.Tick(12);
        Assert(!true.Equals(hw.State["software"]), "Auto persists across restart");
        edit = Settings(autoRestart); edit.control = "custom"; edit.full = true; autoRestart.Update(edit, 4, 13);
        Assert((int)autoRestart.Snapshot()["target"] == 100 && true.Equals(hw.State["fullSpeed"]), "full speed immediate");
        autoRestart.Stop(); Assert(!true.Equals(hw.State["fullSpeed"]) && !true.Equals(hw.State["software"]), "normal service stop releases Auto");
        var records = new List<Dictionary<string, object>>();
        hw = new Hardware(); hw.TemperatureFailures = 1;
        var monitored = new FanEngine(Config(), hw, new Store(), entry => records.Add(entry));
        monitored.Tick(0);
        Assert(hw.Releases == 0 && (long)monitored.Snapshot()["temperatureRetries"] == 1, "a transient temperature read does not switch to factory Auto");
        hw.State["software"] = false; monitored.Tick(2);
        Assert(records.Any(e => Equals(e["reason"], "external_control_changed")) && true.Equals(hw.State["software"]), "external override recorded and repaired");
        hw.State["rpm1"] = 5800; monitored.Tick(4);
        Assert(records.Any(e => Equals(e["reason"], "rpm_above_profile_expectation") && (int)e["rpm1"] == 5800 && (string)e["mode"] == "balanced"), "overspeed stores actual RPM, mode and target");
        hw.Temp = double.NaN; monitored.Tick(6);
        Assert(hw.Releases > 0 && records.Any(e => Equals(e["reason"], "factory_auto_fallback")), "persistent sensor failure retains factory cooling fail-safe and diagnostic reason");
        var rise = new FanGovernor();
        rise.Update(Config().fan.curves["balanced"], 65, 0);
        Assert(rise.Update(Config().fan.curves["balanced"], 80, 1) == 76, "80 C raises cooling immediately rather than waiting for EC thermal boost");
        hw = new Hardware();
        var cooled = new FanEngine(Config(), hw, new Store());
        cooled.Tick(0);
        hw.Temp = 85; hw.State["ecTemperature"] = 84; hw.KeepThermalBoost = true;
        hw.State["duty1"] = hw.State["duty2"] = 94;
        cooled.Tick(1); int thermalWrites = hw.Writes;
        cooled.Tick(20);
        Assert(true.Equals(cooled.Snapshot()["thermalBoost"]) && hw.Writes == thermalWrites, "hot EC boost is detected and never fought");
        hw.Temp = 80; hw.State["ecTemperature"] = 84; cooled.Tick(21); cooled.Tick(40);
        Assert(hw.Writes == thermalWrites, "CPU cooling alone cannot release a still-hot EC boost");
        hw.State["ecTemperature"] = 79; hw.KeepThermalBoost = false;
        cooled.Tick(41); cooled.Tick(50);
        Assert(hw.Writes == thermalWrites, "both sensors must remain cool for the full ten seconds");
        cooled.Tick(51);
        Assert(hw.Writes == thermalWrites + 1 && (int)hw.State["duty1"] == 76 &&
            (long)cooled.Snapshot()["thermalRecoveryCount"] == 1 && !true.Equals(cooled.Snapshot()["thermalBoost"]),
            "cooled EC output override is released even when table points never changed");
        cooled.Tick(52); Assert(hw.Writes == thermalWrites + 1, "normal live output is not periodically rewritten");
        Console.WriteLine("PASS: boot, switching, active/inactive edits, revision, zero-duty, write/readback/disk rollback, sensor faults, reset, suspend/resume, restart, Auto, full-speed, stop"); return 0;
    }
}
