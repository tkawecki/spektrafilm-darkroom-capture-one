using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Ported from nothing-complex/spektrafilm-for-capture-one 285f0aa (OutputPathGuard, GPL-3.0).
// Comparing path strings misses aliases of the same file: junctions, symbolic links, SUBST drives,
// 8.3 short names or different casing on mapped drives. Resolve both paths through an open handle
// so an export can never replace the original image it was rendered from.
internal static class OutputPathGuard
{
    private const uint ShareAll = 7;            // FILE_SHARE_READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // required to open directories

    internal static string Validate(string destination, string original)
    {
        var full = Path.GetFullPath(destination);
        var source = Path.GetFullPath(original);
        if (string.Equals(full, source, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Resolve(full), Resolve(source), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different filename. Spektrafilm cannot replace the original image.");
        return full;
    }

    private static string Resolve(string path)
    {
        if (File.Exists(path)) return ResolveExisting(path);
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("The output folder is unavailable.");
        return Path.Combine(ResolveExisting(parent), Path.GetFileName(path));
    }

    private static string ResolveExisting(string path)
    {
        using var handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("Could not verify that the output is not the original image.", new Win32Exception(Marshal.GetLastWin32Error()));
        var result = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        if (length >= result.Capacity)
        {
            result.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        }
        if (length == 0)
            throw new IOException("Could not resolve the original and output paths.", new Win32Exception(Marshal.GetLastWin32Error()));
        return result.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
