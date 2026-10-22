using System.Text.Json.Serialization;

namespace DesktopSplitter.Models;

// Append values: existing serialized settings must retain their action identity.
public enum ShortcutAction { ShowApp, Apply, Revert, PreviousWindow, NextWindow, Screen1, Screen2, Screen3, Screen4,
    Arrange1, Arrange2, Arrange3, Arrange4, Primary1, Primary2, Primary3, Primary4,
    Arrange5, Arrange6, Arrange7, Arrange8, Arrange9, Primary5, Primary6, Primary7, Primary8, Primary9 }

public sealed class ShortcutSettings
{
    public const int CurrentVersion = 5;
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
        [ShortcutAction.ShowApp] = "Ctrl+Alt+Shift+F10",
        [ShortcutAction.Apply] = "Ctrl+Alt+Shift+F11",
        [ShortcutAction.Revert] = "Ctrl+Alt+Shift+0",
        [ShortcutAction.Primary1] = "Ctrl+Alt+Shift+F1",
        [ShortcutAction.Primary2] = "Ctrl+Alt+Shift+F2",
        [ShortcutAction.Primary3] = "Ctrl+Alt+Shift+F3",
        [ShortcutAction.Primary4] = "Ctrl+Alt+Shift+F4",
        [ShortcutAction.Primary5] = "Ctrl+Alt+Shift+F5",
        [ShortcutAction.Primary6] = "Ctrl+Alt+Shift+F6",
        [ShortcutAction.Primary7] = "Ctrl+Alt+Shift+F7",
        [ShortcutAction.Primary8] = "Ctrl+Alt+Shift+F8",
        [ShortcutAction.Primary9] = "Ctrl+Alt+Shift+F9",
        [ShortcutAction.Arrange1] = "Ctrl+Alt+Shift+1",
        [ShortcutAction.Arrange2] = "Ctrl+Alt+Shift+2",
        [ShortcutAction.Arrange3] = "Ctrl+Alt+Shift+3",
        [ShortcutAction.Arrange4] = "Ctrl+Alt+Shift+4",
        [ShortcutAction.Arrange5] = "Ctrl+Alt+Shift+5",
        [ShortcutAction.Arrange6] = "Ctrl+Alt+Shift+6",
        [ShortcutAction.Arrange7] = "Ctrl+Alt+Shift+7",
        [ShortcutAction.Arrange8] = "Ctrl+Alt+Shift+8",
        [ShortcutAction.Arrange9] = "Ctrl+Alt+Shift+9"
    };

    public ShortcutSettings Copy()
    {
        var bindings = Defaults();
        var addedDefaults = bindings.Keys.Where(action => Bindings?.ContainsKey(action) != true).ToHashSet();
        foreach (var pair in Bindings ?? new())
            if (Actions.Contains(pair.Key)) bindings[pair.Key] = pair.Value;
        // Only migrate the old defaults. User-customized gestures retain their meaning.
        void Migrate(ShortcutAction action, string previous)
        {
            if (Services.ShortcutChord.TryParse(bindings[action], out var current, out _)
                && Services.ShortcutChord.TryParse(previous, out var old, out _) && current == old)
            {
                bindings[action] = Defaults()[action];
                addedDefaults.Add(action);
            }
        }
        if (NumberKeyVersion < 2)
        {
            var previous = new Dictionary<ShortcutAction, string> {
                [ShortcutAction.ShowApp] = "Ctrl+Alt+F11", [ShortcutAction.Apply] = "Ctrl+Alt+F9",
                [ShortcutAction.Revert] = "Ctrl+Alt+F10" };
            for (int i = 0; i < 4; i++) {
                previous[(ShortcutAction)((int)ShortcutAction.Arrange1 + i)] = $"Ctrl+{i + 1}";
            }
            foreach (var pair in previous)
                Migrate(pair.Key, pair.Value);
        }
        if (NumberKeyVersion < 4)
            for (int i = 0; i < 4; i++)
            {
                var action = (ShortcutAction)((int)ShortcutAction.Primary1 + i);
                Migrate(action, $"Ctrl+{i + 5}");
            }
        if (NumberKeyVersion < CurrentVersion)
        {
            Migrate(ShortcutAction.ShowApp, "Ctrl+Alt+Shift+F9");
            Migrate(ShortcutAction.Apply, "Ctrl+Alt+Shift+F10");
            Migrate(ShortcutAction.Revert, "Ctrl+Alt+Shift+F11");
            for (int i = 0; i < 4; i++)
            {
                Migrate((ShortcutAction)((int)ShortcutAction.Arrange1 + i), $"Ctrl+Alt+Shift+F{i + 1}");
                Migrate((ShortcutAction)((int)ShortcutAction.Primary1 + i), $"Ctrl+Alt+Shift+F{i + 5}");
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
        return new() { Enabled = Enabled, Bindings = bindings, DisabledActions = disabled, NumberKeyVersion = CurrentVersion };
    }

    public static int? ScreenNumber(ShortcutAction action) => action switch
    {
        ShortcutAction.Screen1 => 1, ShortcutAction.Screen2 => 2,
        ShortcutAction.Screen3 => 3, ShortcutAction.Screen4 => 4, _ => null
    };

    public static int? ArrangementNumber(ShortcutAction action) => action switch
    {
        ShortcutAction.Arrange1 => 1, ShortcutAction.Arrange2 => 2,
        ShortcutAction.Arrange3 => 3, ShortcutAction.Arrange4 => 4,
        ShortcutAction.Arrange5 => 5, ShortcutAction.Arrange6 => 6, ShortcutAction.Arrange7 => 7,
        ShortcutAction.Arrange8 => 8, ShortcutAction.Arrange9 => 9, _ => null
    };

    public static int? PrimaryNumber(ShortcutAction action) => action switch {
        ShortcutAction.Primary1 => 1, ShortcutAction.Primary2 => 2,
        ShortcutAction.Primary3 => 3, ShortcutAction.Primary4 => 4,
        ShortcutAction.Primary5 => 5, ShortcutAction.Primary6 => 6, ShortcutAction.Primary7 => 7,
        ShortcutAction.Primary8 => 8, ShortcutAction.Primary9 => 9, _ => null
    };
}
