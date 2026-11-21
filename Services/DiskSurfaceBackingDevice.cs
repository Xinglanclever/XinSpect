using System.Buffers.Binary;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

/// <summary>Fail-closed, metadata-only preflight. No protocol commands or disk data reads.</summary>
/// <remarks>
/// Windows SDK 10.0.28000.0 winioctl.h: IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
/// IOCTL_STORAGE_GET_DEVICE_NUMBER, STORAGE_DEVICE_DESCRIPTOR. ReOpenFile preserves the
/// original file object identity (Microsoft Learn, ReOpenFile). Descriptor equality is deliberately
/// conservative: bridge/virtualized layouts without matching direct ATA/SATA evidence are rejected.
/// This is a preflight snapshot, not an atomic topology lock against live storage reconfiguration.
/// Synchronous metadata has no hard cancellation guarantee; no timeout releases in-flight buffers.
/// </remarks>
internal static class DiskSurfaceBackingDevice
{
    private const uint Extents = 0x00560000;
    private const uint DeviceNumber = 0x002D1080;
    private const uint DeviceProperty = 0x002D1400;
    private const uint Read = 0x80000000, Share = 3, Overlapped = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input,
        int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);

    internal readonly record struct Extent(uint Disk, long Offset, long Length);

    // SDK winioctl.h: VOLUME_DISK_EXTENTS has 8-byte-aligned DISK_EXTENT at +8.
    internal static Extent? ParseExtent(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1) return null;
        uint disk = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        long length = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        return disk <= int.MaxValue && offset >= 0 && length > 0 && offset <= long.MaxValue - length
            ? new Extent(disk, offset, length) : null;
    }

    internal static bool IsAllowedDescriptor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 36) return false;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint bus = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        return version >= 36 && version <= size && size <= bytes.Length
            && bytes[8] == 0 && bytes[10] == 0 && bus is 3 or 11;
    }

    private static byte[]? Query(SafeFileHandle handle, uint code, int capacity, CancellationToken ct, uint property = 0)
    {
        ct.ThrowIfCancellationRequested();
        var output = new byte[capacity];
        byte[]? input = code == DeviceProperty ? new byte[12] : null;
        if (input is not null) BinaryPrimitives.WriteUInt32LittleEndian(input, property);
        bool success = DeviceIoControl(handle, code, input, input?.Length ?? 0,
            output, output.Length, out int returned, IntPtr.Zero);
        // Synchronous metadata: retain handle/buffer until the call actually returns, even on cancellation.
        ct.ThrowIfCancellationRequested();
        return success && returned >= 0 && returned <= capacity ? output[..returned] : null;
    }

    internal static bool MatchesDeviceNumber(ReadOnlySpan<byte> bytes, uint disk)
        => bytes.Length == 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 7
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == disk
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) is 0 or uint.MaxValue;

    internal readonly record struct Alignment(int Logical, int Physical);
    internal static Alignment? ParseAlignment(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 28
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 28) return null;
        uint logical = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        uint physical = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        return logical is >= 512 and <= 65536 && (logical & (logical - 1)) == 0
            && physical >= logical && physical <= 65536 && (physical & (physical - 1)) == 0
            && offset == 0 ? new Alignment((int)logical, (int)physical) : null;
    }

    internal static bool VerifyTopology(byte[] before, byte[] after, byte[] number,
        byte[] diskDescriptor, byte[] volumeDescriptor)
        => ParseExtent(before) is Extent extent && ParseExtent(after) == extent
            && MatchesDeviceNumber(number, extent.Disk)
            && IsAllowedDescriptor(diskDescriptor) && IsAllowedDescriptor(volumeDescriptor)
            && diskDescriptor.AsSpan().SequenceEqual(volumeDescriptor);

    public static Lease? Open(string path, CancellationToken ct)
    {
        SafeFileHandle? volume = null, disk = null, reopened = null;
        FileStream? stream = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            volume = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var raw = Query(volume, Extents, 32, ct);
            if (raw is null || ParseExtent(raw) is not Extent extent) return null;
            if (StorageProbeBreaker.ShouldSkip((int)extent.Disk, "TryGetBusType")
                || StorageProbeBreaker.ShouldSkip((int)extent.Disk, "TryReadNvmeLog.2")
                || StorageProbeBreaker.ShouldSkip((int)extent.Disk, DiskSurfaceScanService.BreakerOperation))
            { StorageProbeBreaker.CountSkip(); return null; }
            disk = File.OpenHandle($@"\\.\PhysicalDrive{extent.Disk}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var number = Query(disk, DeviceNumber, 12, ct);
            var descriptor = Query(disk, DeviceProperty, 4096, ct);
            var volumeDescriptor = Query(volume, DeviceProperty, 4096, ct);
            if (number is null || !MatchesDeviceNumber(number, extent.Disk)
                || descriptor is null || !IsAllowedDescriptor(descriptor)
                || volumeDescriptor is null || !IsAllowedDescriptor(volumeDescriptor)
                || !descriptor.AsSpan().SequenceEqual(volumeDescriptor)) return null;
            var diskAlignment = Query(disk, DeviceProperty, 28, ct, property: 6);
            var volumeAlignment = Query(volume, DeviceProperty, 28, ct, property: 6);
            if (diskAlignment is null || volumeAlignment is null
                || ParseAlignment(diskAlignment) is not Alignment alignment
                || ParseAlignment(volumeAlignment) != alignment
                || extent.Offset % alignment.Logical != 0 || extent.Length % alignment.Logical != 0) return null;
            // ReOpenFile binds the scan to the original volume object instead of resolving its letter again.
            ct.ThrowIfCancellationRequested();
            reopened = ReOpenFile(volume, Read, Share, Overlapped);
            ct.ThrowIfCancellationRequested();
            if (reopened.IsInvalid) return null;
            stream = new FileStream(reopened, FileAccess.Read, 1, isAsync: true);
            reopened = null; // FileStream owns the reopened handle.
            var recheck = Query(volume, Extents, 32, ct);
            var finalNumber = Query(disk, DeviceNumber, 12, ct);
            var finalDescriptor = Query(disk, DeviceProperty, 4096, ct);
            if (recheck is null || finalNumber is null || finalDescriptor is null
                || !VerifyTopology(raw, recheck, finalNumber, finalDescriptor, volumeDescriptor)
                || !descriptor.AsSpan().SequenceEqual(finalDescriptor)) return null;
            var lease = new Lease(volume, disk, stream, alignment, checked((int)extent.Disk));
            volume = null; disk = null; stream = null;
            return lease;
        }
        catch (IOException) { ct.ThrowIfCancellationRequested(); return null; }
        catch (UnauthorizedAccessException) { ct.ThrowIfCancellationRequested(); return null; }
        catch (ArgumentException) { ct.ThrowIfCancellationRequested(); return null; }
        finally { stream?.Dispose(); reopened?.Dispose(); disk?.Dispose(); volume?.Dispose(); }
    }

    internal sealed class Lease(SafeFileHandle volume, SafeFileHandle disk, FileStream stream, Alignment alignment, int diskIndex) : IDisposable
    {
        public int DiskIndex => diskIndex; // From the verified single-device extent, never the drive letter.
        public SafeFileHandle Handle => stream.SafeFileHandle;
        public Alignment SectorAlignment => alignment;
        public void Dispose() { stream.Dispose(); disk.Dispose(); volume.Dispose(); }
    }

    internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
    {
        private byte* _pointer;
        private readonly int _length;
        internal nint Address => (nint)_pointer;
        public AlignedBuffer(int length, int alignment)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            if (alignment < IntPtr.Size || (alignment & (alignment - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));
            _length = length;
            nuint allocated = checked(((nuint)length + (nuint)alignment - 1) / (nuint)alignment * (nuint)alignment);
            _pointer = (byte*)NativeMemory.AlignedAlloc(allocated, (nuint)alignment);
            if (_pointer == null) throw new OutOfMemoryException();
        }
        public override Span<byte> GetSpan()
            => _pointer == null ? throw new ObjectDisposedException(nameof(AlignedBuffer)) : new Span<byte>(_pointer, _length);
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            if (_pointer == null) throw new ObjectDisposedException(nameof(AlignedBuffer));
            return new MemoryHandle(_pointer + elementIndex);
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing)
        {
            if (_pointer == null) return;
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}
