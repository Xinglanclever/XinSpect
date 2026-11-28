using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace XinSpect;

public interface IDiskSpaceFileSystem { DiskSpaceOpenNode Open(string path); }
/// <summary>The owned directory handle stays open while enumerating its descendants.</summary>
public abstract class DiskSpaceOpenNode : IDisposable
{
    public abstract DiskSpaceFileMetadata Metadata { get; }
    public abstract IEnumerable<string> EnumerateChildren();
    public abstract void Dispose();
}
/// <summary>Read-only Windows metadata access; directories also request LIST_DIRECTORY to guard traversal.</summary>
/// <remarks>Official contracts (accessed 2026-11-27):
/// https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew
/// https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
/// https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_standard_info
/// https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_id_info
/// AllocationSize is unnamed-stream allocation, not reclaimable capacity.
/// Active directory handles request FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES (0x81),
/// so their no-FILE_SHARE_DELETE share mode participates in Windows sharing checks.
/// A zero-access or FILE_READ_ATTRIBUTES-only handle does NOT prevent rename and is not a traversal guard.
/// This is not a security boundary against hostile filesystem/filter drivers or changing ancestors above the root.
/// </remarks>
public sealed class DiskSpaceWindowsFileSystem : IDiskSpaceFileSystem
{
    public DiskSpaceOpenNode Open(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("磁碟配置空間需要 Windows 檔案 API。");
        var handle = OpenHandle(path, 0);
        try
        {
            var attrs = ReadAttributes(handle);
            bool directory = (attrs.FileAttributes & 0x10) != 0, reparse = (attrs.FileAttributes & 0x400) != 0;
            if (directory && !reparse)
            {
                // This second open is deliberately not FILE_READ_ATTRIBUTES-only: metadata-only handles
                // do not constrain delete/rename sharing. OPEN_REPARSE_POINT applies to both opens.
                var guard = OpenHandle(path, 0x81);
                try { attrs = ReadAttributes(guard); }
                catch { guard.Dispose(); throw; }
                handle.Dispose(); handle = guard;
                directory = (attrs.FileAttributes & 0x10) != 0; reparse = (attrs.FileAttributes & 0x400) != 0;
                if (!directory && !reparse) throw new IOException("目錄在取得遍歷保護前已改變類型，已停止讀取此路徑。");
                // All metadata returned below is now read from the same protected handle that stays alive
                // through NativeNode.EnumerateChildren and the complete descendant traversal.
            }
            if (reparse) return new NativeNode(path, handle, new(directory, true, null, null, null));
            if (!GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (standard.EndOfFile < 0 || standard.AllocationSize < 0) throw new IOException("檔案系統回報負數長度，拒絕納入統計。");
            DiskSpaceFileIdentity? id = null; string? identityError = null;
            if (GetIdentity(handle, 18, out var nativeId, 24)) id = new(nativeId.VolumeSerialNumber, nativeId.IdLow, nativeId.IdHigh);
            else { int error = Marshal.GetLastWin32Error(); identityError = $"FileIdInfo 失敗（Win32 {error}）：{new Win32Exception(error).Message}"; }
            return new NativeNode(path, handle, new(directory, false, directory ? null : standard.EndOfFile,
                directory ? null : standard.AllocationSize, id, standard.NumberOfLinks, identityError));
        }
        catch { handle.Dispose(); throw; }
    }
    private static SafeFileHandle OpenHandle(string path, uint desiredAccess)
    {
        var handle = CreateFileW(ToExtendedPath(path), desiredAccess, 0x1 | 0x2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    private static AttributeTagInfo ReadAttributes(SafeFileHandle handle)
    {
        if (!GetAttributes(handle, 9, out var attrs, (uint)Marshal.SizeOf<AttributeTagInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        return attrs;
    }
    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
    private sealed class NativeNode(string path, SafeFileHandle handle, DiskSpaceFileMetadata metadata) : DiskSpaceOpenNode
    {
        public override DiskSpaceFileMetadata Metadata => metadata;
        public override IEnumerable<string> EnumerateChildren()
        {
            if (!metadata.IsDirectory || metadata.IsReparsePoint) throw new InvalidOperationException("只可列舉非重解析目錄。");
            return Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false });
        }
        public override void Dispose() => handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    {
        public long AllocationSize; public long EndOfFile; public uint NumberOfLinks;
        public byte DeletePending; public byte Directory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong VolumeSerialNumber; public ulong IdLow; public ulong IdHigh; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetAttributes(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(SafeFileHandle file, int infoClass, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdentity(SafeFileHandle file, int infoClass, out FileIdInfo info, uint size);
}
