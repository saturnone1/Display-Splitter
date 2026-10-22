# Read-only Windows 10/11 diagnostic. Never installs/enables devices, sends a
# display configuration, starts the app, or changes window/display placement.
[CmdletBinding()]
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
if (-not ('DeskSplitReadOnlyProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class DeskSplitReadOnlyProbe {
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint n, ref Guid g, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid g, string id, [Out] char[] b, uint n, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    public sealed class Result {
        public string Path;
        public bool Connected;
        public uint Protocol;
        public uint ActiveMonitorCount;
        public string Error;
        public uint? AdapterState;
        public string AdapterInitStatus;
        public string DiagnosticsError;
    }
    public static Result[] Run() {
        Guid guid = new Guid("8E7A9B21-4C55-4C83-9B2E-7D3A1F60D2AD");
        char[] buffer;
        while (true) {
            uint size;
            uint cr = CM_Get_Device_Interface_List_SizeW(out size, ref guid, null, 0);
            if (cr != 0) throw new Exception("CM size: CONFIGRET 0x" + cr.ToString("X"));
            buffer = new char[size];
            cr = CM_Get_Device_Interface_ListW(ref guid, null, buffer, size, 0);
            if (cr == 0x1A) continue;
            if (cr != 0) throw new Exception("CM list: CONFIGRET 0x" + cr.ToString("X"));
            break;
        }
        var results = new List<Result>();
        foreach (string path in new string(buffer).Split(new char[] {'\0'}, StringSplitOptions.RemoveEmptyEntries)) {
            var result = new Result { Path = path };
            // Match the app's handle access; GET_STATUS is the only IOCTL here.
            using (SafeFileHandle handle = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                if (handle.IsInvalid) result.Error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                else {
                    byte[] status = new byte[8];
                    uint returned;
                    if (!DeviceIoControl(handle, 0x00222404, IntPtr.Zero, 0, status, 8, out returned, IntPtr.Zero))
                        result.Error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    else if (returned != 8) result.Error = "Unexpected status size: " + returned;
                    else {
                        result.Connected = true;
                        result.Protocol = BitConverter.ToUInt32(status, 0);
                        result.ActiveMonitorCount = BitConverter.ToUInt32(status, 4);
                        byte[] diagnostics = new byte[12];
                        if (DeviceIoControl(handle, 0x00222408, IntPtr.Zero, 0, diagnostics, 12, out returned, IntPtr.Zero) && returned == 12 && BitConverter.ToUInt32(diagnostics,0) == 1) {
                            result.AdapterState = BitConverter.ToUInt32(diagnostics,4);
                            result.AdapterInitStatus = "0x" + BitConverter.ToUInt32(diagnostics,8).ToString("X8");
                        } else result.DiagnosticsError = "Optional adapter diagnostics unavailable (Win32 " + Marshal.GetLastWin32Error() + ")";
                    }
                }
            }
            results.Add(result);
        }
        return results.ToArray();
    }
}
'@
}
$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture
$devices = @()
$deviceError = $null
try {
    $devices = @(Get-CimInstance Win32_PnPEntity -Filter "PNPClass='Display'" |
        Where-Object { @($_.HardwareID) -contains 'Root\DesktopSplitterVdd' } |
        Select-Object Name,PNPDeviceID,Status,ConfigManagerErrorCode)
} catch { $deviceError = $_.Exception.Message }
$signedDrivers = @()
$packageError = $null
try {
    $signedDrivers = @(Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceID -in @($devices.PNPDeviceID) } |
        Select-Object DeviceName,DeviceID,DriverVersion,InfName,IsSigned)
} catch { $packageError = $_.Exception.Message }
$interfaces = @()
$interfaceError = $null
try { $interfaces = @([DeskSplitReadOnlyProbe]::Run()) }
catch { $interfaceError = $_.Exception.Message }
$report = [ordered]@{
    ReadOnly = $true
    OperatingSystem = $os
    Devices = $devices
    DeviceQueryError = $deviceError
    DriverPackages = $signedDrivers
    PackageQueryError = $packageError
    Interfaces = $interfaces
    InterfaceQueryError = $interfaceError
    ExpectedAppProtocol = 3
    Note = 'Connected proves discovery/open/GET_STATUS only. Protocol 1/2 requires the updated nine-output v3 driver for the new app. No split, clear, install or device restart was performed.'
}
$json = $report | ConvertTo-Json -Depth 6
if ($OutputPath) {
    $json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
$json
