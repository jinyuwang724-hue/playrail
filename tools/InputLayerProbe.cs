using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Stand-in for a game while checking the input layer on the device (docs/gamepad-input-layer.md):
// a foreground window that is NOT in the HidHide whitelist, so it sees exactly what a game sees.
// Logs every XInput slot (vendor / product id and button changes, polled at 1 ms) and the keys its
// window receives, with QueryPerformanceCounter milliseconds (same clock as the engine's timeline).
//   InputLayerProbe.exe <out.json> <seconds>
class InputLayerProbe : Form {
    [StructLayout(LayoutKind.Sequential)] struct Gamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] struct State { public uint Packet; public Gamepad Pad; }
    [StructLayout(LayoutKind.Sequential)] struct Vibration { public ushort Left, Right; }
    [StructLayout(LayoutKind.Sequential)] struct CapsEx { public byte Type, SubType; public ushort Flags; public Gamepad Pad; public Vibration Vibration; public ushort Vendor, Product, Version, Unknown; public uint Unknown2; }
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint GetStateEx(uint index, out State state);
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] static extern uint GetCapabilitiesEx(uint one, uint index, uint flags, out CapsEx caps);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

    readonly List<object> pads = new List<object>(), keys = new List<object>(), slots = new List<object>(), focus = new List<object>();
    readonly string output;
    readonly int seconds;
    volatile bool done;
    static double Ms() { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }

    InputLayerProbe(string output, int seconds) {
        this.output = output; this.seconds = seconds;
        Text = "输入层测试（游戏替身）"; Width = 520; Height = 200; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
        Controls.Clear();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        Activate(); SetForegroundWindow(Handle);
        for (uint i = 0; i < 4; i++) {
            CapsEx c;
            bool ok = GetCapabilitiesEx(1, i, 0, out c) == 0;
            slots.Add(new Dictionary<string, object> { { "slot", i }, { "connected", ok }, { "vendor", ok ? c.Vendor.ToString("X4") : null }, { "product", ok ? c.Product.ToString("X4") : null } });
        }
        new Thread(Poll) { IsBackground = true, Priority = ThreadPriority.Highest }.Start();
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += delegate { lock (focus) focus.Add(new Dictionary<string, object> { { "qpcMs", Math.Round(Ms(), 1) }, { "foreground", GetForegroundWindow() == Handle } }); };
        timer.Start();
        var end = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        end.Tick += delegate { done = true; Save(); Close(); };
        end.Start();
    }

    void Poll() {
        timeBeginPeriod(1);
        var last = new ushort[4]; var lastLt = new byte[4];
        var seen = new bool[4];
        while (!done) {
            for (uint i = 0; i < 4; i++) {
                State s;
                if (GetStateEx(i, out s) != 0) continue;
                if (!seen[i] || s.Pad.Buttons != last[i] || s.Pad.LT != lastLt[i]) {
                    seen[i] = true; last[i] = s.Pad.Buttons; lastLt[i] = s.Pad.LT;
                    lock (pads) pads.Add(new Dictionary<string, object> { { "qpcMs", Math.Round(Ms(), 3) }, { "slot", i }, { "buttons", (int)s.Pad.Buttons }, { "lt", (int)s.Pad.LT } });
                }
            }
            Thread.Sleep(1);
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x100 || m.Msg == 0x101 || m.Msg == 0x104 || m.Msg == 0x105)
            lock (keys) keys.Add(new Dictionary<string, object> { { "qpcMs", Math.Round(Ms(), 3) }, { "vk", m.WParam.ToInt32() }, { "down", m.Msg == 0x100 || m.Msg == 0x104 } });
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { return true; } // keep Tab from moving focus

    void Save() {
        var d = new Dictionary<string, object> { { "process", Process.GetCurrentProcess().ProcessName }, { "slots", slots } };
        lock (pads) d["pads"] = new List<object>(pads);
        lock (keys) d["keys"] = new List<object>(keys);
        lock (focus) d["focus"] = new List<object>(focus);
        File.WriteAllText(output, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
    }

    [STAThread]
    static int Main(string[] args) {
        if (args.Length != 2) { Console.Error.WriteLine("InputLayerProbe <out.json> <seconds>"); return 2; }
        Application.Run(new InputLayerProbe(args[0], Math.Max(1, Math.Min(120, int.Parse(args[1])))));
        return 0;
    }
}
