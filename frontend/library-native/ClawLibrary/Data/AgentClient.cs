// 工具箱后台 agent 的客户端：功耗、风扇、CPU 这些真正动硬件的事都在 agent 里做（它有提权的 helper），
// 游戏库只是另一个前端。协议：127.0.0.1 上的 JSON 行，端口和口令在 %LOCALAPPDATA%\ClawToolbox\agent.json。
//   -> {"hello": token, "client": "library"}        <- {"hello": "ok", ...}
//   -> {"id": 1, "method": "get_power_state", "args": [true]}
//   <- {"id": 1, "result": ...} 或 {"id": 1, "error": "..."}；另有 {"event": ...} 推送（这里不用）
// 断线后下一次调用自动重连；agent 没在跑时调用直接失败，由调用方显示「—」。
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClawLibrary.Data;

public sealed class AgentClient : IDisposable
{
    private static string EndpointPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox", "agent.json");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private int _id;
    private bool _disposed;
    public event Action<bool>? ConnectionChanged;
    public event Action<string, JsonNode?>? EventReceived;

    public Task<JsonNode?> Call(string method, params JsonNode?[] args) => CallLong(method, 10, args);

    /// <summary>要等后台下载 / 搜索的调用（添加游戏的封面）：等 seconds 秒。</summary>
    public async Task<JsonNode?> CallLong(string method, int seconds, params JsonNode?[] args)
    {
        await Connect();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_pending) { id = ++_id; _pending[id] = tcs; }
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["args"] = new JsonArray(args) };
        try
        {
            await _gate.WaitAsync();
            try { await _writer!.WriteLineAsync(request.ToJsonString()); await _writer.FlushAsync(); }
            finally { _gate.Release(); }
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch
        {
            lock (_pending) _pending.Remove(id);
            throw;
        }
    }

    private async Task Connect()
    {
        if (_tcp?.Connected == true && _writer != null) return;
        await _gate.WaitAsync();
        try
        {
            if (_tcp?.Connected == true && _writer != null) return;
            Drop();
            var ep = JsonNode.Parse(await File.ReadAllTextAsync(EndpointPath)) ?? throw new IOException("agent.json 为空");
            int port = (int)ep["port"]!;
            string token = (string)ep["token"]!;
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(3));
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
            await writer.WriteLineAsync(new JsonObject { ["hello"] = token, ["client"] = "library" }.ToJsonString());
            await writer.FlushAsync();
            var hello = JsonNode.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? "null");
            if ((string?)hello?["hello"] != "ok") { tcp.Dispose(); throw new IOException("agent 拒绝了连接"); }
            _tcp = tcp;
            _writer = writer;
            _ = Task.Run(() => ReadLoop(tcp, reader));
            Log.Info($"agent connected (port {port}, version {(string?)hello?["version"]})");
            ConnectionChanged?.Invoke(true);
        }
        finally { _gate.Release(); }
    }

    private async Task ReadLoop(TcpClient tcp, StreamReader reader)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch { continue; }
                if (msg?["event"] is JsonValue eventName && eventName.TryGetValue(out string? name) && name != null)
                {
                    EventReceived?.Invoke(name, msg["value"]?.DeepClone());
                    continue;
                }
                if (msg?["id"] is not JsonValue idv || !idv.TryGetValue(out int id)) continue;
                TaskCompletionSource<JsonNode?>? tcs;
                lock (_pending) { if (_pending.Remove(id, out tcs) == false) continue; }
                if (msg["error"] is JsonNode err) tcs!.TrySetException(new InvalidOperationException((string?)err ?? "agent error"));
                else tcs!.TrySetResult(msg["result"]?.DeepClone());
            }
        }
        catch (Exception ex) { if (!_disposed) Log.Warn($"agent read: {ex.Message}"); }
        if (ReferenceEquals(tcp, _tcp)) Drop();
    }

    private void Drop()
    {
        bool connected = _tcp != null;
        try { _tcp?.Dispose(); } catch { }
        _tcp = null;
        _writer = null;
        List<TaskCompletionSource<JsonNode?>> fail;
        lock (_pending) { fail = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var t in fail) t.TrySetException(new IOException("与 agent 的连接断开了"));
        if (connected && !_disposed) ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        _disposed = true;
        Drop();
    }
}
