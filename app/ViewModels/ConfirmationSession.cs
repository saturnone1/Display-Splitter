namespace DesktopSplitter.ViewModels;

/// <summary>Late/duplicate answers remain harmless after cancellation or expiry.</summary>
public sealed class ConfirmationSession
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsCompleted => _answer.Task.IsCompleted;
    public void Keep() => _answer.TrySetResult(true);
    public void Revert() => _answer.TrySetResult(false);
    public async Task<bool> WaitForDecisionAsync(CancellationToken cancellationToken)
    {
        try { return await _answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Revert(); return false; }
    }

    public async Task<bool> WaitAsync(TimeSpan safetyTimeout, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        if (safetyTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(safetyTimeout));
        try { return await _answer.Task.WaitAsync(safetyTimeout, clock ?? TimeProvider.System, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { Revert(); return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Revert(); return false; }
    }
}
