using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Script.Serialization;

class DriverFrameLimitProbe {
    static void Main(string[] args) {
        var json = new JavaScriptSerializer();
        if (args.Length == 0) { Console.WriteLine(json.Serialize(IntelFrameLimit.Read())); return; }
        if (args[0] == "--game-check" && args.Length == 2) {
            var before = IntelFrameLimit.Session("driver_game_read", new Dictionary<string, object>());
            var checks = new List<object>();
            foreach (int fps in new[] { 0, 30, 60, 90 }) {
                try {
                    var setting = IntelFrameLimit.Set(fps);
                    using (var process = Process.Start(new ProcessStartInfo(args[1]) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                    })) {
                        if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("DX11 test timed out"); }
                        var sample = json.Deserialize<Dictionary<string, object>>(process.StandardOutput.ReadToEnd());
                        if (process.ExitCode != 0) throw new Exception("DX11 test failed");
                        checks.Add(new { requested = fps, setting = setting, measured = sample });
                    }
                } finally {
                    var result = IntelFrameLimit.Session("driver_game_restore", new Dictionary<string, object> {
                        { "written", fps }, { "previousEnabled", before["enabled"] }, { "previousValue", before["value"] }
                    });
                    if (result.ContainsKey("conflict")) throw new Exception("Driver configuration changed during verification");
                }
            }
            Console.WriteLine(json.Serialize(new { before = before, checks = checks, after = IntelFrameLimit.Read() }));
            return;
        }
        if (args[0] == "--check") {
            var before = IntelFrameLimit.Session("driver_game_read", new Dictionary<string, object>());
            var results = new List<object>();
            foreach (int fps in new[] { 30, 40, 60, 90, 0 }) {
                try { results.Add(IntelFrameLimit.Set(fps)); }
                finally {
                    var journal = new Dictionary<string, object> { { "written", fps }, { "previousEnabled", before["enabled"] }, { "previousValue", before["value"] } };
                    var restored = IntelFrameLimit.Session("driver_game_restore", journal);
                    if (restored.ContainsKey("conflict")) throw new Exception("Original driver state could not be restored");
                }
            }
            Console.WriteLine(json.Serialize(new { before = before, checks = results, after = IntelFrameLimit.Read() }));
            return;
        }
        throw new ArgumentException("Use no arguments for read-only query or --check for write/read/restore checks");
    }
}
