namespace DesktopSplitter.Services;

/// <summary>Only the blank-screen safety deadline is time-based. Process death wins over stale readiness.</summary>
public static class ReadinessWait
{
    public static CompositorLauncher.SpecializedWait Wait(WaitHandle live, WaitHandle exited,
        TimeSpan safetyTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int result = WaitHandle.WaitAny(new[] { exited, live, cancellationToken.WaitHandle }, safetyTimeout);
        if (result == 0) return CompositorLauncher.SpecializedWait.CompositorExited;
        if (result == 1) return CompositorLauncher.SpecializedWait.Live;
        if (result == 2) cancellationToken.ThrowIfCancellationRequested();
        return CompositorLauncher.SpecializedWait.TimedOut;
    }
}
