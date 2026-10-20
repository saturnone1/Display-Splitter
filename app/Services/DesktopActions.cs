using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public interface IDesktopActions
{
    string ForegroundDevice();
    string Reorder(int slot, string device, CancellationToken ct);
    SplitTarget Select(int slot, string physicalPath, LayoutKind layout, CancellationToken ct);
    void Revert(IProgress<string> log, CancellationToken ct);
}

public sealed class DesktopActions : IDesktopActions
{
    public string ForegroundDevice() => SplitWindowMover.ForegroundDevice();
    public string Reorder(int slot, string device, CancellationToken ct) => SplitDisplayReorder.MoveTo(slot, device, ct);
    public SplitTarget Select(int slot, string physicalPath, LayoutKind layout, CancellationToken ct)
        => SplitPrimarySelector.Select(slot, physicalPath, layout, ct);
    public void Revert(IProgress<string> log, CancellationToken ct)
    {
        var matrix = SplitMatrixWorkspace.Capture(log);
        ApplyService.Revert(log, ct);
        SplitMatrixWorkspace.RestoreMerged(matrix, log, ct);
    }
}
