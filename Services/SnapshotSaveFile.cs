using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

internal interface ISnapshotSaveFile : IDisposable
{
    void Write(byte[] bytes);
    void Flush();
    void Publish(string destination, bool overwrite);
}

/// <summary>Windows same-handle snapshot staging, publication and failure cleanup.</summary>
internal sealed class SnapshotSaveFile : ISnapshotSaveFile
{
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private bool _published;
    private bool _disposed;

    private SnapshotSaveFile(FileStream stream, SafeFileHandle handle)
    {
        _stream = stream;
        _handle = handle;
    }

    internal static ISnapshotSaveFile Create(string destination)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Snapshot destination requires a directory.", nameof(destination));
        // A fixed-length basename avoids extending an already long destination filename.
        return CreateAtPath(Path.Combine(directory, $".xinsnapshot-{Guid.NewGuid():N}.tmp"));
    }

    // Collision seam: CREATE_NEW must fail without acquiring/cleaning an existing file.
    internal static SnapshotSaveFile CreateAtPath(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // GENERIC_WRITE | DELETE; share=0; CREATE_NEW; WRITE_THROUGH. Never DELETE_ON_CLOSE:
        // after a successful rename that flag would delete the published destination on close.
        var handle = CreateFileW(path, 0x40010000, 0, IntPtr.Zero, 1, 0x80000080, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw IoError("Snapshot staging creation failed.", error);
        }
        try
        {
            return new SnapshotSaveFile(new FileStream(handle, FileAccess.Write, 64 * 1024, false), handle);
        }
        catch
        {
            TryMarkForDeletion(handle);
            handle.Dispose();
            throw;
        }
    }

    public void Write(byte[] bytes) => _stream.Write(bytes);
    public void Flush() => _stream.Flush(flushToDisk: true);

    public void Publish(string destination, bool overwrite)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published) throw new InvalidOperationException("Snapshot already published.");
        byte[] rename = BuildRenameInfo(destination, overwrite, IntPtr.Size);
        if (!SetFileInformationByHandle(_handle, 3, rename, (uint)rename.Length))
            throw IoError("Snapshot publication failed.", Marshal.GetLastPInvokeError());
        _published = true;
    }

    // SDK winbase.h FILE_RENAME_INFO: union(DWORD/BOOLEAN), aligned HANDLE,
    // DWORD byte length, WCHAR[1]. FileName offsets are 20(x64)/12(x86).
    // Zeroed union/padding/root handle; filename length excludes our optional UTF-16 NUL.
    internal static byte[] BuildRenameInfo(string destination, bool overwrite, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (!Path.IsPathFullyQualified(destination) || destination.Contains('\0'))
            throw new ArgumentException("Fully qualified snapshot destination required.", nameof(destination));
        byte[] name = Encoding.Unicode.GetBytes(destination);
        int nameOffset = pointerSize == 8 ? 20 : 12;
        byte[] buffer = new byte[checked(nameOffset + name.Length + 2)];
        buffer[0] = overwrite ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(nameOffset - 4), checked((uint)name.Length));
        name.CopyTo(buffer, nameOffset);
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cleanup addresses the created file object while its exclusive handle is alive.
        // Never unlink by pathname: a pathname might no longer designate our file object.
        // If disposition fails, an orphan may remain; do not claim deletion was verified.
        if (!_published) TryMarkForDeletion(_handle);
        try { _stream.Dispose(); } catch { /* Preserve write/flush/publication failure. */ }
    }

    private static void TryMarkForDeletion(SafeFileHandle handle)
    {
        try { SetFileInformationByHandle(handle, 4, [1], 1); }
        catch { /* Best effort; no pathname fallback. */ }
    }

    private static IOException IoError(string message, int error)
        => new(message, new Win32Exception(error));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        [In] byte[] information, uint size);
}
