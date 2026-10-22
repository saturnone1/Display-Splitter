using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// Private file-backed registry handle: never mounts a hive under host HKLM.
public static class LabBcdStoreMetadata
{
    [DllImport("offreg.dll", CharSet=CharSet.Unicode)] static extern uint OROpenHive(string path,out IntPtr hive);
    [DllImport("offreg.dll", CharSet=CharSet.Unicode)] static extern uint OROpenKey(IntPtr key,string subkey,out IntPtr result);
    [DllImport("offreg.dll", CharSet=CharSet.Unicode)] static extern uint ORSetValue(IntPtr key,string name,uint type,byte[] data,uint size);
    [DllImport("offreg.dll", CharSet=CharSet.Unicode)] static extern uint ORSaveHive(IntPtr hive,string path,uint major,uint minor);
    [DllImport("offreg.dll")] static extern uint ORCloseKey(IntPtr key);
    [DllImport("offreg.dll")] static extern uint ORCloseHive(IntPtr hive);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
    static extern int RegLoadAppKey(string path, out SafeRegistryHandle handle, int access, int options, int reserved);
    public static string Read(string path)
    {
        SafeRegistryHandle handle;
        int error=RegLoadAppKey(path,out handle,0x20019,1,0);
        if(error!=0)throw new Exception("RegLoadAppKey: "+error);
        using(handle)using(var key=RegistryKey.FromHandle(handle))using(var desc=key.OpenSubKey("Description")) {
            if(desc==null)return "No Description";
            string output="";
            foreach(var name in desc.GetValueNames())output+=name+"="+desc.GetValue(name)+"\n";
            return output;
        }
    }
    public static void MarkSystemStore(string path)
    {
        IntPtr hive=IntPtr.Zero, desc=IntPtr.Zero;
        string temporary=path+".lab-marked-"+Guid.NewGuid().ToString("N");
        try {
            Check(OROpenHive(path,out hive));
            Check(OROpenKey(hive,"Description",out desc));
            Check(ORSetValue(desc,"System",4,BitConverter.GetBytes(1),4));
            Check(ORSetValue(desc,"TreatAsSystem",4,BitConverter.GetBytes(1),4));
            // Windows 7-compatible hive format is readable by Windows 10.
            Check(ORSaveHive(hive,temporary,6,1));
        } finally {
            if(desc!=IntPtr.Zero)ORCloseKey(desc);
            if(hive!=IntPtr.Zero)ORCloseHive(hive);
        }
        try { System.IO.File.Copy(temporary,path,true); }
        finally { if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary); }
    }
    static void Check(uint error){if(error!=0)throw new Exception("Offline registry error: "+error);}
}
