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
/// 磁碟表面掃描：循序讀取邏輯卷或實體磁碟，逐塊量延遲，標記慢區／讀取錯誤。
/// 用 <c>\\.\C:</c> 開啟邏輯卷（不需系統管理員，僅能讀不能寫）；如果被拒則誠實回報。
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
    public const int DefaultMaxBlocks = 2048;      // 上限 2 GB（安全值，可由呼叫端覆寫）
    private const double SlowThresholdMs = 100.0;  // 超過 100 ms 標為慢

    /// <summary>
    /// 掃描磁碟表面。drivePath 例如 <c>\\.\C:</c> 或 <c>\\.\PhysicalDrive0</c>。
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
        // 旗標筆誤修正（READ|READ→READ|WRITE）：與 ListVolumes 同款；授予他人讀寫共享，
        // 只降低「卷被別的持有者佔住就開不了」的機率，本服務自身僅讀不寫。
        IntPtr h = CreateFile(drivePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE)
            return null;  // 無法開啟（權限不足或路徑不存在）——呼叫端誠實回報

        try
        {
            if (!GetFileSizeEx(h, out long totalBytes) || totalBytes <= 0)
            {
                // FILE_END 回傳的才是長度；再 seek 到 FILE_BEGIN 會把它覆成 0。
                // 每次讀取都會定位，不需在此重設指標。
                if (!SetFilePointerEx(h, 0, out totalBytes, 2) || totalBytes <= 0)
                    return null;
            }

            return ReadBlocks(drivePath, totalBytes, blockSize, maxBlocks, (offset, buffer, requested) =>
            {
                if (!SetFilePointerEx(h, offset, out _, FILE_BEGIN))
                    return (false, 0);
                bool success = ReadFile(h, buffer, (uint)requested, out uint bytesRead, IntPtr.Zero);
                return (success, (int)bytesRead);
            }, progress, ct);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>可取消的本機固定卷唯讀掃描；每個 ReadAsync 的緩衝區生命週期由 .NET 管理至 I/O 完成。</summary>
    public static async Task<SurfaceScanResult?> ScanAsync(string drivePath, int blockSizeBytes,
        int maxBytes = 64 * 1024 * 1024, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSizeBytes, 512);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSizeBytes, 128 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ct.ThrowIfCancellationRequested();

        if (!TryGetLocalVolume(drivePath, out string root, out int sectorBytes)) return null;
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(drivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, FileOptions.Asynchronous);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        using (handle)
        {
            long totalBytes;
            try { totalBytes = RandomAccess.GetLength(handle); }
            catch (IOException) { return null; }
            if (totalBytes <= 0) return null;
            long scanBytes = Math.Min(totalBytes, maxBytes);
            return await ReadBlocksAsync(drivePath, scanBytes, blockSizeBytes, sectorBytes,
                (offset, buffer, token) => RandomAccess.ReadAsync(handle, buffer, offset, token),
                progress, ct).ConfigureAwait(false);
        }
    }

    public static async Task<IReadOnlyList<SurfaceProfileResult>?> ScanProfilesAsync(string drivePath,
        IEnumerable<int> blockSizes, bool repeat, int maxBytes = 64 * 1024 * 1024,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentNullException.ThrowIfNull(blockSizes);
        int[] sizes = blockSizes.Distinct().ToArray();
        if (sizes.Length == 0 || sizes.Any(size => size is not (512 or 8192 or 65536 or 131072)))
            throw new ArgumentException("只支援 512 B、8 KB、64 KB、128 KB 塊尺。", nameof(blockSizes));
        ct.ThrowIfCancellationRequested();
        if (!TryGetLocalVolume(drivePath, out _, out int sectorBytes)) return null;

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(drivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, FileOptions.Asynchronous);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        using (handle)
        {
            long totalBytes;
            try { totalBytes = RandomAccess.GetLength(handle); }
            catch (IOException) { return null; }
            if (totalBytes <= 0) return null;
            long scanBytes = Math.Min(totalBytes, maxBytes);
            var results = new List<SurfaceProfileResult>(sizes.Length * (repeat ? 2 : 1));
            int passes = repeat ? 2 : 1;
            int totalRuns = sizes.Length * passes;
            for (int sizeIndex = 0; sizeIndex < sizes.Length; sizeIndex++)
            for (int pass = 1; pass <= passes; pass++)
            {
                ct.ThrowIfCancellationRequested();
                int runIndex = sizeIndex * passes + pass - 1;
                var runProgress = progress is null ? null : new Progress<int>(value =>
                    progress.Report((runIndex * 100 + value) / totalRuns));
                var scan = await ReadBlocksAsync(drivePath, scanBytes, sizes[sizeIndex], sectorBytes,
                    (offset, buffer, token) => RandomAccess.ReadAsync(handle, buffer, offset, token),
                    runProgress, ct).ConfigureAwait(false);
                if (scan is null) return null;
                results.Add(new SurfaceProfileResult(sizes[sizeIndex], pass, scan));
            }
            return results;
        }
    }

    private static bool TryGetLocalVolume(string drivePath, out string root, out int sectorBytes)
    {
        root = string.Empty;
        sectorBytes = 0;
        if (drivePath is null || drivePath.Length != 6 ||
            !drivePath.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            drivePath[5] != ':' || drivePath[4] is < 'A' or > 'Z') return false;
        root = $"{drivePath[4]}:\\";
        if (GetDriveType(root) != DRIVE_FIXED) return false;
        if (GetDiskFreeSpace(root, out _, out uint bytesPerSector, out _, out _) &&
            bytesPerSector is >= 512 and <= 65536 && (bytesPerSector & (bytesPerSector - 1)) == 0)
            sectorBytes = (int)bytesPerSector;
        return true;
    }

    internal static async Task<SurfaceScanResult?> ReadBlocksAsync(string drivePath, long totalBytes,
        int blockSize, int logicalSectorBytes,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> read,
        IProgress<int>? progress = null, CancellationToken ct = default)
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
        int retriesRemaining = 10;
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
                if (retriesRemaining == 0) break;
                retriesRemaining--;
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
