using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DesktopSplitter.Models;
using NativeLayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace DesktopSplitter.Services;

/// <summary>Called only from an explicit Apply/Revert action. No process launch or TOPMOST operation.</summary>
public sealed class MatrixWindowSystem : IMatrixWindows
{
    public IReadOnlyList<MatrixCandidate> ReadFrontToBack()
    {
        var found = new Dictionary<nint, MatrixCandidate>();
        var processes = new Dictionary<int, (string Name, long Started)>();
        EnumCallback callback = (window, _) => {
            try {
                if (!Eligible(window)) return true;
                var identity = ReadIdentity(window, processes);
                if (identity is null) return true;
                var placement = Placement.New();
                if (!GetWindowPlacement(window, ref placement)) return true;
                var area = SplitWindowMover.ReadWindowArea(window);
                var r = placement.Normal;
                if (r.Right <= r.Left || r.Bottom <= r.Top) return true;
                found[window] = new(identity, area.DeviceName, new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), area, placement.ShowCommand == 3);
            } catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { }
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<MatrixCandidate>();
        var seen = new HashSet<nint>();
        for (nint window = GetTopWindow(0); window != 0 && seen.Add(window); window = GetWindow(window, 2))
            if (found.TryGetValue(window, out var candidate)) result.Add(candidate);
        return result;
    }

    public IReadOnlyList<MatrixCandidate> ReadRemembered(IReadOnlyList<MatrixWindowIdentity> identities)
    {
        var result = new List<MatrixCandidate>();
        foreach (var identity in identities) {
            try {
                nint handle = (nint)identity.Handle;
                if (!IsCurrent(identity) || !Eligible(handle)) continue; // User-minimized BEFORE topology changes stays minimized.
                var placement = Placement.New();
                if (!GetWindowPlacement(handle, ref placement)) continue;
                var area = SplitWindowMover.ReadWindowArea(handle);
                var r = placement.Normal;
                if (r.Right > r.Left && r.Bottom > r.Top)
                    result.Add(new(identity, area.DeviceName, new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), area, placement.ShowCommand == 3));
            } catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { }
        }
        return result;
    }

    private static MatrixWindowIdentity? ReadIdentity(nint window, Dictionary<int, (string Name, long Started)>? cache = null)
    {
        if (!IsWindow(window)) return null;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        var name = new StringBuilder(256);
        if (GetClassNameW(window, name, name.Capacity) == 0) return null;
        int processId = checked((int)pid);
        if (cache is null || !cache.TryGetValue(processId, out var info)) {
            using var process = Process.GetProcessById(processId);
            info = (process.ProcessName, process.StartTime.ToUniversalTime().Ticks);
            cache?.Add(processId, info);
        }
        if (info.Name.Equals("dscomp", StringComparison.OrdinalIgnoreCase)) return null;
        return new((long)window, processId, info.Started, name.ToString(), "", 0);
    }

    private static bool Eligible(nint window)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, 4) != 0) return false;
        var name = new StringBuilder(256);
        if (GetClassNameW(window, name, name.Capacity) == 0) return false;
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "DeskSplitCompositorWindow") return false;
        if ((GetWindowLongPtrW(window, -20).ToInt64() & (0x80 | 0x08000000)) != 0) return false;
        return DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    public MatrixWindowIdentity? Remember(MatrixCandidate candidate, string markerName)
    {
        nint handle = (nint)candidate.Identity.Handle;
        var current = ReadIdentity(handle);
        if (!SameProcessWindow(candidate.Identity, current)) return null;
        if (SplitWindowMover.NormalizeDevice(SplitWindowMover.ReadWindowArea(handle).DeviceName) != SplitWindowMover.NormalizeDevice(candidate.DeviceName)) return null;
        long token = (long)GetPropW(handle, markerName);
        if (token <= 0) {
            token = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue;
            if (token == 0) token = 1;
            if (!SetPropW(handle, markerName, (nint)token)) return null; // Respect UIPI; no elevation workaround.
        }
        return candidate.Identity with { MarkerName = markerName, MarkerValue = token };
    }

    private static bool SameProcessWindow(MatrixWindowIdentity remembered, MatrixWindowIdentity? current)
        => current is not null && remembered.Handle == current.Handle && remembered.ProcessId == current.ProcessId
            && remembered.ProcessStartedUtcTicks == current.ProcessStartedUtcTicks && remembered.WindowClass == current.WindowClass;

    public bool IsCurrent(MatrixWindowIdentity window)
    {
        try {
            nint handle = (nint)window.Handle;
            return window.MarkerName.StartsWith("DesktopSplitter.Matrix.", StringComparison.Ordinal) && window.MarkerValue > 0
                && (long)GetPropW(handle, window.MarkerName) == window.MarkerValue && SameProcessWindow(window, ReadIdentity(handle))
                && IsWindowVisible(handle); // Identity survives OS minimization caused by monitor removal.
        } catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { return false; }
    }

    public bool Place(MatrixWindow window, SplitTarget target)
    {
        if (!IsCurrent(window.Identity)) return false;
        var box = SplitWindowMover.Plan(window.NormalBounds, window.SourceBounds, window.SourceWork, target.Bounds, target.Work);
        var placement = Placement.New();
        placement.Flags = 4; // WPF_ASYNCWINDOWPLACEMENT: a hung app cannot block this caller.
        placement.ShowCommand = window.Maximized ? 3u : 4u;
        placement.MaxPosition = new() { X = -1, Y = -1 };
        placement.Normal = new() { Left = box.X, Top = box.Y, Right = checked(box.X + box.Width), Bottom = checked(box.Y + box.Height) };
        nint handle = (nint)window.Identity.Handle;
        // One queued placement includes both normal bounds and final show state. Avoid racing
        // two async normal/maximize requests against topology and foreground changes.
        return SetWindowPlacement(handle, ref placement);
    }

    public bool BringToFront(MatrixWindowIdentity window)
    {
        if (!IsCurrent(window)) return false;
        nint handle = (nint)window.Handle;
        bool ordered = SetWindowPos(handle, 0, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x4000); // HWND_TOP, no move/resize/activation, async.
        bool activated = SetForegroundWindow(handle); // May be refused by Windows; never bypass foreground restrictions.
        return ordered || activated;
    }

    public void Forget(MatrixWindowIdentity window)
    {
        try {
            if (window.MarkerName.StartsWith("DesktopSplitter.Matrix.", StringComparison.Ordinal)
                && SameProcessWindow(window, ReadIdentity((nint)window.Handle)) && (long)GetPropW((nint)window.Handle, window.MarkerName) == window.MarkerValue)
                RemovePropW((nint)window.Handle, window.MarkerName);
        } catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { }
    }

    [StructLayout(NativeLayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(NativeLayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(NativeLayoutKind.Sequential)] private struct Placement {
        public int Length; public uint Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rect Normal;
        public static Placement New() => new() { Length = Marshal.SizeOf<Placement>() };
    }
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint GetPropW(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPropW(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint RemovePropW(nint window, string name);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
