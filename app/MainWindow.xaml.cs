using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using DesktopSplitter.Models;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;
using DesktopSplitter.Views;

namespace DesktopSplitter;

public partial class MainWindow : Window, IHidingUi
{
    private const int WM_DISPLAYCHANGE = 0x007E;

    private readonly MainViewModel _viewModel;
    private readonly TrayIconService? _tray;
    private bool _displayRefreshQueued;
    private GlobalShortcutService? _shortcuts;
    private bool _shortcutBusy;
    private bool _shortcutEditing;
    private HwndSource? _shortcutSource;
    private PrimaryHighlightWindow? _primaryHighlight;

    public MainWindow() : this(new MainViewModel(), null) { }

    public MainWindow(MainViewModel viewModel, TrayIconService? tray)
    {
        _viewModel = viewModel;
        _tray = tray;

        InitializeComponent();
        AppAppearance.AttachWindow(this);
        DataContext = _viewModel;

        // Windows fires WM_DISPLAYCHANGE several times per topology change.

        _viewModel.MinimizeToTrayRequested += (_, _) => HideToTray();
        _viewModel.PrimaryHighlightRequested += ShowPrimaryHighlight;
        _viewModel.StatusAppended += (_, _) => Dispatcher.BeginInvoke(new Action(() => StatusBox.ScrollToEnd()));

        _viewModel.Hiding.AttachUi(this);
        _viewModel.AttachConfirmer(new ApplyConfirmer(this, _viewModel.AppendStatus));
        _viewModel.LoadPreferences();

        Title = string.IsNullOrEmpty(MainViewModel.VersionText)
            ? "DesktopSplitter"
            : $"DesktopSplitter {MainViewModel.VersionText}";
        VersionLabel.Text = MainViewModel.VersionText;
        if (_viewModel.IsUiPreview) Title += " — UI 미리 보기 · 디스플레이 변경 없음";
        else if (_viewModel.IsManualReview) Title += " — 수동 검토 · 자동 적용 및 복구 꺼짐";
    }

    // ------------------------------------------------------------------ IHidingUi

    public bool Confirm(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                           MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void ShowOutcome(string title, string message, bool success)
        => MessageBox.Show(this, message, title, MessageBoxButton.OK,
                           success ? MessageBoxImage.Information : MessageBoxImage.Warning);

    public ICountdownHandle ShowCountdown(int seconds, MonitorInfo? placeOn, Action onKeep, Action onRevert)
    {
        var window = new CountdownWindow(seconds);
        window.Answered += keep =>
        {
            if (keep) onKeep(); else onRevert();
        };

        // Show first so the handle exists, then move it in physical pixels.
        window.Show();
        if (placeOn is not null) window.PlaceOn(placeOn);
        window.Activate();

        return new CountdownHandle(window);
    }

    public ICountdownHandle ShowApplyCountdown(string summary, int seconds, Action onKeep, Action onRevert)
    {
        var window = new CountdownWindow(
            seconds,
            heading: "이 화면 설정을 유지할까요?",
            detail: $"적용한 설정: {summary}." + Environment.NewLine +
                    "화면이 제대로 보이지 않으면 기다려 주세요. " +
                    "시간이 지나면 자동으로 이전 설정으로 돌아갑니다.");

        window.Answered += keep =>
        {
            if (keep) onKeep(); else onRevert();
        };

        window.Show();
        window.PlaceOnPrimary();
        window.Activate();
        window.Focus();

        return new CountdownHandle(window);
    }

    private sealed class CountdownHandle : ICountdownHandle
    {
        private readonly CountdownWindow _window;

        public CountdownHandle(CountdownWindow window) => _window = window;

        public void CloseFromHelper() => _window.CloseFromHelper();
    }

    public IGuidedHandle ShowGuidedSettings(
        MonitorInfo target, bool wantEnabled, string reason, MonitorInfo? placeOn, Action<bool> onFinished)
    {
        var window = new ManualSpecializationWindow(target, wantEnabled, reason);
        window.Finished += onFinished;

        window.Show();
        if (placeOn is not null) window.PlaceOn(placeOn);
        window.Activate();

        // Take the user straight there — the button is for a second look.
        ManualSpecializationWindow.OpenDisplaySettings();

        return new GuidedHandle(window);
    }

    private sealed class GuidedHandle : IGuidedHandle
    {
        private readonly ManualSpecializationWindow _window;

        public GuidedHandle(ManualSpecializationWindow window) => _window = window;

        public void CloseFromCaller() => _window.CloseFromCaller();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _shortcutSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _shortcutSource?.AddHook(WndProc);
        if (!_viewModel.IsUiPreview && Application.Current is not App { IsDiagnosticHost: true })
        {
            _shortcuts = new GlobalShortcutService(new WindowInteropHelper(this).Handle);
            _shortcuts.Pressed += OnShortcut;
            _viewModel.Shortcuts.AttachRegistrar(_shortcuts.Configure, () => _shortcuts.RegistrationErrors);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_shortcuts?.HandleMessage(msg, wParam, lParam) == true) handled = true;
        if (msg == WM_DISPLAYCHANGE && !_displayRefreshQueued)
        {
            _displayRefreshQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                _displayRefreshQueued = false;
                if (_shortcutSource is not null) _viewModel.OnDisplayChanged();
            }));
        }
        return IntPtr.Zero;
    }

    private async void OnShortcut(ShortcutAction action)
    {
        try { await ProcessShortcutAsync(action); }
        catch (Exception error) { _viewModel.AppendStatus("단축키 실행 실패: " + error.Message); }
    }

    private Task ProcessShortcutAsync(ShortcutAction action)
        => DesktopUiThread.RunAsync(Dispatcher, () => HandleShortcutAsync(action));

    private async Task HandleShortcutAsync(ShortcutAction action)
    {
        _viewModel.Shortcuts.RecordReceived(action);
        if (!ShortcutExecutionPolicy.CanExecute(action, _shortcutEditing, _viewModel.IsBusy, _shortcutBusy))
        {
            _viewModel.AppendStatus("키 입력 중이거나 다른 작업이 진행 중이라 단축키 동작을 건너뜁니다.");
            return;
        }
        if (action == ShortcutAction.ShowApp)
        {
            try
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                bool activated = Activate();
                bool foreground = SetForegroundWindow(new WindowInteropHelper(this).Handle);
                if (!activated && !foreground)
                    _viewModel.AppendStatus("앱 창을 열었지만 Windows가 전경 전환을 허용하지 않았습니다. 작업 표시줄에서 앱을 선택해 주세요.");
            }
            catch (Exception ex) { _viewModel.AppendStatus("앱 창 열기 실패: " + ex.Message); }
            return;
        }
        _shortcutBusy = true;
        try
        {
            switch (action)
            {
                case ShortcutAction.Apply:
                    await _viewModel.ApplyAsync();
                    break;
                case ShortcutAction.Revert:
                    await _viewModel.RevertAsync();
                    break;
                case ShortcutAction.Primary1:
                case ShortcutAction.Primary2:
                case ShortcutAction.Primary3:
                case ShortcutAction.Primary4:
                case ShortcutAction.Primary5:
                case ShortcutAction.Primary6:
                case ShortcutAction.Primary7:
                case ShortcutAction.Primary8:
                case ShortcutAction.Primary9:
                    await _viewModel.SetPrimaryAsync(ShortcutSettings.PrimaryNumber(action)!.Value);
                    break;
                case ShortcutAction.Arrange1:
                case ShortcutAction.Arrange2:
                case ShortcutAction.Arrange3:
                case ShortcutAction.Arrange4:
                case ShortcutAction.Arrange5:
                case ShortcutAction.Arrange6:
                case ShortcutAction.Arrange7:
                case ShortcutAction.Arrange8:
                case ShortcutAction.Arrange9:
                    await _viewModel.ReorderDisplayAsync(ShortcutSettings.ArrangementNumber(action)!.Value);
                    break;
            }
        }
        catch (Exception ex) { _viewModel.AppendStatus("단축키 실행 실패: " + ex.Message); }
        finally { _shortcutBusy = false; }
    }

    private void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox input) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(ShortcutChord.KeyName(key));
        input.SetCurrentValue(TextBox.TextProperty, string.Join("+", parts));
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void Shortcut_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_shortcutEditing) return;
        _shortcutEditing = true;
        _viewModel.Shortcuts.SetEditing(true);
    }

    private void Shortcut_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBox { DataContext: ShortcutRowViewModel }) return;
        _shortcutEditing = false;
        _viewModel.Shortcuts.SetEditing(false);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // WPF can retain logical focus in a textbox after the window loses focus.
        // Never leave global shortcuts suspended while the user works in another app.
        if (!_shortcutEditing) return;
        _shortcutEditing = false;
        _viewModel.Shortcuts.SetEditing(false);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_shortcutEditing || Keyboard.FocusedElement is not TextBox { DataContext: ShortcutRowViewModel }) return;
        _shortcutEditing = true;
        _viewModel.Shortcuts.SetEditing(true);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    // ------------------------------------------------------------------ splitter dragging

    private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ZoneVisual zone }) return;
        Point p = e.GetPosition(EditorSurface);
        _viewModel.CutAt(zone.Index, p.X, p.Y);
        e.Handled = true;
    }

    private void Splitter_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not int id) return;

        Point p = e.GetPosition(EditorSurface);
        _viewModel.BeginSplitterDrag(id, p.X, p.Y);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Splitter_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || !border.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            border.ReleaseMouseCapture();
            return;
        }

        Point p = e.GetPosition(EditorSurface);
        _viewModel.UpdateSplitterDrag(p.X, p.Y);
        e.Handled = true;
    }

    private void Splitter_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.IsMouseCaptured) border.ReleaseMouseCapture();
        _viewModel.EndSplitterDrag();
        e.Handled = true;
    }

    private void Splitter_LostMouseCapture(object sender, MouseEventArgs e)
        => _viewModel.EndSplitterDrag();

    // ------------------------------------------------------------------ window lifetime

    private void HideToTray()
    {
        Hide();
        _tray?.ShowBalloon("DesktopSplitter",
            "트레이에서 실행 중입니다. 아이콘을 마우스 오른쪽 버튼으로 누르면 적용·복원·종료 메뉴가 표시됩니다.");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window normally keeps the app alive in the tray; Exit is explicit. When the
        // user has turned that off, closing the window really exits — and App's teardown reverts.
        bool minimiseToTray = SettingsStore.Load()?.MinimizeToTrayOnClose ?? true;

        if (Application.Current is App { IsExiting: false } && _tray is not null && minimiseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void ShowPrimaryHighlight(WindowBox bounds)
    {
        if (_viewModel.IsUiPreview || Application.Current is App { IsDiagnosticHost: true }) return;
        _primaryHighlight?.Close();
        var highlight = new PrimaryHighlightWindow(bounds);
        _primaryHighlight = highlight;
        highlight.Closed += (_, _) => { if (ReferenceEquals(_primaryHighlight, highlight)) _primaryHighlight = null; };
        try { highlight.Show(); }
        catch { highlight.Close(); throw; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PrimaryHighlightRequested -= ShowPrimaryHighlight;
        _primaryHighlight?.Close();
        _shortcutSource?.RemoveHook(WndProc);
        _shortcutSource = null;
        _viewModel.Shortcuts.DetachRegistrar();
        if (_shortcuts is not null)
        {
            _shortcuts.Pressed -= OnShortcut;
            _shortcuts.Dispose();
        }
        _viewModel.Hiding.CancelRefresh();
        base.OnClosed(e);
        // Closing can be cancelled, and calling Close again from Closing throws.
        // Start application teardown only after this window has actually closed.
        if (Application.Current is App app && !app.IsExiting) app.ExitApplication();
    }
}
