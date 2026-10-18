using ClawLibrary.Data;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS {message}");
}

static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

// Different rows must retain both edits and their order, even when their debounce times differ.
{
    var delay = Signal();
    var writes = new List<string>();
    int drained = 0;
    var queue = new SettingsWriteQueue(ex => throw ex, () => drained++, ms => ms > 0 ? delay.Task : Task.CompletedTask);
    var brightness = queue.Enqueue("brightness", 150, () => { writes.Add("brightness:95"); return Task.CompletedTask; });
    var mute = queue.Enqueue("mute", 0, () => { writes.Add("mute:true"); return Task.CompletedTask; });
    Check(queue.IsBusy && writes.Count == 0, "later immediate changes wait for earlier delayed changes");
    delay.SetResult();
    await Task.WhenAll(brightness, mute).WaitAsync(TimeSpan.FromSeconds(2));
    Check(writes.SequenceEqual(new[] { "brightness:95", "mute:true" }), "editing different rows preserves both writes in input order");
    Check(!queue.IsBusy && drained == 1, "one readback is requested after all writes finish");
}

// Repeating one slider should coalesce without discarding another GPU option.
{
    var delay = Signal();
    var writes = new List<string>();
    var queue = new SettingsWriteQueue(ex => throw ex, () => { }, _ => delay.Task);
    var tasks = new[] {
        queue.Enqueue("brightness", 150, () => { writes.Add("brightness:95"); return Task.CompletedTask; }),
        queue.Enqueue("brightness", 150, () => { writes.Add("brightness:90"); return Task.CompletedTask; }),
        queue.Enqueue("gpu:lowLatency", 200, () => { writes.Add("latency:on"); return Task.CompletedTask; }),
        queue.Enqueue("gpu:xessFg", 200, () => { writes.Add("xess:false"); return Task.CompletedTask; }),
    };
    delay.SetResult();
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
    Check(writes.SequenceEqual(new[] { "brightness:90", "latency:on", "xess:false" }), "slider coalescing keeps the latest value and independent GPU options");
}

// A write arriving during an active request must follow it, with no overlapping system calls.
{
    var release = Signal();
    var started = Signal();
    var writes = new List<int>();
    int active = 0, maxActive = 0;
    var queue = new SettingsWriteQueue(ex => throw ex, () => { });
    var first = queue.Enqueue("volume", 0, async () => {
        maxActive = Math.Max(maxActive, ++active); started.SetResult();
        await release.Task; writes.Add(40); --active;
    });
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    long before = queue.Revision;
    var second = queue.Enqueue("volume", 0, () => { maxActive = Math.Max(maxActive, ++active); writes.Add(42); --active; return Task.CompletedTask; });
    Check(queue.IsBusy && queue.Revision > before, "new edits invalidate older refresh snapshots while a request is active");
    release.SetResult();
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    Check(writes.SequenceEqual(new[] { 40, 42 }) && maxActive == 1, "in-flight edits finish in order without concurrent writes");
}

// An unsupported or failed setting must not wedge subsequent controls or suppress final readback.
{
    var delay = Signal();
    int errors = 0, sent = 0, drained = 0;
    var queue = new SettingsWriteQueue(_ => errors++, () => drained++, _ => delay.Task);
    var bad = queue.Enqueue("unsupported", 0, () => throw new InvalidOperationException("unsupported"));
    var good = queue.Enqueue("volume", 0, () => { sent++; return Task.CompletedTask; });
    delay.SetResult();
    await Task.WhenAll(bad, good).WaitAsync(TimeSpan.FromSeconds(2));
    Check(errors == 1 && sent == 1 && drained == 1 && !queue.IsBusy, "failed setting recovers and allows the next edit and readback");
}
