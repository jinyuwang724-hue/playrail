using System;
using System.Collections.Generic;
using System.Linq;

class FanServiceTests {
    sealed class Hardware : IFanHardware {
        public double Temp = 65;
        public bool FailApply, BadReadback, KeepThermalBoost;
        public bool RefuseRecovery, DeferRecovery, FailRecovery, MissingRecoveryFeedback;
        public int RecoveryCalls;
        public int TemperatureFailures;
        public int Writes, Releases;
        public int? LastOutputLimit;
        public Dictionary<string, object> State = new Dictionary<string, object> {
            { "cpu", new[] {60,64,68,74,80,84} }, { "gpu", new[] {60,64,68,74,80,84} },
            { "software", false }, { "fullSpeed", false }, { "rpm1", 3000 },
            { "duty1", 60 }, { "duty2", 60 }, { "ecTemperature", 65 },
            { "auxiliaryTemperature", 55 }, { "auxiliaryRecoveryLimit", 60 }, { "thermalRecoverySupported", true }
        };
        public double Temperature() { if (TemperatureFailures-- > 0) throw new Exception("transient sensor failure"); return Temp; }
        public Dictionary<string, object> Read() { return State; }
        static int[] Table(int duty, int limit) { var values = Enumerable.Repeat(duty, 8).ToArray(); values[7] = limit; return values; }
        public Dictionary<string, object> Apply(int duty, bool full, int? outputLimit = null) {
            if (FailApply) { FailApply = false; throw new Exception("apply failed"); }
            Writes++; State = new Dictionary<string, object>(State);
            State["cpu"] = Enumerable.Repeat(BadReadback ? duty + 1 : duty, 6).ToArray();
            State["gpu"] = Enumerable.Repeat(duty, 6).ToArray();
            State["cpuTable"] = State["gpuTable"] = Table(duty, outputLimit ?? 94);
            LastOutputLimit = outputLimit;
            State["duty1"] = State["duty2"] = KeepThermalBoost ? (outputLimit ?? 94) : duty;
            State["software"] = true; State["fullSpeed"] = full; return State;
        }
        public Dictionary<string, object> Auto(int[] cpu, int[] gpu) {
            Releases++; State = new Dictionary<string, object>(State);
            State["cpu"] = cpu; State["gpu"] = gpu; State["software"] = false; State["fullSpeed"] = false; return State;
        }
        public Dictionary<string, object> Recover(int duty) {
            RecoveryCalls++;
            if (FailRecovery) throw new Exception("reacquire failed");
            if (DeferRecovery) { var deferred = new Dictionary<string, object>(State); deferred["recoveryDeferred"] = true; return deferred; }
            KeepThermalBoost = RefuseRecovery;
            var result = Apply(duty, false);
            if (MissingRecoveryFeedback) result.Remove("duty2");
            return result;
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
        try { return Run(); }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }
    static int Run() {
        var originalTable = new byte[31]; originalTable[0] = 94; originalTable[7] = 94; originalTable[8] = 37;
        int[] recoveryCurve = {58,58,58,58,58,58};
        var recoveredPayload = MsiFan.TablePayload(2, originalTable, recoveryCurve);
        Assert(recoveredPayload[1] == 94 && recoveredPayload[0] == 2 && recoveredPayload[8] == 94 && recoveredPayload[9] == 37,
            "table updates preserve live output, protection level and reserved bytes");
        var limitedPayload = MsiFan.TablePayload(1, originalTable, recoveryCurve, 74);
        Assert(limitedPayload[1] == 94 && limitedPayload[8] == 74 && limitedPayload[9] == 37,
            "RPM ceiling covers the actual highest EC duty, preserving live feedback and unknown bytes");
        Reject(() => MsiFan.TablePayload(1, originalTable, recoveryCurve, 0), "invalid highest duty");
        Assert(MsiFan.AuxiliaryCoolingLimit("Claw 8 EX AI+ CG3EM", "1T91EMS1.103") == 60, "audited firmware auxiliary condition");
        Assert(!MsiFan.AuxiliaryCoolingLimit("other device", "1T91EMS1.103").HasValue &&
            !MsiFan.AuxiliaryCoolingLimit("CG3EM", "1T91EMS1.102").HasValue, "unknown firmware/model cannot use audited recovery");
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
        Assert(rise.Update(Config().fan.curves["balanced"], 80, 1) == 40 && rise.Smoothed < 75, "brief spikes retain the existing smoothed governor");
        for (int i = 2; i <= 35; i++) rise.Update(Config().fan.curves["balanced"], 80, i);
        Assert(rise.Duty > 40 && rise.Duty <= 76, "sustained warmth raises the smoothed curve within the cap");
        Assert(rise.Update(Config().fan.curves["balanced"], 93, 36) == 76, "genuinely hot readings raise cooling immediately within the cap");
        hw = new Hardware();
        var fixedCurve = Config(); fixedCurve.fan.curves["balanced"] = new[] {60,60,60,60,60,60};
        var cooled = new FanEngine(fixedCurve, hw, new Store());
        cooled.Tick(0);
        hw.Temp = 85; hw.State["ecTemperature"] = 84; hw.KeepThermalBoost = true;
        hw.State["duty1"] = hw.State["duty2"] = 94;
        cooled.Tick(1); int thermalWrites = hw.Writes;
        cooled.Tick(20);
        Assert(true.Equals(cooled.Snapshot()["thermalBoost"]) && hw.Writes == thermalWrites, "hot EC boost is detected and never fought");
        hw.Temp = 80; hw.State["ecTemperature"] = 84; cooled.Tick(21); cooled.Tick(40);
        Assert(hw.Writes == thermalWrites, "CPU cooling alone cannot release a still-hot EC boost");
        hw.State["ecTemperature"] = 79;
        cooled.Tick(41); cooled.Tick(50);
        Assert(hw.Writes == thermalWrites, "both sensors must remain cool for the full ten seconds");
        cooled.Tick(51);
        Assert(hw.RecoveryCalls == 1 && (int)hw.State["duty1"] == 60 && (long)cooled.Snapshot()["thermalRecoveryCount"] == 0,
            "cooled recovery uses reacquisition but is not counted before sustained feedback");
        cooled.Tick(53); Assert((long)cooled.Snapshot()["thermalRecoveryCount"] == 0, "two seconds is not confirmation");
        cooled.Tick(54);
        Assert((long)cooled.Snapshot()["thermalRecoveryCount"] == 1 && !true.Equals(cooled.Snapshot()["thermalBoost"]), "three seconds of both actual outputs confirms recovery");
        cooled.Tick(55); Assert(hw.RecoveryCalls == 1 && hw.Writes == thermalWrites + 1, "normal live output is not periodically rewritten");

        hw = new Hardware { KeepThermalBoost = true, RefuseRecovery = true };
        records = new List<Dictionary<string, object>>();
        var stuck = new FanEngine(fixedCurve, hw, new Store(), entry => records.Add(entry));
        stuck.Tick(0); stuck.Tick(10);
        Assert(hw.RecoveryCalls == 1 && (long)stuck.Snapshot()["thermalRecoveryCount"] == 0 && (long)stuck.Snapshot()["thermalRecoveryFailures"] == 1,
            "writing matching tables while output stays at 94 is a failed recovery, never success");
        Assert(((string)stuck.Snapshot()["error"]).Contains("未确认") && records.Any(r => Equals(r["reason"], "thermal_recovery_unconfirmed")), "unconfirmed output remains visible in status and telemetry");
        stuck.Tick(25); stuck.Tick(69); Assert(hw.RecoveryCalls == 1, "failed reacquisition backs off for sixty seconds");
        stuck.Tick(70); Assert(hw.RecoveryCalls == 2 && (long)stuck.Snapshot()["thermalRecoveryCount"] == 0, "retry still cannot claim success on a stuck EC");
        stuck.Tick(71); hw.RefuseRecovery = false; stuck.Tick(130); stuck.Tick(133);
        Assert((long)stuck.Snapshot()["thermalRecoveryCount"] == 1 && (string)stuck.Snapshot()["error"] == "", "later sustained recovery clears the warning");

        hw = new Hardware { KeepThermalBoost = true };
        var relapsed = new FanEngine(fixedCurve, hw, new Store()); relapsed.Tick(0); relapsed.Tick(10);
        hw.KeepThermalBoost = true; hw.State["duty1"] = hw.State["duty2"] = 94; relapsed.Tick(11);
        Assert((long)relapsed.Snapshot()["thermalRecoveryCount"] == 0 && (long)relapsed.Snapshot()["thermalRecoveryFailures"] == 1,
            "an EC that reasserts boost after the first read is not counted as recovered");

        hw = new Hardware { KeepThermalBoost = true, DeferRecovery = true };
        var deferredEngine = new FanEngine(fixedCurve, hw, new Store()); deferredEngine.Tick(0); deferredEngine.Tick(10);
        Assert((long)deferredEngine.Snapshot()["thermalRecoveryCount"] == 0 && (string)deferredEngine.Snapshot()["thermalRecoveryStatus"] == "temperature_rebounded", "last-moment heating defers reacquisition");

        hw = new Hardware { KeepThermalBoost = true, FailRecovery = true };
        var failedEngine = new FanEngine(fixedCurve, hw, new Store()); failedEngine.Tick(0); failedEngine.Tick(10);
        Assert(hw.Releases > 0 && (long)failedEngine.Snapshot()["thermalRecoveryCount"] == 0, "reacquisition I/O errors retain factory cooling fail-safe");

        hw = new Hardware { KeepThermalBoost = true, MissingRecoveryFeedback = true };
        var missingFeedback = new FanEngine(fixedCurve, hw, new Store()); missingFeedback.Tick(0); missingFeedback.Tick(10);
        Assert((long)missingFeedback.Snapshot()["thermalRecoveryCount"] == 0 && (long)missingFeedback.Snapshot()["thermalRecoveryFailures"] == 1, "both real output readings are required");
        hw = new Hardware { KeepThermalBoost = true };
        hw.State["auxiliaryTemperature"] = 67;
        var auxiliaryHot = new FanEngine(fixedCurve, hw, new Store()); auxiliaryHot.Tick(0); auxiliaryHot.Tick(20);
        Assert(hw.RecoveryCalls == 0 && (string)auxiliaryHot.Snapshot()["thermalRecoveryStatus"] == "firmware_cooling", "75 C CPU alone cannot release boost with a 67 C auxiliary sensor");
        hw.State["auxiliaryTemperature"] = 60; auxiliaryHot.Tick(21); auxiliaryHot.Tick(30);
        Assert(hw.RecoveryCalls == 0, "auxiliary cooling starts a new ten-second window");
        auxiliaryHot.Tick(31); auxiliaryHot.Tick(34);
        Assert(hw.RecoveryCalls == 1 && (long)auxiliaryHot.Snapshot()["thermalRecoveryCount"] == 1, "all cooling conditions and sustained outputs confirm recovery");
        foreach (int invalid in new[] {0,61,255}) {
            hw = new Hardware { KeepThermalBoost = true }; hw.State["auxiliaryTemperature"] = invalid;
            var guard = new FanEngine(fixedCurve, hw, new Store()); guard.Tick(0); guard.Tick(20);
            Assert(hw.RecoveryCalls == 0, "invalid/hot auxiliary input cannot trigger recovery");
        }
        hw = new Hardware { KeepThermalBoost = true }; hw.State.Remove("auxiliaryTemperature");
        var missingAux = new FanEngine(fixedCurve, hw, new Store()); missingAux.Tick(0); missingAux.Tick(20);
        Assert(hw.RecoveryCalls == 0, "missing auxiliary sensor cannot trigger recovery");
        var cappedConfig = Config(); cappedConfig.rpmLimit = 4700;
        hw = new Hardware { Temp = 83, KeepThermalBoost = true }; hw.State["ecTemperature"] = 87;
        var capped = new FanEngine(cappedConfig, hw, new Store()); capped.Tick(0);
        Assert(hw.LastOutputLimit == 74 && (int)capped.Snapshot()["target"] == 74 &&
            ((int[])hw.State["cpuTable"])[7] == 74 && (int)hw.State["duty1"] == 74,
            "4700 RPM policy caps all seven EC curve levels, including a hot latched level");
        hw.State["rpm1"] = 4800; hw.State["rpm2"] = 4752;
        capped.Tick(14); Assert(hw.LastOutputLimit == 74, "initial spin-down is allowed before feedback trimming");
        capped.Tick(15); capped.Tick(18);
        Assert(hw.LastOutputLimit == 73 && (int)capped.Snapshot()["target"] == 73, "persistent real overspeed trims the highest duty");
        hw.State["rpm1"] = 4550; hw.State["rpm2"] = 4550; capped.Tick(19);
        Assert((string)capped.Snapshot()["rpmCeilingStatus"] == "active", "actual RPM feedback confirms the cap");
        edit = Settings(capped); edit.full = true; capped.Update(edit, 0, 20);
        Assert((int)capped.Snapshot()["target"] == 100 && true.Equals(hw.State["fullSpeed"]) && hw.LastOutputLimit == 94 &&
            ((int[])hw.State["cpuTable"])[7] == 94, "manual full speed restores the factory top duty");
        edit = Settings(capped); edit.full = false; edit.mode = "performance"; capped.Update(edit, 1, 20.2);
        Assert(((int[])hw.State["cpuTable"])[7] == 94 && (int)capped.Snapshot()["target"] > 74 &&
            (string)capped.Snapshot()["rpmCeilingStatus"] == "disabled", "performance mode is not capped");
        hw.LastOutputLimit = null; capped.Tick(20.4);
        Assert(hw.LastOutputLimit == null, "factory top duty is written once, not every tick");
        edit = Settings(capped); edit.mode = "balanced"; capped.Update(edit, 2, 20.6);
        Assert(hw.LastOutputLimit == 73 && ((int[])hw.State["cpuTable"])[7] == 73, "returning to 均衡 re-applies the ceiling");
        hw.Temp = 95; capped.Tick(21);
        Assert(!true.Equals(hw.State["software"]) && true.Equals(capped.Snapshot()["rpmCeilingThermalOverride"]), "exceptional heat returns factory cooling without changing temperature trips");
        hw.Temp = 80; hw.State["ecTemperature"] = 79; capped.Tick(22); capped.Tick(51);
        Assert(!true.Equals(hw.State["software"]), "thermal override waits for thirty seconds of cooling");
        capped.Tick(52); Assert(true.Equals(hw.State["software"]) && !true.Equals(capped.Snapshot()["rpmCeilingThermalOverride"]), "ceiling resumes after sustained safe cooling");
        capped.Stop(); Assert(!true.Equals(hw.State["software"]), "stopping the service relinquishes the limited curve");
        var badLimit = Config(); badLimit.rpmLimit = 470; Reject(() => badLimit.Checked(), "470 RPM is not a supported limit");
        Console.WriteLine("PASS: governor, transactions, faults, recovery confirmation, sensor guards, 4700 RPM ceiling, highest EC level, real-RPM trim, full-speed cap and thermal override"); return 0;
    }
}
