using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ClawSleepAnimation;

// Same interactive user, including its unelevated toolbox agent. Commands have
// fixed schemas: status, boolean settings, validated game IDs, and power actions.
internal sealed class AnimationControl : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Func<JsonElement, Task<object>> handle;
    private readonly PipeSecurity security = new();
    private NamedPipeServerStream? pipe;
    public AnimationControl(Func<JsonElement, Task<object>> handle)
    {
        this.handle = handle;
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadData | PipeAccessRights.WriteData |
            PipeAccessRights.ReadAttributes | PipeAccessRights.WriteAttributes | PipeAccessRights.ReadExtendedAttributes |
            PipeAccessRights.WriteExtendedAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        _ = Task.Run(Serve);
    }
    private async Task Serve()
    {
        string name = "ClawSleepAnimation-" + Process.GetCurrentProcess().SessionId;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
                pipe = server;
                await server.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                byte[] buffer = new byte[4096]; int length = 0, end = -1;
                while (length < buffer.Length && end < 0)
                {
                    int count = await server.ReadAsync(buffer.AsMemory(length), timeout.Token);
                    if (count == 0) break;
                    end = Array.IndexOf(buffer, (byte)'\n', length, count); length += count;
                }
                object reply;
                try
                {
                    if (end < 0) throw new InvalidDataException("请求格式错误");
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, end));
                    reply = new { ok = true, result = await handle(document.RootElement) };
                }
                catch (Exception ex) { reply = new { ok = false, error = ex.Message }; }
                await server.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n"), timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Program.Log("control-error", ex.Message); await Task.Delay(200, stop.Token).ConfigureAwait(false); }
            finally { pipe = null; }
        }
    }
    public void Dispose() { stop.Cancel(); pipe?.Dispose(); }
    public static async Task ConfigureAsync(string command, object value)
    {
        using var client = new NamedPipeClientStream(".", "ClawSleepAnimation-" + Process.GetCurrentProcess().SessionId,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        object request = command == "set_action" ? new { command, action = (string)value } : (object)new { command, enabled = (bool)value };
        await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"), timeout.Token);
        using var reader = new StreamReader(client, Encoding.UTF8);
        string reply = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("后台未回复");
        using var document = JsonDocument.Parse(reply);
        if (!document.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(document.RootElement.GetProperty("error").GetString());
    }
}
