// On-demand bridge to the installed app's own commands. All endpoints stay on loopback.
using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class VergeSession
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "homebrew", "settings", "claw-button-router"); } }
    static string Record { get { return Path.Combine(Root, "verge-session.json"); } }
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);

    static string Exe()
    {
        using (var key = Registry.ClassesRoot.OpenSubKey(@"clash\shell\open\command"))
        {
            string registered = key == null ? "" : key.GetValue("") as string;
            var match = Regex.Match(registered ?? "", "^(?:\"([^\"]+)\"|(.+?\\.exe))(?:\\s|$)", RegexOptions.IgnoreCase);
            string exe = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!match.Success || !Path.IsPathRooted(exe) || !File.Exists(exe) ||
                !String.Equals(Path.GetFileName(exe), "clash-verge.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            return Path.GetFullPath(exe);
        }
    }
    static Dictionary<string, object> Read()
    {
        return (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Record, Encoding.UTF8));
    }
    static Process Owned(Dictionary<string, object> record)
    {
        var process = Process.GetProcessById(Convert.ToInt32(record["pid"]));
        if (!String.Equals(process.MainModule.FileName, Exe(), StringComparison.OrdinalIgnoreCase) ||
            process.StartTime.ToUniversalTime().Ticks.ToString() != (string)record["started"]) throw new InvalidOperationException();
        int port = Convert.ToInt32(record["port"]);
        if (port < 1024 || port > 65535) throw new InvalidOperationException();
        return process;
    }
    static bool Descendant(int candidate, int root)
    {
        for (int i = 0; i < 12 && candidate > 0; i++)
        {
            if (candidate == root) return true;
            using (var search = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + candidate))
            {
                int parent = 0;
                foreach (ManagementObject row in search.Get()) { parent = Convert.ToInt32(row["ParentProcessId"]); break; }
                if (parent == candidate) return false;
                candidate = parent;
            }
        }
        return false;
    }
    static bool OwnsPort(int port, int root)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return false;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = IntPtr.Add(table, 4 + i * 24);
                int rowPort = Marshal.ReadByte(row, 8) * 256 + Marshal.ReadByte(row, 9);
                if (rowPort != port) continue;
                // Reject a listener exposed on all interfaces, even if its process belongs to Verge.
                if ((uint)Marshal.ReadInt32(row, 4) != 0x0100007f) return false;
                return Descendant(Marshal.ReadInt32(row, 20), root);
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }
    public static object Run(string command)
    {
        if (command == "verge-connect")
        {
            try { var record = Read(); using (Owned(record)) { return record; } }
            catch (Exception) { }
            Directory.CreateDirectory(Root);
            string exe = Exe();
            var matching = new List<Process>();
            foreach (var process in Process.GetProcessesByName("clash-verge"))
            {
                // An unreadable identity aborts initialization rather than targeting an unknown owner.
                if (String.Equals(process.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) matching.Add(process);
            }
            if (matching.Count > 1) throw new InvalidOperationException();
            long foreground = GetForegroundWindow().ToInt64();
            string backup = Path.Combine(Root, "subscription-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backup);
            string configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "io.github.clash-verge-rev.clash-verge-rev");
            foreach (string name in new[] { "verge.yaml", "profiles.yaml", "config.yaml" })
                if (File.Exists(Path.Combine(configRoot, name))) File.Copy(Path.Combine(configRoot, name), Path.Combine(backup, name));
            if (matching.Count == 1)
            {
                matching[0].Kill();
                if (!matching[0].WaitForExit(5000)) throw new TimeoutException();
            }
            int port;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            string existing = start.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] ?? "";
            existing = Regex.Replace(existing, @"--remote-debugging-(?:port|address)(?:=|\s+)\S+", "");
            start.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = existing + " --remote-debugging-port=" + port + " --remote-debugging-address=127.0.0.1";
            start.EnvironmentVariables["WEBVIEW2_USER_DATA_FOLDER"] = Path.Combine(Root, "verge-webview-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Process launched;
            try { launched = Process.Start(start); }
            catch { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }); throw; }
            var next = new Dictionary<string, object> { { "pid", launched.Id }, { "started", launched.StartTime.ToUniversalTime().Ticks.ToString() },
                { "port", port }, { "foreground", foreground.ToString() } };
            File.WriteAllText(Record, Json.Serialize(next), new UTF8Encoding(false));
            return next;
        }
        var active = Read();
        using (var process = Owned(active))
        {
            if (command == "verge-session") return active;
            if (command == "verge-owner") return new { owned = OwnsPort(Convert.ToInt32(active["port"]), process.Id) };
            if (command == "verge-show")
            {
                if (!OwnsPort(33331, process.Id)) return new { shown = false };
                IntPtr previous = GetForegroundWindow();
                uint previousPid;
                GetWindowThreadProcessId(previous, out previousPid);
                if (previousPid != 0 && !Descendant((int)previousPid, process.Id))
                {
                    active["foreground"] = previous.ToInt64().ToString();
                    File.WriteAllText(Record, Json.Serialize(active), new UTF8Encoding(false));
                }
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:33331/commands/visible");
                request.Proxy = null; request.Timeout = 3000;
                using (var response = request.GetResponse()) { return new { shown = true }; }
            }
            if (command == "verge-hide")
            {
                IntPtr foreground = GetForegroundWindow();
                uint foregroundPid;
                GetWindowThreadProcessId(foreground, out foregroundPid);
                bool restore = foregroundPid != 0 && Descendant((int)foregroundPid, process.Id);
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) ShowWindow(process.MainWindowHandle, 0);
                long previous;
                if (restore && active.ContainsKey("foreground") && Int64.TryParse((string)active["foreground"], out previous) && IsWindow(new IntPtr(previous)))
                    SetForegroundWindow(new IntPtr(previous));
                return new { hidden = true };
            }
        }
        throw new ArgumentException();
    }
}
