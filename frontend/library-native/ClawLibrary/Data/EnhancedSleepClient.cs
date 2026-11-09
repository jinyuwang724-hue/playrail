using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace ClawLibrary.Data;

// A known library launch is a stronger game identity than foreground heuristics.
// Best-effort, fixed-schema notification to the same-session protected helper.
internal static class EnhancedSleepClient
{
    private static int sending;
    public static void Publish(int pid)
    {
        if (pid < 0 || Interlocked.Exchange(ref sending, 1) != 0) return;
        _ = Send(pid);
    }
    private static async Task Send(int pid)
    {
        try
        {
            using var timeout = new CancellationTokenSource(750);
            using var pipe = new NamedPipeClientStream(".", "ClawSleepAnimation-" + Process.GetCurrentProcess().SessionId,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            byte[] message = Encoding.UTF8.GetBytes("{\"command\":\"set_game_session\",\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + "}\n");
            await pipe.WriteAsync(message, timeout.Token).ConfigureAwait(false);
            byte[] reply = new byte[512];
            await pipe.ReadAsync(reply, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref sending, 0); }
    }
}
