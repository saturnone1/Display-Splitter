using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    /// <summary>The editor canvas is fitted into this box, preserving the monitor's aspect.</summary>
    private const double MaxCanvasWidth = 660;
    private const double MaxCanvasHeight = 380;

    private readonly IProgress<string> _progress;
    private readonly object _statusLock = new();
    private readonly StatusLog _statusLog = new();
    private readonly LatestRefresh<DesktopSnapshot> _refresh;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher = System.Windows.Application.Current?.Dispatcher;
    private readonly IDesktopActions _desktopActions;
    private int _liveSelectedSlot;
    public ObservableCollection<string> CurrentSlots { get; } = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private bool _liveSplit;
    private bool _hasDesktopStatus;
    private string? _preferredMonitorPath;
    private string? _preferredMonitorDevice;
    private bool _selectionPending;
    private bool _isSplitEditorExpanded;
    public bool IsSplitEditorExpanded { get => _isSplitEditorExpanded; set => SetProperty(ref _isSplitEditorExpanded, value); }
    public bool IsOriginalScreen => _hasDesktopStatus && !_liveSplit;
    public string OriginalScreenText => SelectedMonitor is null ? "원본 모니터를 확인할 수 없습니다."
        : $"{SelectedMonitor.DisplayLabel}\n{SelectedMonitor.CurrentWidth} × {SelectedMonitor.CurrentHeight}\n원본 화면";
    private string _livePhysicalPath = "";
    private LayoutKind _liveLayout;
    private bool MatchesActiveMatrix => _liveSplit && SelectedMonitor is not null && SelectedLayout is not null
        && !string.IsNullOrWhiteSpace(_livePhysicalPath) && string.Equals(_livePhysicalPath, SelectedMonitor.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)
        && _liveLayout == SelectedLayout.Kind;
    private int _primarySlot = 1;
    private bool _rememberMatrixWindows = true;
    public bool IsActiveMatrix => MatchesActiveMatrix;
    public string DisplayStateText => !_hasDesktopStatus ? "현재 화면 상태 확인 중"
        : _liveSplit ? "현재 상태: 분할 화면 실행 중" : "현재 상태: 원본 화면 · 분할 없음";
    public string MatrixStateText => MatchesActiveMatrix ? "칸을 고른 뒤 ‘현재 그룹에 지정’을 눌러 확정하세요. 단축키는 바로 지정합니다."
        : !_hasDesktopStatus ? "화면 상태를 확인하고 있습니다."
        : !_liveSplit ? "원본 화면에는 선택 칸이 없습니다. 이전 선택 칸은 재분할할 때 사용합니다."
        : "선택한 모니터·분할 방식은 실행 중인 그룹과 다릅니다.";
    public IEnumerable<int> PrimaryChoices => MatchesActiveMatrix
        ? Enumerable.Range(1, SelectedLayout?.SegmentCount ?? 1) : Enumerable.Empty<int>();
    public int SelectedPrimarySlot {
        get => _primarySlot;
        set {
            if (value < 1 || value > (SelectedLayout?.SegmentCount ?? 1)) return;
            if (!SetProperty(ref _primarySlot, value)) return;
            _selectionPending = value != _liveSelectedSlot;
        }
    }
    public bool RememberMatrixWindows {
        get => _rememberMatrixWindows;
        set { if (SetProperty(ref _rememberMatrixWindows, value)) PersistPreferences(); }
    }
    private bool _closing;
    private bool _statusNotificationQueued;
    private readonly FileStatusLog? _fileLog;

    private MonitorInfo? _selectedMonitor;
    private LayoutDefinition? _selectedLayout;
    private string _statusText = string.Empty;
    private string _driverStatus = string.Empty;
    private string _refreshText = "—";
    private string _currentModeText = "—";
    private string _compositorStatus = string.Empty;
    private string _sumNote = string.Empty;
    private double _canvasWidth = MaxCanvasWidth;
    private double _canvasHeight = 200;
    private bool _isBusy;
    private DisplayOperationState _operationState;
    public DisplayOperationState OperationState { get => _operationState; private set => SetProperty(ref _operationState, value); }
    private bool _suppressSegmentRebuild;
    private IApplyConfirmer? _applyConfirmer;
    private SplitSuspensionWatcher? _suspensionWatcher;
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource? _operationFinished;

    /// <summary>Set while the VM itself is writing segment sizes, to stop feedback loops.</summary>
    private bool _editorSuspended;

    // Splitter drag state.
    private SplitterVisual? _dragSplitter;
    private int[] _dragTracks = Array.Empty<int>();
    private double _dragGrabOffset;

    public bool IsUiPreview { get; }
    public bool IsManualReview { get; }
    private bool SuppressAutomaticDisplayChanges => IsUiPreview || IsManualReview;

    public MainViewModel(bool previewOnly = false, bool manualReview = false, Func<DesktopSnapshot>? statusReader = null, IDesktopActions? desktopActions = null)
    {
        _desktopActions = desktopActions ?? new DesktopActions();
        IsUiPreview = previewOnly;
        IsManualReview = manualReview;
        if (!previewOnly && System.Windows.Application.Current is not DesktopSplitter.App { IsDiagnosticHost: true })
            _fileLog = new FileStatusLog(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopSplitter", "logs"));
        // Constructed on the UI thread: Progress<T> captures the dispatcher context.
        _progress = new Progress<string>(AppendStatus);
        Shortcuts = new ShortcutSettingsViewModel(AppendStatus, previewOnly);

        Layouts = new ObservableCollection<LayoutDefinition>(LayoutDefinition.All);
        Monitors = new ObservableCollection<MonitorInfo>();
        Segments = new ObservableCollection<SegmentViewModel>();
        Zones = new ObservableCollection<ZoneVisual>();
        Splitters = new ObservableCollection<SplitterVisual>();
        Hiding = new HidingViewModel(() => SelectedMonitor, AppendStatus, previewOnly);
        _refresh = new LatestRefresh<DesktopSnapshot>(() => Task.Run(statusReader ?? DesktopStatusReader.Read).WaitAsync(_lifetime.Token), snapshot => {
            if (_closing) return;
            if (IsBusy) return; // Every display operation awaits a fresh snapshot after unlocking.
            bool wasSplit = _liveSplit;
            bool firstStatus = !_hasDesktopStatus;
            _hasDesktopStatus = true;
            _liveSplit = snapshot.ActiveVirtualCount > 0;
            if (firstStatus || wasSplit != _liveSplit) IsSplitEditorExpanded = _liveSplit;
            bool sameGroup = wasSplit && _liveSplit && _liveLayout == snapshot.ActiveGroupLayout
                && string.Equals(_livePhysicalPath, snapshot.ActiveGroupPath, StringComparison.OrdinalIgnoreCase);
            _livePhysicalPath = _liveSplit ? snapshot.ActiveGroupPath : "";
            _liveLayout = snapshot.ActiveGroupLayout;
            _liveSelectedSlot = snapshot.SelectedSlot;
            if (!sameGroup || _primarySlot == _liveSelectedSlot) _selectionPending = false;
            var rows = ((_liveSplit ? snapshot.ActiveTargets : null) ?? Array.Empty<SplitTarget>())
                .Select(target => $"칸 {target.Number} ← 화면 {target.SourceNumber} · {target.Bounds.Width} × {target.Bounds.Height}"
                    + (target.Number == _liveSelectedSlot ? " · 선택됨" : "")).ToList();
            if (_liveSplit && !string.IsNullOrEmpty(snapshot.RoutingIssue)) rows.Add(snapshot.RoutingIssue);
            SnapshotCollection.Update(CurrentSlots, rows);
            ApplyMonitorSnapshot(snapshot.Monitors, announce: false);
            DriverStatus = snapshot.DriverStatus;
            CompositorStatus = snapshot.CompositorStatus;
            NotifyMatrixState();
            SetPrimaryCommand?.RaiseCanExecuteChanged();
        });

        RefreshMonitorsCommand = new RelayCommand(async () =>
        {
            await RefreshMonitorsAsync(announce: true);
            if (!_closing) await Hiding.RefreshAsync();
        }, () => _initialized && !IsBusy && !_closing);
        ResetSegmentsCommand = new RelayCommand(() => RebuildSegments(resetSizes: true),
                                                () => !IsBusy && SelectedLayout is not null);
        ApplyCommand = new RelayCommand(async () => await ApplyAsync().ConfigureAwait(false), () => CanApply);
        RevertCommand = new RelayCommand(async () => await RevertAsync().ConfigureAwait(false), () => _initialized && !_closing && !IsUiPreview && !IsBusy);
        CancelOperationCommand = new RelayCommand(CancelPendingOperations, () => _operationCancellation is { IsCancellationRequested: false });
        SetPrimaryCommand = new RelayCommand(async () => await SetPrimaryAsync(SelectedPrimarySlot), () => _initialized && !_closing && !IsBusy && !IsUiPreview && MatchesActiveMatrix);
        MinimizeToTrayCommand = new RelayCommand(() => MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty), () => !IsUiPreview);

        _selectedLayout = Layouts[0];

        AppendStatus(IsUiPreview ? "UI 미리 보기: 디스플레이 변경·복구·시작 프로그램 등록·설정 저장을 하지 않습니다." : "준비되었습니다.");
        if (IsManualReview) AppendStatus("수동 검토 모드: 버튼을 사용할 수 있습니다. 자동 적용·복구·종료 시 자동 복원은 실행하지 않습니다.");

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        Initialization = dispatcher is null ? InitializeAsync()
            : dispatcher.InvokeAsync(InitializeAsync).Task.Unwrap();
    }

    public Task Initialization { get; }

    private async Task InitializeAsync()
    {
        if (_closing) return;
        await RefreshMonitorsAsync(false);
        if (_closing) return;
        RestoreSettings();
        if (!SuppressAutomaticDisplayChanges) await RecoverThenRefreshAsync();
        else _ = Hiding.RefreshAsync();
        if (_closing) return;
        _initialized = true;
        RaiseCommandStates();
    }

    // ------------------------------------------------------------------ bound state

    /// <summary>The "Display hiding (experimental)" panel.</summary>
    public HidingViewModel Hiding { get; }
    public ShortcutSettingsViewModel Shortcuts { get; }

    /// <summary>Informational assembly version, produced by the shared versioning build step.</summary>
    public static string VersionText { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string? informational = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
                ?.InformationalVersion;

            // Strip any "+<commit>" source-revision suffix the SDK appends.
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                return "v" + (plus > 0 ? informational[..plus] : informational);
            }
            return "v" + (assembly.GetName().Version?.ToString() ?? "1.0.0");
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Supplied by the view once it exists; enables the confirmation countdown.</summary>
    public void AttachConfirmer(IApplyConfirmer confirmer) => _applyConfirmer = confirmer;

    // ------------------------------------------------------------------ suspend / resume

    /// <summary>
    /// Starts handing primary back to the physical monitor whenever the compositor reports a
    /// secure desktop, lock screen, or the manual Ctrl+Alt+Shift+S toggle.
    /// </summary>
    private void StartSuspensionWatcher()
    {
        AppSettings? settings = SettingsStore.Load();
        if (settings is null) return;

        string activePrimary = settings.AppliedPrimaryDevice;
        string physicalPrimary = string.IsNullOrEmpty(settings.PreviousPrimaryDevice)
            ? settings.PhysicalDevice
            : settings.PreviousPrimaryDevice;

        _suspensionWatcher ??= new SplitSuspensionWatcher(_progress);
        _suspensionWatcher.Start(activePrimary, physicalPrimary);
    }

    /// <summary>
    /// Stops the watcher. A Revert restores primary itself, so it must not also be restored here —
    /// that would be pointless extra display churn.
    /// </summary>
    private void StopSuspensionWatcher(bool restorePrimary)
    {
        _suspensionWatcher?.Stop(restorePrimary);
    }

    // ------------------------------------------------------------------ preferences

    private bool _startWithWindows;
    private bool _applyOnStartup;
    private int _safetyConfirmationSeconds = AppSettings.DefaultSafetyConfirmationSeconds;

    public int SafetyConfirmationSeconds
    {
        get => _safetyConfirmationSeconds;
        set
        {
            if (value <= 0 || TimeSpan.FromSeconds(value).TotalMilliseconds >= uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), "안전 복원 시간은 양수이며 타이머 지원 범위 안이어야 합니다.");
            if (SetProperty(ref _safetyConfirmationSeconds, value)) PersistPreferences();
        }
    }

    /// <summary>Backed by the HKCU Run key, which is read live so external changes show up.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (IsUiPreview) return;
            if (!SetProperty(ref _startWithWindows, value)) return;
            StartupRegistration.Set(value, _progress);
            PersistPreferences();
            // Re-read: if the write failed, the checkbox must not lie.
            bool actual = StartupRegistration.IsEnabled();
            if (actual != value) SetProperty(ref _startWithWindows, actual, nameof(StartWithWindows));
        }
    }

    /// <summary>Re-apply the saved configuration automatically at startup.</summary>
    public bool ApplyOnStartup
    {
        get => _applyOnStartup;
        set
        {
            if (IsUiPreview) return;
            if (!SetProperty(ref _applyOnStartup, value)) return;
            PersistPreferences();
        }
    }

    /// <summary>Reads preference state from the registry and settings file.</summary>
    public void LoadPreferences()
    {
        if (!SuppressAutomaticDisplayChanges) StartupRegistration.RefreshIfStale(_progress);
        _startWithWindows = StartupRegistration.IsEnabled();
        var preferences = SettingsStore.Load();
        _applyOnStartup = preferences?.ApplyOnStartup ?? false;
        int seconds = preferences?.SafetyConfirmationSeconds ?? AppSettings.DefaultSafetyConfirmationSeconds;
        _rememberMatrixWindows = preferences?.RememberMatrixWindows ?? true;
        _safetyConfirmationSeconds = seconds > 0 && TimeSpan.FromSeconds(seconds).TotalMilliseconds < uint.MaxValue
            ? seconds : AppSettings.DefaultSafetyConfirmationSeconds;
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(ApplyOnStartup));
        OnPropertyChanged(nameof(SafetyConfirmationSeconds));
        OnPropertyChanged(nameof(RememberMatrixWindows));
    }

    private void PersistPreferences()
    {
        if (IsUiPreview || System.Windows.Application.Current is DesktopSplitter.App { IsDiagnosticHost: true }) return;
        try
        {
            SettingsStore.Update(settings => {
                settings.StartWithWindows = _startWithWindows;
                settings.ApplyOnStartup = _applyOnStartup;
                settings.SafetyConfirmationSeconds = _safetyConfirmationSeconds;
                settings.RememberMatrixWindows = _rememberMatrixWindows;
            });
        }
        catch (Exception ex)
        {
            AppendStatus("설정을 저장하지 못했습니다: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ startup / shutdown

    /// <summary>
    /// Re-applies the saved configuration if it was live when we last exited, or if the user
    /// asked for auto-apply. Never prompts — this is not a user-initiated change.
    /// </summary>
    public Task AutoApplyOnStartupAsync() => DesktopUiThread.RunAsync(_dispatcher, AutoApplyOnStartupCoreAsync);
    private async Task AutoApplyOnStartupCoreAsync()
    {
        if (SuppressAutomaticDisplayChanges) return;
        await Initialization;
        if (_closing) return;
        AppSettings? settings = SettingsStore.Load();
        bool splitActive = await Task.Run(HidingStatus.IsSplitActive).ConfigureAwait(true);

        if (!ApplyService.ShouldAutoApplyOnStartup(settings, splitActive)) return;
        if (!RestoreSettings(settings))
        {
            AppendStatus("자동 적용을 건너뜁니다. 저장된 모니터가 연결되어 있지 않습니다.");
            return;
        }

        AppendStatus("--- 저장된 분할 설정 자동 적용 ---");
        await ApplyAsync(promptToConfirm: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Tears the split down on exit so the desktop is never left split with nothing driving it.
    /// Runs asynchronously and observes actual completion; OS shutdown never blocks on this path.
    /// </summary>
    public Task RevertOnExitAsync() => DesktopUiThread.RunAsync(_dispatcher, RevertOnExitCoreAsync);
    private async Task RevertOnExitCoreAsync()
    {
        _closing = true;
        _lifetime.Cancel();
        CancelPendingOperations();
        Hiding.CancelRefresh();
        if (_operationFinished is not null) await _operationFinished.Task.ConfigureAwait(true);
        if (_suspensionWatcher is not null)
            await Task.Run(() => StopSuspensionWatcher(restorePrimary: false)).ConfigureAwait(true);
        if (SuppressAutomaticDisplayChanges) { if (_fileLog is not null) await _fileLog.DisposeAsync(); return; }
        await Initialization;
        try
        {
            if (!await Task.Run(HidingStatus.IsSplitActive).ConfigureAwait(true))
            {
                ApplyService.MarkSplitInactive(deliberate: false);
                return;
            }

            var log = new Progress<string>(_ => { });
            await Task.Run(() => ApplyService.Revert(log, CancellationToken.None, deliberate: false)).ConfigureAwait(true);

            // Exiting is not a deliberate revert: the configuration should come back next launch.
            AppSettings? settings = SettingsStore.Load();
            if (settings is not null)
            {
                SettingsStore.Update(current => current.SplitActiveOnExit = true);
            }
        }
        catch
        {
            // Shutdown must proceed regardless.
        }
        finally { if (_fileLog is not null) await _fileLog.DisposeAsync(); }
    }

    public ObservableCollection<MonitorInfo> Monitors { get; }
    public ObservableCollection<LayoutDefinition> Layouts { get; }
    public ObservableCollection<SegmentViewModel> Segments { get; }

    /// <summary>Numbered zones drawn on the editor canvas.</summary>
    public ObservableCollection<ZoneVisual> Zones { get; }

    /// <summary>Draggable borders between adjacent zones.</summary>
    public ObservableCollection<SplitterVisual> Splitters { get; }

    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            // Collection refresh temporarily clears WPF's two-way SelectedItem. This is
            // not a user choice and must not rebuild segment sizes using a missing monitor.
            if (_suppressSegmentRebuild) return;
            if (!SetProperty(ref _selectedMonitor, value)) return;
            if (value is not null) {
                _preferredMonitorPath = value.MonitorDevicePath;
                _preferredMonitorDevice = value.DeviceName;
            }
            _selectionPending = false;
            UpdateModeTexts();
            RebuildSegments(resetSizes: true);
            RaiseCommandStates();
            _ = Hiding.RefreshAsync();
        }
    }

    public LayoutDefinition? SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (!SetProperty(ref _selectedLayout, value)) return;
            _selectionPending = false;
            IsSplitEditorExpanded = true; // An explicit preset choice opens its draft editor.
            RebuildSegments(resetSizes: true);
            RaiseCommandStates();
        }
    }

    /// <summary>Read-only: every segment runs at the physical monitor's max refresh.</summary>
    public string RefreshText
    {
        get => _refreshText;
        private set => SetProperty(ref _refreshText, value);
    }

    /// <summary>The selected monitor's mode right now, e.g. "5120x1440 @ 240 Hz".</summary>
    public string CurrentModeText
    {
        get => _currentModeText;
        private set => SetProperty(ref _currentModeText, value);
    }

    public string DriverStatus
    {
        get => _driverStatus;
        private set => SetProperty(ref _driverStatus, value);
    }

    public string CompositorStatus
    {
        get => _compositorStatus;
        private set => SetProperty(ref _compositorStatus, value);
    }

    /// <summary>
    /// Subtle informational note (not an error) shown when the segment sizes do not add up
    /// to the monitor dimension — the compositor scales each capture into its physRect.
    /// </summary>
    public string SumNote
    {
        get => _sumNote;
        private set
        {
            if (!SetProperty(ref _sumNote, value)) return;
            OnPropertyChanged(nameof(HasSumNote));
        }
    }

    public bool HasSumNote => !string.IsNullOrEmpty(_sumNote);

    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetProperty(ref _canvasWidth, value);
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetProperty(ref _canvasHeight, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RaiseCommandStates();
        }
    }

    public bool IsIdle => !_isBusy;

    public bool CanApply =>
        _initialized && !_closing && !IsUiPreview && !IsBusy &&
        SelectedMonitor is not null &&
        SelectedLayout is not null &&
        Segments.Count == SelectedLayout.SegmentCount &&
        Segments.All(s => s.IsValid);

    // ------------------------------------------------------------------ commands

    public RelayCommand RefreshMonitorsCommand { get; }
    public RelayCommand ResetSegmentsCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand SetPrimaryCommand { get; }
    public RelayCommand MinimizeToTrayCommand { get; }

    public event EventHandler? MinimizeToTrayRequested;

    /// <summary>
    /// Crash recovery: a previous session may have died with the physical monitor still removed
    /// from the desktop. Awaiting (rather than TaskScheduler.FromCurrentSynchronizationContext)
    /// keeps this working when the view-model is constructed without a synchronisation context.
    /// </summary>
    private async Task RecoverThenRefreshAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                ApplyService.RecoverStaleSpecialization(_progress, _lifetime.Token);
            }).ConfigureAwait(true);
            if (!_closing) _ = Hiding.RefreshAsync();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            AppendStatus("모니터 숨김 복구 확인 실패: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ monitor list

    /// <summary>Called from the WM_DISPLAYCHANGE hook; keeps the user's edits.</summary>
    public void OnDisplayChanged()
    {
        if (_closing) return;
        if (IsBusy) return; // The operation's completion path performs the final read.
        _ = RefreshAfterDisplayChangeAsync();
    }

    private async Task RefreshAfterDisplayChangeAsync()
    {
        await RefreshMonitorsAsync(false);
        if (!_closing && !IsBusy) await Hiding.RefreshAsync();
    }

    public Task RefreshMonitorsAsync(bool announce = false) => DesktopUiThread.RunAsync(_dispatcher, () => RefreshMonitorsCoreAsync(announce));
    private async Task RefreshMonitorsCoreAsync(bool announce)
    {
        if (_closing) return;
        try {
            await _refresh.RequestAsync();
            if (announce && !_closing) AppendStatus($"모니터 {Monitors.Count}대를 찾았습니다.");
        } catch (Exception error) { if (!_closing) AppendStatus("모니터 목록을 읽지 못했습니다: " + error.Message); }
    }

    private void ApplyMonitorSnapshot(IReadOnlyList<MonitorInfo> physical, bool announce)
    {
        var previousMonitor = SelectedMonitor;
        string? previous = _preferredMonitorDevice ?? previousMonitor?.DeviceName;
        string? previousPath = _preferredMonitorPath ?? previousMonitor?.MonitorDevicePath;
        bool hasPreference = !string.IsNullOrWhiteSpace(previousPath) || !string.IsNullOrWhiteSpace(previous);
        try
        {
            _suppressSegmentRebuild = true;
            try
            {
                SnapshotCollection.Update(Monitors, physical);
                _selectedMonitor = hasPreference ? MonitorSelection.Find(Monitors, previousPath, previous)
                    : Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
                if (_selectedMonitor is not null) {
                    _preferredMonitorPath = _selectedMonitor.MonitorDevicePath;
                    _preferredMonitorDevice = _selectedMonitor.DeviceName;
                }
            }
            finally { _suppressSegmentRebuild = false; }

            if (!ReferenceEquals(previousMonitor, _selectedMonitor)) OnPropertyChanged(nameof(SelectedMonitor));
            UpdateModeTexts();

            // Preserve the user's edited sizes across a plain rescan of the same monitor.
            bool sameSelection = !string.IsNullOrWhiteSpace(previousPath)
                ? string.Equals(_selectedMonitor?.MonitorDevicePath, previousPath, StringComparison.OrdinalIgnoreCase)
                : previous is not null && string.Equals(_selectedMonitor?.DeviceName, previous, StringComparison.OrdinalIgnoreCase);
            bool countMatches = SelectedLayout is not null && Segments.Count == SelectedLayout.SegmentCount;
            RebuildSegments(resetSizes: !((sameSelection || (hasPreference && _selectedMonitor is null)) && countMatches));

            if (announce)
                AppendStatus($"모니터 {Monitors.Count}대를 찾았습니다.");
        }
        catch (Exception ex)
        {
            AppendStatus("모니터 목록을 읽지 못했습니다: " + ex.Message);
        }
        RaiseCommandStates();
    }

    private void UpdateModeTexts()
    {
        MonitorInfo? m = SelectedMonitor;
        if (m is null)
        {
            RefreshText = "—";
            CurrentModeText = "—";
            return;
        }

        CurrentModeText = m.CurrentModeText;
        RefreshText = $"{m.MaxRefreshHz} Hz · 최대 해상도 {m.MaxWidth}x{m.MaxHeight}";
    }

    // ------------------------------------------------------------------ segments + editor

    /// <summary>
    /// Rebuilds the segment editors for the current layout. When <paramref name="resetSizes"/>
    /// is true the sizes go back to the layout's equal subdivision of the monitor; otherwise
    /// existing values are kept (used for rescans / display-change events).
    /// </summary>
    private void RebuildSegments(bool resetSizes)
    {
        if (_suppressSegmentRebuild) return;

        LayoutDefinition? layout = SelectedLayout;
        if (layout is null) return;

        MonitorInfo? monitor = SelectedMonitor;
        int monW = monitor?.CurrentWidth ?? 1920;
        int monH = monitor?.CurrentHeight ?? 1080;

        bool reuse = !resetSizes && Segments.Count == layout.SegmentCount;

        // Per-layout defaults: most layouts subdivide equally, the 16:9-centre preset computes
        // a true 16:9 middle segment instead.
        if (!reuse) {
            IReadOnlyList<(int Width, int Height)> defaults = layout.ComputeDefaultSegmentSizes(monW, monH);
            foreach (SegmentViewModel old in Segments) old.Changed -= OnSegmentChanged;
            Segments.Clear();
            for (int i = 0; i < layout.SegmentCount; i++) {
                (int w, int h) = defaults[i];
                var vm = new SegmentViewModel(i, w, h);
                vm.Changed += OnSegmentChanged;
                Segments.Add(vm);
            }
        }
        if (!_selectionPending && MatchesActiveMatrix && _liveSelectedSlot >= 1 && _liveSelectedSlot <= layout.SegmentCount)
            _primarySlot = _liveSelectedSlot;
        else if (!reuse)
            _primarySlot = SplitPrimarySelector.ReadChoice(SettingsStore.Load(), monitor?.MonitorDevicePath ?? "", layout.Kind, layout.SegmentCount);
        NotifyMatrixState();

        RebuildEditor();
        RaiseCommandStates();
    }

    private void OnSegmentChanged(object? sender, EventArgs e)
    {
        // Manual entry: only the edited segment changed. Redraw proportionally, do not
        // auto-fill the neighbours.
        if (!_editorSuspended) RebuildEditor();
        RaiseCommandStates();
    }

    /// <summary>Recomputes canvas size, zone rects, splitter bars and the sum note.</summary>
    private void RebuildEditor()
    {
        LayoutDefinition? layout = SelectedLayout;
        MonitorInfo? monitor = SelectedMonitor;

        if (layout is null || monitor is null || Segments.Count != layout.SegmentCount)
        {
            Zones.Clear();
            Splitters.Clear();
            SumNote = string.Empty;
            return;
        }

        int monW = Math.Max(1, monitor.CurrentWidth);
        int monH = Math.Max(1, monitor.CurrentHeight);

        double scale = Math.Min(MaxCanvasWidth / monW, MaxCanvasHeight / monH);
        CanvasWidth = Math.Max(120, Math.Round(monW * scale));
        CanvasHeight = Math.Max(80, Math.Round(monH * scale));

        var sizes = Segments.Select(s => (s.Width, s.Height)).ToList();
        IReadOnlyList<ZoneRect> rects =
            LayoutGeometry.ComputeZoneRects(layout, CanvasWidth, CanvasHeight, sizes);

        // Update in place when the shape is unchanged so dragging does not re-create visuals.
        if (Zones.Count != layout.SegmentCount)
        {
            Zones.Clear();
            for (int i = 0; i < layout.SegmentCount; i++) Zones.Add(new ZoneVisual(i));
        }
        for (int i = 0; i < layout.SegmentCount; i++)
        {
            ZoneVisual z = Zones[i];
            z.IsPrimary = MatchesActiveMatrix && z.Number == _liveSelectedSlot;
            z.X = rects[i].X;
            z.Y = rects[i].Y;
            z.W = rects[i].W;
            z.H = rects[i].H;
            z.SizeText = $"{sizes[i].Width}x{sizes[i].Height}";
            z.AspectNote = ZoneVisual.DescribeAspect(sizes[i].Width, sizes[i].Height);
        }

        RebuildSplitters(layout, rects);
        UpdateSumNote(layout, sizes, monW, monH);
    }

    private void RebuildSplitters(LayoutDefinition layout, IReadOnlyList<ZoneRect> rects)
    {
        int expected = layout.VerticalSplitLines.Count + layout.HorizontalSplitLines.Count;
        if (Splitters.Count != expected || !SplittersMatch(layout))
        {
            Splitters.Clear();
            int id = 0;
            foreach (int line in layout.VerticalSplitLines) Splitters.Add(new SplitterVisual(id++, true, line));
            foreach (int line in layout.HorizontalSplitLines) Splitters.Add(new SplitterVisual(id++, false, line));
        }

        foreach (SplitterVisual s in Splitters)
        {
            // Affected segments are those touching the line from either side; the bar spans
            // exactly their union on the cross axis (so e.g. the big-left layout's horizontal
            // splitter only covers the right-hand column).
            int[] affected = s.IsVertical
                ? layout.SegmentsLeftOf(s.Line).Concat(layout.SegmentsRightOf(s.Line)).ToArray()
                : layout.SegmentsAbove(s.Line).Concat(layout.SegmentsBelow(s.Line)).ToArray();
            if (affected.Length == 0) continue;

            if (s.IsVertical)
            {
                double x = layout.SegmentsLeftOf(s.Line)
                    .Select(i => rects[i].X + rects[i].W)
                    .DefaultIfEmpty(0)
                    .Max();
                double top = affected.Select(i => rects[i].Y).Min();
                double bottom = affected.Select(i => rects[i].Y + rects[i].H).Max();

                s.X = x - SplitterVisual.Thickness / 2;
                s.Y = top;
                s.Length = Math.Max(1, bottom - top);
            }
            else
            {
                double y = layout.SegmentsAbove(s.Line)
                    .Select(i => rects[i].Y + rects[i].H)
                    .DefaultIfEmpty(0)
                    .Max();
                double left = affected.Select(i => rects[i].X).Min();
                double right = affected.Select(i => rects[i].X + rects[i].W).Max();

                s.X = left;
                s.Y = y - SplitterVisual.Thickness / 2;
                s.Length = Math.Max(1, right - left);
            }
        }
    }

    private bool SplittersMatch(LayoutDefinition layout)
    {
        int i = 0;
        foreach (int line in layout.VerticalSplitLines)
        {
            if (i >= Splitters.Count || !Splitters[i].IsVertical || Splitters[i].Line != line) return false;
            i++;
        }
        foreach (int line in layout.HorizontalSplitLines)
        {
            if (i >= Splitters.Count || Splitters[i].IsVertical || Splitters[i].Line != line) return false;
            i++;
        }
        return i == Splitters.Count;
    }

    private void UpdateSumNote(LayoutDefinition layout, IReadOnlyList<(int Width, int Height)> sizes,
                               int monW, int monH)
    {
        LayoutGeometry.Tracks tracks = LayoutGeometry.ComputeTracks(layout, sizes);
        int sumW = tracks.ColumnWidths.Sum();
        int sumH = tracks.RowHeights.Sum();

        var parts = new List<string>(2);
        if (sumW != monW) parts.Add($"가로 합계 {sumW}px / 모니터 {monW}px");
        if (sumH != monH) parts.Add($"세로 합계 {sumH}px / 모니터 {monH}px");

        SumNote = parts.Count == 0
            ? string.Empty
            : string.Join("; ", parts) + " · 실제 배치 영역에 맞춰 화면 크기가 조정됩니다.";
    }

    // ------------------------------------------------------------------ splitter dragging

    /// <summary>
    /// Begins an auto-fill drag. The current track sizes are first normalised so they sum to
    /// the monitor dimension exactly — this is how manual (non-summing) values re-enter
    /// auto-fill mode.
    /// </summary>
    public void BeginSplitterDrag(int splitterId, double canvasX, double canvasY)
    {
        LayoutDefinition? layout = SelectedLayout;
        MonitorInfo? monitor = SelectedMonitor;
        if (layout is null || monitor is null || IsBusy) return;

        SplitterVisual? splitter = Splitters.FirstOrDefault(s => s.Id == splitterId);
        if (splitter is null) return;

        var sizes = Segments.Select(s => (s.Width, s.Height)).ToList();
        LayoutGeometry.Tracks tracks = LayoutGeometry.ComputeTracks(layout, sizes);

        int monW = Math.Max(1, monitor.CurrentWidth);
        int monH = Math.Max(1, monitor.CurrentHeight);

        int[] cols = LayoutGeometry.Normalize(tracks.ColumnWidths, monW);
        int[] rows = LayoutGeometry.Normalize(tracks.RowHeights, monH);
        WriteTracks(layout, cols, rows);

        _dragSplitter = splitter;
        _dragTracks = splitter.IsVertical ? cols : rows;

        RebuildEditor();

        double linePos = splitter.IsVertical
            ? splitter.X + SplitterVisual.Thickness / 2
            : splitter.Y + SplitterVisual.Thickness / 2;
        _dragGrabOffset = (splitter.IsVertical ? canvasX : canvasY) - linePos;
    }

    /// <summary>
    /// Moves the captured splitter to an absolute canvas position. Working from the absolute
    /// pointer position (rather than accumulating per-move deltas) means clamping at the
    /// 640 px limit cannot desynchronise the bar from the cursor.
    /// </summary>
    public void UpdateSplitterDrag(double canvasX, double canvasY)
    {
        SplitterVisual? splitter = _dragSplitter;
        LayoutDefinition? layout = SelectedLayout;
        MonitorInfo? monitor = SelectedMonitor;
        if (splitter is null || layout is null || monitor is null) return;
        if (_dragTracks.Length < splitter.Line + 2) return;

        double canvasExtent = splitter.IsVertical ? CanvasWidth : CanvasHeight;
        int monitorExtent = splitter.IsVertical
            ? Math.Max(1, monitor.CurrentWidth)
            : Math.Max(1, monitor.CurrentHeight);
        if (canvasExtent <= 0) return;

        double targetCanvas = (splitter.IsVertical ? canvasX : canvasY) - _dragGrabOffset;
        double targetMonitor = targetCanvas * (monitorExtent / canvasExtent);

        int before = 0;
        for (int i = 0; i < splitter.Line; i++) before += _dragTracks[i];

        int pairTotal = _dragTracks[splitter.Line] + _dragTracks[splitter.Line + 1];
        int leading = LayoutGeometry.ClampSplit((int)Math.Round(targetMonitor - before), pairTotal);
        int trailing = pairTotal - leading;

        var updated = (int[])_dragTracks.Clone();
        updated[splitter.Line] = leading;
        updated[splitter.Line + 1] = trailing;

        if (splitter.IsVertical) WriteTracks(layout, updated, null);
        else WriteTracks(layout, null, updated);

        RebuildEditor();
        RaiseCommandStates();
    }

    /// <summary>Ends the drag. Values are already snapped into the text boxes by WriteTracks.</summary>
    public void EndSplitterDrag()
    {
        if (_dragSplitter is null) return;
        _dragSplitter = null;
        _dragTracks = Array.Empty<int>();
        RebuildEditor();
        RaiseCommandStates();
    }

    public bool IsDragging => _dragSplitter is not null;

    /// <summary>
    /// Pushes track sizes back into the segment editors (and therefore the text boxes).
    /// A null array leaves that axis untouched.
    /// </summary>
    private void WriteTracks(LayoutDefinition layout, int[]? cols, int[]? rows)
    {
        _editorSuspended = true;
        try
        {
            for (int i = 0; i < layout.SegmentCount && i < Segments.Count; i++)
            {
                LayoutCell cell = layout.Cells[i];
                int w = cols is null ? Segments[i].Width : SumRange(cols, cell.Col, cell.LastCol);
                int h = rows is null ? Segments[i].Height : SumRange(rows, cell.Row, cell.LastRow);
                Segments[i].Set(w, h);
            }
        }
        finally { _editorSuspended = false; }
    }

    private static int SumRange(int[] tracks, int first, int last)
    {
        int sum = 0;
        for (int i = first; i <= last && i < tracks.Length; i++) sum += tracks[i];
        return Math.Max(1, sum);
    }

    // ------------------------------------------------------------------ apply / revert

    /// <summary>User-initiated apply — always confirmed with the keep-or-revert countdown.</summary>
    public Task ApplyAsync() => ApplyAsync(promptToConfirm: true);

    public Task ApplyAsync(bool promptToConfirm) => DesktopUiThread.RunAsync(_dispatcher, () => ApplyCoreAsync(promptToConfirm));
    private async Task ApplyCoreAsync(bool promptToConfirm)
    {
        if (!CanApply) return;
        // The background pipeline performs read-only preflight before any mutation.
        MonitorInfo physical = SelectedMonitor!;
        LayoutDefinition layout = SelectedLayout!;
        var resolutions = Segments.Select(s => (s.Width, s.Height)).ToList();

        using var cancellation = BeginOperation(DisplayOperationState.Applying);
        var state = new Progress<DisplayOperationState>(value => {
            if (ReferenceEquals(_operationCancellation, cancellation)) OperationState = value;
        });
        var request = new ApplyRequest
        {
            Physical = physical,
            Layout = layout,
            Resolutions = resolutions,
            PrimarySlot = SelectedPrimarySlot,
            HidePhysicalDisplay = Hiding.IntentOn,
            SpecializationUi = Hiding.CreateSpecializationUi(() => Monitors.ToList()),
            PromptToConfirm = promptToConfirm,
            Confirmer = _applyConfirmer,
            ConfirmationSeconds = SafetyConfirmationSeconds,
            State = state,
        };

        IsBusy = true;
        AppendStatus($"--- {physical.DisplayLabel}에 {layout.Name} 적용 ({physical.DeviceName}) ---");
        try
        {
            ApplyResult result = await Task.Run(() => {
                using var operation = ApplyService.UserOperationGate.Enter(cancellation.Token);
                ApplyService.CheckPrerequisites();
                MatrixSnapshot? prior = promptToConfirm ? SplitMatrixWorkspace.Capture(_progress) : null;
                var restore = promptToConfirm ? SplitMatrixWorkspace.PrepareRestore(physical.MonitorDevicePath, layout.Kind, _progress) : null;
                var applied = ApplyService.Apply(request, _progress, cancellation.Token);
                if (promptToConfirm && !applied.RolledBack)
                    SplitMatrixWorkspace.RestoreSplit(restore, request.PrimarySlot, _progress, cancellation.Token);
                else if (applied.RolledBack && prior is not null)
                    SplitMatrixWorkspace.RestoreSplit(new MatrixRestorePlan(prior, prior.Windows.Select(window => window.Identity).ToHashSet()), prior.PrimarySlot, _progress, cancellation.Token);
                return applied;
            })
                                           .ConfigureAwait(true);
            AppendStatus(result.RolledBack
                ? $"변경을 취소하고 {result.RollbackDescription}(으)로 복원했습니다."
                : $"분할 적용 완료: 가상 모니터 {result.VirtualMonitors.Count}개가 실행 중입니다.");
            OperationState = DisplayOperationState.Completed;

            if (!result.RolledBack) StartSuspensionWatcher();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            OperationState = DisplayOperationState.Cancelled;
            AppendStatus("적용 대기를 취소했습니다. 현재 상태를 확인한 뒤 필요하면 ‘원래 화면으로 복원’을 눌러 주세요.");
        }
        catch (ApplyException ex)
        {
            AppendStatus("분할 적용 실패: " + ex.Message);
        }
        catch (DriverNotFoundException ex)
        {
            AppendStatus("분할 적용 실패: " + ex.Message);
        }
        catch (DriverIoException ex)
        {
            AppendStatus("분할 적용 실패: " + ex.Message);
        }
        catch (DisplayArrangeException ex)
        {
            AppendStatus("분할 적용 실패: " + ex.Message);
        }
        catch (CompositorNotFoundException ex)
        {
            AppendStatus("분할 적용 실패: " + ex.Message);
        }
        catch (Exception ex)
        {
            AppendStatus("예상하지 못한 적용 오류: " + ex.Message);
        }
        finally
        {
            await FinishOperationAsync(refreshHiding: true);
        }
    }

    public Task RevertAsync() => DesktopUiThread.RunAsync(_dispatcher, RevertCoreAsync);
    private async Task RevertCoreAsync()
    {
        if (IsUiPreview || IsBusy || _closing || !_initialized) return;
        using var cancellation = BeginOperation(DisplayOperationState.Reverting);
        IsBusy = true;
        AppendStatus("--- 원래 화면으로 복원 ---");

        // Drop any pending suspend state first: Revert restores primary itself, and a watcher
        // still reacting to events would fight it.
        try
        {
            await Task.Run(() => {
                using var operation = ApplyService.UserOperationGate.Enter(cancellation.Token);
                StopSuspensionWatcher(restorePrimary: false);
                _desktopActions.Revert(_progress, cancellation.Token);
            }).ConfigureAwait(true);
            OperationState = DisplayOperationState.Completed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            OperationState = DisplayOperationState.Cancelled;
            AppendStatus("복원 대기를 취소했습니다. 완료로 기록하지 않았습니다. 현재 화면 상태를 확인해 주세요.");
        }
        catch (ApplyException ex)
        {
            AppendStatus("화면 복원 실패: " + ex.Message);
        }
        catch (Exception ex)
        {
            AppendStatus("예상하지 못한 복원 오류: " + ex.Message);
        }
        finally
        {
            await FinishOperationAsync(refreshHiding: true);
        }
    }

    public Task ReorderDisplayAsync(int slot) => DesktopUiThread.RunAsync(_dispatcher, () => ReorderDisplayCoreAsync(slot));
    private async Task ReorderDisplayCoreAsync(int slot)
    {
        if (IsUiPreview || IsBusy || _closing || !_initialized) return;
        using var cancellation = BeginOperation(DisplayOperationState.Reordering);
        IsBusy = true;
        try { string device = _desktopActions.ForegroundDevice(); AppendStatus(await Task.Run(() => _desktopActions.Reorder(slot, device, cancellation.Token)).ConfigureAwait(true)); OperationState = DisplayOperationState.Completed; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { OperationState = DisplayOperationState.Cancelled; AppendStatus("배치 변경 대기를 취소했습니다."); }
        catch (Exception ex) { AppendStatus("배치 변경 실패: " + ex.Message); }
        finally { await FinishOperationAsync(); }
    }

    public event Action<WindowBox>? PrimaryHighlightRequested;

    public Task SetPrimaryAsync(int slot) => DesktopUiThread.RunAsync(_dispatcher, () => SetPrimaryCoreAsync(slot));
    private async Task SetPrimaryCoreAsync(int slot)
    {
        if (IsUiPreview || IsBusy || _closing || !_initialized || SelectedMonitor is null || SelectedLayout is null) return;
        if (!MatchesActiveMatrix) { AppendStatus("선택한 모니터·분할 방식과 실행 중인 그룹이 다릅니다. 다른 그룹의 선택 칸은 변경하지 않았습니다."); return; }
        string path = SelectedMonitor.MonitorDevicePath;
        var layout = SelectedLayout.Kind;
        using var cancellation = BeginOperation(DisplayOperationState.Reordering);
        IsBusy = true;
        try {
            var target = await Task.Run(() => _desktopActions.Select(slot, path, layout, cancellation.Token));
            if (_closing || cancellation.IsCancellationRequested) return;
            AppendStatus($"이 분할 그룹의 선택 칸을 {slot}번으로 선택했습니다.");
            _primarySlot = slot;
            _liveSelectedSlot = slot;
            _selectionPending = false;
            OnPropertyChanged(nameof(SelectedPrimarySlot));
            foreach (var zone in Zones) zone.IsPrimary = zone.Number == slot;
            OperationState = DisplayOperationState.Completed;
            try { PrimaryHighlightRequested?.Invoke(target.Bounds); }
            catch (Exception error) { AppendStatus("선택 칸은 저장했으나 강조 표시를 띄우지 못했습니다: " + error.Message); }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { OperationState = DisplayOperationState.Cancelled; AppendStatus("선택 칸 선택 대기를 취소했습니다."); }
        catch (Exception error) { AppendStatus("선택 칸 선택 실패: " + error.Message); }
        finally { await FinishOperationAsync(); }
    }

    /// <summary>Tray "Apply last": re-applies the persisted settings without touching the UI selection.</summary>
    public Task ApplyLastAsync() => DesktopUiThread.RunAsync(_dispatcher, ApplyLastCoreAsync);
    private async Task ApplyLastCoreAsync()
    {
        if (IsUiPreview || IsBusy || _closing || !_initialized) return;
        AppSettings? settings = SettingsStore.Load();
        if (settings is null || settings.Segments.Count == 0)
        {
            AppendStatus("이전에 적용한 분할 설정이 없습니다.");
            return;
        }
        if (!RestoreSettings(settings))
        {
            AppendStatus($"저장된 설정의 모니터 {settings.PhysicalDevice}가 연결되어 있지 않습니다.");
            return;
        }
        await ApplyAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ status helpers

    public void AppendStatus(string line)
    {
        lock (_statusLock)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _statusLog.Append(stamped);
            _fileLog?.Append(stamped);
            if (_statusNotificationQueued) return;
            _statusNotificationQueued = true;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) PublishStatus();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(PublishStatus));
    }

    private void PublishStatus()
    {
        lock (_statusLock) { _statusNotificationQueued = false; StatusText = _statusLog.ToString(); }
        StatusAppended?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StatusAppended;

    private void NotifyMatrixState()
    {
        OnPropertyChanged(nameof(IsOriginalScreen));
        OnPropertyChanged(nameof(OriginalScreenText));
        OnPropertyChanged(nameof(IsActiveMatrix));
        OnPropertyChanged(nameof(DisplayStateText));
        OnPropertyChanged(nameof(MatrixStateText));
        OnPropertyChanged(nameof(PrimaryChoices));
        OnPropertyChanged(nameof(SelectedPrimarySlot));
        foreach (var zone in Zones) zone.IsPrimary = MatchesActiveMatrix && zone.Number == _liveSelectedSlot;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        CancelOperationCommand.RaiseCanExecuteChanged();
        SetPrimaryCommand.RaiseCanExecuteChanged();
        RefreshMonitorsCommand.RaiseCanExecuteChanged();
        ResetSegmentsCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ settings

    private CancellationTokenSource BeginOperation(DisplayOperationState phase)
    {
        OperationState = phase;
        _operationCancellation = new CancellationTokenSource();
        _operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _operationCancellation;
    }

    private void EndOperation()
    {
        if (OperationState is not (DisplayOperationState.Completed or DisplayOperationState.Cancelled)) OperationState = DisplayOperationState.Failed;
        _operationCancellation = null;
        IsBusy = false;
        _operationFinished?.TrySetResult();
        _operationFinished = null;
    }

    private async Task FinishOperationAsync(bool refreshHiding = false)
    {
        EndOperation();
        await RefreshMonitorsAsync();
        if (refreshHiding && !_closing) _ = Hiding.RefreshAsync();
    }

    public void CancelPendingOperations()
    {
        try { _operationCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) CancelOperationCommand.RaiseCanExecuteChanged();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(CancelOperationCommand.RaiseCanExecuteChanged));
    }

    private void RestoreSettings() => RestoreSettings(SettingsStore.Load());

    private bool RestoreSettings(AppSettings? settings)
    {
        if (settings is null) return false;

        MonitorInfo? monitor = MonitorSelection.Find(Monitors, settings.PhysicalMonitorPath, settings.PhysicalDevice, settings.PhysicalFriendlyName);
        if (monitor is null) return false;

        _suppressSegmentRebuild = true;
        try
        {
            _selectedMonitor = monitor;
            _preferredMonitorPath = monitor.MonitorDevicePath;
            _preferredMonitorDevice = monitor.DeviceName;
            _selectionPending = false;
            _selectedLayout = LayoutDefinition.FromKind(settings.Layout);
        }
        finally { _suppressSegmentRebuild = false; }

        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(SelectedLayout));
        UpdateModeTexts();
        RebuildSegments(resetSizes: true);

        if (settings.Segments.Count == Segments.Count)
        {
            _editorSuspended = true;
            try
            {
                for (int i = 0; i < Segments.Count; i++)
                    Segments[i].Set(settings.Segments[i].Width, settings.Segments[i].Height);
            }
            finally { _editorSuspended = false; }

            RebuildEditor();
            AppendStatus($"마지막 편집 설정을 불러왔습니다: {SettingsStore.FilePath}");
        }

        RaiseCommandStates();
        return true;
    }
}

