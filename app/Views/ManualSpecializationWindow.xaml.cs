using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using Microsoft.Win32;

namespace DesktopSplitter.Views;

/// <summary>
/// Guides the user through Windows' own "Remove display from desktop" toggle, which drives the
/// same DisplayConfig monitor-specialization mechanism from a privileged context that our
/// in-process SET is refused from.
///
/// It polls the read-only GET probe and closes itself the moment the display actually changes
/// state, so the user never has to tell us they are done. It deliberately lives on a monitor
/// OTHER than the one being hidden — the target goes black while it is off the desktop.
/// </summary>
public partial class ManualSpecializationWindow : Window
{
    private readonly MonitorInfo _target;
    private readonly bool _wantEnabled;

    public ManualSpecializationWindow(MonitorInfo target, bool wantEnabled, string reason)
    {
        InitializeComponent();
        Services.AppAppearance.AttachWindow(this);

        _target = target;
        _wantEnabled = wantEnabled;

        string name = $"{target.DisplayLabel} ({target.DeviceName})";

        HeadingText.Text = wantEnabled ? "Windows 설정에서 모니터 숨기기" : "모니터를 바탕 화면으로 복원";
        StepsText.Text = "1. 아래 디스플레이 설정 열기를 누릅니다.\n" +
            $"2. 설정을 변경할 디스플레이에서 {name}을 선택합니다.\n" +
            (wantEnabled ? "3. 바탕 화면에서 디스플레이 제거를 켭니다.\n\n화면이 잠시 어두워진 뒤 분할 화면이 나타납니다."
                         : "3. 바탕 화면에서 디스플레이 제거를 끕니다.");
        StatusText.Text = wantEnabled ? $"{target.DisplayLabel} 숨김 대기 중…" : $"{target.DisplayLabel} 복원 대기 중…";
        OpenSettingsButton.Content = "디스플레이 설정 열기";
        CancelButton.Content = wantEnabled ? "취소" : "닫기";
        ReasonText.Text = reason;

    }

    /// <summary>True once the display reached the state we were waiting for.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Raised when the wait finishes — true if the display changed, false if cancelled.</summary>
    public event Action<bool>? Finished;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Poll();
        Activate();
    }

    /// <summary>Positions the window on <paramref name="monitor"/> in physical pixels.</summary>
    public void PlaceOn(MonitorInfo monitor)
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero) helper.EnsureHandle();
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        if (!GetWindowRect(hwnd, out RECT current)) return;
        int width = Math.Max(1, current.right - current.left);
        int height = Math.Max(1, current.bottom - current.top);

        PixelRect target = CountdownPlacement.Center(monitor, width, height);
        SetWindowPos(hwnd, HWND_TOPMOST, target.X, target.Y, target.W, target.H,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void Poll()
    {
        if (_finished) return;
        if (!MonitorSpecialization.TryGet(_target, out SpecializationState state, out int rc))
        {
            StatusText.Text = $"디스플레이 상태 조회 실패 (오류 {rc}). " +
                              "Windows 설정에서 직접 변경할 수 있습니다.";
            return;
        }

        if (state.EnabledNow == _wantEnabled)
        {
            Succeeded = true;
            Finish(true);
            return;
        }

        StatusText.Text = _wantEnabled
            ? $"{_target.DisplayLabel} 숨김 대기 중… (현재는 일반 모니터로 표시됨)"
            : $"{_target.DisplayLabel} 복원 대기 중… (현재는 숨겨져 있음)";
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenDisplaySettings();

    /// <summary>Deep-links straight to Settings &gt; System &gt; Display &gt; Advanced display.</summary>
    public static void OpenDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display-advanced") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
            }
            catch { /* the instructions still name the page */ }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);

    /// <summary>Closes the dialog from outside (e.g. the compositor died).</summary>
    public void CloseFromCaller() => Finish(false);

    private bool _finished;

    private void Finish(bool success)
    {
        if (_finished) return;
        _finished = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Succeeded = success;
        Finished?.Invoke(success);
        try { Close(); } catch (InvalidOperationException) { /* already closing */ }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Poll));
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Poll();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (!_finished) { _finished = true; Finished?.Invoke(false); }
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
