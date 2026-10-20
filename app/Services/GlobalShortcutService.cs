using System.Runtime.InteropServices;
using System.Windows.Input;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public readonly record struct ShortcutChord(uint Modifiers, uint Key)
{
    public static string KeyName(System.Windows.Input.Key key) => key switch
    {
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)key - (int)System.Windows.Input.Key.D0).ToString(),
        System.Windows.Input.Key.PageUp => "PageUp", System.Windows.Input.Key.PageDown => "PageDown",
        System.Windows.Input.Key.OemComma => ",", System.Windows.Input.Key.OemPeriod => ".", _ => key.ToString()
    };
    public static bool TryParse(string? text, out ShortcutChord chord, out string error)
    {
        chord = default;
        error = "Ctrl, Alt 또는 Win과 키 하나를 조합해 주세요. 예: Ctrl+Alt+F9";
        string[] parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        uint modifiers = 0;
        foreach (string part in parts[..^1])
        {
            uint modifier = part.ToUpperInvariant() switch { "ALT" => 1u, "CTRL" or "CONTROL" => 2u, "SHIFT" => 4u, "WIN" or "WINDOWS" => 8u, _ => 0u };
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        if ((modifiers & 11) == 0) return false;
        string keyText = parts[^1];
        keyText = keyText.ToUpperInvariant() switch
        {
            "PGUP" or "PRIOR" => "PageUp", "PGDN" or "PGDOWN" or "NEXT" => "PageDown",
            "SPACEBAR" => "Space", "BACKSPACE" => "Back", "ESC" => "Escape", _ => keyText
        };
        keyText = keyText.ToUpperInvariant() switch { "," or "COMMA" => "OemComma", "." or "PERIOD" => "OemPeriod", _ => keyText };
        if (keyText.Length > 1 && char.IsAsciiDigit(keyText[0])) return false;
        if (keyText.Length == 1 && char.IsAsciiDigit(keyText[0])) keyText = "D" + keyText;
        if (!Enum.TryParse(keyText, true, out System.Windows.Input.Key key) || !Enum.IsDefined(key)) return false;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || key is System.Windows.Input.Key.System or System.Windows.Input.Key.ImeProcessed or System.Windows.Input.Key.DeadCharProcessed
            || vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return false;
        if (key == System.Windows.Input.Key.F12 || (modifiers == 1 && key == System.Windows.Input.Key.F4)
            || (modifiers == 3 && key == System.Windows.Input.Key.Delete) || (modifiers == 7 && key == System.Windows.Input.Key.S))
        {
            error = "Windows 또는 화면 합성기의 예약 단축키입니다. 다른 조합을 선택해 주세요.";
            return false;
        }
        chord = new(modifiers, vk);
        error = "";
        return true;
    }

    public static bool Validate(ShortcutSettings settings, out Dictionary<ShortcutAction, ShortcutChord> chords, out string error)
    {
        chords = new();
        var used = new HashSet<ShortcutChord>();
        foreach (ShortcutAction action in ShortcutSettings.Actions)
        {
            if (settings.DisabledActions?.Contains(action) == true) continue;
            if (!TryParse(settings.Bindings?.GetValueOrDefault(action), out var chord, out error)) return false;
            if (!used.Add(chord)) { error = "같은 단축키를 여러 동작에 사용할 수 없습니다."; return false; }
            chords[action] = chord;
        }
        error = "";
        return true;
    }
}

public interface IShortcutRegistrar
{
    bool Register(nint window, int id, uint modifiers, uint key);
    bool Unregister(nint window, int id);
    int LastError { get; }
}

public static class ShortcutExecutionPolicy
{
    public static bool CanExecute(ShortcutAction action, bool editing, bool busy, bool running)
        => !editing && (action == ShortcutAction.ShowApp || (!busy && !running));
}

public sealed class GlobalShortcutService : IDisposable
{
    public const int MessageId = 0x0312;
    private const int BaseId = 0x5100;
    private readonly nint _window;
    private readonly IShortcutRegistrar _registrar;
    private readonly record struct Registration(int Id, ShortcutChord Chord);
    private readonly Dictionary<ShortcutAction, Registration> _registered = new();
    private int _nextId = BaseId;
    private bool _disposed;
    private readonly Dictionary<ShortcutAction, string> _errors = new();
    public IReadOnlyDictionary<ShortcutAction, string> RegistrationErrors => _errors;
    public IReadOnlyCollection<ShortcutAction> RegisteredActions => _registered.Keys;
    public event Action<ShortcutAction>? Pressed;

    public GlobalShortcutService(nint window, IShortcutRegistrar? registrar = null)
    {
        _window = window;
        _registrar = registrar ?? new NativeRegistrar();
    }

    public string? Configure(ShortcutSettings settings)
    {
        if (_disposed) return "단축키 서비스가 종료되었습니다.";
        if (!ShortcutChord.Validate(settings, out var desired, out string error)) return error;
        if (!Clear()) return "기존 단축키를 해제하지 못했습니다. 앱을 종료한 뒤 다시 시도해 주세요.";
        _errors.Clear();
        if (!settings.Enabled) return null;
        foreach (var (action, chord) in desired)
        {
            if (Register(action, chord)) continue;
            int code = _registrar.LastError;
            _errors[action] = $"{settings.Bindings[action]} 등록 실패 (오류 {code}) · 다른 앱과 충돌할 수 있습니다.";
        }
        return null;
    }

    private bool Register(ShortcutAction action, ShortcutChord chord)
    {
        int id = _nextId++;
        if (_nextId > 0xBFFF) _nextId = BaseId;
        if (!_registrar.Register(_window, id, chord.Modifiers | 0x4000, chord.Key)) return false;
        _registered[action] = new(id, chord);
        return true;
    }

    private bool Clear()
    {
        bool success = true;
        foreach (var action in _registered.Keys.ToArray())
        {
            if (_registrar.Unregister(_window, _registered[action].Id)) _registered.Remove(action);
            else success = false;
        }
        return success;
    }

    public bool HandleMessage(int message, nint wParam, nint lParam)
    {
        if (_disposed || message != MessageId) return false;
        var match = _registered.FirstOrDefault(pair => pair.Value.Id == (int)wParam);
        if (match.Value.Id == 0) return false;
        var chord = match.Value.Chord;
        long data = (long)lParam;
        uint modifiers = (uint)(data & 0xFFFF);
        if ((modifiers & ~0x4000u) != chord.Modifiers || (uint)((data >> 16) & 0xFFFF) != chord.Key) return false;
        Pressed?.Invoke(match.Key);
        return true;
    }

    public void Dispose() { if (_disposed) return; Clear(); _disposed = true; }

    private sealed class NativeRegistrar : IShortcutRegistrar
    {
        public int LastError => Marshal.GetLastWin32Error();
        public bool Register(nint window, int id, uint modifiers, uint key) => RegisterHotKey(window, id, modifiers, key);
        public bool Unregister(nint window, int id) => UnregisterHotKey(window, id);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint window, int id);
    }
}
