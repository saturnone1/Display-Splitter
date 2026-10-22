namespace DesktopSplitter.Services;

/// <summary>Show the usable workspace before asking to keep it; compensate exactly once.</summary>
public static class AppliedChangeConfirmation
{
    public static bool Run(Action ready, Func<bool> confirm, Action rollback)
    {
        bool keep = PipelineRecovery.Run(changed => {
            changed();
            ready();
            return confirm();
        }, rollback);
        if (!keep) rollback();
        return keep;
    }
}
