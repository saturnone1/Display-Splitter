using System.Threading;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

/// <summary>
/// Bridges <see cref="ApplyService"/> (background thread) to the "Keep these display settings?"
/// countdown on the UI thread.
///
/// The window's own timer only animates the number — THIS wait is the authority, and it defaults
/// to revert. That ordering matters: if the change left the user unable to see or click the
/// dialog, doing nothing must undo it.
/// </summary>
public sealed class ApplyConfirmer : IApplyConfirmer
{
    private readonly IHidingUi _ui;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;

    public ApplyConfirmer(IHidingUi ui, Action<string> log, TimeProvider? clock = null)
    {
        _ui = ui;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public bool ConfirmApply(string summary, int timeoutSeconds, CancellationToken ct)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return false;

        var session = new ConfirmationSession();
        ICountdownHandle? handle = null;

        // Start the safety deadline before queuing UI work. A stalled dispatcher cannot delay it.
        Task<bool> decision = session.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct, _clock);
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (session.IsCompleted) return;
                try { handle = _ui.ShowApplyCountdown(summary, timeoutSeconds, session.Keep, session.Revert); }
                catch { session.Revert(); }
            }));
            bool keep = decision.GetAwaiter().GetResult();
            if (!keep) _log("설정 유지를 확인하지 않아 복원합니다.");
            return keep;
        }
        finally
        {
            session.Revert();
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => handle?.CloseFromHelper()));
        }
    }
}
