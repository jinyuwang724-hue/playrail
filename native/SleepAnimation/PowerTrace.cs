using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics;

namespace ClawSleepAnimation;

// Reads ETW; never issues button I/O. The normal monitor enables only event 555.
internal sealed class PowerTrace : IDisposable
{
    private readonly TraceEventSession session;
    private readonly Action<string, object?> log;
    public Task Completion { get; }
    public bool HasKernelPower { get; private set; }
    public event Action<PowerButtonEvent>? PowerButton;
    private readonly System.Threading.Timer? flushing;
    private readonly bool buttonsOnly;
    public PowerTrace(Action<string, object?> log, bool buttonsOnly = false)
    {
        this.buttonsOnly = buttonsOnly;
        this.log = log;
        session = new TraceEventSession("ClawSleepDiagnostic-" + Environment.ProcessId,
            TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate |
            TraceEventSessionOptions.NoPerProcessorBuffering)
        { StopOnDispose = true, BufferSizeMB = 1, BufferQuantumKB = 16 };
        try
        {
            session.Source.Dynamic.All += OnEvent;
            session.Source.UnhandledEvents += OnEvent;
            if (!buttonsOnly) session.EnableProvider("Microsoft-Windows-Kernel-Acpi", TraceEventLevel.Verbose, ulong.MaxValue);
            foreach (string name in buttonsOnly ? new[] { "Microsoft-Windows-Kernel-Power" } : new[] { "Microsoft-Windows-Kernel-Power", "Microsoft-Windows-UserModePowerService", "Microsoft-Windows-Kernel-PowerTrigger" })
            {
                try
                {
                    session.EnableProvider(name, TraceEventLevel.Verbose, name.EndsWith("Kernel-Power") ? 0x404ul : ulong.MaxValue,
                        name.EndsWith("Kernel-Power") ? new TraceEventProviderOptions { EventIDsToEnable = buttonsOnly ? new List<int> { 555 } : new List<int> { 1, 2, 42, 107, 149, 150, 165, 187, 188, 189, 404, 405, 416, 506, 507, 555, 558, 566 } } : null);
                    log("diagnostic-provider-ready", name);
                    if (name.EndsWith("Kernel-Power")) HasKernelPower = true;
                }
                catch (Exception ex) { log("diagnostic-provider-unavailable", new { name, error = ex.Message }); }
            }
            Completion = Task.Run(() => session.Source.Process());
            if (buttonsOnly) flushing = new System.Threading.Timer(_ => { try { session.Flush(); } catch { } }, null, 100, 100);
            log("wide-trace-ready", new { session = session.SessionName });
        }
        catch { session.Dispose(); throw; }
    }
    private void OnEvent(TraceEvent e)
    {
        try
        {
            var payload = new Dictionary<string, object?>();
            for (int i = 0; i < e.PayloadNames.Length; i++)
            {
                object? value = e.PayloadValue(i);
                payload[e.PayloadNames[i]] = value is byte[] raw ? Convert.ToHexString(raw) : value?.ToString();
            }
            if (!buttonsOnly) log("power-trace", new { provider = e.ProviderName, id = (int)e.ID, e.Version,
                eventName = e.EventName, eventTime = e.TimeStamp.ToUniversalTime(),
                deliveryMs = (DateTime.UtcNow - e.TimeStamp.ToUniversalTime()).TotalMilliseconds,
                payload, raw = payload.Count == 0 ? Convert.ToHexString(e.EventData()) : null });
            if (e.ProviderGuid == new Guid("331c3b3a-2005-44c2-ac5e-77220c37d6b4") && (int)e.ID == 555 && PowerButtonMatcher.Matches(e.Version, payload))
            {
                // Wall-clock conversion drifted across real S4 resume in this
                // diagnostic. Compare the original QPC clock directly instead.
#pragma warning disable CS0618
                var signal = new PowerButtonEvent(e.TimeStampQPC, Stopwatch.GetTimestamp());
#pragma warning restore CS0618
                log("power-button-signal", new { delayMs = (signal.ReceivedQpc - signal.TimestampQpc) * 1000.0 / Stopwatch.Frequency });
                PowerButton?.Invoke(signal);
            }
        }
        catch (Exception ex) { log("trace-decode-error", ex.Message); }
    }
    public void Dispose() { flushing?.Dispose(); session.Dispose(); }
}
