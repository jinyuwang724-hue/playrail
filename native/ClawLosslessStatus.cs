using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Observe Lossless Scaling's own output windows in the existing elevated helper.
static class ClawLosslessStatus
{
    delegate bool Visit(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visit callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int size);

    static bool Matches(int pid, string exe)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return false;
        try {
            var name = new StringBuilder(32768);
            int length = name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref length)
                && String.Equals(name.ToString(), exe, StringComparison.OrdinalIgnoreCase);
        } finally { CloseHandle(handle); }
    }

    public static Dictionary<string, object> Read(string path, string action)
    {
        string exe = Path.GetFullPath(path);
        if (action != "read" || !String.Equals(Path.GetFileName(exe), "LosslessScaling.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
            throw new ArgumentException("无效的小黄鸭状态请求");
        var instances = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("LosslessScaling"))
            using (process) { if (Matches(process.Id, exe)) instances.Add(process.Id); }
        var result = new Dictionary<string, object> { { "running", instances.Count != 0 }, { "active", false } };
        IntPtr output = IntPtr.Zero;
        int outputPid = 0;
        string outputClass = "";
        if (instances.Count != 0) EnumWindows(delegate(IntPtr window, IntPtr state) {
            int pid;
            GetWindowThreadProcessId(window, out pid);
            if (!instances.Contains(pid) || !IsWindowVisible(window)) return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            string windowClass = name.ToString();
            if (windowClass != "LosslessScaling" && windowClass != "LosslessScalingBlk") return true;
            output = window;
            outputPid = pid;
            outputClass = windowClass;
            return windowClass != "LosslessScaling";
        }, IntPtr.Zero);
        if (outputPid != 0) {
            try {
                using (var process = Process.GetProcessById(outputPid)) {
                    result["outputCreated"] = process.StartTime.ToFileTimeUtc().ToString();
                    result["outputPid"] = outputPid;
                    result["outputWindow"] = output.ToInt64();
                    result["outputClass"] = outputClass;
                    result["active"] = true;
                }
            } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        return result;
    }
}
