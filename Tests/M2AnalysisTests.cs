using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// M.2 介面分析的收集/套用分離（3.13）：收集可以在背景跑、可以注入假的磁碟清單，
/// 而且 WMI 不回應時要「說讀不到」而不是讓整個服務卡住主視窗。
/// </summary>
public class M2AnalysisTests
{
    private static PhysicalDiskInfo Disk(int i, DiskKind kind) => new()
    {
        Index = i,
        Model = kind == DiskKind.NvmeSsd ? "NVMe Test" : "SATA Test",
        Kind = kind,
        SizeBytes = 512L * 1024 * 1024 * 1024,
    };

    [Fact]
    public void 注入磁碟清單_NVMe另外成一份清單_SATA不進M2清單()
    {
        var svc = new M2AnalysisService();
        var snap = svc.Collect(readDisks: () =>
        [
            Disk(0, DiskKind.NvmeSsd),
            Disk(1, DiskKind.NvmeSsd),
            Disk(2, DiskKind.SataSsd),
        ]);

        Assert.Equal(2, snap.Nvme);
        Assert.Equal(3, snap.Drives.Count);
        Assert.Equal(2, snap.NvmeDrives.Count);
        Assert.All(snap.NvmeDrives, note => Assert.Contains("NVMe Test", note));
    }

    [Fact]
    public void WMI不回應時_在預算內回來並如實標示略過()
    {
        var svc = new M2AnalysisService();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snap = svc.Collect(readDisks: () =>
        {
            Thread.Sleep(1500);
            return [Disk(0, DiskKind.NvmeSsd)];
        }, timeoutMs: 50);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1000, $"收集花了 {watch.ElapsedMilliseconds} ms，沒有守住逾時");
        Assert.Equal(0, snap.Nvme);
        Assert.Contains(snap.Drives, d => d.Contains("WMI 無回應"));
        Assert.DoesNotContain(snap.NvmeDrives, d => d.Contains("NVMe"));
    }

    [Fact]
    public void 讀取快速失敗時_要說讀取失敗而不是WMI無回應()
    {
        var svc = new M2AnalysisService();
        var snap = svc.Collect(readDisks: () => throw new InvalidOperationException("access denied"), timeoutMs: 50);
        Assert.Equal(0, snap.Nvme);
        Assert.Contains(snap.Drives, d => d.Contains("讀取失敗"));
        Assert.DoesNotContain(snap.Drives, d => d.Contains("WMI 無回應"));
    }
}
