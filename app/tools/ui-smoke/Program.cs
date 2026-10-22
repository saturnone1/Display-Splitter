using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using System.Reflection;
using System.Windows.Interop;
using DesktopSplitter.ViewModels;
using DesktopSplitter.Services;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Views;
using Wpf.Ui.Appearance;
using FluentButton = Wpf.Ui.Controls.Button;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

// No Window.Show, driver mutation, or OS setting changes. Rendering uses inert
// fixtures; lifecycle checks use the real view model in its guarded preview mode.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--shortcuts")) { ShortcutChecks.Run(); return 0; }
            if (args.Contains("--stability")) { StabilityChecks.Run(); return 0; }
            if (args.Contains("--matrix")) { MatrixChecks.Run(); return 0; }
            if (args.Contains("--matrix-ui")) return CheckMatrixUi();
            if (args.Contains("--live-sync")) { LiveSyncChecks.Run(); return 0; }
            if (args.Contains("--free-layout")) { FreeLayoutChecks.Run(); return 0; }
            if (args.Contains("--win10-fixes")) { Win10FixChecks.Run(); return 0; }
            if (args.Contains("--refactor")) { RefactorChecks.Run(); return 0; }
            if (args.Contains("--performance")) { RefactorChecks.Benchmark(); return 0; }
            if (args.Contains("--confirmation-ui")) return CheckConfirmationUi();
            if (args.Contains("--appearance")) return CheckAppearance();
            if (args.Contains("--shortcut-diagnostics"))
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Settings = (SettingsStore.Load()?.Shortcuts ?? new ShortcutSettings()).Copy(),
                    Targets = SplitWindowMover.ReadTargets()
                }));
                return 0; // read-only enumeration; no key registration or window operations
            }
            if (args.Contains("--driver-status"))
            {
                var status = DriverClient.GetStatus(); // GET only; never applies or restores
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status.Version, status.ActiveMonitorCount,
                    ProtocolMatches = status.Version == DriverClient.DESKSPLIT_PROTOCOL_VERSION
                }));
                return status.Version == DriverClient.DESKSPLIT_PROTOCOL_VERSION ? 0 : 1;
            }
            if (args.Contains("--state-checks")) return CheckStatusFlows();
            if (args.Contains("--hiding-ui")) return CheckHidingDetails();
            if (args.Contains("--error-reporter")) return CheckErrorReporter();
            if (args.Any(arg => arg.StartsWith("--lifecycle-", StringComparison.Ordinal)))
                return CheckWindowLifetime(args.Contains("--lifecycle-exit"), args.Contains("--lifecycle-preview"), args.Contains("--lifecycle-manual"));

            var app = new DesktopSplitter.App(suppressStartup: true);
            app.InitializeComponent();
            string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
            Directory.CreateDirectory(output);

            foreach (ApplicationTheme theme in new[]
                     { ApplicationTheme.Light, ApplicationTheme.Dark, ApplicationTheme.HighContrast })
            {
                AppAppearance.Apply(theme == ApplicationTheme.HighContrast);
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (string key in new[] { "ApplicationBackgroundBrush", "TextFillColorPrimaryBrush", "AccentButtonBackground" })
                    Require(app.FindResource(key) is Brush, $"{theme}: missing brush {key}");

                var keep = new CountdownWindow(15, "이 화면 설정을 유지할까요?", "적용한 설정: 좌우 2분할 · 2560 × 1440.");
                var keepButton = (FluentButton)keep.FindName("KeepButton");
                Require(keepButton.Appearance == Wpf.Ui.Controls.ControlAppearance.Primary,
                    $"{theme}: confirmation button lost its primary appearance");
                bool? answer = null;
                keep.Answered += value => answer = value;
                Render(keep, Path.Combine(output, $"countdown-{theme}.png"));
                keepButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(answer == true && keep.Decision == true, $"{theme}: Keep event is disconnected");
                Require(!keepButton.IsEnabled, $"{theme}: Keep can be submitted twice");
                keep.CloseFromHelper();

                var revert = new CountdownWindow(15);
                answer = null;
                revert.Answered += value => answer = value;
                ((FluentButton)revert.FindName("RevertButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(answer == false && revert.Decision == false, $"{theme}: Revert event is disconnected");
                revert.CloseFromHelper();

                var dismissed = new CountdownWindow(15);
                answer = null;
                dismissed.Answered += value => answer = value;
                dismissed.Close();
                Require(answer == false && dismissed.Decision == false, $"{theme}: Closing confirmation did not decline it");

                var manual = new ManualSpecializationWindow(
                    new MonitorInfo { DeviceName = "TEST", FriendlyName = "검토용 모니터" }, true,
                    "Windows 설정에서 직접 변경해야 합니다.");
                bool? completed = null;
                manual.Finished += value => completed = value;
                Render(manual, Path.Combine(output, $"manual-{theme}.png"));
                ((FluentButton)manual.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(completed == false && !manual.Succeeded, $"{theme}: Cancel event is disconnected");
                var dismissedGuide = new ManualSpecializationWindow(new MonitorInfo { DeviceName = "TEST" }, true, "fixture");
                completed = null;
                dismissedGuide.Finished += value => completed = value;
                dismissedGuide.Close();
                Require(completed == false, $"{theme}: Closing guide left its caller waiting");
                RenderMainLayout(theme, output);
                Console.WriteLine($"PASS {theme}: compiled resources, offscreen rendering, Keep/Revert/Cancel events");
            }
            Console.WriteLine($"Rendered previews: {output}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int CheckMatrixUi()
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        string? before = File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null;
        var monitor = new MonitorInfo { DeviceName = "MATRIX-FIXTURE", MonitorDevicePath = "MATRIX-FIXTURE-PATH", FriendlyName = "매트릭스 검토용 모니터", CurrentWidth = 2560, CurrentHeight = 1440, MaxRefreshHz = 144 };
        var targets = Enumerable.Range(1,4).Select(slot=>new SplitTarget(slot,$"FIXTURE-V{slot}",new(0,0,1280,720),new(0,0,1280,720),slot)).ToArray();
        var snapshot = new DesktopSnapshot(new[] { monitor }, "검토용 상태", "검토용 상태", 4, monitor.MonitorDevicePath, DesktopSplitter.Models.LayoutKind.Grid2x2, targets, SelectedSlot: 3);
        var view = new MainViewModel(previewOnly: true, statusReader: () => snapshot);
        CompleteOnDispatcher(view.Initialization);
        view.SelectedLayout = LayoutDefinition.FromKind(DesktopSplitter.Models.LayoutKind.Grid2x2);
        view.SelectedPrimarySlot = 3;
        var window = new DesktopSplitter.MainWindow(view, null);
        // MainWindow loads preferences; restore the local fixture choice without executing its command.
        view.SelectedPrimarySlot = 3;
        Require(view.PrimaryChoices.SequenceEqual(new[] { 1, 2, 3, 4 }) && view.Zones.Single(zone => zone.IsPrimary).Number == 3,
            "Representative selection/badge is disconnected");
        Require(!view.SetPrimaryCommand.CanExecute(null), "Preview enabled a live matrix operation");
        var panel = (FrameworkElement)window.FindName("MatrixPanel");
        string output = Path.GetFullPath("artifacts/ui-smoke-matrix");
        Directory.CreateDirectory(output);
        foreach (ApplicationTheme theme in new[] { ApplicationTheme.Dark, ApplicationTheme.HighContrast }) {
            AppAppearance.Apply(theme == ApplicationTheme.HighContrast);
            Render(window, Path.Combine(output, $"main-{theme}.png"));
            panel.BringIntoView();
            Render(window, Path.Combine(output, $"matrix-{theme}.png"));
        }
        // Simulate a real topology notification arriving during an operation. No native actions.
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(view, true);
        snapshot = snapshot with { ActiveVirtualCount = 0 }; // Keep stale saved group info deliberately.
        view.OnDisplayChanged();
        CompleteOnDispatcher(app.Dispatcher.InvokeAsync(async () => {
            typeof(MainViewModel).GetMethod("EndOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            await view.RefreshMonitorsAsync();
        }).Task.Unwrap());
        Require(!view.IsActiveMatrix && !view.PrimaryChoices.Any() && view.Zones.All(zone => !zone.IsPrimary)
            && view.DisplayStateText.Contains("원본 화면") && view.MatrixStateText.Contains("선택 칸이 없습니다")
            && view.IsOriginalScreen,
            $"Merge notification left stale split state or representative numbers: active={view.IsActiveMatrix}, choices={view.PrimaryChoices.Count()}, badges={view.Zones.Count(zone => zone.IsPrimary)}, state={view.DisplayStateText}, matrix={view.MatrixStateText}, log={view.StatusText}");
        AppAppearance.Apply(false);
        window.UpdateLayout();
        Require(((FrameworkElement)window.FindName("SplitEditorContent")).Visibility == Visibility.Visible
            && window.FindName("OriginalScreenPreview") is null && window.FindName("LiveSlotsList") is null,
            "Merged view hid the editor or retained the removed current-state panel");
        panel.BringIntoView();
        Render(window, Path.Combine(output, "merged.png"));
        snapshot = snapshot with { ActiveVirtualCount = 4 };
        CompleteOnDispatcher(app.Dispatcher.InvokeAsync(() => view.RefreshMonitorsAsync()).Task.Unwrap());
        view.SelectedPrimarySlot = 3;
        Require(view.IsActiveMatrix && view.PrimaryChoices.Count() == 4 && view.Zones.Single(zone => zone.IsPrimary).Number == 3,
            "Re-split did not restore live matrix controls");
        view.SelectedLayout = LayoutDefinition.FromKind(DesktopSplitter.Models.LayoutKind.TwoVertical);
        Require(!view.IsActiveMatrix && !view.PrimaryChoices.Any() && view.Zones.All(zone => !zone.IsPrimary),
            "Another layout displayed the running group's representative");
        Require(before == (File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null), "Matrix preview changed saved user settings");
        window.Close();
        Console.WriteLine("PASS matrix UI: four choices, representative badge, charcoal/high contrast, preview command guards and unchanged product settings; offscreen only");
        return 0;
    }

    private static int CheckConfirmationUi()
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        var clock = new StabilityChecks.ManualClock();
        var ui = new ConfirmationUiFixture();
        var confirmer = new ApplyConfirmer(ui, _ => { }, clock);
        var answer = Task.Run(() => confirmer.ConfirmApply("fixture", 15, default));
        clock.Armed.Wait();
        // Intentionally do not pump the dispatcher: safety must work while UI is blocked.
        clock.Advance(TimeSpan.FromSeconds(15));
        Require(!answer.GetAwaiter().GetResult(), "Blocked dispatcher prevented safety expiry");
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Require(ui.Shown == 0, "Expired confirmation appeared after UI resumed");
        Console.WriteLine("PASS confirmation UI: fake-clock expiry while dispatcher is blocked, no late dialog or display action");
        return 0;
    }

    private sealed class ConfirmationUiFixture : IHidingUi
    {
        public int Shown { get; private set; }
        public bool Confirm(string title, string message) => throw new InvalidOperationException("Unexpected prompt");
        public void ShowOutcome(string title, string message, bool success) => throw new InvalidOperationException("Unexpected outcome");
        public ICountdownHandle ShowApplyCountdown(string summary, int seconds, Action onKeep, Action onRevert)
        { Shown++; throw new InvalidOperationException("Expired UI work must be skipped"); }
        public ICountdownHandle ShowCountdown(int seconds, MonitorInfo? placeOn, Action onKeep, Action onRevert) => throw new InvalidOperationException("Unexpected hiding UI");
        public IGuidedHandle ShowGuidedSettings(MonitorInfo target, bool wantEnabled, string reason, MonitorInfo? placeOn, Action<bool> onFinished) => throw new InvalidOperationException("Unexpected guide");
    }

    private static int CheckWindowLifetime(bool explicitExit, bool previewShutdown, bool manualReview)
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        app.ShutdownMode = previewShutdown ? ShutdownMode.OnMainWindowClose : ShutdownMode.OnExplicitShutdown;
        var viewModel = new MainViewModel(previewOnly: !manualReview, manualReview: manualReview);
        CompleteOnDispatcher(viewModel.Initialization);
        if (manualReview)
        {
            Require(viewModel.RevertCommand.CanExecute(null), "Manual review must leave Revert enabled");
            Require(viewModel.CanApply == (viewModel.SelectedMonitor is not null && viewModel.SelectedLayout is not null
                && viewModel.Segments.Count == viewModel.SelectedLayout.SegmentCount && viewModel.Segments.All(s => s.IsValid)),
                "Manual review must use normal Apply eligibility");
            CompleteOnDispatcher(viewModel.AutoApplyOnStartupAsync());
            CompleteOnDispatcher(viewModel.RevertOnExitAsync());
        }
        else
            Require(!viewModel.CanApply && !viewModel.RevertCommand.CanExecute(null),
                "Preview must block display mutations");
        var window = new DesktopSplitter.MainWindow(viewModel, null);
        app.MainWindow = window;
        // Bind the normal App-owned objects without running OnStartup/Show or creating a tray icon.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DesktopSplitter.App).GetField("_viewModel", flags)!.SetValue(app, viewModel);
        typeof(DesktopSplitter.App).GetField("_window", flags)!.SetValue(app, window);
        new WindowInteropHelper(window).EnsureHandle(); // hidden native source; never shown
        bool closed = false;
        bool exited = false;
        app.Exit += (_, _) => exited = true;
        window.Closed += (_, _) => closed = true;

        // Cancellation must leave the application alive and allow a subsequent close.
        System.ComponentModel.CancelEventHandler cancel = (_, e) => e.Cancel = true;
        window.Closing += cancel;
        window.Close();
        Require(!closed, "Cancelled close destroyed the window");
        Require(!(bool)typeof(DesktopSplitter.App).GetProperty("IsExiting", flags)!.GetValue(app)!,
            "Cancelled close started application exit");
        window.Closing -= cancel;

        if (explicitExit)
            typeof(DesktopSplitter.App).GetMethod("ExitApplication", flags)!.Invoke(app, null);
        else
            window.Close();

        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Require(closed && exited, "Window close did not complete application shutdown");
        Console.WriteLine($"PASS lifecycle {(explicitExit ? "explicit exit" : "window close")}: cancelled close, hidden-window close, application exit; no display changes");
        return 0;
    }

    internal static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        // The unshown window has no presentation source. Supply the same inherited
        // foreground and opaque background its native window template would provide.
        TextElement.SetForeground(root, (Brush)Application.Current.FindResource("WindowForeground"));
        var background = (Brush)Application.Current.FindResource("ApplicationBackgroundBrush");
        if (root is System.Windows.Controls.Border border) border.Background = background;
        if (root is System.Windows.Controls.Panel panel) panel.Background = background;
        double width = window.Width;
        root.Measure(new Size(width, double.IsNaN(window.Height) ? double.PositiveInfinity : window.Height));
        double height = double.IsNaN(window.Height) ? root.DesiredSize.Height : window.Height;
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Require(root.ActualWidth > 0 && root.ActualHeight > 0, "Empty dialog layout");

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int CheckStatusFlows()
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var monitor = new MonitorInfo { DeviceName = "TEST", FriendlyName = "상태 검증용 모니터" };
        HidingSnapshot Snapshot(uint raw, bool intent = false, bool unknown = false)
        {
            var state = new SpecializationState(raw);
            var support = new StatusRow("지원 여부", state.Available ? CheckState.Ok : CheckState.No, "지원 여부");
            var current = new StatusRow("현재 실제 숨김 상태", unknown ? CheckState.Unknown : CheckState.Info, "현재 상태");
            return new HidingSnapshot(support, support, current, new StatusRow("예약", CheckState.Info, "예약"),
                false, state.Available, state.EnabledNow, intent);
        }
        void Complete(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => app.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }

        uint bits = 6; // Both support flags on, actual-hidden flag off.
        bool intent = true;
        var hiding = new HidingViewModel(() => monitor, _ => { }, query: _ => Snapshot(bits, intent));
        Complete(hiding.RefreshAsync());
        Require(!hiding.IsChecking && hiding.Summary.Contains("숨겨져 있지 않음"), "Capability/intent was mistaken for actual hiding");
        Require(hiding.IntentOn && hiding.CanDisable, "Pending hiding intent was lost");
        bits = 7;
        Complete(hiding.RefreshAsync());
        Require(hiding.Summary == "현재 모니터가 숨겨져 있음", "Actual hiding state was not displayed");
        bits = 6;
        intent = false;
        Complete(hiding.RefreshAsync());
        Require(hiding.CanEnable && !hiding.CanDisable, "Read-only refresh did not restore intent controls");

        var unknown = new HidingViewModel(() => monitor, _ => { }, query: _ => Snapshot(0, unknown: true));
        Complete(unknown.RefreshAsync());
        Require(unknown.Summary == "현재 상태 확인 불가", "Unsupported GET was reported as a known state");
        var failed = new HidingViewModel(() => monitor, _ => { }, query: _ => throw new InvalidOperationException("test failure"));
        Complete(failed.RefreshAsync());
        Require(!failed.IsChecking && !failed.CanEnable && failed.DisabledReason.Contains("test failure"), "Query failure left stale/busy state");

        using var release = new System.Threading.ManualResetEventSlim();
        int queries = 0;
        using var queryStarted = new System.Threading.ManualResetEventSlim();
        var timed = new HidingViewModel(() => monitor, _ => { }, query: _ =>
        {
            System.Threading.Interlocked.Increment(ref queries);
            queryStarted.Set();
            release.Wait();
            return Snapshot(7);
        });
        try
        {
            Task pending = timed.RefreshAsync();
            queryStarted.Wait();
            timed.CancelRefresh(); Complete(pending);
            Require(!timed.IsChecking && timed.Summary == "상태 조회 취소됨", "Cancellation left a perpetual checking state");
            Task repeated = timed.RefreshAsync();
            timed.CancelRefresh(); Complete(repeated);
            Require(queries == 1, "Cancelled probe accumulated blocked workers");
        }
        finally { release.Set(); }

        if (!DriverClient.TryFindDevicePath(out _))
        {
            bool blocked = false;
            try { ApplyService.CheckPrerequisites(); }
            catch (DriverNotFoundException) { blocked = true; }
            Require(blocked, "Missing driver did not block read-only preflight");
            Console.WriteLine("PASS missing-driver preflight: read-only rejection before the mutation pipeline");
        }
        Console.WriteLine("PASS hiding: capability vs actual state, intent, refresh, unknown, exception, cancellation and bounded workers; no SET calls");
        return 0;
    }

    private static int CheckHidingDetails()
    {
        string? originalSettings = File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null;
        int originalMonitorCount = DisplayConfig.EnumerateVirtual().Count;
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        var viewModel = new MainViewModel(previewOnly: true);
        CompleteOnDispatcher(viewModel.Initialization);
        var window = new DesktopSplitter.MainWindow(viewModel, null);
        var details = (System.Windows.Controls.Expander)window.FindName("HidingDetailsExpander");
        Require(details is not null, "Hiding details expander not found");
        for (int i = 0; i < viewModel.Hiding.Rows.Count; i++)
            viewModel.Hiding.Rows[i].Update(new StatusRow($"상태 {i + 1}", CheckState.Info, "읽기 전용 상태 · 디스플레이 변경 없음"));
        details!.IsExpanded = true;
        string output = Path.GetFullPath("artifacts/ui-smoke-hiding");
        Directory.CreateDirectory(output);
        foreach (ApplicationTheme theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark, ApplicationTheme.HighContrast })
        {
            AppAppearance.Apply(theme == ApplicationTheme.HighContrast);
            Render(window, Path.Combine(output, $"expanded-{theme}.png"));
            viewModel.Hiding.Rows[0].Update(new StatusRow("갱신된 상태", CheckState.Ok, "지원함"));
            Render(window, Path.Combine(output, $"updated-{theme}.png"));
            details.IsExpanded = false;
            window.UpdateLayout();
            details.IsExpanded = true;
        }
        var shortcuts = (System.Windows.Controls.Expander)window.FindName("ShortcutsExpander");
        details.IsExpanded = false;
        shortcuts.IsExpanded = true;
        var animationFrame = new DispatcherFrame();
        var animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        animationTimer.Tick += (_, _) => { animationTimer.Stop(); animationFrame.Continue = false; };
        animationTimer.Start();
        Dispatcher.PushFrame(animationFrame);
        foreach (ApplicationTheme theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark, ApplicationTheme.HighContrast })
        {
            AppAppearance.Apply(theme == ApplicationTheme.HighContrast);
            Render(window, Path.Combine(output, $"shortcuts-{theme}.png"));
            shortcuts.BringIntoView();
            Render(window, Path.Combine(output, $"shortcuts-{theme}.png"));
        }
        var appKeys = (System.Windows.Controls.Expander)window.FindName("AppShortcutsExpander");
        var windowKeys = (System.Windows.Controls.Expander)window.FindName("WindowShortcutsExpander");
        windowKeys.IsExpanded = true;
        Render(window, Path.Combine(output, "shortcuts-window-controls.png"));
        appKeys.IsExpanded = true;
        Render(window, Path.Combine(output, "shortcuts-app-controls.png"));
        Console.WriteLine("PASS compiled hiding details: real read-only status rows, expand/collapse, updates and three themes; no commands executed");
        Require(typeof(DesktopSplitter.App).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app) is null,
            "Diagnostic rendering invoked product startup");
        Require(originalSettings == (File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null),
            "Diagnostic rendering changed product settings");
        Require(DisplayConfig.EnumerateVirtual().Count == originalMonitorCount, "Diagnostic rendering changed virtual monitor count");
        window.Close();
        return 0;
    }

    private static int CheckErrorReporter()
    {
        var reporter = new UiErrorReporter();
        var error = new InvalidOperationException("read-only Glyph binding");
        int records = 0, notifications = 0;
        bool nestedShown = true;
        Require(reporter.Report(error, _ => records++, _ =>
        {
            notifications++;
            nestedShown = reporter.Report(new InvalidOperationException("nested layout error"), _ => records++, _ => notifications++);
        }), "Initial UI error was not reported");
        Require(!nestedShown && records == 1 && notifications == 1, "UI error reporting re-entered itself");
        Require(!reporter.Report(error, _ => records++, _ => notifications++), "Identical error was not throttled");
        Require(reporter.Report(new InvalidOperationException("different error"), _ => records++, _ => notifications++), "Distinct error was hidden");
        Require(reporter.Report(new Exception("failing notification"), _ => throw new IOException("log unavailable"), _ => throw new Exception("notification unavailable")),
            "Reporting callback failure escaped the handler");
        Require(reporter.Report(new Exception("next error"), _ => records++, _ => notifications++), "Reporting gate was not released after a callback failure");
        Console.WriteLine("PASS UI error reporting: nested errors, duplicate throttling, distinct errors and failing log/notification callbacks; no windows shown");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static int CheckAppearance()
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        AppAppearance.Apply(false);
        Require(((SolidColorBrush)app.FindResource("ApplicationBackgroundBrush")).Color == Color.FromRgb(32,34,37), "Charcoal background was not applied");
        var family = (FontFamily)app.FindResource("AppFontFamily");
        using (var menuFonts = new BundledMenuFonts())
            Require(menuFonts.Regular?.FontFamily.GetName(1033) == "NanumSquareRound Regular"
                && menuFonts.Bold?.FontFamily.GetName(1033) == "NanumSquareRound Bold", "Tray fonts fell back to OS fonts");
        Require(family.GetTypefaces().Any(face => face.TryGetGlyphTypeface(out var glyph)
            && glyph.FontUri.ToString().Contains("NanumSquareRoundR.ttf", StringComparison.OrdinalIgnoreCase)
            && glyph.CharacterToGlyphMap.ContainsKey(0xAC00)), "Embedded Korean font was missing or fell back to a system font");
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/DesktopSplitter;component/Assets/App.ico")).Stream)
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Require(decoder.Frames.Count == 8 && decoder.Frames.Any(frame => frame.PixelWidth == 16)
                && decoder.Frames.Any(frame => frame.PixelWidth == 256), "Shared icon resolutions missing");
        }
        var vm = new MainViewModel(previewOnly: true);
        CompleteOnDispatcher(vm.Initialization);
        var window = new DesktopSplitter.MainWindow(vm, null);
        string output = Path.GetFullPath("artifacts/ui-smoke-charcoal");
        Directory.CreateDirectory(output);
        Render(window, Path.Combine(output, "main.png"));
        Require(window.Icon is not null && window.FontFamily.Source == family.Source, "Window icon/font missing");
        Require(((System.Windows.Controls.TextBlock)window.FindName("VersionLabel")).FontFamily.Source == family.Source, "Text controls did not inherit the embedded font");
        ((System.Windows.Controls.Expander)window.FindName("ShortcutsExpander")).IsExpanded = true;
        Render(window, Path.Combine(output, "shortcuts.png"));
        ((System.Windows.Controls.Expander)window.FindName("ShortcutsExpander")).BringIntoView();
        Render(window, Path.Combine(output, "shortcuts.png"));
        ((System.Windows.Controls.Expander)window.FindName("ShortcutsExpander")).IsExpanded = false;
        var preferences = (System.Windows.Controls.Expander)window.FindName("SafetyPreferencesExpander");
        preferences.IsExpanded = true;
        preferences.BringIntoView();
        Render(window, Path.Combine(output, "safety-preferences.png"));
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(vm, true);
        window.Width = 960;
        Render(window, Path.Combine(output, "busy-compact.png"));
        var cancelButton = (FluentButton)window.FindName("CancelOperationButton");
        Require(cancelButton.Visibility == Visibility.Visible, "Busy view hides cancellation");
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(vm, false);
        Require(cancelButton.Visibility == Visibility.Collapsed, "Idle view retains unnecessary cancellation button");
        AppAppearance.Apply(true);
        Require(!app.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString.EndsWith("Themes/Charcoal.xaml", StringComparison.Ordinal) == true), "Custom palette masked high contrast");
        AppAppearance.Apply(false);
        Require(((SolidColorBrush)app.FindResource("ApplicationBackgroundBrush")).Color == Color.FromRgb(32,34,37), "Charcoal palette did not return after high contrast");
        window.Close();
        Console.WriteLine("PASS appearance: embedded Korean glyphs, shared 16..256 icon, compiled text styles, charcoal surfaces and high-contrast fallback; offscreen only");
        return 0;
    }

    internal static void CompleteOnDispatcher(Task task)
    {
        if (!task.IsCompleted) {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void RenderMainLayout(ApplicationTheme theme, string output)
    {
        // Load the actual view as loose XAML with inert sample data. Strip only code-behind
        // hooks so constructing MainViewModel cannot probe/recover real display state.
        XDocument document = XDocument.Load("app/MainWindow.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        document.Root!.Attribute(xaml + "Class")!.Remove();
        document.Descendants().Where(element => element.Name.LocalName == "EventSetter").Remove();
        document.Descendants().Attributes().Where(attribute => attribute.Name.LocalName is
            "PreviewKeyDown" or "GotKeyboardFocus" or "LostKeyboardFocus" or "MouseLeftButtonDown").Remove();
        foreach (XAttribute attribute in document.Descendants().Attributes()
                     .Where(attribute => attribute.Value.StartsWith("{Binding ") && !attribute.Value.Contains("Mode=")))
            attribute.Value = attribute.Value.Insert(attribute.Value.Length - 1, ", Mode=OneWay");
        var window = (Window)XamlReader.Parse(document.ToString());
        var monitor = new { Caption = "검토용 모니터 · 2560 × 1440 · 144 Hz" };
        var layout = LayoutDefinition.All[0];
        window.DataContext = new
        {
            DriverStatus = "드라이버 미설치 · 편집은 가능하며, 실제 분할에는 드라이버 설치가 필요합니다.",
            CompositorStatus = "화면 합성기: 준비됨 · 현재 중지",
            Hiding = new { Summary = "현재 모니터는 숨겨져 있지 않음", Rows = Array.Empty<StatusRow>() },
            Monitors = new[] { monitor }, SelectedMonitor = monitor, IsIdle = true,
            CurrentModeText = "2560 × 1440 @ 144 Hz", RefreshText = "144 Hz",
            Layouts = LayoutDefinition.All, SelectedLayout = layout,
            CanvasWidth = 660.0, CanvasHeight = 220.0,
            Zones = new[]
            {
                new { X = 0.0, Y = 0.0, W = 328.0, H = 220.0, Number = 1, SizeText = "1280 × 1440", HasAspectNote = false },
                new { X = 332.0, Y = 0.0, W = 328.0, H = 220.0, Number = 2, SizeText = "1280 × 1440", HasAspectNote = false },
            },
            Segments = new[]
            {
                new { Title = "화면 1", WidthText = "1280", HeightText = "1440", HasError = false },
                new { Title = "화면 2", WidthText = "1280", HeightText = "1440", HasError = false },
            },
            StatusText = ".NET 10 · WPF UI · offscreen preview",
        };
        Render(window, Path.Combine(output, $"main-{theme}.png"));
        window.Width = 960;
        window.Height = 680;
        Render(window, Path.Combine(output, $"main-compact-{theme}.png"));
        window.Close();
    }
}
