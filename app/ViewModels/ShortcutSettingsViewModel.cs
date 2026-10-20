using System.Collections.ObjectModel;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

namespace DesktopSplitter.ViewModels;

public sealed class ShortcutRowViewModel : ObservableObject
{
    public ShortcutAction Action { get; }
    public string Label { get; }
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    private string _issue = "";
    public string RegistrationIssue
    {
        get => _issue;
        set { if (SetProperty(ref _issue, value)) OnPropertyChanged(nameof(HasRegistrationIssue)); }
    }
    public bool HasRegistrationIssue => !string.IsNullOrEmpty(RegistrationIssue);
    private string _gesture;
    public string Gesture { get => _gesture; set => SetProperty(ref _gesture, value); }
    public ShortcutRowViewModel(ShortcutAction action, string gesture)
    {
        Action = action;
        _gesture = gesture;
        Label = action switch
        {
            ShortcutAction.ShowApp => "앱 창 열기",
            ShortcutAction.Apply => "현재 분할 적용",
            ShortcutAction.Revert => "원래 화면으로 복원",
            _ => ShortcutSettings.ArrangementNumber(action) is int slot
                ? $"현재 화면 ↔ 배치 {slot}번" : $"이 그룹의 선택 칸 {ShortcutSettings.PrimaryNumber(action)}"
        };
    }
}

public sealed class ShortcutSettingsViewModel : ObservableObject
{
    private readonly Action<string> _log;
    private readonly bool _previewOnly;
    private readonly Func<AppSettings?> _load;
    private readonly Action<AppSettings> _save;
    private Func<ShortcutSettings, string?>? _configure;
    private Func<IReadOnlyDictionary<ShortcutAction, string>>? _registrationErrors;
    private ShortcutSettings _current;
    private bool _enabled;
    private string _status = "단축키가 꺼져 있습니다. 변경한 설정은 저장 후 적용됩니다.";
    public ObservableCollection<ShortcutRowViewModel> Rows { get; }
    public IEnumerable<ShortcutRowViewModel> ArrangementRows => Rows.Where(r => ShortcutSettings.ArrangementNumber(r.Action).HasValue);
    public IEnumerable<ShortcutRowViewModel> PrimaryRows => Rows.Where(r => ShortcutSettings.PrimaryNumber(r.Action).HasValue)
        .OrderBy(r => ShortcutSettings.PrimaryNumber(r.Action));
    public IEnumerable<ShortcutRowViewModel> AppRows => Rows.Where(r => r.Action is ShortcutAction.ShowApp or ShortcutAction.Apply or ShortcutAction.Revert);
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public ShortcutSettings Current => _current.Copy();

    public ShortcutSettingsViewModel(Action<string> log, bool previewOnly = false,
        Func<AppSettings?>? load = null, Action<AppSettings>? save = null)
    {
        _log = log;
        _previewOnly = previewOnly;
        _load = load ?? SettingsStore.Load;
        _save = save ?? (desired => SettingsStore.Update(current => current.Shortcuts = desired.Shortcuts.Copy()));
        _current = (_load()?.Shortcuts ?? new ShortcutSettings()).Copy();
        if (!ShortcutChord.Validate(_current, out _, out string error))
        {
            _current = new();
            _status = "저장된 단축키를 사용할 수 없어 기본값으로 표시합니다: " + error;
        }
        else if (_current.Enabled) _status = "저장된 단축키 설정을 등록할 준비가 되었습니다.";
        _enabled = _current.Enabled;
        Rows = new(ShortcutSettings.Actions.Select(action => new ShortcutRowViewModel(action, _current.Bindings[action])
            { Enabled = !_current.DisabledActions.Contains(action) }));
        SaveCommand = new RelayCommand(Save, () => !_previewOnly);
        ResetCommand = new RelayCommand(() =>
        {
            foreach (var row in Rows) row.Gesture = ShortcutSettings.Defaults()[row.Action];
            Status = "기본 키 조합을 불러왔습니다. 저장을 눌러 적용해 주세요.";
        });
    }

    public void AttachRegistrar(Func<ShortcutSettings, string?> configure,
        Func<IReadOnlyDictionary<ShortcutAction, string>>? registrationErrors = null)
    {
        if (_previewOnly) return;
        _configure = configure;
        _registrationErrors = registrationErrors;
        string? error = configure(_current);
        Status = error ?? DescribeRegistration(_current);
        if (error is not null) _log(error);
    }

    public void SetEditing(bool editing)
    {
        if (_previewOnly || _configure is null) return;
        var snapshot = Current;
        if (editing) snapshot.Enabled = false;
        string? error = _configure(snapshot);
        Status = error ?? (editing ? "키 입력 중에는 전역 단축키를 잠시 해제합니다. 변경 후 저장해 주세요."
            : DescribeRegistration(snapshot));
        if (error is not null) _log(error);
    }

    public void DetachRegistrar() { _configure = null; _registrationErrors = null; }

    private string DescribeRegistration(ShortcutSettings settings)
    {
        var errors = _registrationErrors?.Invoke();
        foreach (var row in Rows)
            row.RegistrationIssue = settings.Enabled && !settings.DisabledActions.Contains(row.Action)
                ? errors?.GetValueOrDefault(row.Action) ?? "" : "";
        if (!settings.Enabled) return "단축키 꺼짐 · 사용을 켜고 저장해 주세요.";
        int active = ShortcutSettings.Actions.Count(a => !settings.DisabledActions.Contains(a));
        int failures = Rows.Count(r => r.HasRegistrationIssue);
        return failures > 0 ? $"{active - failures}개 사용 중 · {failures}개 등록 실패. 충돌한 키만 바꾸거나 꺼주세요. 나머지는 동작합니다."
            : $"{active}개 단축키 사용 중 · 트레이에서도 동작합니다.";
    }

    public void RecordReceived(ShortcutAction action)
        => _log($"단축키 수신: {Rows.First(r => r.Action == action).Label} · {_current.Bindings[action]}");

    private void Save()
    {
        if (_previewOnly) return;
        var desired = new ShortcutSettings { Enabled = Enabled, NumberKeyVersion = 4, Bindings = Rows.ToDictionary(row => row.Action, row => row.Gesture),
            DisabledActions = Rows.Where(row => !row.Enabled).Select(row => row.Action).ToHashSet() };
        if (!ShortcutChord.Validate(desired, out _, out string validation)) { Status = validation; return; }
        string? error = _configure?.Invoke(desired);
        if (error is not null) { Status = error; _log(error); return; }
        try
        {
            AppSettings settings = _load() ?? new();
            settings.Shortcuts = desired.Copy();
            _save(settings);
            _current = desired.Copy();
            Status = "설정을 저장했습니다. " + DescribeRegistration(desired);
            _log(Status);
        }
        catch (Exception ex)
        {
            string? rollback = _configure?.Invoke(_current);
            Status = "단축키 설정을 저장하지 못했습니다: " + ex.Message + (rollback is null ? " 이전 등록 설정을 유지합니다." : " " + rollback);
            _log(Status);
        }
    }
}
