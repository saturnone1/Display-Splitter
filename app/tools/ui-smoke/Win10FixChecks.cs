using System.ComponentModel;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Services;
using DesktopSplitter.ViewModels;

internal static class Win10FixChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        Check(System.Runtime.InteropServices.Marshal.SizeOf<DESKSPLIT_DIAGNOSTICS>() == 12, "Adapter diagnostics ABI changed");
        Check(DriverClient.AdapterFailureMessage(new DESKSPLIT_DIAGNOSTICS { Version = 1, AdapterState = 3, AdapterInitStatus = unchecked((int)0xC00000BB) })?.Contains("C00000BB") == true,
            "Adapter initialization failure lost its native status");
        Check(DriverClient.AdapterFailureMessage(null) is null, "Older driver without diagnostics was blocked");
        Check(DriverClient.AdapterUnavailableMessage(new DESKSPLIT_DIAGNOSTICS { Version=1, AdapterState=1 })?.Contains("초기화") == true
            && DriverClient.AdapterUnavailableMessage(new DESKSPLIT_DIAGNOSTICS { Version=1, AdapterState=2 }) is null,
            "Initialization preflight accepted a configuration before adapter readiness");
        foreach (uint state in new uint[] { 0, 1, 2 })
            Check(DriverClient.AdapterFailureMessage(new DESKSPLIT_DIAGNOSTICS { Version = 1, AdapterState = state }) is null, "Pending/ready adapter was treated as failed");
        Check(DriverInterfaceDiscovery.PresentInterfaces == 0, "PRESENT flag includes inactive devices");
        Check(DriverDeviceDiagnostics.Format("fixture", 0, 52).Contains("서명")
            && DriverDeviceDiagnostics.Format("fixture", 0, 22).Contains("사용 안 함")
            && DriverDeviceDiagnostics.Format("fixture", 8, 0).Contains("시작됨"), "Missing-interface diagnostics hide PnP load/signature/disabled states");
        int sizes = 0, reads = 0;
        var paths = DriverInterfaceDiscovery.Read(() => (++sizes == 1 ? (0, 2u) : (0, 40u)), buffer => {
            if (++reads == 1) return DriverInterfaceDiscovery.BufferSmall;
            "live-a\0live-b\0\0".CopyTo(0, buffer, 0, 15); return 0;
        }, () => throw new Exception("Unneeded fallback"));
        Check(paths.SequenceEqual(new[] { "live-a", "live-b" }) && sizes == 2, "Device-arrival buffer race lost interfaces");
        var fallback = DriverInterfaceDiscovery.Read(() => (1, 0u), _ => throw new Exception(), () => new[] { "setupdi" });
        Check(fallback.Single() == "setupdi", "CM failure did not use SetupDi");
        using var cancel = new CancellationTokenSource();
        try { DriverInterfaceDiscovery.Read(() => (0, 2u), _ => { cancel.Cancel(); return DriverInterfaceDiscovery.BufferSmall; }, () => [], cancel.Token);
            throw new Exception("Enumeration ignored cancellation"); } catch (OperationCanceledException) { }
        var opened = new List<FakeHandle>();
        var probed = new List<string>();
        using (var connection = DriverDeviceConnector.Connect(new[] { "gone", "denied", "old", "healthy", "HEALTHY" }, path => {
            if (path == "gone") throw new Win32Exception(2);
            if (path == "denied") throw new Win32Exception(5);
            var handle = new FakeHandle(path); opened.Add(handle); return handle;
        }, handle => {
            probed.Add(handle.Path);
            return new DESKSPLIT_STATUS { Version = handle.Path == "old" ? 1u : DriverClient.DESKSPLIT_PROTOCOL_VERSION, ActiveMonitorCount = 0 };
        })) {
            Check(connection.Path == "healthy" && probed.SequenceEqual(new[] { "old", "healthy" }) && opened[0].Disposed && !opened[1].Disposed,
                "First stale/denied/incompatible interface prevented connection to a healthy one");
        }
        Check(opened.All(handle => handle.Disposed), "Probe leaked handles");
        foreach (bool duringOpen in new[] { true, false }) {
            using var handshakeCancel = new CancellationTokenSource();
            var interrupted = new FakeHandle("cancelled");
            bool queried = false;
            try {
                using var unexpected = DriverDeviceConnector.Connect(new[] { "cancelled" }, _ => {
                    if (duringOpen) handshakeCancel.Cancel();
                    return interrupted;
                }, _ => {
                    queried = true;
                    handshakeCancel.Cancel();
                    return new DESKSPLIT_STATUS { Version = DriverClient.DESKSPLIT_PROTOCOL_VERSION };
                }, handshakeCancel.Token);
                throw new Exception("Cancellation during handshake returned a usable connection");
            } catch (OperationCanceledException) { }
            Check(interrupted.Disposed && queried == !duringOpen, "Cancelled handshake leaked a handle or queried after cancellation");
        }
        using (var statusCancel = new CancellationTokenSource()) {
            statusCancel.Cancel();
            try { DesktopStatusReader.ReadDriverStatus(statusCancel.Token); throw new Exception("Status reader swallowed cancellation"); }
            catch (OperationCanceledException) { }
        }
        try { DriverDeviceConnector.Connect(Array.Empty<string>(), path => new FakeHandle(path), _ => new DESKSPLIT_STATUS());
            throw new Exception("No interfaces reported success"); } catch (DriverNotFoundException) { }
        var legacy = new FakeHandle("legacy");
        try { DriverDeviceConnector.Connect(new[] { "legacy" }, _ => legacy, _ => new DESKSPLIT_STATUS { Version = 1 });
            throw new Exception("Old restricted driver was accepted"); } catch (DriverIoException error) {
            Check(error.Message.Contains("업데이트") && legacy.Disposed, "Version mismatch lost the actionable diagnostic/handle cleanup");
        }

        foreach (var size in new[] { (1920, 1080), (1366, 768), (1280, 720) })
        foreach (var layout in LayoutDefinition.All) {
            var resolutions = layout.ComputeDefaultSegmentSizes(size.Item1, size.Item2);
            Check(resolutions.All(r => new SegmentViewModel(0, r.Width, r.Height).IsValid), $"Default {layout.Name} on {size} was blocked");
            ApplyService.ValidateRequest(new ApplyRequest {
                Physical = new MonitorInfo { DeviceName = "fixture", CurrentWidth = size.Item1, CurrentHeight = size.Item2 },
                Layout = layout, Resolutions = resolutions
            });
        }
        Check(LayoutDefinition.FromKind(LayoutKind.Grid2x2).ComputeDefaultSegmentSizes(1920, 1080).All(size => size == (960, 540)), "FHD four-way geometry changed");
        Check(new SegmentViewModel(0, 1, 1).IsValid && new SegmentViewModel(0, 10000, 540).IsValid
            && !new SegmentViewModel(0, 0, 540).IsValid && !new SegmentViewModel(0, 16385, 540).IsValid,
            "Arbitrary bounds remain, or zero/GPU-invalid sizes are accepted");
        Check(LayoutGeometry.ClampSplit(100, 1920) == 100 && LayoutGeometry.ClampSplit(540, 1080) == 540, "Dragging retained the 640-pixel clamp");

        var order = new List<string>();
        Check(AppliedChangeConfirmation.Run(() => order.Add("windows"), () => { order.Add("confirm"); return true; }, () => order.Add("rollback"))
            && order.SequenceEqual(new[] { "windows", "confirm" }), "Windows were restored only after confirmation");
        order.Clear();
        Check(!AppliedChangeConfirmation.Run(() => order.Add("windows"), () => false, () => order.Add("rollback"))
            && order.SequenceEqual(new[] { "windows", "rollback" }), "Decline did not compensate exactly once");
        order.Clear();
        try { AppliedChangeConfirmation.Run(() => throw new InvalidOperationException("placement"), () => { order.Add("unexpected-confirm"); return true; }, () => order.Add("rollback")); }
        catch (InvalidOperationException) { }
        Check(order.SequenceEqual(new[] { "rollback" }), "Placement failure bypassed compensation");
        order.Clear();
        try { AppliedChangeConfirmation.Run(() => order.Add("windows"), () => throw new OperationCanceledException(), () => order.Add("rollback")); }
        catch (OperationCanceledException) { }
        Check(order.SequenceEqual(new[] { "windows", "rollback" }), "Confirmation cancellation left early restored windows without rollback");
        Console.WriteLine("PASS Win10 fixes: active interfaces, resize race, SetupDi fallback, candidate handshake/cleanup/version diagnostics; all presets at FHD/1366x768/720p, small modes and real GPU bounds; windows-before-confirm and exact-once rollback. Fakes only; no SET/window calls.");
    }
    private sealed class FakeHandle(string path) : IDisposable
    {
        public string Path { get; } = path;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
