// Short-lived local-only Mihomo client. Never opens a TCP controller or reads subscriptions.
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Text.RegularExpressions;

class ClashBridge
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8388608 };
    static object Request(string route, string method, object payload)
    {
        byte[] body = payload == null ? new byte[0] : Encoding.UTF8.GetBytes(Json.Serialize(payload));
        string header = method + " " + route + " HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\n\r\n";
        using (var pipe = new NamedPipeClientStream(".", "verge-mihomo", PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            pipe.Connect(1000);
            var timer = Stopwatch.StartNew();
            byte[] head = Encoding.ASCII.GetBytes(header);
            var write = pipe.BeginWrite(head, 0, head.Length, null, null);
            if (!write.AsyncWaitHandle.WaitOne(2000)) throw new TimeoutException();
            pipe.EndWrite(write);
            if (body.Length != 0)
            {
                write = pipe.BeginWrite(body, 0, body.Length, null, null);
                if (!write.AsyncWaitHandle.WaitOne(2000)) throw new TimeoutException();
                pipe.EndWrite(write);
            }
            using (var data = new MemoryStream())
            {
                byte[] buffer = new byte[16384];
                while (true)
                {
                    var read = pipe.BeginRead(buffer, 0, buffer.Length, null, null);
                    if (!read.AsyncWaitHandle.WaitOne(Math.Max(1, 8000 - (int)timer.ElapsedMilliseconds))) throw new TimeoutException();
                    int count = pipe.EndRead(read);
                    if (count == 0) break;
                    data.Write(buffer, 0, count);
                    if (data.Length > 8388608) throw new IOException();
                }
                byte[] response = data.ToArray();
                int split = -1;
                for (int i = 0; i + 3 < response.Length; i++)
                    if (response[i] == 13 && response[i+1] == 10 && response[i+2] == 13 && response[i+3] == 10) { split = i; break; }
                if (split < 0) throw new IOException();
                string headers = Encoding.ASCII.GetString(response, 0, split);
                string[] first = headers.Split('\n')[0].Trim().Split(' ');
                int status = Int32.Parse(first[1]);
                if (status < 200 || status >= 300) throw new InvalidOperationException("HTTP " + status);
                byte[] content;
                if (headers.ToLowerInvariant().Contains("transfer-encoding: chunked"))
                {
                    using (var decoded = new MemoryStream())
                    {
                        int offset = split + 4;
                        while (true)
                        {
                            int end = offset;
                            while (end + 1 < response.Length && !(response[end] == 13 && response[end+1] == 10)) end++;
                            string size = Encoding.ASCII.GetString(response, offset, end - offset).Split(';')[0];
                            int length = Convert.ToInt32(size, 16);
                            if (length == 0) break;
                            offset = end + 2;
                            if (length < 0 || offset + length + 2 > response.Length) throw new IOException();
                            decoded.Write(response, offset, length);
                            offset += length + 2;
                        }
                        content = decoded.ToArray();
                    }
                }
                else { content = new byte[response.Length - split - 4]; Array.Copy(response, split+4, content, 0, content.Length); }
                return content.Length == 0 ? new Dictionary<string, object>() : Json.DeserializeObject(Encoding.UTF8.GetString(content));
            }
        }
    }
    static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
    static object SystemProxy()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                return key == null ? null : (object)(Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0);
        }
        catch { return null; }
    }
    static object State()
    {
        var config = Map(Request("/configs", "GET", null));
        var raw = Map(Map(Request("/proxies", "GET", null))["proxies"]);
        var proxies = new Dictionary<string, object>();
        int nodeCount = 0;
        foreach (var pair in raw)
        {
            var item = Map(pair.Value);
            var safe = new Dictionary<string, object>();
            foreach (string key in new[] { "type", "now", "all", "history", "hidden" })
                if (item.ContainsKey(key)) safe[key] = item[key];
            proxies[pair.Key] = safe;
            string type = item.ContainsKey("type") ? item["type"] as string : "";
            if (!item.ContainsKey("all") && type != "Direct" && type != "Reject" && type != "RejectDrop" && type != "Pass" && type != "Compatible") nodeCount++;
        }
        return new Dictionary<string, object> { { "mode", config["mode"] },
            { "systemProxy", SystemProxy() },
            { "tun", config.ContainsKey("tun") ? Map(config["tun"])["enable"] : false }, { "proxies", proxies }, { "nodeCount", nodeCount } };
    }
    static object Run(Dictionary<string, object> input)
    {
        string command = input["command"] as string;
        if (command != null && command.StartsWith("verge-", StringComparison.Ordinal)) return VergeSession.Run(command);
        if (command == "launch")
        {
            using (var key = Registry.ClassesRoot.OpenSubKey(@"clash\shell\open\command"))
            {
                string registered = key == null ? "" : key.GetValue("") as string;
                var match = Regex.Match(registered ?? "", "^(?:\"([^\"]+)\"|(.+?\\.exe))(?:\\s|$)", RegexOptions.IgnoreCase);
                string exe = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (!match.Success || !Path.IsPathRooted(exe) || !File.Exists(exe) ||
                    !String.Equals(Path.GetFileName(exe), "clash-verge.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                return new { launched = true };
            }
        }
        if (command == "state") return State();
        if (command == "mode")
        {
            string mode = input["mode"] as string;
            if (mode != "rule" && mode != "direct" && mode != "global") throw new ArgumentException();
            Request("/configs", "PATCH", new { mode = mode });
            return State();
        }
        string group = input.ContainsKey("group") ? input["group"] as string : null;
        string node = input.ContainsKey("node") ? input["node"] as string : null;
        if (command == "test" && group != null && group.Length <= 256)
        {
            var current = Map(Request("/proxies/" + Uri.EscapeDataString(group), "GET", null));
            if ((string)current["type"] != "Selector") throw new ArgumentException();
            object test;
            try
            {
                var measured = Map(Request("/proxies/" + Uri.EscapeDataString(group) +
                    "/delay?url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204") + "&timeout=5000", "GET", null));
                int delay = Convert.ToInt32(measured["delay"]);
                test = new { ok = delay > 0, delay = delay, node = current["now"],
                    message = delay > 0 ? "本地连接正常，当前节点测试通过" : "节点没有返回有效延迟，请重试" };
            }
            catch (TimeoutException) { test = new { ok = false, node = current["now"], message = "节点测试超时，请重试或切换节点" }; }
            catch (InvalidOperationException) { test = new { ok = false, node = current["now"], message = "节点或测试站暂不可达，请重试或切换节点" }; }
            var result = Map(State());
            result["test"] = test;
            return result;
        }
        if (command == "select" && group != null && node != null && group.Length <= 256 && node.Length <= 256)
        {
            var current = Map(Request("/proxies/" + Uri.EscapeDataString(group), "GET", null));
            if ((string)current["type"] != "Selector") throw new ArgumentException();
            bool member = false;
            foreach (object name in (IEnumerable)current["all"]) if (Object.Equals(name, node)) member = true;
            if (!member) throw new ArgumentException();
            Request("/proxies/" + Uri.EscapeDataString(group), "PUT", new { name = node });
            return State();
        }
        throw new ArgumentException();
    }
    static int Main()
    {
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            string line = Console.ReadLine();
            if (line == null || line.Length > 16384) throw new ArgumentException();
            Console.WriteLine(Json.Serialize(new { ok = true, state = Run(Map(Json.DeserializeObject(line))) }));
            return 0;
        }
        catch (Exception)
        {
            // Controller errors and user configuration are never printed.
            Console.WriteLine("{\"ok\":false,\"error\":\"Clash Verge 本地连接不可用或操作未完成，请刷新状态\"}");
            return 1;
        }
    }
}
