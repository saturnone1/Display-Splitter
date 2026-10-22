using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

// Real display mutations are permitted only inside the isolated lab guest.
public sealed class DeskSplitVmTopology
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;

        internal static DEVMODE Create()
        {
            var dm = default(DEVMODE);
            dm.dmDeviceName = string.Empty;
            dm.dmFormName = string.Empty;
            dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="EnumDisplayDevicesW")]
    static extern bool EnumDisplayDevices(string device,uint index,ref DISPLAY_DEVICE display,uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="EnumDisplaySettingsExW")]
    static extern bool EnumSettings(string device,int index,ref DEVMODE mode,uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="ChangeDisplaySettingsExW")]
    static extern int SetMode(string device,ref DEVMODE mode,IntPtr window,uint flags,IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="ChangeDisplaySettingsExW")]
    static extern int Commit(string device,IntPtr mode,IntPtr window,uint flags,IntPtr parameter);

    readonly string source;
    readonly DEVMODE original;
    bool restored;
    const uint Fields=0x20|0x40000|0x80000|0x100000|0x400000;
    static void Guard() {
        if(Environment.MachineName!="DSWIN10LAB" || !System.IO.File.Exists(@"C:\DesktopSplitterLab\lab-id.txt"))
            throw new InvalidOperationException("VM-only topology test");
    }
    static List<DISPLAY_DEVICE> Displays() {
        var list=new List<DISPLAY_DEVICE>();
        for(uint i=0;;i++) {
            var d=DISPLAY_DEVICE.Create();
            if(!EnumDisplayDevices(null,i,ref d,0)) return list;
            if((d.StateFlags&8)==0) list.Add(d);
        }
    }
    static bool IsVirtual(DISPLAY_DEVICE d) { return d.DeviceString.IndexOf("DesktopSplitter",StringComparison.OrdinalIgnoreCase)>=0; }
    static DEVMODE Read(string device) {
        var mode=DEVMODE.Create();
        if(!EnumSettings(device,-1,ref mode,0)) throw new Exception("Cannot read active display mode: "+device);
        return mode;
    }
    public DeskSplitVmTopology() {
        Guard();
        var physical=Displays().Where(d=>!IsVirtual(d)&&(d.StateFlags&1)!=0).ToArray();
        if(physical.Length!=1 || (physical[0].StateFlags&4)==0) throw new Exception("Expected one primary VM source display");
        source=physical[0].DeviceName; original=Read(source);
    }
    public void EnsureFhd() {
        Guard(); var mode=Read(source);
        if(mode.dmPelsWidth==1920 && mode.dmPelsHeight==1080) return;
        mode.dmPelsWidth=1920; mode.dmPelsHeight=1080; mode.dmFields=Fields;
        int result=SetMode(source,ref mode,IntPtr.Zero,1,IntPtr.Zero);
        if(result!=0) throw new Exception("VM source FHD mode failed: "+result);
        mode=Read(source);
        if(mode.dmPelsWidth!=1920 || mode.dmPelsHeight!=1080) throw new Exception("VM source did not enter FHD");
    }
    public object ArrangeNine(int timeoutSeconds) {
        Guard(); DateTime deadline=DateTime.UtcNow.AddSeconds(timeoutSeconds);
        DISPLAY_DEVICE[] virtuals;
        do {
            virtuals=Displays().Where(IsVirtual).OrderBy(d=>d.DeviceName,StringComparer.Ordinal).ToArray();
            if(virtuals.Length==9) break;
            if(DateTime.UtcNow>=deadline) throw new Exception("Windows did not enumerate nine display sources");
            System.Threading.Thread.Sleep(100);
        } while(true);
        var physical=Read(source);
        for(int i=0;i<9;i++) {
            var mode=DEVMODE.Create();
            if(!EnumSettings(virtuals[i].DeviceName,-1,ref mode,0) && !EnumSettings(virtuals[i].DeviceName,0,ref mode,0))
                throw new Exception("No mode for "+virtuals[i].DeviceName);
            mode.dmPositionX=physical.dmPositionX+(int)physical.dmPelsWidth+(i%3)*640;
            mode.dmPositionY=physical.dmPositionY+(i/3)*360;
            mode.dmPelsWidth=640;mode.dmPelsHeight=360;mode.dmBitsPerPel=32;mode.dmDisplayFrequency=60;mode.dmFields=Fields;
            int result=SetMode(virtuals[i].DeviceName,ref mode,IntPtr.Zero,0x10000001,IntPtr.Zero);
            if(result!=0) throw new Exception("Nine-grid stage failed: "+virtuals[i].DeviceName+" "+result);
        }
        int committed=Commit(null,IntPtr.Zero,IntPtr.Zero,0,IntPtr.Zero);
        if(committed!=0) throw new Exception("Nine-grid commit failed: "+committed);
        var slots=new List<object>();
        for(int i=0;i<9;i++) {
            var device=Displays().Single(d=>d.DeviceName==virtuals[i].DeviceName);
            var mode=Read(device.DeviceName);
            int x=physical.dmPositionX+(int)physical.dmPelsWidth+(i%3)*640,y=physical.dmPositionY+(i/3)*360;
            if((device.StateFlags&1)==0 || mode.dmPositionX!=x || mode.dmPositionY!=y || mode.dmPelsWidth!=640 || mode.dmPelsHeight!=360)
                throw new Exception("Windows nine-grid mode/position verification failed: "+device.DeviceName);
            slots.Add(new {Slot=i+1,Device=device.DeviceName,X=x,Y=y,Width=mode.dmPelsWidth,Height=mode.dmPelsHeight});
        }
        return new {Source=source,SourceWidth=physical.dmPelsWidth,SourceHeight=physical.dmPelsHeight,ActiveVirtualDisplays=9,Slots=slots};
    }
    public void Restore() {
        Guard(); var mode=Read(source);
        if(mode.dmPelsWidth!=original.dmPelsWidth || mode.dmPelsHeight!=original.dmPelsHeight
            || mode.dmPositionX!=original.dmPositionX || mode.dmPositionY!=original.dmPositionY) {
            mode=original;mode.dmFields=Fields;
            int result=SetMode(source,ref mode,IntPtr.Zero,1,IntPtr.Zero);
            if(result!=0) throw new Exception("VM source restoration failed: "+result);
        }
        restored=true;
    }
    bool IsRestored() {
        Guard();var mode=Read(source);var displays=Displays();
        return restored && !displays.Any(d=>IsVirtual(d)&&(d.StateFlags&1)!=0)
            && displays.Any(d=>d.DeviceName==source&&(d.StateFlags&4)!=0)
            && mode.dmPositionX==original.dmPositionX && mode.dmPositionY==original.dmPositionY
            && mode.dmPelsWidth==original.dmPelsWidth && mode.dmPelsHeight==original.dmPelsHeight;
    }
    public bool VerifyRestored(int timeoutSeconds) {
        DateTime deadline=DateTime.UtcNow.AddSeconds(timeoutSeconds);
        do {
            if(IsRestored()) return true;
            if(DateTime.UtcNow>=deadline) return false;
            System.Threading.Thread.Sleep(100);
        } while(true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;

        internal static DISPLAY_DEVICE Create()
        {
            var dd = default(DISPLAY_DEVICE);
            dd.DeviceName = string.Empty;
            dd.DeviceString = string.Empty;
            dd.DeviceID = string.Empty;
            dd.DeviceKey = string.Empty;
            dd.cb = (uint)Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            return dd;
        }
    }

}
