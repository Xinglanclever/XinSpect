using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>單一區塊的讀取結果：索引、延遲 ms、狀態（OK/慢/錯誤）。</summary>
public sealed record SurfaceBlock(int Index, double LatencyMs, SurfaceBlockStatus Status, long OffsetBytes);

public enum SurfaceBlockStatus { Ok, Slow, Error }

/// <summary>整碟表面掃描結果。</summary>
public sealed record SurfaceScanResult(
    string DrivePath, long TotalBytes, int BlockSize, int TotalBlocks,
    IReadOnlyList<SurfaceBlock> Blocks, double ElapsedSec, int OkCount, int SlowCount, int ErrorCount);

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

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private const uint FILE_BEGIN = 0;

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
        int blockSize = blockSizeKB * 1024;
        IntPtr h = CreateFile(drivePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_READ, IntPtr.Zero,
            OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE)
            return null;  // 無法開啟（權限不足或路徑不存在）——呼叫端誠實回報

        try
        {
            if (!GetFileSizeEx(h, out long totalBytes) || totalBytes <= 0)
            {
                // 卷可能不支援 GetFileSizeEx，嘗試 SetFilePointer 到尾
                if (!SetFilePointerEx(h, 0, out _, 2)) // FILE_END
                    return null;
                if (!SetFilePointerEx(h, 0, out totalBytes, FILE_BEGIN))
                    return null;
            }

            int totalBlocks = (int)Math.Min(totalBytes / blockSize, maxBlocks > 0 ? maxBlocks : int.MaxValue);
            if (totalBlocks <= 0) return null;

            var blocks = new List<SurfaceBlock>(totalBlocks);
            var buf = new byte[blockSize];
            var sw = Stopwatch.StartNew();
            int ok = 0, slow = 0, err = 0;

            for (int i = 0; i < totalBlocks; i++)
            {
                ct.ThrowIfCancellationRequested();
                long offset = (long)i * blockSize;
                if (!SetFilePointerEx(h, offset, out _, FILE_BEGIN))
                { blocks.Add(new SurfaceBlock(i, 0, SurfaceBlockStatus.Error, offset)); err++; continue; }

                var blockSw = Stopwatch.StartNew();
                bool success = ReadFile(h, buf, (uint)blockSize, out uint bytesRead, IntPtr.Zero);
                blockSw.Stop();
                double ms = blockSw.Elapsed.TotalMilliseconds;

                if (!success || bytesRead < blockSize)
                { blocks.Add(new SurfaceBlock(i, ms, SurfaceBlockStatus.Error, offset)); err++; }
                else if (ms > SlowThresholdMs)
                { blocks.Add(new SurfaceBlock(i, ms, SurfaceBlockStatus.Slow, offset)); slow++; }
                else
                { blocks.Add(new SurfaceBlock(i, ms, SurfaceBlockStatus.Ok, offset)); ok++; }

                if (progress is not null && (i % 16 == 0 || i == totalBlocks - 1))
                    progress.Report(i * 100 / totalBlocks);
            }
            sw.Stop();
            return new SurfaceScanResult(drivePath, totalBytes, blockSize, totalBlocks,
                blocks, sw.Elapsed.TotalSeconds, ok, slow, err);
        }
        finally { CloseHandle(h); }
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
