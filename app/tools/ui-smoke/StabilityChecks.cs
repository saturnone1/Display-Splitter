using System.IO;
using DesktopSplitter.Models;
using DesktopSplitter.Interop;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;

internal static class StabilityChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static void Run()
    {
        CheckShutdown().GetAwaiter().GetResult();
        CheckConfirmation();
        CheckSignals();
        CheckSettings();
        CheckIdentities();
        CheckRecovery();
        Console.WriteLine("PASS stability: cooperative exits/cancellation, fake-clock safety expiry, late answers, readiness/death races, notification waits, atomic saves, concurrent updates, malformed settings and strict monitor identity; no display actions or actual process starts/stops");
    }

    private static async Task CheckShutdown()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        Func<CancellationToken, Task>[] exits = { _ => { waits++; return exit.Task; } };
        Check(!await CooperativeShutdown.StopAsync(() => false, exits, default) && waits == 0, "Rejected stop observed as completed shutdown");
        using var cancellation = new CancellationTokenSource();
        Task<bool> pending = CooperativeShutdown.StopAsync(() => true, exits, cancellation.Token);
        Check(!pending.IsCompleted, "Stop request was mistaken for actual process exit");
        cancellation.Cancel();
        try { await pending; throw new Exception("Cancelled shutdown succeeded"); } catch (OperationCanceledException) { }
        Check(!exit.Task.IsCompleted, "Cancelling a wait terminated the underlying lifetime");
        Task<bool> completed = CooperativeShutdown.StopAsync(() => true, exits, default);
        exit.SetResult();
        Check(await completed, "Observed exit did not finish shutdown");
    }

    private static void CheckConfirmation()
    {
        var clock = new ManualClock();
        var session = new ConfirmationSession();
        var pending = session.WaitAsync(TimeSpan.FromSeconds(15), default, clock);
        clock.Advance(TimeSpan.FromSeconds(14));
        Check(!pending.IsCompleted, "Safety confirmation expired early");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(!pending.GetAwaiter().GetResult(), "Unconfirmed change was kept");
        session.Keep(); session.Revert(); // late UI callbacks cannot touch a disposed wait handle
        var accepted = new ConfirmationSession();
        var keep = accepted.WaitAsync(TimeSpan.FromSeconds(30), default, clock);
        accepted.Keep(); accepted.Revert();
        Check(keep.GetAwaiter().GetResult(), "Duplicate callback replaced an accepted decision");
        using var cancellation = new CancellationTokenSource();
        var cancelled = new ConfirmationSession();
        var answer = cancelled.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token, clock);
        cancellation.Cancel();
        Check(!answer.GetAwaiter().GetResult(), "Cancelled confirmation kept a change");
        cancelled.Keep();
    }

    private static void CheckSignals()
    {
        using var live = new ManualResetEvent(false);
        using var exited = new ManualResetEvent(false);
        live.Set(); exited.Set();
        Check(ReadinessWait.Wait(live, exited, TimeSpan.Zero, default) == CompositorLauncher.SpecializedWait.CompositorExited,
            "Stale readiness hid an already-dead compositor");
        exited.Reset();
        Check(ReadinessWait.Wait(live, exited, TimeSpan.Zero, default) == CompositorLauncher.SpecializedWait.Live, "Readiness signal ignored");
        live.Reset();
        Check(ReadinessWait.Wait(live, exited, TimeSpan.Zero, default) == CompositorLauncher.SpecializedWait.TimedOut, "Missing frames were treated as ready");

        using var changed = new AutoResetEvent(false);
        using var read = new ManualResetEventSlim();
        int state = 0;
        var result = Task.Run(() => DisplayStateWaiter.Wait(() => { read.Set(); return Volatile.Read(ref state); }, value => value == 1, changed, default));
        read.Wait();
        Volatile.Write(ref state, 1); changed.Set();
        Check(result.GetAwaiter().GetResult() == 1, "Notification during the initial read was lost");
        try { DisplayStateWaiter.Wait<int>(() => throw new IOException("probe failed"), _ => true, changed, default); throw new Exception("Read failure was swallowed"); }
        catch (IOException) { }
        using var cancellation = new CancellationTokenSource();
        read.Reset();
        var cancelled = Task.Run(() => DisplayStateWaiter.Wait(() => { read.Set(); return false; }, value => value, changed, cancellation.Token));
        read.Wait(); cancellation.Cancel();
        try { cancelled.GetAwaiter().GetResult(); throw new Exception("Cancelled monitor wait completed successfully"); } catch (OperationCanceledException) { }
    }

    private static void CheckSettings()
    {
        string directory = Path.GetFullPath(Path.Combine("artifacts", "stability-tests", Guid.NewGuid().ToString("N")));
        string path = Path.Combine(directory, "settings.json");
        var store = new SettingsFile(path);
        try
        {
            store.Save(new AppSettings { PhysicalDevice = "fixture", SafetyConfirmationSeconds = 42 });
            Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() => new SettingsFile(path).Update(settings =>
                settings.SpecializationHashes[index.ToString()] = "retained")))).GetAwaiter().GetResult();
            Check(store.Load() is { SafetyConfirmationSeconds: 42, PhysicalDevice: "fixture" } saved && saved.SpecializationHashes.Count == 12,
                "Concurrent updates lost another preference");
            string before = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { AtomicFile.WriteText(path, "partial"); throw new Exception("Locked destination unexpectedly replaced"); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                Check(File.ReadAllText(path) == before, "Failed commit damaged the previous file");
            }
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed save leaked a temporary file");
            try { store.Update(settings => { settings.PhysicalDevice = "uncommitted"; throw new IOException("fixture mutation failed"); }); } catch (IOException) { }
            Check(store.Load()!.PhysicalDevice == "fixture", "Failed update was committed");
            AtomicFile.WriteText(path, "{\"segments\":null,\"shortcuts\":null,\"specializationHashes\":null}");
            Check(store.Load() is { Segments: not null, Shortcuts: not null, SpecializationHashes: not null }, "Null settings can crash startup");
            AtomicFile.WriteText(path, "corrupt");
            Check(store.Load() is null, "Corrupt JSON was accepted");
            try { store.Update(settings => settings.PhysicalDevice = "lost"); throw new Exception("Corrupt settings were overwritten"); }
            catch (System.Text.Json.JsonException) { }
            Check(File.ReadAllText(path) == "corrupt", "Failed update destroyed corrupt source evidence");
        }
        finally
        {
            if (Directory.Exists(directory)) {
                foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static void CheckIdentities()
    {
        var monitors = new[] { new MonitorInfo { DeskSplitMonitorIndex = 1 }, new MonitorInfo { DeskSplitMonitorIndex = 0 } };
        Check(ApplyService.TryMapByEdid(monitors, 2, out var mapped, out _) && mapped[0].DeskSplitMonitorIndex == 0, "Driver indices did not determine order");
        Check(!ApplyService.TryMapByEdid(new[] { monitors[0], monitors[0] }, 2, out _, out _), "Duplicate identities were guessed");
        Check(!ApplyService.TryMapByEdid(monitors.Take(1).ToArray(), 2, out _, out _), "Incomplete identities were guessed");
        Check(SplitSuspensionWatcher.DecodeState(true, true) == SuspensionRequest.Unknown &&
              SplitSuspensionWatcher.DecodeState(false, false) == SuspensionRequest.Unknown,
              "Ambiguous compositor signals changed the primary state");
        Check(SplitSuspensionWatcher.DecodeState(true, false) == SuspensionRequest.Suspended &&
              SplitSuspensionWatcher.DecodeState(false, true) == SuspensionRequest.Active, "Exclusive compositor signal ignored");
        var hidden = new MonitorInfo { MonitorDevicePath = "exact-path", TargetId = 7, DeviceName = "" };
        var unrelated = new MonitorInfo { MonitorDevicePath = "other-path", TargetId = 8, DeviceName = "DISPLAY1" };
        Check(DisplayConfig.FindUniqueTarget(new[] { unrelated, hidden, hidden }, "EXACT-PATH") == hidden, "Inactive target could not be resolved by identity");
        Check(DisplayConfig.FindUniqueTarget(new[] { unrelated }, "exact-path") is null, "Recovery guessed another target");
        try {
            DisplayConfig.FindUniqueTarget(new[] { hidden, new MonitorInfo { MonitorDevicePath = "exact-path", TargetId = 9 } }, "exact-path");
            throw new Exception("Ambiguous recovery identity was accepted");
        } catch (DisplayConfigException) { }
    }

    private static void CheckRecovery()
    {
        int recovered = 0;
        Check(PipelineRecovery.Run(_ => 42, () => recovered++) == 42 && recovered == 0, "Successful work invoked recovery");
        try { PipelineRecovery.Run<int>(_ => throw new IOException("preflight"), () => recovered++); } catch (IOException) { }
        Check(recovered == 0, "Read-only failure invoked recovery");
        try { PipelineRecovery.Run<int>(changed => { changed(); throw new OperationCanceledException(); }, () => recovered++); }
        catch (OperationCanceledException) { }
        Check(recovered == 1, "Cancelled partial mutation was not recovered exactly once");
        try {
            PipelineRecovery.Run<int>(changed => { changed(); throw new IOException("operation"); }, () => { recovered++; throw new IOException("recovery"); });
            throw new Exception("Recovery failure was hidden");
        } catch (AggregateException error) { Check(error.InnerExceptions.Count == 2, "Original failure was lost"); }
        Check(recovered == 2, "Failed recovery was retried recursively");
    }

    internal sealed class ManualClock : TimeProvider
    {
        public ManualResetEventSlim Armed { get; } = new();
        private readonly List<ManualTimer> _timers = new();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            Armed.Set();
            return timer;
        }
        public void Advance(TimeSpan elapsed) {
            _ticks += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.Due <= _ticks) timer.Fire();
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, long due) : ITimer
        {
            public long Due { get; } = due;
            public bool Disposed { get; private set; }
            public void Fire() { Disposed = true; callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
