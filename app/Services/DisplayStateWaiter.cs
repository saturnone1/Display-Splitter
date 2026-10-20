using Microsoft.Win32;

namespace DesktopSplitter.Services;

/// <summary>Re-read on Windows notifications, without polling sleeps or a forced completion deadline.</summary>
public static class DisplayStateWaiter
{
    public static T Wait<T>(Func<T> read, Func<T, bool> ready, CancellationToken cancellationToken)
    {
        using var changed = new AutoResetEvent(false);
        var gate = new object();
        bool stopped = false;
        EventHandler handler = (_, _) => { lock (gate) { if (!stopped) changed.Set(); } };
        SystemEvents.DisplaySettingsChanged += handler;
        try { return Wait(read, ready, changed, cancellationToken); }
        finally
        {
            SystemEvents.DisplaySettingsChanged -= handler;
            lock (gate) stopped = true;
        }
    }

    public static T Wait<T>(Func<T> read, Func<T, bool> ready, WaitHandle changed, CancellationToken cancellationToken)
    {
        var waits = new[] { changed, cancellationToken.WaitHandle };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            T value = read(); // Exceptions are not an empty display list or a successful transition.
            if (ready(value)) return value;
            if (WaitHandle.WaitAny(waits) == 1) cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
