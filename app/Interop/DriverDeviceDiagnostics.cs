using System.Runtime.InteropServices;
using System.Text;

namespace DesktopSplitter.Interop;

/// <summary>Read PnP state when no active interface exists. Never install, enable or restart a device.</summary>
public static class DriverDeviceDiagnostics
{
    public static string Describe()
    {
        Guid displayClass = new("4D36E968-E325-11CE-BFC1-08002BE10318");
        nint set = SetupDiGetClassDevsW(ref displayClass, 0, 0, 2); // DIGCF_PRESENT, including disabled devices.
        if (set == -1) return "PnP 상태 조회 실패.";
        try {
            var descriptions = new List<string>();
            for (uint index = 0; ; index++) {
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref device)) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    return $"PnP 장치 열거 실패 (오류 {error}). 미설치로 판정하지 않았습니다.";
                }
                SetupDiGetDeviceRegistryPropertyW(set, ref device, 1, out _, null, 0, out uint needed);
                if (needed == 0) continue;
                var buffer = new byte[checked((int)needed)];
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref device, 1, out _, buffer, needed, out _)) continue;
                if (!Encoding.Unicode.GetString(buffer).Split('\0').Any(id => string.Equals(id, @"Root\DesktopSplitterVdd", StringComparison.OrdinalIgnoreCase))) continue;
                var name = new StringBuilder(512);
                SetupDiGetDeviceInstanceIdW(set, ref device, name, name.Capacity, out _);
                int result = CM_Get_DevNode_Status(out uint status, out uint problem, device.Instance, 0);
                descriptions.Add(result == 0 ? Format(name.ToString(), status, problem) : "장치는 있으나 PnP 상태 조회 실패.");
            }
            return descriptions.Count == 0 ? "현재 PnP 장치 목록에 DesktopSplitter 장치가 없습니다. 패키지 설치 후 실제 장치 생성까지 완료됐는지 확인해 주세요."
                : string.Join(" | ", descriptions);
        } catch (Exception error) { return "PnP 진단 실패: " + error.Message; }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static string Format(string instance, uint status, uint problem)
        => $"장치 {instance}: PnP 문제 코드 {problem}, " + ((status & 8) != 0 ? "시작됨" : "시작되지 않음") +
            (problem == 22 ? " · 장치가 사용 안 함 상태입니다." : problem == 52 ? " · Windows가 드라이버 서명을 확인하지 못했습니다."
            : problem != 0 ? " · 드라이버 설치/로드 상태를 확인해 주세요." : " · 활성 인터페이스 등록 상태를 확인해 주세요.");

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint Instance; public nint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint SetupDiGetClassDevsW(ref Guid guid, nint enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo data, uint property, out uint type, [Out] byte[]? buffer, uint size, out uint needed);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo data, StringBuilder id, int length, out uint needed);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);
}
