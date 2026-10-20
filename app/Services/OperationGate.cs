namespace DesktopSplitter.Services;

/// <summary>Synchronous, reentrant ownership with cancellable contention. Leases stay on one thread.</summary>
public sealed class OperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ThreadLocal<int> _depth = new(() => 0);

    public IDisposable Enter(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_depth.Value == 0) _semaphore.Wait(cancellationToken);
        _depth.Value++;
        return new Lease(this);
    }

    /// <summary>Let secure-desktop reconciliation run while a dialog/elevated helper is waiting.
    /// The separate user-operation gate must stay held throughout this interval.</summary>
    public T Outside<T>(Func<T> action)
    {
        int depth = _depth.Value;
        if (depth == 0) return action();
        _depth.Value = 0;
        _semaphore.Release();
        try { return action(); }
        finally { _semaphore.Wait(); _depth.Value = depth; } // Restore lease before error recovery/disposal.
    }

    private sealed class Lease(OperationGate gate) : IDisposable
    {
        private OperationGate? _gate = gate;
        public void Dispose()
        {
            var owner = _gate;
            if (owner is null) return;
            _gate = null;
            if (--owner._depth.Value == 0) owner._semaphore.Release();
        }
    }
}
