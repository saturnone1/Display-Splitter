using DesktopSplitter.Services;

namespace DesktopSplitter.Models;

public sealed record MatrixWindowIdentity(long Handle, int ProcessId, long ProcessStartedUtcTicks, string WindowClass, string MarkerName, long MarkerValue);
public sealed record MatrixWindow(int Slot, MatrixWindowIdentity Identity, WindowBox NormalBounds, WindowBox SourceBounds, WindowBox SourceWork, bool Maximized);
public sealed class MatrixSnapshot
{
    public string PhysicalMonitorPath { get; set; } = "";
    public LayoutKind Layout { get; set; }
    public int SlotCount { get; set; }
    public int PrimarySlot { get; set; } = 1;
    public List<MatrixWindow> Windows { get; set; } = new();
}
