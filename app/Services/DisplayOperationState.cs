namespace DesktopSplitter.Services;

public enum DisplayOperationState
{
    Idle, Applying, AwaitingConfirmation, Reverting, Reordering, Restoring, Completed, Cancelled, Failed
}
