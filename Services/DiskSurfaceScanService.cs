using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>單一區塊的讀取結果：索引、延遲 ms、狀態（OK/慢/錯誤）。</summary>
public sealed record SurfaceBlock(int Index, double LatencyMs, SurfaceBlockStatus Status, long OffsetBytes);

public enum SurfaceBlockStatus { Ok, Slow, Error }

/// <summary>整碟表面掃描結果。</summary>
public sealed record SurfaceScanResult(
    string DrivePath, long TotalBytes, int BlockSize, int TotalBlocks,
    IReadOnlyList<SurfaceBlock> Blocks, double ElapsedSec, int OkCount, int SlowCount, int ErrorCount,
    int LogicalSectorBytes = 0, string IoMode = "buffered", int RetryCount = 0);

public sealed record SurfaceProfileResult(int BlockSizeBytes, int Pass, SurfaceScanResult Scan);

/// <summary>
/// 磁碟表面掃描：循序讀取已核單一 ATA/SATA backing device 的邏輯卷，逐塊量延遲。
/// 用 <c>\\.\C:</c> 開啟邏輯卷與對應實體磁碟的唯讀 handle；權限不足或拓撲不明則拒讀。
/// 這是「實際讀一輪」的量測，和 SMART 報告值是互補——SMART 是韌體說的，這裡是我們自己讀的。
/// </summary>
public static class DiskSurfaceScanService
{
    // P/Invoke
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(IntPtr handle, long dist, out long newPtr, uint whence);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(IntPtr handle, out long size);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster,
        out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private const uint FILE_BEGIN = 0;
    private const uint DRIVE_FIXED = 3;

    // 預設掃描參數
    public const int DefaultBlockSizeKB = 1024;   // 1 MB 區塊
    public const int DefaultMaxBlocks = 2048;      // 舊同步入口的預設範圍；不代表安全性或全碟驗證。
    public const int MaxProfileBytes = 64 * 1024 * 1024;
    public const string PreflightUnavailableMessage =
        "未開始掃描：需可讀取、幾何有效且可核對單一 ATA/SATA 實體磁碟的本機固定卷；"
        + "NVMe、RAID、Storage Spaces、身份不明或已觸發斷路器的路徑均拒讀。";
    internal const string BreakerOperation = "DiskSurfaceScan";
    private const double SlowThresholdMs = 100.0;  // 超過 100 ms 標為慢

    /// <summary>
    /// 掃描邏輯卷，例如 <c>\\.\C:</c>；需通過 backing-device 身份與協定預檢。
    /// maxBlocks 限制掃描範圍（0＝不限制）。progress 回報已完成百分比（0-100）。
    /// </summary>
    public static SurfaceScanResult? Scan(string drivePath, int blockSizeKB = DefaultBlockSizeKB,
        int maxBlocks = DefaultMaxBlocks, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSizeKB, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSizeKB, int.MaxValue / 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBlocks);
        ct.ThrowIfCancellationRequested();
        int blockSize = blockSizeKB * 1024;
        using var backing = DiskSurfaceBackingDevice.Open(drivePath, ct);
        if (backing is null) return null;
        using var observation = new ReadFailureObservation(backing.DiskIndex);
        ct.ThrowIfCancellationRequested();
        long totalBytes;
        try { totalBytes = RandomAccess.GetLength(backing.Handle); }
        catch (IOException) { ct.ThrowIfCancellationRequested(); return null; }
        ct.ThrowIfCancellationRequested();
        if (totalBytes <= 0) return null;
        if (blockSize % backing.SectorAlignment.Logical != 0 || totalBytes % backing.SectorAlignment.Logical != 0) return null;
        var scan = ReadBlocks(drivePath, totalBytes, blockSize, maxBlocks, (offset, buffer, requested) =>
        {
            ct.ThrowIfCancellationRequested();
            using var aligned = new DiskSurfaceBackingDevice.AlignedBuffer(requested, backing.SectorAlignment.Physical);
            try { return (true, observation.Read(() => RandomAccess.Read(backing.Handle, aligned.GetSpan(), offset), ct)); }
            catch (IOException ex) when ((ex.HResult & 0xffff) != 87) { return (false, 0); }
        }, progress, ct);
        return scan is null ? null : scan with { LogicalSectorBytes = backing.SectorAlignment.Logical,
            IoMode = "volume synchronous / aligned; cache policy provider-dependent" };
    }
    /// <summary>可取消的本機固定卷唯讀掃描；每個 ReadAsync 的緩衝區生命週期由 .NET 管理至 I/O 完成。</summary>
    public static async Task<SurfaceScanResult?> ScanAsync(string drivePath, int blockSizeBytes,
        int maxBytes = 64 * 1024 * 1024, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSizeBytes, 512);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSizeBytes, 128 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, MaxProfileBytes);
        ct.ThrowIfCancellationRequested();

        if (!TryGetLocalVolume(drivePath, ct, out string root, out int sectorBytes)
            || blockSizeBytes % sectorBytes != 0) return null;
        ct.ThrowIfCancellationRequested();
        using var backing = DiskSurfaceBackingDevice.Open(drivePath, ct);
        if (backing is null) return null;
        using var observation = new ReadFailureObservation(backing.DiskIndex);
        var handle = backing.Handle;
        {
            ct.ThrowIfCancellationRequested();
            long totalBytes;
            try { totalBytes = RandomAccess.GetLength(handle); }
            catch (IOException) { ct.ThrowIfCancellationRequested(); return null; }
            ct.ThrowIfCancellationRequested();
            if (totalBytes <= 0) return null;
            long scanBytes = Math.Min(totalBytes, maxBytes);
            if (backing.SectorAlignment.Logical != sectorBytes || scanBytes % sectorBytes != 0) return null;
            var scan = await ReadBlocksAsync(drivePath, scanBytes, blockSizeBytes, sectorBytes,
                (offset, buffer, token) => ReadAlignedAsync(offset, buffer, backing.SectorAlignment,
                    (position, memory, cancellation) => observation.ReadAsync(
                        () => RandomAccess.ReadAsync(handle, memory, position, cancellation), cancellation), token),
                progress, ct).ConfigureAwait(false);
            return scan is null ? null : scan with { IoMode = "volume async / aligned; cache policy provider-dependent" };
        }
    }

    public static async Task<IReadOnlyList<SurfaceProfileResult>?> ScanProfilesAsync(string drivePath,
        IEnumerable<int> blockSizes, bool repeat, int maxBytes = 64 * 1024 * 1024,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, MaxProfileBytes);
        ArgumentNullException.ThrowIfNull(blockSizes);
        ct.ThrowIfCancellationRequested();
        // Bound enumeration as well as I/O: an unbounded/repeating caller enumerable must not hang preflight.
        int[] requestedSizes = blockSizes.Take(5).ToArray();
        int[] sizes = requestedSizes.Distinct().ToArray();
        if (requestedSizes.Length > 4 || sizes.Length == 0 || sizes.Any(size => size is not (512 or 8192 or 65536 or 131072)))
            throw new ArgumentException("只支援 512 B、8 KB、64 KB、128 KB 塊尺。", nameof(blockSizes));
        ct.ThrowIfCancellationRequested();
        if (!TryGetLocalVolume(drivePath, ct, out _, out int sectorBytes)
            || sizes.Any(size => size % sectorBytes != 0)) return null;
        ct.ThrowIfCancellationRequested();

        using var backing = DiskSurfaceBackingDevice.Open(drivePath, ct);
        if (backing is null) return null;
        using var observation = new ReadFailureObservation(backing.DiskIndex);
        var handle = backing.Handle;
        {
            ct.ThrowIfCancellationRequested();
            long totalBytes;
            try { totalBytes = RandomAccess.GetLength(handle); }
            catch (IOException) { ct.ThrowIfCancellationRequested(); return null; }
            ct.ThrowIfCancellationRequested();
            if (totalBytes <= 0) return null;
            long scanBytes = Math.Min(totalBytes, maxBytes);
            if (backing.SectorAlignment.Logical != sectorBytes || scanBytes % sectorBytes != 0) return null;
            var results = await ReadProfilesAsync(drivePath, scanBytes, sizes, repeat, sectorBytes,
                (offset, buffer, token) => ReadAlignedAsync(offset, buffer, backing.SectorAlignment,
                    (position, memory, cancellation) => observation.ReadAsync(
                        () => RandomAccess.ReadAsync(handle, memory, position, cancellation), cancellation), token),
                progress, ct).ConfigureAwait(false);
            return results?.Select(result => result with { Scan = result.Scan with
                { IoMode = "volume async / aligned; cache policy provider-dependent" } }).ToArray();
        }
    }

    /// <summary>
    /// Observes completed native data-read failures for one verified backing disk. This records
    /// path cooldown evidence, never a diagnosis of bad media. Configuration/permission/cancellation
    /// failures and short-count observations alone are not device-I/O error evidence.
    /// </summary>
    internal sealed class ReadFailureObservation(int diskIndex) : IDisposable
    {
        private bool _deviceIoFailed, _disposed;

        // Windows SDK 10.0.26100.0 shared/winerror.h: only HRESULT_FROM_WIN32 device/read failures.
        internal static bool IsDeviceIoFailure(IOException error)
            => ((uint)error.HResult & 0xFFFF0000u) == 0x80070000u
                && (error.HResult & 0xFFFF) is 21 or 23 or 27 or 30 or 31 or 121 or 1117 or 1167 or 1460;

        internal int Read(Func<int> read, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            try { return read(); }
            catch (IOException ex) when (!ct.IsCancellationRequested && IsDeviceIoFailure(ex))
            {
                _deviceIoFailed = true;
                throw;
            }
        }

        internal async ValueTask<int> ReadAsync(Func<ValueTask<int>> read, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            try { return await read().ConfigureAwait(false); }
            catch (IOException ex) when (!ct.IsCancellationRequested && IsDeviceIoFailure(ex))
            {
                _deviceIoFailed = true;
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Awaited reads have finished before this scope exits. Keep the existing bounded retry
            // contract, then suppress later scans after any confirmed I/O failure, even if retried.
            // Success must not clear a fault concurrently recorded by another session on this disk.
            if (_deviceIoFailed) StorageProbeBreaker.Report(diskIndex, BreakerOperation, read: false);
        }
    }

    internal static async ValueTask<int> ReadAlignedAsync(long offset, Memory<byte> destination,
        DiskSurfaceBackingDevice.Alignment alignment,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> read, CancellationToken ct)
    {
        if (offset < 0 || offset % alignment.Logical != 0 || destination.Length == 0
            || destination.Length % alignment.Logical != 0)
            throw new InvalidOperationException("卷讀取的位移或長度不符合已回報的磁區幾何；不是媒體壞塊。");
        ct.ThrowIfCancellationRequested();
        using var aligned = new DiskSurfaceBackingDevice.AlignedBuffer(destination.Length, alignment.Physical);
        int count;
        try { count = await read(offset, aligned.Memory, ct).ConfigureAwait(false); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 87)
        { throw new InvalidOperationException("卷讀取設定遭 ERROR_INVALID_PARAMETER 拒絕；不當作媒體壞塊重試。", ex); }
        ct.ThrowIfCancellationRequested();
        if (count > 0 && count <= destination.Length && count % alignment.Logical != 0)
            throw new InvalidOperationException("卷短讀長度不符合磁區幾何；不發出未對齊的續讀。");
        if (count > 0 && count <= destination.Length) aligned.Memory[..count].CopyTo(destination);
        return count;
    }

    internal static async Task<IReadOnlyList<SurfaceProfileResult>?> ReadProfilesAsync(string drivePath,
        long scanBytes, int[] sizes, bool repeat, int sectorBytes,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> read,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var results = new List<SurfaceProfileResult>(sizes.Length * (repeat ? 2 : 1));
        var retryBudget = new ReadRetryBudget();
        int passes = repeat ? 2 : 1;
        int totalRuns = sizes.Length * passes;
        for (int sizeIndex = 0; sizeIndex < sizes.Length; sizeIndex++)
        for (int pass = 1; pass <= passes; pass++)
        {
            ct.ThrowIfCancellationRequested();
            int runIndex = sizeIndex * passes + pass - 1;
            var runProgress = progress is null ? null : new ProfileProgress(progress, runIndex, totalRuns);
            var scan = await ReadBlocksAsync(drivePath, scanBytes, sizes[sizeIndex], sectorBytes,
                read, runProgress, ct, retryBudget).ConfigureAwait(false);
            if (scan is null) return null;
            results.Add(new SurfaceProfileResult(sizes[sizeIndex], pass, scan));
        }
        ct.ThrowIfCancellationRequested();
        return results;
    }

    internal sealed class ReadRetryBudget
    {
        public int Remaining { get; set; } = 10;
    }

    private sealed class ProfileProgress(IProgress<int> progress, int runIndex, int totalRuns) : IProgress<int>
    {
        public void Report(int value) => progress.Report((runIndex * 100 + value) / totalRuns);
    }

    private static bool TryGetLocalVolume(string drivePath, CancellationToken ct, out string root, out int sectorBytes)
    {
        root = string.Empty;
        sectorBytes = 0;
        if (drivePath is null || drivePath.Length != 6 ||
            !drivePath.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            drivePath[5] != ':' || drivePath[4] is < 'A' or > 'Z') return false;
        root = $"{drivePath[4]}:\\";
        sectorBytes = ReadLocalGeometry(root, GetDriveType, path =>
        {
            bool ok = GetDiskFreeSpace(path, out _, out uint bytes, out _, out _);
            return (ok, bytes);
        }, ct);
        return sectorBytes != 0;
    }

    // These synchronous OS metadata calls have no cancellation token. Check between stages;
    // cancellation cannot promise termination of a metadata call already inside the kernel.
    internal static int ReadLocalGeometry(string root, Func<string, uint> driveTypeQuery,
        Func<string, (bool Success, uint BytesPerSector)> sectorQuery, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        uint driveType = driveTypeQuery(root);
        ct.ThrowIfCancellationRequested();
        if (driveType != DRIVE_FIXED) return 0;
        var (geometryRead, bytesPerSector) = sectorQuery(root);
        ct.ThrowIfCancellationRequested();
        if (geometryRead &&
            bytesPerSector is >= 512 and <= 65536 && (bytesPerSector & (bytesPerSector - 1)) == 0)
            return (int)bytesPerSector;
        return 0;
    }

    internal static async Task<SurfaceScanResult?> ReadBlocksAsync(string drivePath, long totalBytes,
        int blockSize, int logicalSectorBytes,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> read,
        IProgress<int>? progress = null, CancellationToken ct = default, ReadRetryBudget? retryBudget = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalSectorBytes, 0);
        ArgumentNullException.ThrowIfNull(read);
        ct.ThrowIfCancellationRequested();
        if (totalBytes <= 0) return null;

        long availableBlocks = totalBytes / blockSize + (totalBytes % blockSize == 0 ? 0 : 1);
        int totalBlocks = (int)Math.Min(availableBlocks, int.MaxValue);
        var blocks = new List<SurfaceBlock>(Math.Min(totalBlocks, 4096));
        var buffer = new byte[(int)Math.Min(blockSize, totalBytes)];
        var sw = Stopwatch.StartNew();
        int ok = 0, slow = 0, errors = 0;
        retryBudget ??= new ReadRetryBudget();
        int retriesUsed = 0;

        for (int i = 0; i < totalBlocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = (long)i * blockSize;
            int requested = (int)Math.Min(blockSize, totalBytes - offset);
            var blockSw = Stopwatch.StartNew();
            bool complete = true;
            int received = 0;
            while (true)
            {
                complete = true;
                received = 0;
                try
                {
                    while (received < requested)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = await read(offset + received,
                            buffer.AsMemory(received, requested - received), ct).ConfigureAwait(false);
                        if (count <= 0 || count > requested - received) { complete = false; break; }
                        received += count;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (IOException) { complete = false; }
                catch (UnauthorizedAccessException) { complete = false; }
                if (complete && received == requested) break;
                if (retryBudget.Remaining == 0) break;
                retryBudget.Remaining--;
                retriesUsed++;
                ct.ThrowIfCancellationRequested();
            }
            ct.ThrowIfCancellationRequested();
            blockSw.Stop();

            double ms = blockSw.Elapsed.TotalMilliseconds;
            SurfaceBlockStatus status;
            if (!complete || received != requested) { status = SurfaceBlockStatus.Error; errors++; }
            else if (ms > SlowThresholdMs) { status = SurfaceBlockStatus.Slow; slow++; }
            else { status = SurfaceBlockStatus.Ok; ok++; }
            blocks.Add(new SurfaceBlock(i, ms, status, offset));
            if (i % 16 == 0 || i == totalBlocks - 1)
                progress?.Report((int)((i + 1L) * 100 / totalBlocks));
        }

        ct.ThrowIfCancellationRequested();
        sw.Stop();
        return new SurfaceScanResult(drivePath, totalBytes, blockSize, totalBlocks,
            blocks, sw.Elapsed.TotalSeconds, ok, slow, errors, logicalSectorBytes, "buffered async", retriesUsed);
    }

    /// <summary>共用分塊／完整性判定；測試注入讀取結果，不接觸真實磁碟。</summary>
    internal static SurfaceScanResult? ReadBlocks(string drivePath, long totalBytes, int blockSize,
        int maxBlocks, Func<long, byte[], int, (bool Success, int BytesRead)> read,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBlocks);
        ArgumentNullException.ThrowIfNull(read);
        ct.ThrowIfCancellationRequested();
        if (totalBytes <= 0) return null;

        // 除法＋餘數避免 totalBytes + blockSize - 1 溢位；最後不足一塊也必須掃到。
        long availableBlocks = totalBytes / blockSize + (totalBytes % blockSize == 0 ? 0 : 1);
        int totalBlocks = (int)Math.Min(availableBlocks, maxBlocks > 0 ? maxBlocks : int.MaxValue);
        var blocks = new List<SurfaceBlock>(Math.Min(totalBlocks, 4096));
        var buffer = new byte[(int)Math.Min(blockSize, totalBytes)];
        var sw = Stopwatch.StartNew();
        int ok = 0, slow = 0, errors = 0;

        for (int i = 0; i < totalBlocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = (long)i * blockSize;
            int requested = (int)Math.Min(blockSize, totalBytes - offset);
            var blockSw = Stopwatch.StartNew();
            var result = read(offset, buffer, requested);
            blockSw.Stop();
            ct.ThrowIfCancellationRequested();
            double ms = blockSw.Elapsed.TotalMilliseconds;

            // 只接受與本次預期長度完全相等的讀取；掃描上限的末塊不是裝置真正的尾塊。
            SurfaceBlockStatus status;
            if (!result.Success || result.BytesRead != requested)
            { status = SurfaceBlockStatus.Error; errors++; }
            else if (ms > SlowThresholdMs)
            { status = SurfaceBlockStatus.Slow; slow++; }
            else
            { status = SurfaceBlockStatus.Ok; ok++; }
            blocks.Add(new SurfaceBlock(i, ms, status, offset));

            if (i % 16 == 0 || i == totalBlocks - 1)
                progress?.Report((int)((i + 1L) * 100 / totalBlocks));
        }
        ct.ThrowIfCancellationRequested();
        sw.Stop();
        return new SurfaceScanResult(drivePath, totalBytes, blockSize, totalBlocks,
            blocks, sw.Elapsed.TotalSeconds, ok, slow, errors);
    }

    /// <summary>列出可掃描的磁碟（邏輯卷 A-Z 中存在且可開啟的）。</summary>
    public static List<(string Path, string Label)> ListVolumes()
    {
        var result = new List<(string, string)>();
        for (char c = 'C'; c <= 'Z'; c++)
        {
            string path = $"\\\\.\\{c}:";
            IntPtr h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h != INVALID_HANDLE)
            {
                CloseHandle(h);
                result.Add((path, $"{c}: 磁碟"));
            }
        }
        return result;
    }
}
