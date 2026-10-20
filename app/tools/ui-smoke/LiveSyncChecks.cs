using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DesktopSplitter.Models;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;

internal static class LiveSyncChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        string? before = File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null;
        var fake = new FakeDesktop();
        var view = new MainViewModel(manualReview: true, statusReader: fake.Read, desktopActions: fake);
        Program.CompleteOnDispatcher(view.Initialization);
        view.SelectedLayout = LayoutDefinition.FromKind(LayoutKind.Grid2x2);
        var expectedSizes = view.Segments.Select(segment => (segment.Width, segment.Height)).ToArray();
        var window = new DesktopSplitter.MainWindow(view, null);
        var apply = (System.Windows.Controls.Primitives.ButtonBase)window.FindName("ApplyButton");
        var revert = (System.Windows.Controls.Primitives.ButtonBase)window.FindName("RevertButton");
        var liveRows = (System.Windows.Controls.ItemsControl)window.FindName("LiveSlotsList");
        var choice = (System.Windows.Controls.ComboBox)window.FindName("MatrixPrimaryChoice");
        var issues = new List<string>();
        void AssertUi(string name) {
            if (!app.Dispatcher.CheckAccess() || SynchronizationContext.Current is not DispatcherSynchronizationContext) issues.Add(name);
        }
        view.PropertyChanged += (_, args) => AssertUi(args.PropertyName ?? "property");
        view.ApplyCommand.CanExecuteChanged += (_, _) => AssertUi("Apply eligibility");
        view.RevertCommand.CanExecuteChanged += (_, _) => AssertUi("Revert eligibility");
        view.PrimaryHighlightRequested += _ => AssertUi("highlight");
        // WM_HOTKEY can arrive without a WPF SynchronizationContext on the UI OS thread.
        // Call the actual shortcut entry pipeline, with only desktop actions replaced by fakes.
        var process = typeof(DesktopSplitter.MainWindow).GetMethod("ProcessShortcutAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Press(ShortcutAction action) {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { return (Task)process.Invoke(window, new object[] { action })!; }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        void Enabled() {
            window.UpdateLayout();
            Check(view.Segments.Select(segment => (segment.Width, segment.Height)).SequenceEqual(expectedSizes),
                "Monitor refresh overwrote edited sizes through a transient null selection");
            Check(!view.IsBusy && view.IsIdle && view.CanApply && view.ApplyCommand.CanExecute(null) && view.RevertCommand.CanExecute(null)
                && apply.IsEnabled && revert.IsEnabled, $"Completed shortcut left actual bound buttons disabled: busy={view.IsBusy}, idle={view.IsIdle}, canApply={view.CanApply}, applyCommand={view.ApplyCommand.CanExecute(null)}, revertCommand={view.RevertCommand.CanExecute(null)}, applyButton={apply.IsEnabled}, revertButton={revert.IsEnabled}, sizes={string.Join(';', view.Segments.Select(s => $"{s.Width}x{s.Height}: {s.Error}"))}, log={view.StatusText}");
        }
        Program.CompleteOnDispatcher(Press(ShortcutAction.Primary3));
        Check(view.SelectedPrimarySlot == 3 && view.Zones.Single(zone => zone.IsPrimary).Number == 3
            && view.CurrentSlots.Single(text => text.Contains("선택됨")).StartsWith("칸 3")
            && Equals(choice.SelectedItem, 3), "Selection shortcut left stale UI state");
        Enabled();
        // A dropdown is a pending choice; the live badge/settings change only after Select succeeds.
        Program.CompleteOnDispatcher(app.Dispatcher.InvokeAsync(() => view.SelectedPrimarySlot = 2).Task);
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.SelectedPrimarySlot == 2 && view.Zones.Single(zone => zone.IsPrimary).Number == 3 && fake.Snapshot.SelectedSlot == 3,
            "A pending dropdown choice changed live selection or was erased by refresh");
        fake.FailSelect = true;
        Program.CompleteOnDispatcher(Press(ShortcutAction.Primary2));
        Check(view.Zones.Single(zone => zone.IsPrimary).Number == 3 && fake.Snapshot.SelectedSlot == 3,
            "Failed selection was displayed as committed");
        fake.FailSelect = false;
        Program.CompleteOnDispatcher(app.Dispatcher.InvokeAsync(() => view.SelectedPrimarySlot = 3).Task);
        Enabled();
        var segmentObjects = view.Segments.ToArray();
        int monitorChanges = 0, segmentChanges = 0, rowChanges = 0;
        view.Monitors.CollectionChanged += (_, _) => monitorChanges++;
        view.Segments.CollectionChanged += (_, _) => segmentChanges++;
        view.CurrentSlots.CollectionChanged += (_, _) => rowChanges++;
        for (int i = 0; i < 10; i++) Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(monitorChanges == 0 && segmentChanges == 0 && rowChanges == 0 && view.Segments.SequenceEqual(segmentObjects),
            "Unchanged snapshots rebuilt bound collections/editor objects");
        Program.CompleteOnDispatcher(app.Dispatcher.InvokeAsync(() => view.Segments[0].WidthText = "12x").Task);
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.Segments[0].WidthText == "12x" && ReferenceEquals(view.Segments[0], segmentObjects[0]),
            "Refresh rewrote partially typed input as an integer");
        Program.CompleteOnDispatcher(app.Dispatcher.InvokeAsync(() => view.Segments[0].WidthText = expectedSizes[0].Width.ToString()).Task);
        Program.CompleteOnDispatcher(Press(ShortcutAction.Arrange4));
        Check(view.CurrentSlots[3].Contains("화면 1") && view.CurrentSlots[0].Contains("화면 4")
            && view.CurrentSlots[2].Contains("선택됨") && liveRows.Items.Count == 4
            && liveRows.Items[3].ToString()!.Contains("화면 1"), "Swap shortcut left old routing or changed selection position");
        Enabled();
        Program.CompleteOnDispatcher(Press(ShortcutAction.Revert));
        Check(view.IsOriginalScreen && view.CurrentSlots.Count == 0 && liveRows.Items.Count == 0 && !view.IsSplitEditorExpanded
            && view.Zones.All(zone => !zone.IsPrimary), "Revert shortcut left the UI split");
        Enabled();
        fake.FailRevert = true;
        Program.CompleteOnDispatcher(Press(ShortcutAction.Revert));
        Enabled();
        fake.FailRevert = false;
        fake.WaitRevert = true;
        var pending = Press(ShortcutAction.Revert);
        Program.CompleteOnDispatcher(fake.Entered.Task);
        Check(view.IsBusy && !apply.IsEnabled && !revert.IsEnabled, "In-flight operation did not disable buttons");
        int readsBeforeCompletion = fake.Reads, choicesBeforeBusy = fake.SelectionCalls;
        Program.CompleteOnDispatcher(app.Dispatcher.InvokeAsync(() => {
            for (int i = 0; i < 10; i++) view.OnDisplayChanged();
        }).Task);
        Program.CompleteOnDispatcher(Press(ShortcutAction.Primary4));
        Check(fake.SelectionCalls == choicesBeforeBusy && fake.Reads == readsBeforeCompletion, "Busy shortcut/notifications started overlapping work");
        app.Dispatcher.InvokeAsync(view.CancelPendingOperations);
        Program.CompleteOnDispatcher(pending);
        Check(fake.Reads == readsBeforeCompletion + 1, "Completion spawned duplicate status readers");
        Enabled();
        Program.CompleteOnDispatcher(Task.Run(() => view.RefreshMonitorsAsync()));
        fake.Snapshot = fake.Snapshot with { Monitors = new[] { new MonitorInfo { DeviceName = "P-RENUMBERED", MonitorDevicePath = "fixture",
            FriendlyName = "검증 모니터", CurrentWidth = 2560, CurrentHeight = 1440, MaxWidth = 2560, MaxHeight = 1440 } } };
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.SelectedMonitor?.DeviceName == "P-RENUMBERED", "Monitor identity did not survive Windows device-name reassignment");
        Enabled();
        var remembered = fake.Snapshot.Monitors[0];
        fake.Snapshot = fake.Snapshot with { Monitors = new[] { remembered with { MonitorDevicePath = "foreign" } } };
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.SelectedMonitor is null && !view.CanApply && view.Segments.SequenceEqual(segmentObjects),
            "A disconnected preferred monitor silently fell back to another device or lost its draft");
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.SelectedMonitor is null, "A later refresh forgot the disconnected physical identity");
        fake.Snapshot = fake.Snapshot with { Monitors = new[] { remembered with { DeviceName = "P-RETURNED" } } };
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Check(view.SelectedMonitor?.DeviceName == "P-RETURNED", "Reconnected physical monitor was not recognized");
        Enabled();
        Check(MonitorSelection.Find(new[] { remembered, remembered with { MonitorDevicePath = "other", DeviceName = "X" } }, "missing", remembered.DeviceName) is null,
            "Known missing path fell back to a recycled Windows device name");
        Check(MonitorSelection.Find(new[] { remembered, remembered with { MonitorDevicePath = "other", DeviceName = "X" } }, null, null, remembered.FriendlyName) is null,
            "Legacy friendly-name lookup accepted ambiguous physical monitors");
        fake.WaitRevert = false;
        fake.Snapshot = fake.Snapshot with { ActiveVirtualCount = 4,
            ActiveTargets = Enumerable.Range(1, 4).Select(i => new SplitTarget(i, $"NEW{i}", new(0, 0, 1280, 720), new(0, 0, 1280, 720), i)).ToArray() };
        Program.CompleteOnDispatcher(view.RefreshMonitorsAsync());
        Program.CompleteOnDispatcher(Press(ShortcutAction.Primary1));
        Check(view.IsActiveMatrix && view.SelectedPrimarySlot == 1 && view.CurrentSlots[0].Contains("선택됨"),
            "A later split/selection stayed locked after merge or cancellation");
        Enabled();
        Check(issues.Count == 0, "UI notifications escaped WPF context: " + string.Join(", ", issues.Distinct()));
        Check(before == (File.Exists(SettingsStore.FilePath) ? File.ReadAllText(SettingsStore.FilePath) : null), "Live sync fixture changed user settings");
        window.Close();
        Console.WriteLine("PASS live sync: actual shortcut entry, committed vs pending choice, failure/cancellation unlock, monitor loss/reconnect/renumbering, partial input preservation, single completion read; 10 equal snapshots produced 0 monitor/segment/live-row mutations. Fake desktop only; no real keys/windows/displays changed.");
    }

    private sealed class FakeDesktop : IDesktopActions
    {
        public DesktopSnapshot Snapshot;
        public bool FailRevert, WaitRevert, FailSelect;
        public int Reads;
        public int SelectionCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DesktopSnapshot Read() {
            Interlocked.Increment(ref Reads);
            return Snapshot with { Monitors = Snapshot.Monitors.Select(monitor => monitor with { }).ToArray(),
                ActiveTargets = Snapshot.ActiveTargets?.Select(target => target with { }).ToArray() };
        }
        public FakeDesktop() {
            var monitor = new MonitorInfo { DeviceName = "P", MonitorDevicePath = "fixture", FriendlyName = "검증 모니터",
                CurrentWidth = 2560, CurrentHeight = 1440, MaxWidth = 2560, MaxHeight = 1440 };
            var targets = Enumerable.Range(1, 4).Select(i => new SplitTarget(i, $"V{i}", new(0, 0, 1280, 720), new(0, 0, 1280, 720), i)).ToArray();
            Snapshot = new(new[] { monitor }, "fixture", "fixture", 4, "fixture", LayoutKind.Grid2x2, targets, 1);
        }
        public string ForegroundDevice() => "V1";
        public string Reorder(int slot, string device, CancellationToken ct) {
            var targets = Snapshot.ActiveTargets!.ToArray();
            int source = Array.FindIndex(targets, target => target.DeviceName == device);
            (targets[source], targets[slot - 1]) = (targets[slot - 1], targets[source]);
            Snapshot = Snapshot with { ActiveTargets = targets.Select((target, index) => target with { Number = index + 1 }).ToArray() };
            return "fixture swapped";
        }
        public SplitTarget Select(int slot, string path, LayoutKind layout, CancellationToken ct) {
            SelectionCalls++;
            if (FailSelect) throw new InvalidOperationException("fixture selection failure");
            Snapshot = Snapshot with { SelectedSlot = slot };
            return Snapshot.ActiveTargets!.Single(target => target.Number == slot);
        }
        public void Revert(IProgress<string> log, CancellationToken ct) {
            if (FailRevert) throw new InvalidOperationException("fixture failure");
            if (WaitRevert) {
                Entered.TrySetResult();
                new TaskCompletionSource().Task.Wait(ct); // Only cancellation releases this fixture; no timeout.
            }
            Snapshot = Snapshot with { ActiveVirtualCount = 0, ActiveTargets = Array.Empty<SplitTarget>() };
        }
    }
}
