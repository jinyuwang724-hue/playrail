using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

// Driver / BIOS / firmware updates for the 系统 page, run in the elevated helper: download from the
// vendor's own server (download.msi.com, downloadmirror.intel.com only), check size and hash, unpack,
// run the vendor's installer silently, and report progress. One job at a time; the agent polls it.
// Files live in %ProgramData%\ClawToolbox\Updates, which only administrators can write, so nothing a
// normal process drops there can be run elevated.
static class UpdateJob {
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClawToolbox", "Updates");
    static readonly string[] Hosts = { "https://download.msi.com/", "https://downloadmirror.intel.com/" };
    static readonly object Gate = new object();
    static Dictionary<string, object> state = new Dictionary<string, object> { { "state", "idle" } };
    static Thread worker;

    public static Dictionary<string, object> Status() {
        lock (Gate) return new Dictionary<string, object>(state);
    }

    static void Set(string key, object value) { lock (Gate) state[key] = value; }

    /// <summary>
    /// job: the agent's item id. url: the package. kind: "exe" (run the download) or "zip" (unpack, then run
    /// the file named by run, searched anywhere in the package). args: the installer's silent switches.
    /// sha1 / sha256 / size: checked when given.
    /// </summary>
    public static Dictionary<string, object> Start(Dictionary<string, object> r) {
        string id = Convert.ToString(r["job"]), url = Convert.ToString(r["url"]), kind = Convert.ToString(r["kind"]);
        string run = r.ContainsKey("run") ? Convert.ToString(r["run"]) : "", args = r.ContainsKey("args") ? Convert.ToString(r["args"]) : "";
        string sha1 = r.ContainsKey("sha1") ? Convert.ToString(r["sha1"]) : "", sha256 = r.ContainsKey("sha256") ? Convert.ToString(r["sha256"]) : "";
        long size = r.ContainsKey("size") && r["size"] != null ? Convert.ToInt64(r["size"]) : 0;
        bool hostOk = false;
        foreach (string h in Hosts) if (url.StartsWith(h, StringComparison.OrdinalIgnoreCase)) hostOk = true;
        if (!hostOk) throw new ArgumentException("只从微星和 Intel 的官方下载服务器下载");
        if (kind != "exe" && kind != "zip") throw new ArgumentException("不认识的更新包类型");
        if (kind == "zip" && (run.Length == 0 || run.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)) throw new ArgumentException("缺少安装程序名");
        if (args.IndexOfAny(new[] { '&', '|', '>', '<', '^', '"' }) >= 0) throw new ArgumentException("安装参数不对");
        if (id.Length == 0 || id.Length > 40 || id.Replace(':', '-').IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) throw new ArgumentException("更新编号不对");
        lock (Gate) {
            string s = Convert.ToString(state["state"]);
            if (s == "downloading" || s == "verifying" || s == "installing") throw new InvalidOperationException("已经有一个更新在进行");
            state = new Dictionary<string, object> { { "state", "downloading" }, { "id", id }, { "bytes", 0L }, { "total", size } };
        }
        bool dry = r.ContainsKey("dryRun") && true.Equals(r["dryRun"]);
        worker = new Thread(() => Run(id, url, kind, run, args, sha1, sha256, size, dry)) { IsBackground = true };
        worker.Start();
        return Status();
    }

    /// <summary>dry: stop after download, check and unpack (tests the whole path without installing anything).</summary>
    static void Run(string id, string url, string kind, string run, string args, string sha1, string sha256, long size, bool dry) {
        try {
            string dir = Path.Combine(Root, id.Replace(':', '-'));
            Prepare(dir);
            string name = Uri.UnescapeDataString(new Uri(url).Segments[new Uri(url).Segments.Length - 1]);
            string file = Path.Combine(dir, name);
            Download(url, file, size);
            Set("state", "verifying");
            Verify(file, size, sha1, sha256);
            string exe = file, workDir = dir;
            if (kind == "zip") {
                string unpacked = Path.Combine(dir, "package");
                if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
                ZipFile.ExtractToDirectory(file, unpacked);
                exe = Find(unpacked, run);
                workDir = Path.GetDirectoryName(exe);
            }
            if (dry) { lock (Gate) { state["exitCode"] = 0; state["dryRun"] = exe; state["state"] = "done"; } return; }
            Set("state", "installing");
            var start = exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c \"\"" + exe + "\" " + args + "\"")
                : new ProcessStartInfo(exe, args);
            start.WorkingDirectory = workDir;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (var p = Process.Start(start)) {
                if (!p.WaitForExit(90 * 60 * 1000)) throw new TimeoutException("安装程序 90 分钟还没结束");
                lock (Gate) { state["exitCode"] = p.ExitCode; state["state"] = "done"; }
            }
        } catch (Exception e) {
            lock (Gate) { state["state"] = "failed"; state["error"] = e.Message; }
        }
    }

    /// <summary>The download folder: administrators and SYSTEM may write, users may only read.</summary>
    static void Prepare(string dir) {
        Directory.CreateDirectory(Root);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.SetAccessControl(Root, sec);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }

    static void Download(string url, string file, long size) {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;  // TLS 1.2 / 1.3
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClawToolbox";
        request.Timeout = 30000;
        request.ReadWriteTimeout = 60000;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var input = response.GetResponseStream())
        using (var output = File.Create(file)) {
            long total = response.ContentLength > 0 ? response.ContentLength : size, done = 0;
            Set("total", total);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                output.Write(buffer, 0, n);
                done += n;
                Set("bytes", done);
            }
        }
    }

    static void Verify(string file, long size, string sha1, string sha256) {
        long length = new FileInfo(file).Length;
        if (size > 0 && length != size) throw new InvalidDataException(string.Format("下载的文件大小不对：{0} / {1}", length, size));
        if (sha1.Length > 0 && !Hash(SHA1.Create(), file).Equals(sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA1 校验不对，已丢弃");
        if (sha256.Length > 0 && !Hash(SHA256.Create(), file).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 校验不对，已丢弃");
    }

    static string Hash(HashAlgorithm algorithm, string file) {
        using (algorithm)
        using (var s = File.OpenRead(file)) return BitConverter.ToString(algorithm.ComputeHash(s)).Replace("-", "");
    }

    /// <summary>The installer inside a package: the shallowest file with this name.</summary>
    static string Find(string root, string name) {
        string best = null;
        foreach (string f in Directory.GetFiles(root, name, SearchOption.AllDirectories))
            if (best == null || f.Split('\\').Length < best.Split('\\').Length) best = f;
        if (best == null) throw new FileNotFoundException("更新包里没有 " + name);
        return best;
    }

    /// <summary>Every device's hardware IDs and driver version, for matching Intel's and MSI's catalogues.</summary>
    public static List<Dictionary<string, object>> Drivers() {
        var list = new List<Dictionary<string, object>>();
        using (var s = new System.Management.ManagementObjectSearcher("SELECT DeviceName, HardWareID, DriverVersion, DeviceClass FROM Win32_PnPSignedDriver"))
            foreach (var o in s.Get()) {
                string hw = Convert.ToString(o["HardWareID"]), ver = Convert.ToString(o["DriverVersion"]);
                if (hw.Length == 0 || ver.Length == 0) continue;
                list.Add(new Dictionary<string, object> { { "name", Convert.ToString(o["DeviceName"]) }, { "hwid", hw }, { "version", ver }, { "class", Convert.ToString(o["DeviceClass"]) } });
            }
        return list;
    }
}

// Windows Update through its own COM interface (Microsoft.Update.Session), run in the elevated helper:
// search the pending software updates, keep the ones the user picked (by UpdateID), download and install
// them. One job at a time, polled by the agent like UpdateJob. Never reboots by itself.
static class WindowsUpdateJob {
    static readonly object Gate = new object();
    static Dictionary<string, object> state = new Dictionary<string, object> { { "state", "idle" } };

    public static Dictionary<string, object> Status() { lock (Gate) return new Dictionary<string, object>(state); }
    static void Set(string key, object value) { lock (Gate) state[key] = value; }

    public static Dictionary<string, object> Start(List<string> ids) {
        if (ids == null || ids.Count == 0 || ids.Count > 100) throw new ArgumentException("要安装的 Windows 更新格式错误");
        foreach (string id in ids) { Guid g; if (!Guid.TryParse(id, out g)) throw new ArgumentException("Windows 更新编号不对"); }
        lock (Gate) {
            string s = Convert.ToString(state["state"]);
            if (s == "searching" || s == "downloading" || s == "installing") throw new InvalidOperationException("Windows 更新已经在安装");
            state = new Dictionary<string, object> { { "state", "searching" }, { "ids", ids } };
        }
        var t = new Thread(() => Run(ids)) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return Status();
    }

    static void Run(List<string> ids) {
        try {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            session.ClientApplicationID = "Claw Toolbox";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic found = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            dynamic picked = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl"));
            var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < found.Updates.Count; i++) {
                dynamic u = found.Updates.Item(i);
                if (!wanted.Contains((string)u.Identity.UpdateID)) continue;
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                picked.Add(u);
            }
            if (picked.Count == 0) throw new InvalidOperationException("这些更新已经不在待装列表里了，请重新检查");
            Set("total", (int)picked.Count);
            Set("state", "downloading");
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = picked;
            downloader.Download();
            Set("state", "installing");
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = picked;
            dynamic result = installer.Install();
            int code = (int)result.ResultCode;   // 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted
            lock (Gate) {
                state["resultCode"] = code;
                state["reboot"] = (bool)result.RebootRequired;
                state["state"] = code == 2 || code == 3 ? "done" : "failed";
                if (code != 2 && code != 3) state["error"] = "Windows 更新返回 " + code;
            }
        } catch (Exception e) {
            lock (Gate) { state["state"] = "failed"; state["error"] = e.Message; }
        }
    }
}
