using System.Collections.Concurrent;

namespace ListenFlow.Tests;

internal sealed class DedicatedThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly ManualResetEventSlim _started = new();
    private readonly Thread _thread;

    public DedicatedThreadSynchronizationContext()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ListenFlow 测试状态线程"
        };
        _thread.Start();
        _started.Wait();
    }

    public int ThreadId { get; private set; }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId == ThreadId)
        {
            callback(state);
            return;
        }

        using var completed = new ManualResetEventSlim();
        var item = new WorkItem(callback, state, completed);
        _queue.Add(item);
        completed.Wait();
        if (item.Error is not null)
            throw new AggregateException(item.Error);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _started.Dispose();
        _queue.Dispose();
    }

    private void Run()
    {
        ThreadId = Environment.CurrentManagedThreadId;
        SetSynchronizationContext(this);
        _started.Set();
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item.Callback(item.State);
            }
            catch (Exception exception)
            {
                item.Error = exception;
            }
            finally
            {
                item.Completed.Set();
            }
        }
    }

    private sealed class WorkItem(
        SendOrPostCallback callback,
        object? state,
        ManualResetEventSlim completed)
    {
        public SendOrPostCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public ManualResetEventSlim Completed { get; } = completed;
        public Exception? Error { get; set; }
    }
}
