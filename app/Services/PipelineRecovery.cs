namespace DesktopSplitter.Services;

/// <summary>Compensate a partially started pipeline once; never retry recovery recursively.</summary>
public static class PipelineRecovery
{
    public static T Run<T>(Func<Action, T> operation, Action recover)
    {
        bool changed = false;
        try { return operation(() => changed = true); }
        catch (Exception failure) when (changed)
        {
            try { recover(); }
            catch (Exception recoveryFailure) { throw new AggregateException("작업과 이전 상태 복원에 모두 실패했습니다.", failure, recoveryFailure); }
            throw;
        }
    }
}
