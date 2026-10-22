using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopSplitter.Interop;

public static class DriverInterfaceDiscovery
{
    public const uint PresentInterfaces = 0; // cfgmgr32.h: 1 means ALL, including inactive registrations.
    public const int BufferSmall = 0x1A;

    public static IReadOnlyList<string> Enumerate(CancellationToken ct = default)
    {
        Guid guid = DriverClient.GUID_DEVINTERFACE_DESKSPLIT;
        return Read(() => {
            int result = CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, 0, PresentInterfaces);
            return (result, size);
        }, buffer => CM_Get_Device_Interface_ListW(ref guid, 0, buffer, (uint)buffer.Length, PresentInterfaces),
        () => ReadSetupDi(guid), ct);
    }

    public static IReadOnlyList<string> Read(Func<(int Result, uint Size)> size, Func<char[], int> list,
        Func<IReadOnlyList<string>> fallback, CancellationToken ct = default)
    {
        while (true) {
            ct.ThrowIfCancellationRequested();
            var measured = size();
            if (measured.Result != 0) return fallback();
            if (measured.Size < 2) return fallback();
            var buffer = new char[checked((int)measured.Size)];
            int result = list(buffer);
            if (result == BufferSmall) continue; // Device arrival raced the size query; retry on API feedback only.
            if (result != 0) return fallback();
            var paths = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return paths.Length > 0 ? paths : fallback();
        }
    }

    private static IReadOnlyList<string> ReadSetupDi(Guid guid)
    {
        nint set = SetupDiGetClassDevsW(ref guid, 0, 0, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == -1) throw new DriverIoException("드라이버 장치 열거 실패: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try {
            var paths = new List<string>();
            for (uint index = 0; ; index++) {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref data)) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out uint needed, 0);
                if (needed < 6) throw new Win32Exception(Marshal.GetLastWin32Error());
                nint detail = Marshal.AllocHGlobal(checked((int)needed));
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                } finally { Marshal.FreeHGlobal(detail); }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public uint Size; public Guid Guid; public uint Flags; public nint Reserved; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, nint device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int CM_Get_Device_Interface_ListW(ref Guid guid, nint device, [Out] char[] buffer, uint size, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern nint SetupDiGetClassDevsW(ref Guid guid, nint enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint needed, nint device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
