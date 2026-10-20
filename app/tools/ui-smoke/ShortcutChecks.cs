using DesktopSplitter.Models;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;
using System.Text.Json;
using System.IO;

internal static class ShortcutChecks
{
    public static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var defaults = new ShortcutSettings();
        Check(!defaults.Enabled, "Shortcuts must default off");
        Check(ShortcutChord.Validate(defaults, out var chords, out _), "Default gestures must be valid");
        Check(chords.Count == 11 && chords.Where(pair => ShortcutSettings.PrimaryNumber(pair.Key) is null).All(pair => pair.Value.Modifiers == 7 && pair.Value.Key is >= 0x70 and <= 0x7A), "Arrangement/app defaults changed");
        Check(chords.Where(pair => ShortcutSettings.PrimaryNumber(pair.Key) is not null).All(pair => pair.Value.Modifiers == 7 && pair.Value.Key == 0x73 + ShortcutSettings.PrimaryNumber(pair.Key)), "Representative defaults must use Ctrl+Alt+Shift+F5..F8");
        var removed = new ShortcutSettings { Bindings = new() { [ShortcutAction.PreviousWindow] = "Ctrl+Alt+PageUp", [ShortcutAction.NextWindow] = "Ctrl+Alt+PageDown" } }.Copy();
        Check(!removed.Bindings.ContainsKey(ShortcutAction.PreviousWindow) && !removed.Bindings.ContainsKey(ShortcutAction.NextWindow), "Removed actions survived migration");
        foreach (string invalid in new[] { "", "A", "Shift+A", "Ctrl+Ctrl+A", "Ctrl+123", "Ctrl+F12", "Alt+F4", "Ctrl+Alt+Delete", "Ctrl+Alt+Shift+S" })
            Check(!ShortcutChord.TryParse(invalid, out _, out _), "Invalid/reserved gesture accepted: " + invalid);
        Check(ShortcutChord.TryParse("control+ALT+1", out var digit, out _) && digit.Key == 0x31, "Digit gesture parsing");
        Check(ShortcutChord.TryParse("Ctrl+Alt+PgUp", out var pgup, out _) && pgup.Key == 0x21, "PgUp alias failed");
        Check(ShortcutChord.TryParse("Ctrl+Alt+PgDn", out var pgdn, out _) && pgdn.Key == 0x22, "PgDn alias failed");
        Check(ShortcutChord.TryParse("Ctrl+Alt+,", out var comma, out _) && comma.Key == 0xBC, "Laptop comma key failed");
        Check(ShortcutChord.KeyName(System.Windows.Input.Key.D4) == "4" && ShortcutChord.KeyName(System.Windows.Input.Key.Next) == "PageDown", "Captured key names were not normalized");
        var legacy = new ShortcutSettings { Enabled = true, Bindings = new() { [ShortcutAction.ShowApp] = "Ctrl+Alt+Space" } }.Copy();
        Check(legacy.Bindings.Count == 11 && legacy.Bindings[ShortcutAction.ShowApp] == "Ctrl+Alt+Space" && legacy.Bindings[ShortcutAction.Primary4] == "Ctrl+Alt+Shift+F8", "Migration lost custom keys or new defaults");
        var oldNumbers = new ShortcutSettings { Bindings = new() { [ShortcutAction.Screen1] = "Ctrl+Alt+1", [ShortcutAction.Screen2] = "Ctrl+Shift+2", [ShortcutAction.ShowApp] = "Ctrl+Alt+Shift+F1" } }.Copy();
        Check(!oldNumbers.Bindings.ContainsKey(ShortcutAction.Screen1) && !oldNumbers.Bindings.ContainsKey(ShortcutAction.Screen2)
            && oldNumbers.DisabledActions.Contains(ShortcutAction.Arrange1) && ShortcutChord.Validate(oldNumbers, out _, out _), "Number-key migration lost custom keys or created a conflict");
        var currentNumbers = oldNumbers.Copy(); currentNumbers.Bindings[ShortcutAction.Primary1] = "Ctrl+Alt+1";
        Check(currentNumbers.Copy().Bindings[ShortcutAction.Primary1] == "Ctrl+Alt+1", "Explicit post-migration customization was overwritten");
        var existingFive = new ShortcutSettings { Bindings = new() { [ShortcutAction.ShowApp] = "Ctrl+Alt+Shift+F5", [ShortcutAction.Screen1] = "Ctrl+Alt+1" } }.Copy();
        Check(existingFive.DisabledActions.Contains(ShortcutAction.Primary1) && existingFive.Bindings[ShortcutAction.ShowApp] == "Ctrl+Alt+Shift+F5"
            && ShortcutChord.Validate(existingFive, out _, out _), "Renumbering stole an existing custom Ctrl+5 binding");
        var previousDefaults = new ShortcutSettings { NumberKeyVersion = 3, Bindings = new() {
            [ShortcutAction.Primary1] = "Ctrl+5", [ShortcutAction.Primary2] = "Ctrl+6",
            [ShortcutAction.Primary3] = "Ctrl+7", [ShortcutAction.Primary4] = "Ctrl+8" } }.Copy();
        Check(previousDefaults.NumberKeyVersion == 4 && previousDefaults.Bindings[ShortcutAction.Primary1] == "Ctrl+Alt+Shift+F5"
            && previousDefaults.Bindings[ShortcutAction.Primary4] == "Ctrl+Alt+Shift+F8", "Representative defaults did not migrate");
        var duplicate = defaults.Copy();
        duplicate.Bindings[ShortcutAction.Primary4] = "Control+Alt+Shift+F9";
        Check(!ShortcutChord.Validate(duplicate, out _, out _), "Equivalent duplicate was accepted");
        duplicate.DisabledActions.Add(ShortcutAction.Primary4);
        Check(ShortcutChord.Validate(duplicate, out _, out _), "Disabled shortcut participated in duplicate validation");
        Check(ShortcutExecutionPolicy.CanExecute(ShortcutAction.ShowApp, false, true, true), "F11 was blocked by a running operation");
        Check(!ShortcutExecutionPolicy.CanExecute(ShortcutAction.Primary1, false, true, false), "Representative choice was allowed while busy");
        Check(!ShortcutExecutionPolicy.CanExecute(ShortcutAction.ShowApp, true, false, false), "Editing executed an action");

        var fake = new FakeRegistrar();
        using var service = new GlobalShortcutService(1, fake);
        Check(service.Configure(defaults) is null && fake.RegisterCalls == 0, "Disabled configuration registered global keys");
        var enabled = defaults.Copy(); enabled.Enabled = true;
        Check(service.Configure(enabled) is null && fake.Registered.Count == ShortcutSettings.Actions.Count, "Registration failed");
        Check(fake.Registered.Values.All(c => (c.Modifiers & 0x4000) != 0), "MOD_NOREPEAT was not used");
        var dispatched = new List<ShortcutAction>();
        service.Pressed += dispatched.Add; // A recorder only: no display or window actions.
        static nint Data(ShortcutChord chord) => (nint)((chord.Key << 16) | chord.Modifiers);
        var registrations = fake.Registered.ToArray();
        foreach (var action in chords)
        {
            var registered = fake.Registered.Single(r => r.Value.Key == action.Value.Key && (r.Value.Modifiers & 15) == action.Value.Modifiers);
            Check(service.HandleMessage(GlobalShortcutService.MessageId, registered.Key, Data(action.Value)), "Matching hotkey was ignored");
            Check(dispatched[^1] == action.Key, "Incorrect shortcut action routed");
        }
        Check(!service.HandleMessage(GlobalShortcutService.MessageId, registrations[0].Key, 0), "Mismatched payload was routed");
        var firstAction = chords[ShortcutAction.ShowApp];
        Check(service.HandleMessage(GlobalShortcutService.MessageId, registrations[0].Key, Data(firstAction) | 0x4000), "Registration behavior flag prevented dispatch");
        fake.FailKey = 0x7C; // F13, deliberately unavailable (defaults are F1..F11)
        var changed = enabled.Copy(); changed.Bindings[ShortcutAction.Apply] = "Ctrl+Alt+F13";
        Check(service.Configure(changed) is null && fake.Registered.Count == ShortcutSettings.Actions.Count - 1 && service.RegistrationErrors.ContainsKey(ShortcutAction.Apply), "One conflict disabled unrelated shortcuts");
        Check(!service.HandleMessage(GlobalShortcutService.MessageId, registrations[0].Key, Data(chords[ShortcutAction.ShowApp])), "Stale queued message survived reconfiguration");
        fake.FailKey = 0;
        Check(service.Configure(defaults) is null && fake.Registered.Count == 0, "Disabling did not unregister keys");
        Check(!service.HandleMessage(GlobalShortcutService.MessageId, registrations[0].Key, Data(chords[ShortcutAction.ShowApp])), "Disabled shortcut dispatched");

        AppSettings stored = new() { PhysicalDevice = "TEST", ApplyOnStartup = false, HidePhysicalDisplay = true };
        static AppSettings Copy(AppSettings value) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value))!;
        bool failSave = false;
        var vm = new ShortcutSettingsViewModel(_ => { }, load: () => Copy(stored), save: value =>
        {
            if (failSave) throw new IOException("test save failure");
            stored = Copy(value);
        });
        vm.AttachRegistrar(service.Configure, () => service.RegistrationErrors);
        vm.Enabled = true;
        vm.SaveCommand.Execute(null);
        Check(stored.Shortcuts.Enabled && stored.PhysicalDevice == "TEST" && stored.HidePhysicalDisplay && !stored.ApplyOnStartup, "Saving shortcuts lost other settings");
        Check(fake.Registered.Count == ShortcutSettings.Actions.Count, "Saved configuration not registered");
        vm.SetEditing(true);
        Check(fake.Registered.Count == 0 && stored.Shortcuts.Enabled, "Editing did not pause keys independently of saved settings");
        vm.SetEditing(false);
        Check(fake.Registered.Count == ShortcutSettings.Actions.Count, "Editing did not restore saved keys");
        failSave = true;
        vm.Rows.Single(r => r.Action == ShortcutAction.Apply).Gesture = "Ctrl+Alt+F2";
        vm.SaveCommand.Execute(null);
        Check(vm.Status.Contains("저장하지 못"), "Save error was not reported");
        Check(vm.Current.Bindings[ShortcutAction.Apply] == enabled.Bindings[ShortcutAction.Apply] && fake.Registered.Values.Any(c => c.Key == 0x78), "Save failure did not restore old key");
        failSave = false;
        vm.ResetCommand.Execute(null);
        vm.Rows.Single(r => r.Action == ShortcutAction.Primary4).Enabled = false;
        vm.SaveCommand.Execute(null);
        Check(stored.Shortcuts.DisabledActions.Contains(ShortcutAction.Primary4) && fake.Registered.Count == 10, "Per-action opt-out was not persisted");
        vm.Rows.Single(r => r.Action == ShortcutAction.Apply).Gesture = "Ctrl+Alt+F13";
        fake.FailKey = 0x7C;
        vm.SaveCommand.Execute(null);
        Check(vm.Rows.Single(r => r.Action == ShortcutAction.Apply).HasRegistrationIssue && fake.Registered.Count == 9
            && vm.Status.Contains("등록 실패"), "Partial registration failure was not visible per row");
        vm.ResetCommand.Execute(null);
        Check(vm.Rows.All(row => row.Gesture == ShortcutSettings.Defaults()[row.Action]), "Reset did not restore function keys");
        service.Dispose();
        Check(fake.Registered.Count == 0 && !service.HandleMessage(GlobalShortcutService.MessageId, registrations[0].Key, 0), "Disposed service retained keys");

        var sourceBounds = new WindowBox(0, 0, 1920, 1080);
        var sourceWork = new WindowBox(0, 40, 1920, 1040); // top taskbar
        var targetBounds = new WindowBox(-1280, 0, 1280, 1024);
        var targetWork = new WindowBox(-1240, 0, 1240, 1024); // left taskbar
        var plan = SplitWindowMover.Plan(new(100, 100, 800, 600), sourceBounds, sourceWork, targetBounds, targetWork);
        Check(plan == new WindowBox(-1241, 96, 800, 600), "Taskbar workspace offsets or relative placement changed");
        int screenX = plan.X + targetWork.X - targetBounds.X;
        int screenY = plan.Y + targetWork.Y - targetBounds.Y;
        Check(screenX >= targetWork.X && screenX + plan.Width <= targetWork.X + targetWork.Width && screenY >= targetWork.Y && screenY + plan.Height <= targetWork.Y + targetWork.Height, "Window escaped target work area");
        var oversize = SplitWindowMover.Plan(new(-500, -500, 4000, 3000), sourceBounds, sourceWork, targetBounds, targetWork);
        Check(oversize.Width == targetWork.Width && oversize.Height == targetWork.Height, "Oversized window not fitted");
        var monitors = new[]
        {
            new MonitorInfo { DeviceName = "DISPLAY3", IsDeskSplitVirtual = true, DeskSplitMonitorIndex = 2 },
            new MonitorInfo { DeviceName = "display1", IsDeskSplitVirtual = true, DeskSplitMonitorIndex = 0 },
            new MonitorInfo { DeviceName = "PHYSICAL" }
        };
        var areas = new[] { "DISPLAY3", "DISPLAY1\0", "PHYSICAL" }.Select(name => new MonitorArea(name, targetBounds, targetWork));
        var targets = SplitWindowMover.BuildTargets(monitors, areas);
        Check(targets.Select(t => t.Number).SequenceEqual(new[] { 1, 3 }), "Screen numbers were reindexed or physical display included");
        Check(SplitWindowMover.SelectTarget(targets, 3)?.Number == 3, "Direct screen target was not selected");
        Check(SplitWindowMover.SelectTarget(targets, 2) is null, "Missing screen number targeted another display");
        for (int i = 1; i <= 4; i++)
            Check(ShortcutSettings.ScreenNumber((ShortcutAction)((int)ShortcutAction.Screen1 + i - 1)) == i, "Action-to-screen mapping failed");
        var sequence = new List<string>();
        SplitWindowMover.ExecutePlacement(true, () => sequence.Add("restore"), () => sequence.Add("place"), () => sequence.Add("maximize"));
        Check(sequence.SequenceEqual(new[] { "restore", "place", "maximize" }), "Maximized window move order was incorrect");
        sequence.Clear();
        try { SplitWindowMover.ExecutePlacement(true, () => sequence.Add("restore"), () => throw new IOException("placement failed"), () => sequence.Add("maximize")); }
        catch (IOException) { }
        Check(sequence.SequenceEqual(new[] { "restore", "maximize" }), "Failed move lost maximized state");
        CheckReordering();
        Console.WriteLine("PASS shortcuts: Ctrl+Alt+Shift+F5..F8 matrix representatives, seven arrangement/app keys, retired window-move actions, custom-key preservation, conflicts, stale messages, editing, persistence and geometry; no native keys registered, displays changed or windows moved");
    }

    private static void CheckReordering()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var config = new CompositorConfig { PhysicalDevice = "P", Segments = Enumerable.Range(0, 4).Select(i => new CompositorSegment {
            VirtualDevice = $"V{i}", Width = 100, Height = 100,
            PhysRect = new() { X = (i % 2) * 100, Y = (i / 2) * 100, W = 100, H = 100 } }).ToList() };
        var monitors = Enumerable.Range(0, 4).Select(i => new MonitorInfo { DeviceName = $"V{i}", IsDeskSplitVirtual = true,
            DeskSplitMonitorIndex = i, CurrentWidth = 100, CurrentHeight = 100,
            PositionX = (i % 2) * 100, PositionY = (i / 2) * 100, IsPrimary = i == 0 }).ToArray();
        uint identity = SplitDisplayReorder.Encode(new[] { 0, 1, 2, 3 });
        Check(identity == 0x445304E4 && SplitDisplayReorder.Decode(identity).SequenceEqual(new[] { 0, 1, 2, 3 }), "Native routing wire format changed");
        Check(SplitDisplayReorder.Fingerprint(config) == 0x586EFB67, "Native config fingerprint changed");
        var order = SplitDisplayReorder.Swap(new[] { 0, 1, 2, 3 }, 3, 1);
        Check(order.SequenceEqual(new[] { 3, 1, 2, 0 }), "Whole-desktop swap shifted unrelated slots");
        var targets = monitors.Select((m, i) => new SplitTarget(i + 1, m.DeviceName, new(0, 0, 100, 100), new(0, 0, 100, 100))).ToArray();
        var routed = SplitDisplayReorder.MapTargets(targets, config, order);
        Check(routed[0].Number == 1 && routed[0].DeviceName == "V3" && routed[3].DeviceName == "V0", "Window movement ignored current slot routing");
        Check(SplitWindowMover.SelectTarget(routed, 4)?.DeviceName == "V0", "Direct window movement ignored slot order");
        var plan = SplitDisplayReorder.Plan(monitors, config, order);
        var a = plan.Single(p => p.DeviceName == "V0"); var b = plan.Single(p => p.DeviceName == "V3");
        Check(a.IsPrimary && a.X == 0 && a.Y == 0 && b.X == -100 && b.Y == -100, "Primary identity or relative positions changed");
        Check(plan.All(p => p.Width == 100 && p.Height == 100 && p.RefreshHz == 0), "Swap changed native display modes");
        Check(SplitDisplayReorder.Swap(order, 3, 1).SequenceEqual(order), "Repeated target was not idempotent");
        Check(SplitDisplayReorder.Swap(order, 3, 4).SequenceEqual(new[] { 0, 1, 2, 3 }), "Repeated swap could not restore ordering");
        try { SplitDisplayReorder.Swap(new[] { 0, 1 }, 1, 4); throw new Exception("Missing slot accepted"); } catch (InvalidOperationException) { }
        try { SplitDisplayReorder.Decode(0x44530400); throw new Exception("Duplicate sources accepted"); } catch (ArgumentException) { }
        // Unequal resolutions use new tracks, preserving modes instead of resizing desktops.
        monitors[3] = new MonitorInfo { DeviceName = "V3", IsDeskSplitVirtual = true, CurrentWidth = 200, CurrentHeight = 150, PositionX = 100, PositionY = 100 };
        plan = SplitDisplayReorder.Plan(monitors, config, order);
        Check(plan.Single(p => p.DeviceName == "V3").Width == 200 && plan.Single(p => p.DeviceName == "V0").Width == 100, "Unequal resolutions were changed");
        var calls = new List<string>();
        SplitDisplayReorder.Execute(() => calls.Add("positions"), () => calls.Add("route"), () => calls.Add("undo-positions"), () => calls.Add("undo-route"));
        Check(calls.SequenceEqual(new[] { "positions", "route" }), "Successful transaction rolled back");
        calls.Clear();
        try { SplitDisplayReorder.Execute(() => calls.Add("positions"), () => throw new IOException("route failed"), () => calls.Add("undo-positions"), () => calls.Add("undo-route")); } catch (IOException) { }
        Check(calls.SequenceEqual(new[] { "positions", "undo-route", "undo-positions" }), "Failed route left desktop positions changed");
        calls.Clear();
        try { SplitDisplayReorder.Execute(() => throw new IOException("positions failed"), () => calls.Add("route"), () => calls.Add("undo-positions"), () => calls.Add("undo-route")); } catch (IOException) { }
        Check(calls.Count == 0, "Rejected CCD batch triggered further display operations");
        try { SplitDisplayReorder.Execute(() => calls.Add("positions"), () => throw new IOException("route failed"), () => calls.Add("undo-positions"), () => throw new IOException("undo-route failed")); }
        catch (AggregateException ex) { Check(ex.InnerExceptions.Count == 2, "Rollback error lost original failure"); }
        Check(calls.SequenceEqual(new[] { "positions" }), "Unknown routing triggered a blind position restoration");
        var physical = new MonitorInfo { DeviceName = "REAL", CurrentWidth = 100, CurrentHeight = 100, PositionX = 0, PositionY = 0 };
        try { SplitDisplayReorder.Plan(monitors.Append(physical).ToArray(), config, order); throw new Exception("Physical overlap accepted"); } catch (InvalidOperationException) { }
    }

    private sealed class FakeRegistrar : IShortcutRegistrar
    {
        public Dictionary<int, ShortcutChord> Registered { get; } = new();
        public uint FailKey { get; set; }
        public int RegisterCalls { get; private set; }
        public int LastError => 1409;
        public bool Register(nint window, int id, uint modifiers, uint key)
        {
            RegisterCalls++;
            if (key == FailKey) return false;
            Registered.Add(id, new(modifiers, key));
            return true;
        }
        public bool Unregister(nint window, int id) => Registered.Remove(id);
    }
}
