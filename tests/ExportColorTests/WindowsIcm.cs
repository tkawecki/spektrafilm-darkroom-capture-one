using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Read-only independent Windows ICC engine, explicit relative colorimetric
// intent and BEST_MODE. COLOR_RGB transports 16-bit channel values.
internal static class WindowsIcm
{
    [StructLayout(LayoutKind.Sequential)] private struct Profile { internal uint Type; internal IntPtr Data; internal uint Bytes; }
    [StructLayout(LayoutKind.Explicit, Size=16)] internal struct Color
    { [FieldOffset(0)] internal ushort R; [FieldOffset(2)] internal ushort G; [FieldOffset(4)] internal ushort B; }
    [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenColorProfileW(ref Profile profile,uint access,uint share,uint creation);
    [DllImport("mscms.dll",SetLastError=true)] private static extern bool CloseColorProfile(IntPtr profile);
    [DllImport("mscms.dll",SetLastError=true)] private static extern IntPtr CreateMultiProfileTransform(IntPtr[] profiles,uint count,uint[] intents,uint intentCount,uint flags,uint preferred);
    [DllImport("mscms.dll",SetLastError=true)] private static extern bool DeleteColorTransform(IntPtr transform);
    [DllImport("mscms.dll",SetLastError=true)] private static extern bool TranslateColors(IntPtr transform,[In] Color[] input,uint count,uint inputType,[Out] Color[] output,uint outputType);
    private static IntPtr Open(string path)
    {
        IntPtr text=Marshal.StringToHGlobalUni(path);
        try
        {
            var profile=new Profile {Type=1,Data=text,Bytes=(uint)(path.Length+1)*2};
            var handle=OpenColorProfileW(ref profile,1,1,3); // PROFILE_READ, FILE_SHARE_READ, OPEN_EXISTING
            return handle!=IntPtr.Zero?handle:throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally {Marshal.FreeHGlobal(text);}
    }
    internal static Color[] Convert(Color[] input,string source,string target)
    {
        IntPtr first=Open(source),second=IntPtr.Zero,transform=IntPtr.Zero;
        try
        {
            second=Open(target);
            transform=CreateMultiProfileTransform(new[]{first,second},2,new uint[]{1,1},2,3|0x20000,0);
            if(transform==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var output=new Color[input.Length];
            if(!TranslateColors(transform,input,(uint)input.Length,2,output,2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return output;
        }
        finally {if(transform!=IntPtr.Zero) DeleteColorTransform(transform);if(second!=IntPtr.Zero) CloseColorProfile(second);CloseColorProfile(first);}
    }
}
