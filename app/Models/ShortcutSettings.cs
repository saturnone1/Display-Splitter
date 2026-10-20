using System.Text.Json.Serialization;

namespace DesktopSplitter.Models;

// Append values: existing serialized settings must retain their action identity.
public enum ShortcutAction { ShowApp, Apply, Revert, PreviousWindow, NextWindow, Screen1, Screen2, Screen3, Screen4,
    Arrange1, Arrange2, Arrange3, Arrange4, Primary1, Primary2, Primary3, Primary4 }

public sealed class ShortcutSettings
{
    // Keep removed enum IDs reserved so older serialized actions never change identity.
    public static IReadOnlyList<ShortcutAction> Actions { get; } = Enum.GetValues<ShortcutAction>()
        .Where(action => action is not (ShortcutAction.PreviousWindow or ShortcutAction.NextWindow
            or ShortcutAction.Screen1 or ShortcutAction.Screen2 or ShortcutAction.Screen3 or ShortcutAction.Screen4)).ToArray();
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
    [JsonPropertyName("bindings")]
    public Dictionary<ShortcutAction, string> Bindings { get; set; } = Defaults();
    [JsonPropertyName("disabledActions")]
    public HashSet<ShortcutAction> DisabledActions { get; set; } = new();

    [JsonPropertyName("numberKeyVersion")]
    public int NumberKeyVersion { get; set; }

    public static Dictionary<ShortcutAction, string> Defaults() => new()
    {
        [ShortcutAction.ShowApp] = "Ctrl+Alt+Shift+F9",
        [ShortcutAction.Apply] = "Ctrl+Alt+Shift+F10",
        [ShortcutAction.Revert] = "Ctrl+Alt+Shift+F11",
        [ShortcutAction.Primary1] = "Ctrl+Alt+Shift+F5",
        [ShortcutAction.Primary2] = "Ctrl+Alt+Shift+F6",
        [ShortcutAction.Primary3] = "Ctrl+Alt+Shift+F7",
        [ShortcutAction.Primary4] = "Ctrl+Alt+Shift+F8",
        [ShortcutAction.Arrange1] = "Ctrl+Alt+Shift+F1",
        [ShortcutAction.Arrange2] = "Ctrl+Alt+Shift+F2",
        [ShortcutAction.Arrange3] = "Ctrl+Alt+Shift+F3",
        [ShortcutAction.Arrange4] = "Ctrl+Alt+Shift+F4"
    };

    public ShortcutSettings Copy()
    {
        var bindings = Defaults();
        var addedDefaults = bindings.Keys.Where(action => Bindings?.ContainsKey(action) != true).ToHashSet();
        foreach (var pair in Bindings ?? new())
            if (Actions.Contains(pair.Key)) bindings[pair.Key] = pair.Value;
        // Only migrate the old defaults. User-customized gestures retain their meaning.
        if (NumberKeyVersion < 1)
            for (int i = 0; i < 4; i++)
            {
                var action = (ShortcutAction)((int)ShortcutAction.Screen1 + i);
                if (bindings.TryGetValue(action, out string? old) && string.Equals(old, $"Ctrl+Alt+{i + 1}", StringComparison.OrdinalIgnoreCase))
                {
                    bindings[action] = $"Ctrl+{i + 5}";
                    addedDefaults.Add(action);
                }
            }
        if (NumberKeyVersion < 2)
        {
            var previous = new Dictionary<ShortcutAction, string> {
                [ShortcutAction.ShowApp] = "Ctrl+Alt+F11", [ShortcutAction.Apply] = "Ctrl+Alt+F9",
                [ShortcutAction.Revert] = "Ctrl+Alt+F10" };
            for (int i = 0; i < 4; i++) {
                previous[(ShortcutAction)((int)ShortcutAction.Screen1 + i)] = $"Ctrl+{i + 5}";
                previous[(ShortcutAction)((int)ShortcutAction.Arrange1 + i)] = $"Ctrl+{i + 1}";
            }
            foreach (var pair in previous)
                if (bindings.TryGetValue(pair.Key, out string? old) && string.Equals(old, pair.Value, StringComparison.OrdinalIgnoreCase)) {
                    bindings[pair.Key] = Defaults()[pair.Key];
                    addedDefaults.Add(pair.Key);
                }
        }
        if (NumberKeyVersion < 4)
            for (int i = 0; i < 4; i++)
            {
                var action = (ShortcutAction)((int)ShortcutAction.Primary1 + i);
                if (string.Equals(bindings[action], $"Ctrl+{i + 5}", StringComparison.OrdinalIgnoreCase))
                {
                    bindings[action] = Defaults()[action];
                    addedDefaults.Add(action);
                }
            }
        var disabled = new HashSet<ShortcutAction>((DisabledActions ?? new()).Where(Actions.Contains));
        foreach (var action in addedDefaults)
        {
            if (!Services.ShortcutChord.TryParse(bindings[action], out var added, out _)) continue;
            if (bindings.Any(pair => pair.Key != action && !disabled.Contains(pair.Key)
                && Services.ShortcutChord.TryParse(pair.Value, out var existing, out _) && existing == added))
                disabled.Add(action); // Preserve a custom key instead of silently taking it over.
        }
        return new() { Enabled = Enabled, Bindings = bindings, DisabledActions = disabled, NumberKeyVersion = 4 };
    }

    public static int? ScreenNumber(ShortcutAction action) => action switch
    {
        ShortcutAction.Screen1 => 1, ShortcutAction.Screen2 => 2,
        ShortcutAction.Screen3 => 3, ShortcutAction.Screen4 => 4, _ => null
    };

    public static int? ArrangementNumber(ShortcutAction action) => action switch
    {
        ShortcutAction.Arrange1 => 1, ShortcutAction.Arrange2 => 2,
        ShortcutAction.Arrange3 => 3, ShortcutAction.Arrange4 => 4, _ => null
    };

    public static int? PrimaryNumber(ShortcutAction action) => action switch {
        ShortcutAction.Primary1 => 1, ShortcutAction.Primary2 => 2,
        ShortcutAction.Primary3 => 3, ShortcutAction.Primary4 => 4, _ => null
    };
}
