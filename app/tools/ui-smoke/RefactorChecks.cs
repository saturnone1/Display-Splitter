using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DesktopSplitter.Models;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;

internal static class RefactorChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var app = new DesktopSplitter.App(suppressStartup: true);
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Check(!HidingStatus.ResolveIntent(new AppSettings { HidePhysicalDisplay = false }, () => throw new Exception("Stale HKLM accessed")), "User opt-out was lost");
        Check(HidingStatus.ResolveIntent(new AppSettings { HidePhysicalDisplay = true }, () => 0), "User opt-in was lost");
        Check(HidingStatus.ResolveIntent(null, () => 1) && !HidingStatus.ResolveIntent(null, () => null), "Legacy defaults changed");
        Check(!ApplyService.SameMonitorTarget(new MonitorInfo { DeviceName = "DISPLAY1", MonitorDevicePath = "old" },
            new MonitorInfo { DeviceName = "DISPLAY1", MonitorDevicePath = "replacement" }), "Recycled GDI number was accepted as original target");
        CheckGates();
        Program.CompleteOnDispatcher(CheckRefreshAsync());
        Program.CompleteOnDispatcher(CheckRefreshLifetimeAsync());
        CheckLog();
        Console.WriteLine("PASS refactor: user intent precedence, cancellable/reentrant gates, UAC handoff, coalesced refresh/stale results/errors, bounded UI/file diagnostics; no display mutations");
    }

    private static void CheckGates()
    {
        var gate = new OperationGate();
        using var owner = gate.Enter();
        using (gate.Enter()) { } // Recursive compensation must never deadlock.
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var waiting = Task.Run(() => { started.Set(); using var lease = gate.Enter(cancellation.Token); });
        started.Wait(); cancellation.Cancel();
        try { waiting.GetAwaiter().GetResult(); throw new Exception("Contended gate ignored cancellation"); } catch (OperationCanceledException) { }
        var userGate = new OperationGate();
        using var user = userGate.Enter();
        using var userCancellation = new CancellationTokenSource();
        using var userStarted = new ManualResetEventSlim();
        var otherUser = Task.Run(() => { userStarted.Set(); using var lease = userGate.Enter(userCancellation.Token); throw new Exception("Concurrent user action entered"); });
        userStarted.Wait();
        Check(gate.Outside(() => Task.Run(() => { using var mutation = gate.Enter(); return 42; }).GetAwaiter().GetResult()) == 42,
            "Secure-desktop reconciliation could not acquire a released mutation gate");
        userCancellation.Cancel();
        try { otherUser.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        try { gate.Outside<int>(() => throw new IOException("UI failed")); } catch (IOException) { }
        using var restored = gate.Enter(); // Outside must restore recursive ownership on exceptions.
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { using var blocked = userGate.Enter(cancelled.Token); throw new Exception("Cancelled recursive entry succeeded"); } catch (OperationCanceledException) { }
    }

    private static async Task CheckRefreshAsync()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>(); int reads = 0;
        var refresh = new LatestRefresh<int>(() => {
            if (++reads == 1) { started.SetResult(); return first.Task; }
            restarted.SetResult(); return second.Task;
        }, seen.Add);
        Task pending = refresh.RequestAsync();
        for (int i = 0; i < 100; i++) Check(ReferenceEquals(pending, refresh.RequestAsync()), "Refresh spawned duplicate readers");
        await started.Task;
        _ = refresh.RequestAsync();
        first.SetResult(1);
        await restarted.Task;
        Check(seen.Count == 0 && reads == 2, "Obsolete snapshot was published");
        second.SetResult(2); await pending;
        Check(seen.SequenceEqual(new[] { 2 }), "Latest snapshot was lost");
        int attempts = 0;
        var recover = new LatestRefresh<int>(() => ++attempts == 1 ? Task.FromException<int>(new IOException("query")) : Task.FromResult(3), seen.Add);
        try { await recover.RequestAsync(); throw new Exception("Query failure was hidden"); } catch (IOException) { }
        await recover.RequestAsync();
        Check(seen.Last() == 3, "Failed query poisoned subsequent refresh");
        var stale = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int retry = 0;
        var refreshAfterFailure = new LatestRefresh<int>(() => { if (++retry == 1) { staleStarted.SetResult(); return stale.Task; } return Task.FromResult(4); }, seen.Add);
        Task last = refreshAfterFailure.RequestAsync(); await staleStarted.Task;
        _ = refreshAfterFailure.RequestAsync(); stale.SetException(new IOException("obsolete failure"));
        await last;
        Check(seen.Last() == 4, "An obsolete query failure discarded the newer request");
    }

    private static void CheckLog()
    {
        var log = new StatusLog(512);
        for (int i = 0; i < 10000; i++) log.Append($"{i}: {new string('x', 40)}");
        Check(log.Length <= 512 && log.ToString().EndsWith(new string('x', 40)) && log.ToString().Contains("9999:"), "UI log is unbounded or lost its latest entry");
        log.Append(new string('y', 1000));
        Check(log.Length == 512 && log.ToString().Length == 512, "Oversized line escaped UI limit");
        string directory = Path.GetFullPath(Path.Combine("artifacts", "refactor-tests", Guid.NewGuid().ToString("N")));
        var fileLog = new FileStatusLog(directory, queueCapacity: 1024, maxFileBytes: 512, maxFiles: 3);
        try {
            for (int i = 0; i < 100; i++) fileLog.Append($"{i}: {new string('한', 300)}");
            fileLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var files = new DirectoryInfo(directory).GetFiles("*.log");
            Check(fileLog.Failure is null && files.Length is > 0 and <= 3 && files.All(file => file.Length <= 512),
                $"File diagnostics exceeded retention/size bounds: failure={fileLog.Failure}, count={files.Length}, lengths={string.Join(',', files.Select(file => file.Length))}");
            Check(files.Select(file => File.ReadAllText(file.FullName)).Any(text => text.Contains("99:")), "Flushed file log lost latest entry");
        } finally {
            fileLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(directory)) { foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
    }

    private static async Task CheckRefreshLifetimeAsync()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var workerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new DesktopSnapshot(new[] { new MonitorInfo { DeviceName = "fixture", CurrentWidth = 2560, CurrentHeight = 1440, MaxRefreshHz = 144 } }, "fixture", "fixture");
        var closing = new MainViewModel(previewOnly: true, statusReader: () => {
            started.Set(); release.Wait(); workerFinished.SetResult(); return snapshot;
        });
        try {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            started.Wait();
            await closing.RevertOnExitAsync();
            await closing.Initialization;
            Check(closing.Monitors.Count == 0, "Closing view published an unfinished snapshot");
        } finally { release.Set(); await workerFinished.Task; }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(closing.Monitors.Count == 0, "Late worker updated a closed view");

        var view = new MainViewModel(previewOnly: true, statusReader: () => snapshot);
        await view.Initialization;
        view.Segments[0].WidthText = "1234";
        await view.RefreshMonitorsAsync();
        Check(view.Segments[0].WidthText == "1234", "Read-only refresh discarded edited segment dimensions");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        int publications = 0;
        view.StatusAppended += (_, _) => publications++;
        for (int i = 0; i < 100; i++) view.AppendStatus($"burst-{i}");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(publications == 1 && view.StatusText.Contains("burst-99"), "Log burst caused per-line UI refresh or lost newest line");
        await view.RevertOnExitAsync();
    }

    public static void Benchmark()
    {
        const int rows = 1024;
        string line = new('x', 160);
        object Measure(bool bounded) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            int publications = 0; string text = "";
            if (bounded) {
                var log = new StatusLog();
                for (int i = 0; i < rows; i++) { log.Append(line); if ((i + 1) % 32 == 0) { text = log.ToString(); publications++; } }
            } else {
                for (int i = 0; i < rows; i++) { text = string.IsNullOrEmpty(text) ? line : text + Environment.NewLine + line; publications++; }
            }
            watch.Stop();
            return new { milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, publications, retainedCharacters = text.Length };
        }
        _ = Measure(true); _ = Measure(false); // JIT warm-up, not a product-time delay.
        var report = new { scenario = "Synthetic 1024 x 160-character log burst; new UI publishes once per 32 entries. Not display/FPS measurement.", baseline = Measure(false), bounded = Measure(true) };
        string path = Path.GetFullPath("artifacts/refactor-performance.json");
        AtomicFile.WriteText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(path));
    }
}
