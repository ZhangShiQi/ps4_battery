using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public partial class Program {
 [StructLayout(LayoutKind.Sequential)] struct Interface {public uint Size;public Guid ClassGuid;public uint Flags;public UIntPtr Reserved;}
 [StructLayout(LayoutKind.Sequential)] struct Attr {public int Size;public ushort Vendor,Product,Version;}
 [DllImport("hid.dll")]static extern void HidD_GetHidGuid(out Guid g);
 [DllImport("hid.dll")]static extern bool HidD_GetAttributes(SafeFileHandle h,ref Attr a);
 [DllImport("hid.dll")]static extern bool HidD_GetPreparsedData(SafeFileHandle h,out IntPtr p);
 [DllImport("hid.dll")]static extern bool HidD_FreePreparsedData(IntPtr p);
 [DllImport("hid.dll")]static extern int HidP_GetCaps(IntPtr p,IntPtr caps);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode)]static extern IntPtr SetupDiGetClassDevs(ref Guid g,IntPtr enumerator,IntPtr parent,uint flags);
 [DllImport("setupapi.dll")]static extern bool SetupDiEnumDeviceInterfaces(IntPtr s,IntPtr d,ref Guid g,uint i,ref Interface data);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode)]static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s,ref Interface data,IntPtr detail,uint size,out uint required,IntPtr d);
 [DllImport("setupapi.dll")]static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr sec,uint create,uint flags,IntPtr template);
}
