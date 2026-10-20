using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

/// <summary>A live countdown dialog the caller can dismiss early.</summary>
public interface ICountdownHandle
{
    void CloseFromHelper();
}

/// <summary>A live guided-Settings dialog the caller can dismiss early.</summary>
public interface IGuidedHandle
{
    void CloseFromCaller();
}

/// <summary>The dialogs the hiding flows need. Implemented by the main window.</summary>
public interface IHidingUi
{
    /// <summary>Explanation dialog. Returns true to proceed.</summary>
    bool Confirm(string title, string message);

    void ShowOutcome(string title, string message, bool success);

    /// <summary>Shows the keep-or-revert countdown on <paramref name="placeOn"/>.</summary>
    ICountdownHandle ShowCountdown(int seconds, MonitorInfo? placeOn, Action onKeep, Action onRevert);

    /// <summary>
    /// Shows the generic "Keep these display settings?" countdown, centred on the primary display.
    /// </summary>
    ICountdownHandle ShowApplyCountdown(string summary, int seconds, Action onKeep, Action onRevert);

    /// <summary>
    /// Shows the guided "Remove display from desktop" instructions on <paramref name="placeOn"/>.
    /// The dialog polls the display state itself and reports through <paramref name="onFinished"/>
    /// (true when the display reached <paramref name="wantEnabled"/>, false if cancelled).
    /// </summary>
    IGuidedHandle ShowGuidedSettings(
        MonitorInfo target, bool wantEnabled, string reason, MonitorInfo? placeOn, Action<bool> onFinished);
}

public sealed class StatusRowViewModel : ObservableObject
{
    private CheckState _state;
    private string _detail = string.Empty;
    private string _label;

    public StatusRowViewModel(string label) => _label = label;

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    public CheckState State
    {
        get => _state;
        private set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(Glyph)); }
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string Glyph => State switch
    {
        CheckState.Ok => "●",
        CheckState.No => "○",
        CheckState.Info => "·",
        _ => "—",
    };

    public void Update(StatusRow row)
    {
        Label = row.Label;
        State = row.State;
        Detail = row.Detail;
    }
}

/// <summary>
/// The "Display hiding (experimental)" panel.
///
/// Hiding uses DisplayConfig monitor specialization — the supported mechanism behind
/// Settings &gt; Advanced display &gt; "Remove display from desktop". Enable/Disable here only
/// record the INTENT; the display is actually hidden during Apply, once the split is up and the
/// physical monitor is no longer primary (Windows will not specialize a primary display).
/// </summary>
public sealed class HidingViewModel : ObservableObject
{
    private readonly Func<MonitorInfo?> _selectedMonitor;
    private readonly Action<string> _log;
    private readonly bool _previewOnly;
    private readonly Func<MonitorInfo?, HidingSnapshot> _query;
    private Task<HidingSnapshot>? _pendingQuery;
    private MonitorInfo? _pendingMonitor;
    private CancellationTokenSource? _queryWait;
    private bool _isChecking;
    private string _summary = "아직 확인하지 않음";

    private IHidingUi? _ui;
    private bool _busy;
    private string _disabledReason = string.Empty;
    private HidingSnapshot? _snapshot;

    public HidingViewModel(Func<MonitorInfo?> selectedMonitor, Action<string> log, bool previewOnly = false,
        Func<MonitorInfo?, HidingSnapshot>? query = null)
    {
        _previewOnly = previewOnly;
        _query = query ?? HidingStatus.Query;
        _selectedMonitor = selectedMonitor;
        _log = log;

        Rows = new ObservableCollection<StatusRowViewModel>
        {
            new("Windows 지원 여부"),
            new("선택 모니터 지원 여부"),
            new("현재 실제 숨김 상태"),
            new("다음 적용 시 숨김"),
        };

        RefreshCommand = new RelayCommand(async () =>
        {
            if (IsChecking) CancelRefresh();
            else await RefreshAsync().ConfigureAwait(true);
        }, () => !IsBusy);
        EnableCommand = new RelayCommand(async () => await SetIntentAsync(true).ConfigureAwait(false),
                                         () => CanEnable);
        DisableCommand = new RelayCommand(async () => await SetIntentAsync(false).ConfigureAwait(false),
                                          () => CanDisable);
    }

    public void AttachUi(IHidingUi ui) => _ui = ui;

    /// <summary>The main window's hiding dialogs, wrapped for <see cref="ApplyService"/>.</summary>
    public ISpecializationUi CreateSpecializationUi(Func<IReadOnlyList<MonitorInfo>> allMonitors)
        => new SpecializationUi(this, allMonitors);

    public ObservableCollection<StatusRowViewModel> Rows { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand EnableCommand { get; }
    public RelayCommand DisableCommand { get; }

    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) RaiseStates(); }
    }

    public string DisabledReason
    {
        get => _disabledReason;
        private set
        {
            if (!SetProperty(ref _disabledReason, value)) return;
            OnPropertyChanged(nameof(HasDisabledReason));
        }
    }

    public bool HasDisabledReason => !string.IsNullOrEmpty(_disabledReason);
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsChecking
    {
        get => _isChecking;
        private set { if (SetProperty(ref _isChecking, value)) { OnPropertyChanged(nameof(RefreshLabel)); RaiseStates(); } }
    }
    public string RefreshLabel => IsChecking ? "조회 취소" : "상태 확인";
    public void CancelRefresh() => _queryWait?.Cancel();

    /// <summary>Whether Apply should try to hide the physical monitor.</summary>
    public bool IntentOn => _snapshot?.IntentOn ?? false;

    public bool CanEnable => !_previewOnly && !IsBusy && !IsChecking && _snapshot is { Available: true, IntentOn: false, SplitActive: false }
                             && _selectedMonitor() is not null;

    public bool CanDisable => !_previewOnly && !IsBusy && !IsChecking && _snapshot is { IntentOn: true, SplitActive: false };

    // ------------------------------------------------------------------ status

    public async Task RefreshAsync()
    {
        if (_previewOnly)
        {
            Summary = "검토 전용 모드";
            DisabledReason = "이 검토 모드에서는 모니터 숨김을 변경하지 않습니다.";
            return;
        }
        if (IsChecking) return;
        using var wait = new CancellationTokenSource();
        _queryWait = wait;
        IsChecking = true;
        Summary = "상태 확인 중…";
        try
        {
            HidingSnapshot snapshot;
            MonitorInfo? selected;
            while (true)
            {
                selected = _selectedMonitor();
                if (_pendingQuery is null || _pendingQuery.IsCompleted)
                {
                    _pendingMonitor = selected;
                    _pendingQuery = Task.Run(() => _query(selected));
                    _ = _pendingQuery.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                snapshot = await _pendingQuery.WaitAsync(wait.Token).ConfigureAwait(true);
                _pendingQuery = null;
                if (ReferenceEquals(_pendingMonitor, _selectedMonitor())) break;
                wait.Token.ThrowIfCancellationRequested();
            }
            _snapshot = snapshot;
            IReadOnlyList<StatusRow> rows = snapshot.Rows;
            for (int i = 0; i < Rows.Count && i < rows.Count; i++) Rows[i].Update(rows[i]);
            Summary = snapshot.CurrentlySpecialized.State == CheckState.Unknown ? "현재 상태 확인 불가"
                : snapshot.EnabledNow ? "현재 모니터가 숨겨져 있음" : "현재 모니터는 숨겨져 있지 않음";
            DisabledReason = ComputeDisabledReason(snapshot, selected);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            _snapshot = null;
            Summary = "상태 조회 취소됨";
            DisabledReason = "조회 대기를 취소했습니다. 디스플레이 설정은 변경하지 않았습니다.";
            foreach (var row in Rows) row.Update(new StatusRow(row.Label, CheckState.Unknown, "조회 취소됨"));
        }
        catch (Exception ex)
        {
            _snapshot = null;
            Summary = "현재 상태 확인 불가";
            DisabledReason = "상태를 읽지 못했습니다: " + ex.Message;
            foreach (var row in Rows) row.Update(new StatusRow(row.Label, CheckState.Unknown, "확인 불가"));
            _log(DisabledReason);
        }
        finally
        {
            _queryWait = null;
            IsChecking = false;
            OnPropertyChanged(nameof(IntentOn));
            RaiseStates();
        }
    }

    private static string ComputeDisabledReason(HidingSnapshot snapshot, MonitorInfo? selected)
    {
        if (selected is null) return "먼저 모니터를 선택해 주세요.";
        if (snapshot.SplitActive)
            return "분할이 실행 중입니다. 숨김 설정을 바꾸려면 먼저 ‘원래 화면으로 복원’을 실행해 주세요.";
        if (!snapshot.Available && !snapshot.IntentOn)
            return "현재 Windows 또는 모니터에서는 숨김 기능을 사용할 수 없거나 지원 여부를 확인하지 못했습니다.";
        return string.Empty;
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanDisable));
        RefreshCommand.RaiseCanExecuteChanged();
        EnableCommand.RaiseCanExecuteChanged();
        DisableCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ enable / disable intent

    private async Task SetIntentAsync(bool enable)
    {
        IHidingUi? ui = _ui;
        MonitorInfo? target = _selectedMonitor();
        if (ui is null) return;
        if (enable && !CanEnable) return;
        if (!enable && !CanDisable) return;

        string title = enable ? "다음 적용 때 모니터 숨기기" : "모니터 숨김 예약 해제";
        if (!ui.Confirm(title, BuildExplanation(target, enable))) return;

        IsBusy = true;
        try
        {
            HidingStatus.IntentWriteResult result =
                await Task.Run(() => HidingStatus.WriteIntent(enable)).ConfigureAwait(true);

            _log(result.Message);
            await RefreshAsync().ConfigureAwait(true);
            ui.ShowOutcome(title, result.Message, result.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildExplanation(MonitorInfo? target, bool enable)
    {
        string name = target?.DisplayLabel ?? "선택한 모니터";

        if (!enable)
        {
            return
                "다음 분할 적용부터 실제 모니터를 숨기지 않습니다." +
                Environment.NewLine + Environment.NewLine +
                "이미 분할이 실행 중이면 ‘원래 화면으로 복원’을 눌러 주세요. 예약 해제만으로 현재 디스플레이를 바꾸지는 않습니다.";
        }

        return
            $"대상: {name} ({target?.DeviceName}){Environment.NewLine}{Environment.NewLine}" +
            "지금은 숨김 예약만 저장합니다. 실제 디스플레이는 바뀌지 않습니다. " +
            "나중에 ‘분할 적용’을 누르면 다음 순서로 진행합니다:" + Environment.NewLine +
            "  1. 가상 모니터를 만들고 화면 1을 주 모니터로 설정합니다." + Environment.NewLine +
            "  2. 실제 모니터를 Windows 바탕 화면에서 제외합니다." + Environment.NewLine +
            "  3. 숨긴 모니터에 분할 화면을 출력합니다." +
            Environment.NewLine +
            "  4. 안전 확인 시간 안에 설정 유지 여부를 확인합니다." +
            Environment.NewLine + Environment.NewLine +
            "화면이 보이지 않거나 이상하면 아무것도 누르지 말고 기다려 주세요. " +
            "안전 확인 시간이 지나면 모니터를 복원하고 " +
            "일반 화면 합성 방식으로 다시 시작합니다." +
            Environment.NewLine + Environment.NewLine +
            "‘원래 화면으로 복원’으로 분할과 모니터 숨김을 되돌릴 수 있습니다.";
    }

    // ------------------------------------------------------------------ keep/revert countdown

    /// <summary>
    /// Bridges <see cref="ApplyService"/> (background thread) to the countdown window (UI thread).
    /// Blocks the apply until the user answers or the countdown lapses; a lapse means REVERT, so
    /// a user who cannot see the dialog always ends up back where they started.
    /// </summary>
    private sealed class SpecializationUi : ISpecializationUi
    {
        private readonly HidingViewModel _owner;
        private readonly Func<IReadOnlyList<MonitorInfo>> _allMonitors;

        public SpecializationUi(HidingViewModel owner, Func<IReadOnlyList<MonitorInfo>> allMonitors)
        {
            _owner = owner;
            _allMonitors = allMonitors;
        }

        /// <summary>
        /// The compositor is already running and waiting to take over the display; now walk the
        /// user through Windows' own toggle. Blocks until the display actually leaves the desktop
        /// or the user cancels.
        /// </summary>
        public bool GuideManualEnable(MonitorInfo physical, SpecializationResult failure, CancellationToken ct)
            => RunGuided(physical, wantEnabled: true,
                reason: "Windows에서 앱의 직접 변경 요청을 허용하지 않았습니다. " +
                        "화면 합성기가 대기 중입니다. Windows 설정에서 변경하면 분할 화면이 표시됩니다.",
                ct);

        /// <summary>Undo could not be done programmatically either — walk the user back out.</summary>
        public void GuideManualDisable(MonitorInfo physical, SpecializationResult failure, CancellationToken ct)
            => RunGuided(physical, wantEnabled: false,
                reason: "앱이 모니터를 직접 복원하지 못했습니다. Windows 설정에서 모니터 숨김을 해제해 주세요.",
                ct);

        private bool RunGuided(MonitorInfo physical, bool wantEnabled, string reason, CancellationToken ct)
        {
            IHidingUi? ui = _owner._ui;
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (ui is null || dispatcher is null || dispatcher.HasShutdownStarted) return false;

            var session = new ConfirmationSession();
            IGuidedHandle? handle = null;

            MonitorInfo? placeOn = PickHostMonitor(physical);
            _owner._log(wantEnabled
                ? $"Windows 설정 안내를 표시할 모니터: {placeOn?.DisplayLabel ?? "유일한 모니터"}."
                : $"복원 안내를 표시할 모니터: {placeOn?.DisplayLabel ?? "유일한 모니터"}.");

            Task<bool> decision = session.WaitForDecisionAsync(ct);
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (session.IsCompleted) return;
                    try { handle = ui.ShowGuidedSettings(physical, wantEnabled, reason, placeOn, ok =>
                    {
                        if (ok) session.Keep(); else session.Revert();
                    }); }
                    catch { session.Revert(); }
                }));
                return decision.GetAwaiter().GetResult();
            }
            finally
            {
                session.Revert();
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => handle?.CloseFromCaller()));
            }
        }

        private MonitorInfo? PickHostMonitor(MonitorInfo physical)
            => CountdownPlacement.ChooseMonitor(_allMonitors(), physical.DeviceName);

        public bool ConfirmKeep(MonitorInfo physical, int timeoutSeconds, CancellationToken ct)
        {
            IHidingUi? ui = _owner._ui;
            System.Windows.Threading.Dispatcher? dispatcher =
                System.Windows.Application.Current?.Dispatcher;
            if (ui is null || dispatcher is null || dispatcher.HasShutdownStarted) return false;

            var session = new ConfirmationSession();
            ICountdownHandle? handle = null;

            MonitorInfo? placeOn = PickHostMonitor(physical);
            _owner._log(placeOn is null || SameDevice(placeOn.DeviceName, physical.DeviceName)
                ? "사용할 수 있는 모니터에 설정 유지 확인 창을 표시합니다."
                : $"설정 유지 확인 창 표시 위치: {placeOn.DisplayLabel} ({placeOn.DeviceName}) · " +
                  "숨기는 모니터와 다른 모니터");

            Task<bool> decision = session.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (session.IsCompleted) return;
                    try { handle = ui.ShowCountdown(timeoutSeconds, placeOn, session.Keep, session.Revert); }
                    catch { session.Revert(); }
                }));
                bool keep = decision.GetAwaiter().GetResult();
                if (!keep) _owner._log("설정 유지를 확인하지 않아 자동으로 복원합니다.");
                return keep;
            }
            finally
            {
                session.Revert();
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => handle?.CloseFromHelper()));
            }
        }

        private static bool SameDevice(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
