using System;
using System.Linq;

class NativeGpuCapabilitiesTests {
    static void Main() {
        var current = IntelGpu.FrameSyncOptions(0x2d).Select(x => (string)x["id"]).ToArray();
        if (string.Join(",", current) != "app,vsync,smooth,capped") throw new Exception("Current driver's unsupported sync modes must not appear");
        if (IntelGpu.FrameSyncOptions(0x3f).Count != 6) throw new Exception("All supported modes must remain selectable");
        if (IntelGpu.FrameSyncOptions(0x80).Count != 0) throw new Exception("Unknown capability bits must not create choices");
        Console.WriteLine("PASS native GPU capability filtering (3 checks)");
    }
}
