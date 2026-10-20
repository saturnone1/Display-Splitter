using System.Diagnostics;
using System.Threading;

namespace DesktopSplitter.Services;

/// <summary>Prevents a failing layout from recursively opening modal error dialogs.</summary>
public sealed class UiErrorReporter
{
    private int _reporting;
    private string? _lastError;
    private long _lastReported;

    public bool Report(Exception exception, Action<string> record, Action<string> notify)
    {
        if (Interlocked.CompareExchange(ref _reporting, 1, 0) != 0) return false;
        try
        {
            string diagnostic = exception.ToString();
            long now = Stopwatch.GetTimestamp();
            if (diagnostic == _lastError && Stopwatch.GetElapsedTime(_lastReported, now) < TimeSpan.FromSeconds(30))
                return false;
            _lastError = diagnostic;
            _lastReported = now;
            try { record(diagnostic); } catch { /* diagnostic I/O must not re-enter the dispatcher handler */ }
            try { notify(exception.GetBaseException().Message); } catch { /* a broken UI cannot report another UI error */ }
            return true;
        }
        finally { Volatile.Write(ref _reporting, 0); }
    }
}
