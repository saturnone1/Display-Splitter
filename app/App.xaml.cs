using System.Threading;
using System.Windows;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;
using Microsoft.Win32;

namespace DesktopSplitter;

public partial class App : Application
{
    private readonly bool _suppressStartup;
    internal bool IsDiagnosticHost => _suppressStartup;
    public App() : this(false) { }

    /// <summary>Diagnostic hosts can load the real resources without product startup side effects.</summary>
    public App(bool suppressStartup) => _suppressStartup = suppressStartup;
    private TrayIconService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private Task? _teardownTask;
    private readonly UiErrorReporter _uiErrorReporter = new();

    internal bool IsExiting { get; private set; }

    /// <summary>True when Windows auto-started us from the Run key.</summary>
    internal bool LaunchedAtStartup { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (_suppressStartup) return;
        base.OnStartup(e);

        ApplySystemAppearance();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        LaunchedAtStartup = StartupRegistration.LaunchedAtStartup(e.Args);

        DispatcherUnhandledException += (_, args) =>
        {
            // Mark handled BEFORE showing a modal window: its nested dispatcher can
            // encounter the same failed layout again while the first dialog is open.
            args.Handled = true;
            _uiErrorReporter.Report(args.Exception, diagnostic =>
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopSplitter", "logs");
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "ui-errors.log"),
                    $"[{DateTime.Now:O}] {diagnostic}{Environment.NewLine}");
            }, message => MessageBox.Show(
                "화면 표시 중 오류가 발생했습니다:" + Environment.NewLine + message + Environment.NewLine +
                "같은 오류는 30초 동안 반복 표시하지 않습니다. 진단 기록은 로컬 앱 데이터의 DesktopSplitter/logs 폴더에 저장됩니다.",
                "DesktopSplitter", MessageBoxButton.OK, MessageBoxImage.Error));
        };

        // Dying-process handlers only signal cancellation; they cannot safely run display mutations.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _viewModel?.CancelPendingOperations();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _viewModel?.CancelPendingOperations();

        // Logoff / shutdown / restart.
        SystemEvents.SessionEnding += OnSessionEnding;

        bool previewOnly = e.Args.Any(arg => string.Equals(arg, "--ui-preview", StringComparison.OrdinalIgnoreCase));
        bool manualReview = e.Args.Any(arg => string.Equals(arg, "--manual-review", StringComparison.OrdinalIgnoreCase));
        _viewModel = new MainViewModel(previewOnly, manualReview);

        if (previewOnly)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _window = new MainWindow(_viewModel, null);
            MainWindow = _window;
            _window.Show();
            return;
        }

        _tray = new TrayIconService();
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.ApplyLastRequested += async (_, _) =>
        {
            ShowMainWindow();
            await _viewModel.ApplyLastAsync();
        };
        _tray.RevertRequested += async (_, _) => await _viewModel.RevertAsync();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _window = new MainWindow(_viewModel, _tray);

        // Auto-started at logon: go straight to the tray rather than popping the window open.
        if (!LaunchedAtStartup || manualReview) _window.Show();

        if (!manualReview) _ = _viewModel.AutoApplyOnStartupAsync();
    }

    private void ShowMainWindow()
    {
        if (_window is null || IsExiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    internal async void ExitApplication()
    {
        if (Dispatcher.HasShutdownStarted) return;
        await DesktopUiThread.RunAsync(Dispatcher, ExitApplicationCoreAsync);
    }

    private async Task ExitApplicationCoreAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        try { await TeardownAsync().ConfigureAwait(true); }
        catch (Exception error) { _viewModel?.AppendStatus("종료 중 정리 실패: " + error.Message); }
        finally { Shutdown(); }
        // Application.Shutdown owns closing any remaining windows. The main
        // window may already be in Closed when it initiates application exit.
    }

    /// <summary>
    /// Reverts the split on the way out, whichever exit path we took. Idempotent, because several
    /// of these paths can fire for a single shutdown.
    /// </summary>
    private Task TeardownAsync() => _teardownTask ??= _viewModel?.RevertOnExitAsync() ?? Task.CompletedTask;

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ExitApplication));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        IsExiting = true;
        _viewModel?.CancelPendingOperations();

        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!IsExiting && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(ApplySystemAppearance));
    }

    private static void ApplySystemAppearance()
        => AppAppearance.Apply();
}
