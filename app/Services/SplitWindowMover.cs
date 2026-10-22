using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using LayoutKind = System.Runtime.InteropServices.LayoutKind;

namespace DesktopSplitter.Services;

public readonly record struct WindowBox(int X, int Y, int Width, int Height);
public sealed record MonitorArea(string DeviceName, WindowBox Bounds, WindowBox Work);
public sealed record SplitTarget(int Number, string DeviceName, WindowBox Bounds, WindowBox Work, int SourceNumber = 0);

/// <summary>Moves only the foreground window; never changes display topology or modes.</summary>
public static class SplitWindowMover
{
    public static WindowBox Plan(WindowBox normalWorkspace, WindowBox sourceBounds, WindowBox sourceWork,
        WindowBox targetBounds, WindowBox targetWork)
    {
        if (sourceWork.Width <= 0 || sourceWork.Height <= 0 || targetWork.Width <= 0 || targetWork.Height <= 0)
            throw new ArgumentException("모니터 작업 영역이 올바르지 않습니다.");
        int width = Math.Clamp(normalWorkspace.Width, 1, targetWork.Width);
        int height = Math.Clamp(normalWorkspace.Height, 1, targetWork.Height);
        int sourceX = normalWorkspace.X + sourceWork.X - sourceBounds.X;
        int sourceY = normalWorkspace.Y + sourceWork.Y - sourceBounds.Y;
        double fractionX = Math.Clamp((double)(sourceX - sourceWork.X) / Math.Max(1, sourceWork.Width - normalWorkspace.Width), 0, 1);
        double fractionY = Math.Clamp((double)(sourceY - sourceWork.Y) / Math.Max(1, sourceWork.Height - normalWorkspace.Height), 0, 1);
        int screenX = targetWork.X + (int)Math.Round(fractionX * (targetWork.Width - width));
        int screenY = targetWork.Y + (int)Math.Round(fractionY * (targetWork.Height - height));
        // WINDOWPLACEMENT uses workspace coordinates, not SetWindowPos screen coordinates.
        return new(screenX - (targetWork.X - targetBounds.X), screenY - (targetWork.Y - targetBounds.Y), width, height);
    }

    public static void ExecutePlacement(bool maximized, Action restore, Action place, Action maximize)
    {
        try { if (maximized) restore(); place(); }
        finally { if (maximized) maximize(); }
    }

    public static string NormalizeDevice(string device) => device.TrimEnd('\0', ' ').ToUpperInvariant();

    public static IReadOnlyList<SplitTarget> BuildTargets(IEnumerable<MonitorInfo> monitors, IEnumerable<MonitorArea> areas)
    {
        var available = areas.ToDictionary(a => NormalizeDevice(a.DeviceName));
        var result = new List<SplitTarget>();
        var virtuals = monitors.Where(m => m.IsDeskSplitVirtual).OrderBy(m => m.DeskSplitMonitorIndex < 0 ? int.MaxValue : m.DeskSplitMonitorIndex)
            .ThenBy(m => m.PositionY).ThenBy(m => m.PositionX).ToArray();
        var used = virtuals.Where(m => m.DeskSplitMonitorIndex >= 0).Select(m => m.DeskSplitMonitorIndex + 1).ToHashSet();
        foreach (var monitor in virtuals)
        {
            if (!available.TryGetValue(NormalizeDevice(monitor.DeviceName), out var area)) continue;
            int number = monitor.DeskSplitMonitorIndex + 1;
            if (number <= 0)
            {
                number = Enumerable.Range(1, DriverClient.DESKSPLIT_MAX_MONITORS).FirstOrDefault(n => !used.Contains(n));
                if (number == 0) continue;
                used.Add(number);
            }
            if (result.Any(t => t.Number == number)) throw new InvalidOperationException("분할 화면 번호가 중복되어 이동하지 않았습니다.");
            result.Add(new(number, area.DeviceName, area.Bounds, area.Work, number));
        }
        return result.OrderBy(t => t.Number).ToArray();
    }

    public static SplitTarget? SelectTarget(IReadOnlyList<SplitTarget> targets, int number)
        => targets.FirstOrDefault(t => t.Number == number);

    public static IReadOnlyList<SplitTarget> ReadTargets()
        => SplitDisplayReorder.ResolveTargets(BuildTargets(DisplayConfig.Enumerate(), ReadAreas()));

    public static string ForegroundDevice()
    {
        nint window = GetForegroundWindow();
        if (window == 0) throw new InvalidOperationException("먼저 배치할 화면의 창을 선택해 주세요.");
        return ReadArea(MonitorFromWindow(window, 2)).DeviceName;
    }
    public static MonitorArea ReadWindowArea(nint window) => ReadArea(MonitorFromWindow(window, 2));
    public static string MoveTo(int number)
    {
        if (number is < 1 or > 9) return "화면 번호는 1~9만 사용할 수 있습니다.";
        return MoveCore(number);
    }

    private static string MoveCore(int number)
    {
        nint window = GetForegroundWindow();
        var targets = ReadTargets();
        if (targets.Count == 0) return "실행 중인 분할 화면이 없습니다. 먼저 앱에서 분할을 적용해 주세요.";
        if (targets.All(t => t.Number != number))
            return $"화면 {number}은 실행 중이 아닙니다. 사용 가능한 화면: {string.Join(", ", targets.Select(t => t.Number))}";
        if (window == 0 || window == GetShellWindow() || window == GetDesktopWindow() || IsIconic(window))
            return "이동할 일반 앱 창을 먼저 선택해 주세요.";
        GetWindowThreadProcessId(window, out uint process);
        if (process == (uint)Environment.ProcessId) return "DesktopSplitter 이외의 앱 창을 선택한 뒤 단축키를 눌러 주세요.";
        var className = new StringBuilder(256);
        if (GetClassNameW(window, className, className.Capacity) == 0)
            return "선택한 창의 정보를 읽지 못했습니다.";
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return "바탕 화면과 작업 표시줄은 이동하지 않습니다.";
        if ((GetWindowLongPtrW(window, -20).ToInt64() & 0x80) != 0)
            return "도구 창은 이동하지 않습니다. 일반 앱 창을 선택해 주세요.";
        var source = ReadArea(MonitorFromWindow(window, 2));
        var target = SelectTarget(targets, number)!;
        if (NormalizeDevice(source.DeviceName) == NormalizeDevice(target.DeviceName)) return $"현재 창이 이미 화면 {target.Number}에 있습니다.";
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(window, ref placement)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var old = placement.Normal;
        var next = Plan(new(old.Left, old.Top, old.Right - old.Left, old.Bottom - old.Top),
            source.Bounds, source.Work, target.Bounds, target.Work);
        placement.Normal = new Rect { Left = next.X, Top = next.Y, Right = next.X + next.Width, Bottom = next.Y + next.Height };
        placement.MaxPosition = new Point { X = -1, Y = -1 };
        // Updating only a maximized window's restore rectangle can leave the visible
        // window on its old monitor. Move restored, then maximize on the destination.
        bool maximized = placement.ShowCommand == 3;
        placement.ShowCommand = 4; // SW_SHOWNOACTIVATE
        placement.Flags &= ~2u; // clear WPF_RESTORETOMAXIMIZED during the move
        ExecutePlacement(maximized, () => ShowWindow(window, 9), () =>
        {
            if (!SetWindowPlacement(window, ref placement))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "창을 이동하지 못했습니다. 관리자 권한 앱 등은 이동이 제한될 수 있습니다.");
        }, () => ShowWindow(window, 3));
        return $"현재 창을 화면 {target.Number} ({target.DeviceName})로 이동했습니다.";
    }

    private static MonitorArea ReadArea(nint handle)
    {
        var info = new MonitorInfoNative { Size = Marshal.SizeOf<MonitorInfoNative>(), Device = "" };
        if (!GetMonitorInfoW(handle, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        static WindowBox Box(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return new(info.Device.TrimEnd('\0'), Box(info.Bounds), Box(info.Work));
    }

    public static IReadOnlyList<MonitorArea> ReadAreas()
    {
        var result = new List<MonitorArea>();
        Exception? failure = null;
        MonitorCallback callback = (nint handle, nint dc, ref Rect rect, nint data) =>
        {
            try { result.Add(ReadArea(handle)); return true; }
            catch (Exception ex) { failure = ex; return false; }
        };
        bool ok = EnumDisplayMonitors(0, 0, callback, 0);
        if (failure is not null) throw failure;
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int Length;
        public uint Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect Normal;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoNative
    {
        public int Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoNative info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPlacement(nint window, ref Placement placement);
}
