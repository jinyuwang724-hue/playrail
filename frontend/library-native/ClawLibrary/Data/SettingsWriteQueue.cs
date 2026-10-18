namespace ClawLibrary.Data;

/// <summary>同一设置合并连续修改，不同设置分别保留；系统写入按顺序执行。</summary>
internal sealed class SettingsWriteQueue
{
    private readonly Dictionary<string, long> _pending = new();
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private readonly Action<Exception> _failed;
    private readonly Action _drained;
    private readonly Func<int, Task> _delay;
    private long _revision;

    public SettingsWriteQueue(Action<Exception> failed, Action drained, Func<int, Task>? delay = null)
    {
        _failed = failed; _drained = drained; _delay = delay ?? Task.Delay;
    }

    public bool IsBusy { get { lock (_gate) return _pending.Count > 0; } }
    public long Revision { get { lock (_gate) return _revision; } }

    public Task Enqueue(string key, int delayMs, Func<Task> write)
    {
        long revision;
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[key] = revision = ++_revision;
            previous = _tail;
            _tail = completion.Task;
        }
        return Execute(key, revision, delayMs, write, previous, completion);
    }

    private async Task Execute(string key, long revision, int delayMs, Func<Task> write, Task previous, TaskCompletionSource completion)
    {
        try
        {
            var delay = _delay(delayMs);
            await previous;
            await delay;
            lock (_gate)
                if (!_pending.TryGetValue(key, out var current) || current != revision) return;
            await write();
        }
        catch (Exception ex) { _failed(ex); }
        finally
        {
            bool drained = false;
            lock (_gate)
                if (_pending.TryGetValue(key, out var current) && current == revision)
                {
                    _pending.Remove(key);
                    drained = _pending.Count == 0;
                }
            completion.TrySetResult();
            if (drained) _drained();
        }
    }
}
