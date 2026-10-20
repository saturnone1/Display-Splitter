using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.ComponentModel;
using DesktopSplitter.Models;

namespace DesktopSplitter.Views;

/// <summary>
/// The keep-or-revert safety dialog. This monotonic countdown is cosmetic; ConfirmationSession
/// owns the safety deadline independently of UI rendering.
/// </summary>
public partial class CountdownWindow : Window
{
    private long? _started;
    private bool _answered;

    public CountdownWindow(int timeoutSeconds) : this(timeoutSeconds, null, null) { }

    public CountdownWindow(int timeoutSeconds, string? heading, string? detail)
    {
        InitializeComponent();
        Services.AppAppearance.AttachWindow(this);

        if (!string.IsNullOrEmpty(heading)) HeadingText.Text = heading;
        if (!string.IsNullOrEmpty(detail)) DetailText.Text = detail;

        TimeoutSeconds = Math.Max(1, timeoutSeconds);
        UpdateCountdown();
    }

    public int TimeoutSeconds { get; }

    /// <summary>True when the user pressed Keep, false when they pressed Revert, null on timeout.</summary>
    public bool? Decision { get; private set; }

    /// <summary>Raised when the user answers. Argument is true for keep.</summary>
    public event Action<bool>? Answered;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _started = TimeProvider.System.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        Activate();
    }

    /// <summary>
    /// Centres the dialog on whichever display is primary. For a general display change that is
    /// the right place: it is where the user is looking, and the countdown auto-reverts anyway,
    /// so it does not need to dodge the affected monitor.
    /// </summary>
    public void PlaceOnPrimary()
    {
        try
        {
            MonitorInfo? primary = DesktopSplitter.Interop.DisplayConfig.Enumerate()
                .FirstOrDefault(m => m.IsPrimary);
            if (primary is not null) PlaceOn(primary);
        }
        catch
        {
            // Leave it wherever WPF put it rather than failing the confirmation.
        }
    }

    /// <summary>
    /// Positions the window on <paramref name="monitor"/> using PHYSICAL pixels, sidestepping
    /// WPF's DIP coordinates entirely — Left/Top are unreliable across monitors with different
    /// scaling, and this dialog must land precisely on the non-target display.
    /// </summary>
    public void PlaceOn(MonitorInfo monitor)
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            helper.EnsureHandle();
            hwnd = helper.Handle;
        }
        if (hwnd == IntPtr.Zero) return;

        // Ask the window how big it actually is in physical pixels, then centre that.
        if (!GetWindowRect(hwnd, out RECT current)) return;
        int width = Math.Max(1, current.right - current.left);
        int height = Math.Max(1, current.bottom - current.top);

        PixelRect target = CountdownPlacement.Center(monitor, width, height);

        SetWindowPos(hwnd, HWND_TOPMOST, target.X, target.Y, target.W, target.H,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void UpdateCountdown()
    {
        double remaining = TimeoutSeconds - (_started.HasValue ? TimeProvider.System.GetElapsedTime(_started.Value).TotalSeconds : 0);
        if (remaining < 0) remaining = 0;

        CountdownText.Text = Math.Ceiling(remaining).ToString("0");

        if (remaining <= 0 && !_answered)
        {
            CompositionTarget.Rendering -= OnRendering;
            CountdownCaption.Text = "복원 중…";
            KeepButton.IsEnabled = false;
            RevertButton.IsEnabled = false;
        }
    }

    /// <summary>Closes the dialog because the helper finished on its own.</summary>
    public void CloseFromHelper()
    {
        CompositionTarget.Rendering -= OnRendering;
        _answered = true;
        try { Close(); } catch (InvalidOperationException) { /* already closing */ }
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => Answer(true);

    private void Revert_Click(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool keep)
    {
        if (_answered) return;
        _answered = true;
        CompositionTarget.Rendering -= OnRendering;
        Decision = keep;

        KeepButton.IsEnabled = false;
        RevertButton.IsEnabled = false;
        CountdownCaption.Text = keep ? "설정 유지 중…" : "복원 중…";

        Answered?.Invoke(keep);
    }

    private void OnRendering(object? sender, EventArgs e) => UpdateCountdown();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_answered) Answer(false);
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ P/Invoke

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
