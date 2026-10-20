namespace DesktopSplitter.Services;

/// <summary>A stop request and observed exits are distinct. Cancellation stops waiting; it never kills a process.</summary>
public static class CooperativeShutdown
{
    public static async Task<bool> StopAsync(Func<bool> requestStop,
        IReadOnlyList<Func<CancellationToken, Task>> exits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (exits.Count == 0) return true;
        if (!requestStop()) return false;
        await Task.WhenAll(exits.Select(wait => wait(cancellationToken))).WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
