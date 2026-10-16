using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// What the input layer changed in HidHide, written before every change so any process (the engine,
// its guardian, the stdio bridge, the toolbox agent at its next start) can undo exactly that and
// nothing else: our device / application entries come out, the cloak goes back to how it was.
namespace ClawInput {
    public sealed class HideJournal {
        public int Pid;
        public bool Active;
        public bool PreviousCloak;
        public List<string> AddedDevices = new List<string>();
        public List<string> AddedApps = new List<string>();
        public string At;
        public string RestoredBy;

        public static HideJournal Load(string path) {
            try {
                if (!File.Exists(path)) return null;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                var j = new HideJournal();
                object v;
                if (d.TryGetValue("pid", out v)) j.Pid = Convert.ToInt32(v);
                if (d.TryGetValue("active", out v)) j.Active = Equals(v, true);
                if (d.TryGetValue("previousCloak", out v)) j.PreviousCloak = Equals(v, true);
                if (d.TryGetValue("addedDevices", out v) && v is System.Collections.IList) foreach (var s in (System.Collections.IList)v) j.AddedDevices.Add((string)s);
                if (d.TryGetValue("addedApps", out v) && v is System.Collections.IList) foreach (var s in (System.Collections.IList)v) j.AddedApps.Add((string)s);
                if (d.TryGetValue("at", out v)) j.At = v as string;
                if (d.TryGetValue("restoredBy", out v)) j.RestoredBy = v as string;
                return j;
            } catch (Exception) {
                return null;
            }
        }

        public void Save(string path) {
            var d = new Dictionary<string, object> { { "pid", Pid }, { "active", Active }, { "previousCloak", PreviousCloak },
                { "addedDevices", AddedDevices }, { "addedApps", AddedApps }, { "at", DateTime.UtcNow.ToString("o") }, { "restoredBy", RestoredBy } };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }

    public static class Hiding {
        static bool Has(List<string> list, string value) {
            foreach (var s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static int RemoveAll(List<string> list, List<string> remove) {
            return list.RemoveAll(delegate (string s) { return Has(remove, s); });
        }

        // Hide the devices from everyone but the apps. Applications first, so the engine never loses the pad.
        public static HideJournal Apply(IHidHide hh, string path, int pid, List<string> devices, List<string> apps) {
            var white = hh.Whitelist; var black = hh.Blacklist;
            var j = new HideJournal { Pid = pid, Active = true, PreviousCloak = hh.Active };
            foreach (var a in apps) if (!Has(white, a) && !Has(j.AddedApps, a)) j.AddedApps.Add(a);
            foreach (var d in devices) if (!Has(black, d) && !Has(j.AddedDevices, d)) j.AddedDevices.Add(d);
            j.Save(path);
            if (j.AddedApps.Count > 0) { white.AddRange(j.AddedApps); hh.Whitelist = white; }
            if (j.AddedDevices.Count > 0) { black.AddRange(j.AddedDevices); hh.Blacklist = black; }
            if (!j.PreviousCloak) hh.Active = true;
            return j;
        }

        // Put back what someone else took away while we are active (ClawTweaks rewrites HidHide on its own).
        // New devices / apps (the pad re-enumerated, the game library started) are added the same way.
        public static int Ensure(IHidHide hh, string path, HideJournal j, List<string> devices, List<string> apps) {
            int fixes = 0;
            var white = hh.Whitelist;
            var missingApps = new List<string>();
            foreach (var a in apps) if (!Has(white, a)) missingApps.Add(a);
            var black = hh.Blacklist;
            var missingDevices = new List<string>();
            foreach (var d in devices) if (!Has(black, d)) missingDevices.Add(d);
            if (missingApps.Count > 0 || missingDevices.Count > 0) {
                foreach (var a in missingApps) if (!Has(j.AddedApps, a)) j.AddedApps.Add(a);
                foreach (var d in missingDevices) if (!Has(j.AddedDevices, d)) j.AddedDevices.Add(d);
                j.Save(path);
            }
            if (missingApps.Count > 0) { white.AddRange(missingApps); hh.Whitelist = white; fixes++; }
            if (missingDevices.Count > 0) { black.AddRange(missingDevices); hh.Blacklist = black; fixes++; }
            if (!hh.Active) { hh.Active = true; fixes++; }
            return fixes;
        }

        // Idempotent: unhide first (cloak back off), then take our entries out of both lists.
        public static void Restore(IHidHide hh, string path, HideJournal j, string by) {
            if (!j.PreviousCloak) hh.Active = false;
            var black = hh.Blacklist;
            if (RemoveAll(black, j.AddedDevices) > 0) hh.Blacklist = black;
            var white = hh.Whitelist;
            if (RemoveAll(white, j.AddedApps) > 0) hh.Whitelist = white;
            j.Active = false; j.RestoredBy = by;
            j.Save(path);
        }
    }
}
