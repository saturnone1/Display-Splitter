using System.ComponentModel;
using System.IO;

namespace DesktopSplitter.Interop;

public sealed class DriverConnection<T>(string path, T handle, DESKSPLIT_STATUS status) : IDisposable where T : IDisposable
{
    public string Path { get; } = path;
    public T Handle { get; } = handle;
    public DESKSPLIT_STATUS Status { get; } = status;
    public void Dispose() => Handle.Dispose();
}

/// <summary>Read-only handshake before any SET. A stale/incompatible candidate cannot hide a healthy device.</summary>
public static class DriverDeviceConnector
{
    public static DriverConnection<T> Connect<T>(IEnumerable<string> candidates, Func<string, T> open,
        Func<T, DESKSPLIT_STATUS> query, CancellationToken ct = default) where T : IDisposable
    {
        var paths = candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new DriverNotFoundException(DriverClient.DriverMissingMessage);
        var failures = new List<string>();
        foreach (string path in paths) {
            ct.ThrowIfCancellationRequested();
            T handle = default!;
            bool opened = false, retained = false;
            try {
                handle = open(path); opened = true;
                ct.ThrowIfCancellationRequested();
                var status = query(handle);
                ct.ThrowIfCancellationRequested();
                if (status.Version != DriverClient.DESKSPLIT_PROTOCOL_VERSION || status.ActiveMonitorCount > DriverClient.DESKSPLIT_MAX_MONITORS)
                    throw new DriverIoException($"앱 규격 v{DriverClient.DESKSPLIT_PROTOCOL_VERSION}와 드라이버 v{status.Version} 불일치 또는 잘못된 모니터 수({status.ActiveMonitorCount}). 같은 빌드의 드라이버 패키지로 업데이트해 주세요.");
                retained = true;
                return new(path, handle, status);
            } catch (Exception error) when (error is DriverIoException or Win32Exception or UnauthorizedAccessException or IOException) {
                failures.Add($"{path}: {error.Message}");
            } finally { if (opened && !retained) handle.Dispose(); }
        }
        throw new DriverIoException("드라이버 인터페이스는 발견했지만 연결 가능한 장치가 없습니다. " + string.Join(" | ", failures));
    }
}
