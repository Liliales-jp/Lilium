using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Lilium;

// Read-only native identity, path and metadata checks for shell operations.
internal static class SafeFileSystem
{
#if LILIUM_SAFETY_TEST
    // Only the test assembly can stop at its freshly-created, trusted fixture root.
    // Production always pins ancestors to the drive root. This seam lets tests run
    // in a sandbox which denies opening the user-profile ancestor itself.
    internal static string? TestBoundary;
#endif
    internal static string Full(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Length < 3 || path[1] != ':')
            throw new IOException(L10n.Get("SafeFileSystem_001"));
        var full = Path.GetFullPath(path);
        foreach (var part in full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':'))
                throw new IOException(L10n.Get("SafeFileSystem_002"));
        return Path.TrimEndingDirectorySeparator(full);
    }
    internal static bool Within(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    internal static void Separate(string a, string b)
    {
        a = Full(a); b = Full(b);
        if (Within(a, b) || Within(b, a)) throw new IOException(L10n.Get("SafeFileSystem_003"));
        var physicalA = Physical(a); var physicalB = Physical(b);
        if (Within(physicalA, physicalB) || Within(physicalB, physicalA))
            throw new IOException(L10n.Get("SafeFileSystem_004"));
    }
    internal static string Physical(string path)
    {
        path = Full(path);
        if (!Exists(path)) return Physical(Path.GetDirectoryName(path)!).TrimEnd('\\') + "\\" + Path.GetFileName(path);
        string parent = Path.GetDirectoryName(path) ?? path;
#if LILIUM_SAFETY_TEST
        if (string.Equals(path, TestBoundary, StringComparison.OrdinalIgnoreCase)) parent = path;
#endif
        using var pins = new Pins(parent);
        using var h = Open(path, 0x80, 3);
        RejectLink(h, path);
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(h, name, (uint)name.Capacity, 1); // volume GUID, resolves SUBST/8.3 aliases
        if (length == 0 || length >= name.Capacity) throw Error(path);
        return name.ToString().TrimEnd('\\');
    }
    internal static string Volume(string path)
    {
        var name = Physical(path);
        int end = name.IndexOf('\\', 4);
        return end < 0 ? name : name[..end];
    }
    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal static string Native(string path) { var full = Full(path); return full.Length < 240 ? full : @"\\?\" + full; }
    internal static void RequireNtfs(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Full(path))!);
        if (drive.DriveType == DriveType.Network || !drive.IsReady || drive.DriveFormat != "NTFS")
            throw new IOException(L10n.Get("SafeFileSystem_005"));
    }
    internal static void NotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L10n.Get("SafeFileSystem_006") + path);
    }
    // Hold every existing ancestor without FILE_SHARE_DELETE. Junctions cannot be
    // exchanged between validation and use. Open the link itself, never its target.
    internal sealed class Pins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal Pins(params string[] paths)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    var chain = new Stack<string>();
                    for (var p = Full(path); p is not null; p = Path.GetDirectoryName(p))
                    {
                        chain.Push(p);
#if LILIUM_SAFETY_TEST
                        if (TestBoundary is not null && string.Equals(p, TestBoundary, StringComparison.OrdinalIgnoreCase)) break;
#endif
                    }
                    while (chain.TryPop(out var p))
                    {
                        if (!seen.Add(p)) continue;
                        var h = Open(p, 1, 3); // FILE_LIST_DIRECTORY participates in share-delete enforcement
                        _handles.Add(h);
                        RejectLink(h, p);
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); }
    }
    private static SafeFileHandle Open(string path, uint access, uint share)
    {
        var h = CreateFileW(Native(path), access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); throw Error(path); }
        return h;
    }
    private static Info ReadInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Error(L10n.Get("SafeFileSystem_007"));
        return info;
    }
    private static void RejectLink(SafeFileHandle h, string path)
    {
        if ((ReadInfo(h).Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException(L10n.Get("SafeFileSystem_008") + path);
    }
    private static string Id(Info i) => $"{i.Volume:X8}:{i.IndexHigh:X8}{i.IndexLow:X8}";
    internal static string Identity(string path)
    {
        using var pins = new Pins(Path.GetDirectoryName(Full(path))!);
        using var h = Open(path, 0x80, 3);
        RejectLink(h, path);
        return Id(ReadInfo(h));
    }
    internal static long Size(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        NotLink(path);
        if (!Directory.Exists(path)) return Streams(path).Sum(s => s.Size);
        long size = 0;
        using var pins = new Pins(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) size = checked(size + Size(child, token));
        return checked(size + Streams(path).Sum(s => s.Size));
    }
    internal static List<(string Name, long Size)> Streams(string path)
    {
        var result = new List<(string, long)>();
        var find = FindFirstStreamW(Native(path), 0, out var data, 0);
        if (find == new IntPtr(-1))
        {
            if (Marshal.GetLastWin32Error() == 38) return result;
            throw Error(path);
        }
        try
        {
            do { result.Add((data.Name, data.Size)); } while (FindNextStreamW(find, out data));
            if (Marshal.GetLastWin32Error() != 38) throw Error(path);
        }
        finally { FindClose(find); }
        return result;
    }
    private static IOException Error(string path) => new(new Win32Exception(Marshal.GetLastWin32Error()).Message + "\n" + path);
    [StructLayout(LayoutKind.Sequential)] private struct Info
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StreamData
    { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstStreamW(string file, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextStreamW(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr find);
}
