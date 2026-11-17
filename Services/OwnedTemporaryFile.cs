using System.IO;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

/// <summary>Owns only the file object exclusively created by this lease, never a pathname deletion.</summary>
internal sealed class OwnedTemporaryFile : IDisposable
{
    internal string Path { get; }
    internal SafeFileHandle Handle { get; }

    private OwnedTemporaryFile(string path, SafeFileHandle handle)
    {
        Path = path;
        Handle = handle;
    }

    internal static OwnedTemporaryFile Create(string directory, string prefix, FileOptions options = FileOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A filename prefix is required.", nameof(prefix));
        string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory),
            $"{prefix}.{Guid.NewGuid():N}.tmp");
        return CreateAtPath(path, options);
    }

    // Explicit collision seam for small filesystem fixture tests. No retry, overwrite, or path cleanup.
    internal static OwnedTemporaryFile CreateAtPath(string path, FileOptions options = FileOptions.None)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, options | FileOptions.DeleteOnClose);
        return new OwnedTemporaryFile(path, handle);
    }

    // Call only after every operation borrowing Handle has completed, including cancellation drain.
    // Close/delete errors are not observable through SafeFileHandle.Dispose; do not claim verification.
    public void Dispose() => Handle.Dispose();
}
