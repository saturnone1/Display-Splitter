namespace DesktopSplitter.Services;

/// <summary>UI-context-owned refresh queue: one reader, latest request wins, no polling timer.</summary>
public sealed class LatestRefresh<T>(Func<Task<T>> read, Action<T> publish)
{
    private long _version;
    private Task? _running;

    public Task RequestAsync()
    {
        _version++;
        return _running ??= RunAsync();
    }

    private async Task RunAsync()
    {
        await Task.Yield(); // Publish the shared task before the reader can complete synchronously.
        try
        {
            long version;
            do
            {
                version = _version;
                T snapshot;
                try { snapshot = await read(); }
                catch when (version != _version) { continue; } // A newer request still deserves a read after an obsolete failure.
                if (version == _version) publish(snapshot);
            } while (version != _version);
        }
        finally { _running = null; }
    }
}
