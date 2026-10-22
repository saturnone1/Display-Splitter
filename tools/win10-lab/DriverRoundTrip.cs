using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Compiled/executed only by the VM-guarded guest script, never by the product.
public static class DeskSplitVmRoundTrip
{
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string id, [Out] char[] buffer, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    static uint[] Read(SafeFileHandle handle)
    {
        byte[] output = new byte[8]; uint returned;
        if (!DeviceIoControl(handle, 0x222404, null, 0, output, 8, out returned, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned != 8) throw new Exception("Truncated GET_STATUS");
        return new uint[] { BitConverter.ToUInt32(output,0), BitConverter.ToUInt32(output,4) };
    }
    static void Set(SafeFileHandle handle, uint count)
    {
        byte[] input = new byte[944];
        Array.Copy(BitConverter.GetBytes((uint)3),0,input,0,4);
        Array.Copy(BitConverter.GetBytes(count),0,input,4,4);
        for (uint i=0; i<count; ++i) {
            int offset = 8 + (int)i*104;
            Array.Copy(BitConverter.GetBytes((uint)1),0,input,offset,4);
            Array.Copy(BitConverter.GetBytes(count == 9 ? (uint)640 : (uint)960),0,input,offset+8,4);
            Array.Copy(BitConverter.GetBytes(count == 9 ? (uint)360 : (uint)540),0,input,offset+12,4);
            Array.Copy(BitConverter.GetBytes((uint)60000),0,input,offset+16,4);
        }
        uint returned;
        if (!DeviceIoControl(handle,0x222400,input,(uint)input.Length,null,0,out returned,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    static uint[] WaitForCount(SafeFileHandle handle, uint count, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true) {
            uint[] status = Read(handle);
            if (status[1] == count) return status;
            if (DateTime.UtcNow >= deadline) throw new Exception("Monitor arrival/departure test deadline");
            System.Threading.Thread.Sleep(100);
        }
    }
    static void WaitForAdapter(string path, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true) {
            uint state;
            using (var probe = CreateFileW(path,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                if(probe.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                byte[] output=new byte[12]; uint returned;
                if(!DeviceIoControl(probe,0x222408,null,0,output,12,out returned,IntPtr.Zero) || returned!=12)
                    throw new Exception("Adapter diagnostics unavailable");
                state=BitConverter.ToUInt32(output,4);
                if(state==3) throw new Exception("Adapter initialization failed: 0x"+BitConverter.ToUInt32(output,8).ToString("X8"));
            }
            if(state==2) return;
            if(DateTime.UtcNow>=deadline) throw new TimeoutException("Adapter initialization did not complete before any SET was sent");
            System.Threading.Thread.Sleep(100);
        }
    }
    public static object Run(int timeoutSeconds)
    { return Run(timeoutSeconds,null); }
    public static object Run(int timeoutSeconds, Action<object> onGridReady)
    {
        // A second local guard prevents accidental standalone use on the host.
        if (Environment.MachineName != "DSWIN10LAB" || !System.IO.File.Exists(@"C:\DesktopSplitterLab\lab-id.txt")) throw new Exception("VM-only test");
        Guid guid = new Guid("8E7A9B21-4C55-4C83-9B2E-7D3A1F60D2AD"); uint size;
        if (CM_Get_Device_Interface_List_SizeW(out size,ref guid,null,0)!=0 || size<2) throw new Exception("No active interface");
        char[] paths = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid,null,paths,size,0)!=0) throw new Exception("Interface enumeration failed");
        string path = new string(paths).Split('\0')[0];
        WaitForAdapter(path,timeoutSeconds);
        using (var handle=CreateFileW(path,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint[] before=Read(handle);
            if (before[0]!=3 || before[1]!=0) throw new Exception("Expected clean protocol 3 driver");
            byte[] diagnostic=new byte[12]; uint diagnosticBytes;
            if(DeviceIoControl(handle,0x222408,null,0,diagnostic,12,out diagnosticBytes,IntPtr.Zero) && diagnosticBytes==12 && BitConverter.ToUInt32(diagnostic,4)==3)
                throw new Exception("Adapter initialization failed: NTSTATUS 0x"+BitConverter.ToUInt32(diagnostic,8).ToString("X8"));
            uint[] split; uint fourCount;
            var topology=new DeskSplitVmTopology();
            object nineGrid=null;
            try {
                topology.EnsureFhd();
                Set(handle,4); split=WaitForCount(handle,4,timeoutSeconds);
                if (split[1]!=4) throw new Exception("Driver did not arrive four 960x540 monitors");
                fourCount=split[1];
                Set(handle,0); WaitForCount(handle,0,timeoutSeconds);
                Set(handle,9); split=WaitForCount(handle,9,timeoutSeconds);
                if (split[1]!=9) throw new Exception("Driver did not arrive nine 640x360 monitors");
                nineGrid=topology.ArrangeNine(timeoutSeconds);
                if(onGridReady!=null) onGridReady(nineGrid);
            } finally {
                try { Set(handle,0); WaitForCount(handle,0,timeoutSeconds); }
                finally { topology.Restore(); }
            }
            uint[] after=WaitForCount(handle,0,timeoutSeconds);
            if (after[1]!=0) throw new Exception("Driver did not clear the VM monitors");
            if(!topology.VerifyRestored(timeoutSeconds)) throw new Exception("Windows VM topology did not restore cleanly");
            return new { Protocol=after[0], Width=640, Height=360, FourSplitCount=fourCount, SplitCount=split[1], RestoredCount=after[1], NineGrid=nineGrid, PhysicalRestored=true, CompositorVerified=onGridReady!=null };
        }
    }
}
