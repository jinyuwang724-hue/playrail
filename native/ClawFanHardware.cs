using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Standard ACPI EC command transport, using the signed PawnIO module restricted
// to ports 0x62/0x66. Only the two audited live fan outputs are exposed here.
sealed class AcpiFanOutput : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_open(out IntPtr handle);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_close(IntPtr handle);
    IntPtr handle;
    readonly Mutex access = new Mutex(false, @"Global\Access_EC");
    public AcpiFanOutput() {
        string library=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PawnIO","PawnIOLib.dll");
        if(LoadLibraryW(library)==IntPtr.Zero) throw new IOException("PawnIO 风扇通道未就绪");
        byte[] module=File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pawnio","LpcACPIEC.bin"));
        using(var sha=System.Security.Cryptography.SHA256.Create())
            if(BitConverter.ToString(sha.ComputeHash(module)).Replace("-","")!="C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906")
                throw new IOException("EC 风扇模块版本或完整性不匹配");
        if(pawnio_open(out handle)<0) throw new IOException("无法打开 EC 风扇通道");
        if(pawnio_load(handle,module,(UIntPtr)module.Length)<0) { pawnio_close(handle); handle=IntPtr.Zero; throw new IOException("EC 风扇模块加载失败"); }
    }
    byte PortRead(ushort port) {
        var value=new ulong[1]; UIntPtr returned;
        if(pawnio_execute(handle,"ioctl_pio_read",new ulong[]{port},(UIntPtr)1,value,(UIntPtr)1,out returned)<0 || returned.ToUInt64()!=1)
            throw new IOException("EC 风扇状态读取失败");
        return (byte)value[0];
    }
    void PortWrite(ushort port, byte value) {
        UIntPtr returned;
        if(pawnio_execute(handle,"ioctl_pio_write",new ulong[]{port,value},(UIntPtr)2,null,UIntPtr.Zero,out returned)<0)
            throw new IOException("EC 风扇命令写入失败");
    }
    void Wait(bool output) {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(true) {
            byte status=PortRead(0x66);
            if(output ? (status&1)!=0 : (status&2)==0) return;
            if(watch.ElapsedMilliseconds>=100) throw new IOException("EC 风扇命令超时");
            Thread.Sleep(1);
        }
    }
    void Locked(Action action) {
        bool held=false;
        try {
            try { held=access.WaitOne(500); } catch(AbandonedMutexException) { held=true; }
            if(!held) throw new IOException("EC 风扇通道忙");
            action();
        } finally { if(held) access.ReleaseMutex(); }
    }
    void Write(byte address, byte value) {
        Wait(false); PortWrite(0x66,0x81); Wait(false); PortWrite(0x62,address); Wait(false); PortWrite(0x62,value); Wait(false);
    }
    public void Pulse(int duty) {
        if(duty<1 || duty>100) throw new ArgumentException("风扇输出需要在 1–100 范围内");
        Locked(()=>{ Write(0x71,(byte)duty); Write(0x89,(byte)duty); });
    }
    public void Dispose() { if(handle!=IntPtr.Zero){pawnio_close(handle);handle=IntPtr.Zero;} access.Dispose(); }
}

static class MsiFan {
    static bool testActive;
    static string detectedModel, detectedFirmware;
    internal static bool ServiceOwner = false;
    internal static readonly string ServiceConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClawToolbox", "FanService", "service.json");
    static void RequireWriter() {
        if (!ServiceOwner && File.Exists(ServiceConfigPath))
            throw new InvalidOperationException("风扇已由独立服务管理，请通过服务切换或修改档位");
    }
    const string Instance = @"MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'";

    static byte[] Call(string method, byte[] data) {
        using (var obj = new System.Management.ManagementObject(@"root\WMI", Instance, null)) {
            using (var pkgClass = new System.Management.ManagementClass(@"root\WMI", "Package_32", null))
            using (var pkg = pkgClass.CreateInstance()) {
                var bytes = new byte[32];
                Array.Copy(data, bytes, Math.Min(32, data.Length));
                pkg["Bytes"] = bytes;
                var input = obj.GetMethodParameters(method);
                if (input != null) input["Data"] = pkg;
                var output = obj.InvokeMethod(method, input, null);
                var result = output == null ? null : output["Data"] as System.Management.ManagementBaseObject;
                var raw = result == null ? null : result["Bytes"] as byte[];
                if (raw == null || raw.Length < 2) throw new InvalidOperationException("MSI 风扇接口没有返回数据：" + method);
                if (raw[0] != 1) throw new InvalidOperationException("MSI 风扇接口返回失败：" + method);
                var payload = new byte[raw.Length - 1];
                Array.Copy(raw, 1, payload, 0, payload.Length);
                return payload;
            }
        }
    }

    static int readRetries;
    static byte[] Get(string method, byte index) {
        // A short WMI read failure should not immediately hand a quiet curve back to the factory governor.
        // Only reads are retried; writes retain their existing transaction/rollback semantics.
        for (int attempt = 0; ; attempt++) {
            try { return Call(method, new[] { index }); }
            catch { if (attempt >= 2) throw; readRetries++; System.Threading.Thread.Sleep(25); }
        }
    }

    static int[] Duties(byte[] table) { var d = new int[6]; for (int i = 0; i < 6; i++) d[i] = table[1 + i]; return d; }

    static int[] Temps(byte[] axis) { return new[] { (int)axis[0], axis[3], axis[4], axis[5], axis[6], axis[7] }; }

    static int Rpm(byte a, byte b) { int d = Math.Abs(a - b); return d == 0 ? 0 : (int)Math.Round(480000.0 / d); }

    public static Dictionary<string, object> Probe() {
        var result = new Dictionary<string, object>();
        foreach (string method in new[] { "Get_Temperature", "Get_Thermal", "Get_Fan" }) {
            for (byte index = 0; index <= 2; index++) {
                try { result[method + "_" + index] = Array.ConvertAll(Get(method, index), b => (int)b); }
                catch (Exception error) { result[method + "_" + index] = error.Message; }
            }
        }
        result["AP1"] = Array.ConvertAll(Get("Get_AP", 1), b => (int)b);
        result["scenario"] = Array.ConvertAll(Get("Get_Data", 0xD2), b => (int)b);
        result["model"] = Model();
        result["firmware"] = Firmware();
        foreach (byte address in new byte[] { 0xDD, 0xF4, 0x33, 0x34, 0xDC })
            result["EC_" + address.ToString("X2")] = EcRead(address);
        return result;
    }

    static string Model() {
        using (var search = new System.Management.ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
        using (var rows = search.Get()) {
            foreach (System.Management.ManagementObject row in rows) return Convert.ToString(row["Model"]);
        }
        return "";
    }

    static string Firmware() {
        string text = System.Text.Encoding.ASCII.GetString(Get("Get_EC", 0)).Trim('\0');
        var version = System.Text.RegularExpressions.Regex.Match(text, @"[0-9A-Z]{4}EMS[0-9]\.[0-9A-F]{3}");
        return version.Success ? version.Value : text;
    }

    public static Dictionary<string, object> Read() {
        var rpm = Get("Get_Fan", 0);
        byte[] cpu = Get("Get_Fan", 1), gpu = Get("Get_Fan", 2);
        byte[] cpuTemps = Get("Get_Temperature", 1), gpuTemps = Get("Get_Temperature", 2);
        byte ap = Get("Get_AP", 1)[0], full = Get("Get_Data", 0x98)[0];
        if (detectedModel == null) {
            string model = Model(), firmware = Firmware();
            detectedFirmware = firmware;
            detectedModel = model;
        }
        int? auxiliaryLimit = AuxiliaryCoolingLimit(detectedModel, detectedFirmware);
        var state = new Dictionary<string, object> {
            { "rpm1", Rpm(rpm[0], rpm[1]) }, { "rpm2", Rpm(rpm[2], rpm[3]) },
            { "duty1", (int)cpu[0] }, { "duty2", (int)gpu[0] },
            { "cpu", Duties(cpu) }, { "gpu", Duties(gpu) },
            { "cpuTable", Array.ConvertAll(cpu, b => (int)b) }, { "gpuTable", Array.ConvertAll(gpu, b => (int)b) },
            { "ecTemperature", (int)Get("Get_Temperature", 0)[0] },
            { "apData", Array.ConvertAll(Get("Get_AP", 1), b => (int)b) },
            { "cpuTemps", Temps(cpuTemps) }, { "gpuTemps", Temps(gpuTemps) },
            { "cpuTemperatureData", Array.ConvertAll(cpuTemps, b => (int)b) },
            { "gpuTemperatureData", Array.ConvertAll(gpuTemps, b => (int)b) },
            { "software", (ap & 0x80) != 0 }, { "fullSpeed", (full & 0x80) != 0 },
            { "scenario", (int)Get("Get_Data", 0xD2)[0] },
            { "readRetries", readRetries },
            { "thermalRecoverySupported", auxiliaryLimit.HasValue },
        };
        if (auxiliaryLimit.HasValue) {
            state["auxiliaryTemperature"] = EcRead(0xF4);
            state["auxiliaryRecoveryLimit"] = auxiliaryLimit.Value;
        }
        return state;
    }

    // Audited against the original 1T91EMS1.103 ROM: bank 3, 47AD -> 5976
    // compares EC F4 with F38A + level. At level 7, F391 is 60 C. CPU cooling
    // alone cannot release this level. Other firmware/model combinations are unverified.
    internal static int? AuxiliaryCoolingLimit(string model, string firmware) {
        return model != null && model.IndexOf("CG3EM", StringComparison.OrdinalIgnoreCase) >= 0 &&
            firmware == "1T91EMS1.103" ? (int?)60 : null;
    }

    internal static bool CooledHardware(Dictionary<string, object> sample) {
        return sample.ContainsKey("thermalRecoverySupported") && true.Equals(sample["thermalRecoverySupported"])
            && sample.ContainsKey("ecTemperature") && Convert.ToInt32(sample["ecTemperature"]) > 0
            && Convert.ToInt32(sample["ecTemperature"]) <= 80
            && sample.ContainsKey("auxiliaryTemperature") && sample.ContainsKey("auxiliaryRecoveryLimit")
            && Convert.ToInt32(sample["auxiliaryRecoveryLimit"]) == 60
            && Convert.ToInt32(sample["auxiliaryTemperature"]) > 0
            && Convert.ToInt32(sample["auxiliaryTemperature"]) <= 60;
    }

    internal static byte[] TablePayload(byte block, byte[] current, int[] duties, int? outputLimit = null) {
        if (duties == null || duties.Length != 6) throw new ArgumentException("风扇曲线需要 6 个点");
        var payload = new byte[32];
        payload[0] = block;
        Array.Copy(current, 0, payload, 1, Math.Min(31, current.Length));
        // Preserve the live feedback and unknown bytes. A service-owned RPM ceiling also
        // covers the eighth duty, which the EC selects above the six UI curve points.
        for (int i = 0; i < 6; i++) payload[2 + i] = (byte)Math.Max(0, Math.Min(150, duties[i]));
        if (outputLimit.HasValue) {
            if (outputLimit.Value < 1 || outputLimit.Value > 150) throw new ArgumentException("最高风量档无效");
            payload[8] = (byte)outputLimit.Value;
        }
        return payload;
    }
    static void WriteTable(byte block, int[] duties, int? outputLimit = null) {
        var payload = TablePayload(block, Get("Get_Fan", block), duties, outputLimit);
        Call("Set_Fan", payload);
        var table = Get("Get_Fan", block);
        var check = Duties(table);
        for (int i = 0; i < 6; i++)
            if (check[i] != payload[2 + i]) throw new InvalidOperationException("风扇曲线写入后回读不一致");
        if (outputLimit.HasValue && table[7] != outputLimit.Value)
            throw new InvalidOperationException("最高风量档写入后回读不一致");
    }

    /// <summary>One EC byte through MSI's WMI reader, read-only (the 系统 page's charge-limit reading).</summary>
    internal static int EcRead(byte index) { return Get("Get_Data", index)[0]; }

    /// <summary>One EC byte through MSI's WMI writer, read back afterwards (the charge limit at 0xD7, as ClawTweaks writes it).</summary>
    internal static int EcWrite(byte index, byte value) {
        Call("Set_Data", new[] { index, value });
        int check = EcRead(index);
        if (check != value) throw new InvalidOperationException(string.Format("固件回读不一致：写 {0:X2}，读到 {1:X2}", value, check));
        return check;
    }

    // The audited CG3EM runtime outputs are live fields: firmware can change them
    // between a write and a read. Judge sustained control from output/RPM samples,
    // rather than treating an instantaneous mismatch as a transport failure.
    internal static void WriteCurrentOutputs(int duty) {
        RequireWriter();
        if (duty < 1 || duty > 100) throw new ArgumentException("风扇输出需要在 1–100 范围内");
        if (detectedModel == null) Read();
        if (!AuxiliaryCoolingLimit(detectedModel, detectedFirmware).HasValue)
            throw new InvalidOperationException("直接风扇输出仅支持已核对的 CG3EM 固件");
        Call("Set_Data", new byte[] { 0x71, (byte)duty });
        Call("Set_Data", new byte[] { 0x89, (byte)duty });
    }

    static void SetBit(byte index, bool on, string getter) {
        byte cur = Get(getter, getter == "Get_AP" ? (byte)1 : index)[0];
        byte next = on ? (byte)(cur | 0x80) : (byte)(cur & 0x7F);
        if (next != cur) Call("Set_Data", new[] { index, next });
    }

    internal static void BeginDirect(int[] cpu, int[] gpu) {
        RequireWriter();
        var before=Read();
        if(!true.Equals(before["thermalRecoverySupported"])) throw new InvalidOperationException("直接输出未验证此机型和固件");
        Restore(cpu,gpu); // Remove the previous flat table/ceiling before changing actuators.
        SetBit(0xD4,true,"Get_AP");
    }
    internal static void DirectFullSpeed(bool full) { RequireWriter(); SetBit(0x98,full,"Get_Data"); }

    // Diagnostic only: compare the direct Set_AP selector with the old Set_Data alias,
    // and test the seven-duty interpretation without changing temperature registers.
    public static Dictionary<string, object> TestApply(int[] cpu, int[] gpu, bool custom, string transport) {
        RequireWriter();
        if (Model().IndexOf("CG3EM", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("风扇固件测试仅支持已核对的 CG3EM 机型");
        if (transport != "set_ap" && transport != "legacy") throw new ArgumentException("未知风扇控制接口");
        foreach (var duties in new[] { cpu, gpu }) {
            if (duties == null || duties.Length != 7) throw new ArgumentException("测试表需要 7 个风量值");
            foreach (int duty in duties) if (duty < 0 || duty > 150) throw new ArgumentException("风量超出范围");
        }
        var before = Read();
        testActive = true;
        try {
            WriteSeven(1, cpu); WriteSeven(2, gpu);
            SetCustom(custom, transport);
            SetBit(0x98, false, "Get_Data");
            var state = Read();
            if ((bool)state["software"] != custom || (bool)state["fullSpeed"])
                throw new InvalidOperationException("测试控制位回读不一致");
            return state;
        } catch {
            WriteSeven(1, FirstSeven((int[])before["cpuTable"]));
            WriteSeven(2, FirstSeven((int[])before["gpuTable"]));
            SetCustom((bool)before["software"], transport);
            SetBit(0x98, (bool)before["fullSpeed"], "Get_Data");
            throw;
        }
    }

    public static void EndTest(bool release) {
        if (release && testActive) {
            SetCustom(false, "set_ap");
            SetBit(0x98, false, "Get_Data");
        }
        testActive = false;
    }

    static int[] FirstSeven(int[] table) { var values = new int[7]; Array.Copy(table, values, 7); return values; }

    static void WriteSeven(byte block, int[] values) {
        var current = Get("Get_Fan", block);
        var payload = new byte[32]; payload[0] = block;
        Array.Copy(current, 0, payload, 1, Math.Min(31, current.Length));
        for (int i = 0; i < 7; i++) payload[i + 1] = (byte)values[i];
        Call("Set_Fan", payload);
        var check = Get("Get_Fan", block);
        for (int i = 0; i < 7; i++) if (check[i] != values[i])
            throw new InvalidOperationException("测试曲线逐点回读不一致");
    }

    static void SetCustom(bool enabled, string transport) {
        if (transport == "legacy") { SetBit(0xD4, enabled, "Get_AP"); return; }
        var current = Get("Get_AP", 1);
        var payload = new byte[32]; payload[0] = 1;
        Array.Copy(current, 0, payload, 1, Math.Min(31, current.Length));
        payload[1] = enabled ? (byte)(payload[1] | 0x80) : (byte)(payload[1] & 0x7F);
        Call("Set_AP", payload);
    }

    public static Dictionary<string, object> Apply(int[] cpu, int[] gpu, bool full, int? outputLimit = null) {
        RequireWriter();
        foreach (var duties in new[] { cpu, gpu }) {
            if (duties == null || duties.Length != 6) throw new ArgumentException("风扇曲线需要 6 个点");
            foreach (int duty in duties) if (duty < 0 || duty > 150) throw new ArgumentException("风扇风量超出范围");
        }
        var before = Read();
        if (outputLimit.HasValue && (!true.Equals(before["thermalRecoverySupported"]) || full ||
            outputLimit.Value < 65 || outputLimit.Value > 76 ||
            Array.Exists(cpu, d => d > outputLimit.Value) || Array.Exists(gpu, d => d > outputLimit.Value)))
            throw new InvalidOperationException("当前机型、风量或全速状态不支持转速上限");
        try {
            WriteTable(1, cpu, outputLimit);
            WriteTable(2, gpu, outputLimit);
            SetBit(0xD4, true, "Get_AP"); // enable custom table control
            SetBit(0x98, full, "Get_Data");
            var state = Read();
            if (!(bool)state["software"] || (bool)state["fullSpeed"] != full)
                throw new InvalidOperationException("EC 风扇模式回读不一致");
            return state;
        } catch (Exception error) {
            try {
                WriteTable(1, (int[])before["cpu"], ((int[])before["cpuTable"])[7]);
                WriteTable(2, (int[])before["gpu"], ((int[])before["gpuTable"])[7]);
                SetBit(0xD4, (bool)before["software"], "Get_AP");
                SetBit(0x98, (bool)before["fullSpeed"], "Get_Data");
            } catch (Exception rollback) {
                throw new InvalidOperationException("风扇写入失败：" + error.Message + "；恢复失败：" + rollback.Message);
            }
            throw;
        }
    }

    public static Dictionary<string, object> Restore(int[] cpu, int[] gpu) {
        RequireWriter();
        SetBit(0x98, false, "Get_Data");
        SetBit(0xD4, false, "Get_AP");  // hand back to the firmware curve
        // This exact firmware's factory highest duty is 94. Restore it when returning
        // to Auto, including after a service crash left a limited table in EC RAM.
        int? factoryLimit = AuxiliaryCoolingLimit(detectedModel, detectedFirmware).HasValue ? (int?)94 : null;
        if (cpu != null) WriteTable(1, cpu, factoryLimit);
        if (gpu != null) WriteTable(2, gpu, factoryLimit);
        return Read();
    }

    // Service-only cooled recovery. Never force the live output or clear protection bits.
    public static Dictionary<string, object> RecoverCustom(int duty, Func<double> temperature) {
        RequireWriter();
        if (duty < 1 || duty > 76) throw new ArgumentException("冷却恢复只适用于非零、最高 76% 的曲线");
        var before = Read();
        double cpu = temperature();
        if (double.IsNaN(cpu) || double.IsInfinity(cpu) || cpu <= 0) throw new InvalidOperationException("恢复时 CPU 温度无效");
        if (cpu > 80 || !CooledHardware(before) || true.Equals(before["fullSpeed"])) {
            before["recoveryDeferred"] = true; return before;
        }
        int[] flat = { duty, duty, duty, duty, duty, duty };
        var result = Apply(flat, flat, false);
        // The output register can trail the table write; observe without repeatedly writing.
        for (int i = 0; i < 6; i++) {
            if (Math.Abs(Convert.ToInt32(result["duty1"]) - duty) <= 2 && Math.Abs(Convert.ToInt32(result["duty2"]) - duty) <= 2) break;
            System.Threading.Thread.Sleep(100); result = Read();
            if (Convert.ToInt32(result["ecTemperature"]) > 80) break;
        }
        return result;
    }
}

