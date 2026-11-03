using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using System.IO;
using System.Text;

namespace ClawSleepAnimation;

// Reads Windows' existing ACPI notifications; does not issue a button IOCTL or
// compete with the Windows power manager for a pending button request.
internal sealed class AcpiButtons : IDisposable
{
    private static readonly Guid Provider = new("c514638f-7723-485b-bcfc-96565d735d4a");
    private readonly TraceEventSession session;
    private readonly Action<string, object?> log;
    public event Action<ButtonSignal>? Button;
    public Task Completion { get; }

    public AcpiButtons(Action<string, object?> log)
    {
        this.log = log;
        session = new TraceEventSession("ClawSleepAnimation-" + Environment.ProcessId,
            TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate |
            TraceEventSessionOptions.NoPerProcessorBuffering)
        {
            StopOnDispose = true, BufferSizeMB = 1, BufferQuantumKB = 16
        };
        try
        {
            session.Source.Dynamic.All += OnEvent;
            session.Source.UnhandledEvents += OnEvent;
            session.EnableProvider(Provider, TraceEventLevel.Informational, 1,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 24, 25 } });
            Completion = Task.Run(() => session.Source.Process());
            log("acpi-trace-ready", new { session = session.SessionName, bufferMB = 1 });
        }
        catch { session.Dispose(); throw; }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        if (e.ProviderGuid != Provider || (id != 24 && id != 25)) return;
        try
        {
            if (e.Version != 0) { log("unsupported-acpi-version", new { id, e.Version }); return; }
            var signal = ButtonSignal.Parse(id, e.EventData(), e.TimeStamp.ToUniversalTime(), DateTime.UtcNow);
            log("acpi-button", signal);
            Button?.Invoke(signal);
        }
        catch (Exception ex) { log("acpi-event-error", new { id, error = ex.Message }); }
    }

    public void Dispose() => session.Dispose();
}
