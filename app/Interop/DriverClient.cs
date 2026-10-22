using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopSplitter.Interop;

/// <summary>Thrown when the DesktopSplitter driver device interface cannot be found.</summary>
public sealed class DriverNotFoundException : Exception
{
    public DriverNotFoundException(string message) : base(message) { }
}

/// <summary>Thrown when the driver is present but rejects/fails an IOCTL.</summary>
public sealed class DriverIoException : Exception
{
    public DriverIoException(string message) : base(message) { }
    public DriverIoException(string message, Exception inner) : base(message, inner) { }
}

// ---------------------------------------------------------------------------
// shared/DeskSplitProtocol.h — struct layout replicated EXACTLY (#pragma pack(4)).
// ---------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DESKSPLIT_MODE
{
    public uint Width;
    public uint Height;
    public uint RefreshMillihertz;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DESKSPLIT_MONITOR_CONFIG
{
    public uint ModeCount;
    public uint PreferredModeIndex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = DriverClient.DESKSPLIT_MAX_MODES)]
    public DESKSPLIT_MODE[] Modes;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DESKSPLIT_CONFIG
{
    public uint Version;
    public uint MonitorCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = DriverClient.DESKSPLIT_MAX_MONITORS)]
    public DESKSPLIT_MONITOR_CONFIG[] Monitors;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DESKSPLIT_STATUS
{
    public uint Version;
    public uint ActiveMonitorCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DESKSPLIT_DIAGNOSTICS
{
    public uint Version;
    public uint AdapterState;
    public int AdapterInitStatus;
}

/// <summary>
/// Opens the driver's device interface and issues IOCTL_DESKSPLIT_SET_CONFIG /
/// IOCTL_DESKSPLIT_GET_STATUS.
/// </summary>
public static class DriverClient
{
    public const int DESKSPLIT_MAX_MONITORS = 9;
    public const int DESKSPLIT_MAX_MODES = 8;
    public const uint DESKSPLIT_PROTOCOL_VERSION = 3;

    /// <summary>{8E7A9B21-4C55-4C83-9B2E-7D3A1F60D2AD}</summary>
    public static readonly Guid GUID_DEVINTERFACE_DESKSPLIT =
        new(0x8e7a9b21, 0x4c55, 0x4c83, 0x9b, 0x2e, 0x7d, 0x3a, 0x1f, 0x60, 0xd2, 0xad);

    // CTL_CODE(DeviceType, Function, Method, Access)
    //   = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
    private const uint FILE_DEVICE_UNKNOWN = 0x00000022;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    private static uint CTL_CODE(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    /// <summary>0x00222400</summary>
    public static readonly uint IOCTL_DESKSPLIT_SET_CONFIG =
        CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900, METHOD_BUFFERED, FILE_ANY_ACCESS);

    /// <summary>0x00222404</summary>
    public static readonly uint IOCTL_DESKSPLIT_GET_STATUS =
        CTL_CODE(FILE_DEVICE_UNKNOWN, 0x901, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_DESKSPLIT_GET_DIAGNOSTICS =
        CTL_CODE(FILE_DEVICE_UNKNOWN, 0x902, METHOD_BUFFERED, FILE_ANY_ACCESS);

    public static string? AdapterFailureMessage(DESKSPLIT_DIAGNOSTICS? diagnostic)
        => diagnostic is { Version: 1, AdapterState: 3 } failure
            ? $"드라이버 검색·통신은 성공했지만 가상 디스플레이 어댑터 초기화가 실패했습니다 (NTSTATUS 0x{unchecked((uint)failure.AdapterInitStatus):X8}). 그래픽 장치·드라이버의 지원 상태를 확인해 주세요."
            : null;

    public static string? AdapterUnavailableMessage(DESKSPLIT_DIAGNOSTICS? diagnostic)
        => AdapterFailureMessage(diagnostic) ?? (diagnostic is { AdapterState: 0 or 1 }
            ? "가상 디스플레이 어댑터를 초기화하고 있습니다. 준비가 완료된 후 다시 적용해 주세요." : null);

    public static DESKSPLIT_DIAGNOSTICS? ReadDiagnostics(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<DESKSPLIT_DIAGNOSTICS>();
        nint buffer = Marshal.AllocHGlobal(size);
        try {
            if (!DeviceIoControl(handle, IOCTL_DESKSPLIT_GET_DIAGNOSTICS, 0, 0, buffer, (uint)size, out uint returned, 0)) {
                int error = Marshal.GetLastWin32Error();
                if (error is 1 or 50) return null; // Optional query is absent on an earlier v2 driver.
                throw new DriverIoException($"드라이버 초기화 상태 조회 실패: {new Win32Exception(error).Message} (Win32 {error}).");
            }
            if (returned != size) throw new DriverIoException("드라이버 초기화 상태 응답의 크기가 올바르지 않습니다.");
            var diagnostic = Marshal.PtrToStructure<DESKSPLIT_DIAGNOSTICS>(buffer);
            if (diagnostic.Version != 1 || diagnostic.AdapterState > 3) throw new DriverIoException("드라이버 초기화 상태 응답의 버전 또는 상태가 올바르지 않습니다.");
            return diagnostic;
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    public const string DriverMissingMessage =
        "활성 가상 디스플레이 드라이버 인터페이스를 찾지 못했습니다. 미설치뿐 아니라 장치 사용 안 함·서명 또는 로드 실패일 수 있습니다. 설치 프로그램과 장치 관리자에서 드라이버 상태를 확인해 주세요.";

    /// <summary>Builds an all-zero-but-valid config with room for the fixed arrays.</summary>
    public static DESKSPLIT_CONFIG NewConfig()
    {
        var cfg = new DESKSPLIT_CONFIG
        {
            Version = DESKSPLIT_PROTOCOL_VERSION,
            MonitorCount = 0,
            Monitors = new DESKSPLIT_MONITOR_CONFIG[DESKSPLIT_MAX_MONITORS],
        };
        for (int i = 0; i < DESKSPLIT_MAX_MONITORS; i++)
        {
            cfg.Monitors[i] = new DESKSPLIT_MONITOR_CONFIG
            {
                ModeCount = 0,
                PreferredModeIndex = 0,
                Modes = new DESKSPLIT_MODE[DESKSPLIT_MAX_MODES],
            };
        }
        return cfg;
    }

    /// <summary>True when the driver's device interface is present.</summary>
    public static bool IsDriverPresent() => TryFindDevicePath(out _);

    /// <summary>Device interface path, or throws <see cref="DriverNotFoundException"/>.</summary>
    public static string GetDevicePath()
    {
        using var connection = Connect();
        return connection.Path;
    }

    public static bool TryFindDevicePath(out string devicePath)
    {
        devicePath = DriverInterfaceDiscovery.Enumerate().FirstOrDefault() ?? "";
        return devicePath.Length > 0;
    }

    public static DriverConnection<SafeFileHandle> Connect(CancellationToken ct = default)
    {
        try { return DriverDeviceConnector.Connect(DriverInterfaceDiscovery.Enumerate(ct), OpenPath, ReadStatus, ct); }
        catch (DriverNotFoundException error) { throw new DriverNotFoundException(error.Message + " " + DriverDeviceDiagnostics.Describe()); }
    }

    /// <summary>Sends IOCTL_DESKSPLIT_SET_CONFIG.</summary>
    public static void SetConfig(DESKSPLIT_CONFIG config, CancellationToken ct = default)
    {
        if (config.Monitors is null || config.Monitors.Length != DESKSPLIT_MAX_MONITORS)
            throw new DriverIoException("DESKSPLIT_CONFIG.Monitors must match DESKSPLIT_MAX_MONITORS.");
        if (config.MonitorCount > DESKSPLIT_MAX_MONITORS)
            throw new DriverIoException($"MonitorCount {config.MonitorCount} exceeds DESKSPLIT_MAX_MONITORS.");

        using var connection = Connect(ct);
        SafeFileHandle handle = connection.Handle;
        if (config.MonitorCount > 0 && AdapterUnavailableMessage(ReadDiagnostics(handle)) is { } failure)
            throw new DriverIoException(failure);

        int size = Marshal.SizeOf<DESKSPLIT_CONFIG>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(config, buffer, false);
            ct.ThrowIfCancellationRequested(); // Last cancellable boundary before the mutating IOCTL.
            bool ok = DeviceIoControl(handle, IOCTL_DESKSPLIT_SET_CONFIG,
                                      buffer, (uint)size, IntPtr.Zero, 0,
                                      out _, IntPtr.Zero);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new DriverIoException(
                    $"IOCTL_DESKSPLIT_SET_CONFIG failed: {new Win32Exception(err).Message} (Win32 {err}).");
            }
        }
        finally
        {
            Marshal.DestroyStructure<DESKSPLIT_CONFIG>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Sends IOCTL_DESKSPLIT_GET_STATUS.</summary>
    public static DESKSPLIT_STATUS GetStatus(CancellationToken ct = default)
    {
        using var connection = Connect(ct);
        return connection.Status;
    }

    private static DESKSPLIT_STATUS ReadStatus(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<DESKSPLIT_STATUS>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = DeviceIoControl(handle, IOCTL_DESKSPLIT_GET_STATUS,
                                      IntPtr.Zero, 0, buffer, (uint)size,
                                      out uint returned, IntPtr.Zero);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                throw new DriverIoException(
                    $"IOCTL_DESKSPLIT_GET_STATUS failed: {new Win32Exception(err).Message} (Win32 {err}).");
            }
            if (returned < size)
                throw new DriverIoException($"IOCTL_DESKSPLIT_GET_STATUS returned {returned} bytes, expected {size}.");

            return Marshal.PtrToStructure<DESKSPLIT_STATUS>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Convenience: SET_CONFIG with MonitorCount = 0 (removes all virtual monitors).</summary>
    public static void ClearConfig(CancellationToken ct = default)
    {
        DESKSPLIT_CONFIG cfg = NewConfig();
        cfg.MonitorCount = 0;
        SetConfig(cfg, ct);
    }

    private static SafeFileHandle OpenPath(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new DriverIoException(
                $"Could not open the driver device '{path}': {new Win32Exception(err).Message} (Win32 {err}).");
        }
        return handle;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
