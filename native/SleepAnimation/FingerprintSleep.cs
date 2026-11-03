using System.Runtime.InteropServices;
using System.Text;

namespace ClawSleepAnimation;

internal static class FingerprintSleep
{
    private const string Class = "{53D29EF7-377C-4D14-864B-EB3A85769359}";
    private const uint PresentClass = 0x300;
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, uint size, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Disable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Enable_DevNode(uint node, uint flags);
    private static void Check(uint result) { if (result != 0) throw new InvalidOperationException($"指纹设备操作失败（{result:X}）"); }
    internal static bool Physical(string id) => !string.IsNullOrWhiteSpace(id) &&
        !id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) && !id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase);
    private static string[] Devices()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Check(CM_Get_Device_ID_List_SizeW(out uint size, Class, PresentClass));
            if (size is < 2 or > 65536) return [];
            char[] data = new char[size];
            uint result = CM_Get_Device_ID_ListW(Class, data, size, PresentClass);
            if (result == 0x1A) continue; // CR_BUFFER_SMALL: enumeration changed.
            Check(result); return new string(data).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(Physical).ToArray();
        }
        throw new InvalidOperationException("指纹设备列表正在变化");
    }
    private static (uint Node, uint Status, uint Problem) Inspect(string id)
    {
        Check(CM_Locate_DevNodeW(out uint node, id, 0));
        var actual = new StringBuilder(4096);
        Check(CM_Get_Device_IDW(node, actual, (uint)actual.Capacity, 0));
        if (!actual.ToString().Equals(id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("指纹设备标识已变化");
        Check(CM_Get_DevNode_Status(out uint status, out uint problem, node, 0));
        return (node, status, problem);
    }
    public static string? Candidate()
    {
        string[] ids = Devices();
        if (ids.Length != 1) return null;
        var device = Inspect(ids[0]);
        return device.Problem == 0 && (device.Status & 8) != 0 ? ids[0] : null;
    }
    public static void Disable(string id)
    {
        var device = Inspect(id);
        if (device.Problem != 0 || (device.Status & 8) == 0) throw new InvalidOperationException("指纹设备状态已变化");
        Check(CM_Disable_DevNode(device.Node, 4));
        if (Inspect(id).Problem != 22) throw new InvalidOperationException("指纹设备未确认暂停");
    }
    public static void Restore(string id)
    {
        if (!Devices().Contains(id, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("等待指纹设备重新连接");
        var device = Inspect(id);
        if (device.Problem == 22) Check(CM_Enable_DevNode(device.Node, 0));
        device = Inspect(id);
        if (device.Problem != 0 || (device.Status & 8) == 0) throw new InvalidOperationException("等待指纹设备恢复");
    }
}
