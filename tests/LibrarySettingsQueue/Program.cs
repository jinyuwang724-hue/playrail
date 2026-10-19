using ClawLibrary.Data;
using ClawLibrary.Engine;

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

// Quickly touching both sticks and triggers must preserve every independent firmware range.
{
    var delay = Signal();
    var writes = new List<string>();
    int drained = 0;
    var queue = new SettingsWriteQueue(ex => throw ex, () => drained++, _ => delay.Task);
    var tasks = new[] {
        queue.Enqueue("stick:left", 600, () => { writes.Add("left:4/100"); return Task.CompletedTask; }),
        queue.Enqueue("stick:right", 600, () => { writes.Add("right:6/100"); return Task.CompletedTask; }),
        queue.Enqueue("stick:left", 600, () => { writes.Add("left:8/95"); return Task.CompletedTask; }),
        queue.Enqueue("trigger:left", 600, () => { writes.Add("lt:5/95"); return Task.CompletedTask; }),
        queue.Enqueue("trigger:right", 600, () => { writes.Add("rt:10/90"); return Task.CompletedTask; }),
    };
    delay.SetResult();
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
    Check(writes.SequenceEqual(new[] { "right:6/100", "left:8/95", "lt:5/95", "rt:10/90" }),
        "rapid left/right stick and trigger changes retain every range with its latest snapshot");
    Check(drained == 1 && !queue.IsBusy, "firmware ranges request readback only after every pending write finishes");
}

// The gesture classifier is shared by mouse, touch and pen Pointer events.
{
    var gesture = new SettingsPointerGesture();
    Check(gesture.Begin(1, 100, 100, true), "first pointer starts a gesture");
    Check(gesture.Move(1, 104, 103) == SettingsPointerMode.Pending && gesture.End(1) == SettingsPointerMode.Pending,
        "small finger movement stays a tap until release");
    Check(gesture.End(1) == SettingsPointerMode.None, "duplicate release cannot activate a control twice");

    gesture.Begin(2, 100, 100, true);
    Check(gesture.Move(2, 103, 120) == SettingsPointerMode.Scroll, "vertical swipe over a slider scrolls without changing its value");
    Check(gesture.Move(2, 180, 125) == SettingsPointerMode.Scroll && gesture.End(2) == SettingsPointerMode.Scroll,
        "scroll intent stays locked when a finger later moves sideways");

    gesture.Begin(3, 100, 100, true);
    Check(gesture.Move(3, 125, 102) == SettingsPointerMode.Slider && gesture.End(3) == SettingsPointerMode.Slider,
        "horizontal slider drag is distinct from vertical scrolling");

    gesture.Begin(4, 100, 100, false);
    Check(!gesture.Begin(5, 100, 100, true) && gesture.Move(5, 200, 200) == SettingsPointerMode.None
        && gesture.End(5) == SettingsPointerMode.None && gesture.Owns(4), "secondary finger cannot replace or release the active pointer");
    gesture.Cancel(5);
    Check(gesture.Owns(4), "capture loss for another pointer does not cancel the primary gesture");
    gesture.Cancel(4);
    Check(!gesture.Active && gesture.End(4) == SettingsPointerMode.None, "cancellation never produces a tap");

    gesture.Begin(6, 100, 100, false);
    Check(gesture.Move(6, 140, 100) == SettingsPointerMode.Scroll, "dragging a choice row never changes its option");
    gesture.Reset();
    Check(!gesture.Active, "closing the page discards its captured gesture");

    Check(SettingsPointerGesture.SliderValue(-40, 100, 200, 0, 100, 5) == 0
        && SettingsPointerGesture.SliderValue(450, 100, 200, 0, 100, 5) == 100
        && SettingsPointerGesture.SliderValue(201, 100, 200, 0, 100, 5) == 50,
        "slider values snap to supported steps and clamp outside the track");
    Check(SettingsPointerGesture.SliderValue(150, 100, 200, 100, 200, 25) == 125,
        "slider mapping preserves nonzero minima");
    Check(SettingsPointerGesture.Scroll(-20, 900, 664) == 0 && SettingsPointerGesture.Scroll(500, 900, 664) == 236
        && SettingsPointerGesture.Scroll(100, 300, 664) == 0, "scrolling clamps at both ends and handles a short list");
    var hit = new SettingsPointerTarget(3, SettingsTargetKind.Choice, 100, 80, 44, 42, 1);
    Check(hit.Contains(100, 80) && !hit.Contains(144, 80) && !hit.Contains(120, 122), "adjacent hit targets have unambiguous boundaries");
}
