using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 顯示卡 PCIe 流量的換算與判讀。
///
/// 守的規則：<b>流量與鏈路容量必須分清楚</b>。NVML 給的是驅動量到的 KB/s，
/// 鏈路容量則由世代與寬度算出；把兩者混為一談就會說出「PCIe 滿載」這種錯結論。
/// 而且世代與寬度讀不到時就不算佔用率，不拿一個猜的分母去除。
/// </summary>
public class GpuPcieDecoderTests
{
    [Fact]
    public void KB每秒換算成人看得懂的單位()
    {
        Assert.Equal("—", GpuPcieDecoder.RateText(null));
        Assert.Equal("0 KB/s", GpuPcieDecoder.RateText(0));
        Assert.Equal("512 KB/s", GpuPcieDecoder.RateText(512));
        Assert.Equal("1.50 MB/s", GpuPcieDecoder.RateText(1536));
        Assert.Equal("2.00 GB/s", GpuPcieDecoder.RateText(2 * 1024 * 1024));
    }

    [Fact]
    public void 鏈路容量由世代與寬度算出()
    {
        // Gen3 每通道 985 MB/s（8 GT/s、128b/130b 編碼）；×16 ≈ 15.75 GB/s
        double? gb = GpuPcieDecoder.LinkCapacityGbPerSec(3, 16);
        Assert.NotNull(gb);
        Assert.InRange(gb!.Value, 15.0, 16.5);

        // Gen4 ×8 應該與 Gen3 ×16 相當
        Assert.InRange(GpuPcieDecoder.LinkCapacityGbPerSec(4, 8)!.Value, 15.0, 16.5);
    }

    [Fact]
    public void 世代或寬度讀不到時不算容量也不算佔用率()
    {
        Assert.Null(GpuPcieDecoder.LinkCapacityGbPerSec(0, 16));
        Assert.Null(GpuPcieDecoder.LinkCapacityGbPerSec(3, 0));
        Assert.Null(GpuPcieDecoder.UtilizationPercent(1024, 1024, 0, 16));
    }

    [Fact]
    public void 佔用率是傳送加接收除以容量()
    {
        // 容量 Gen3×16 ≈ 15.75 GB/s；傳送＋接收共 1.575 GB/s → 約 10%
        double? pct = GpuPcieDecoder.UtilizationPercent(
            txKbPerSec: 1024 * 1024 / 2, rxKbPerSec: 1024 * 1024 / 2 + 51_200, gen: 3, width: 16);
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 5, 12);
    }

    [Fact]
    public void 鏈路降級時要說出目前與能力的差距()
    {
        var (text, sev) = GpuPcieDecoder.JudgeLink(curGen: 1, curWidth: 16, maxGen: 3, maxWidth: 16);
        Assert.Contains("Gen1", text);
        Assert.Contains("Gen3", text);
        Assert.Equal(Severity.Warning, sev);
    }

    [Fact]
    public void 寬度不足比世代不足嚴重()
    {
        var (text, sev) = GpuPcieDecoder.JudgeLink(3, 8, 3, 16);
        Assert.Contains("x8", text);
        Assert.Contains("x16", text);
        Assert.Equal(Severity.Serious, sev);
    }

    [Fact]
    public void 世代較低但那是省電狀態時要說明可能只是閒置()
    {
        var (text, _) = GpuPcieDecoder.JudgeLink(1, 16, 3, 16);
        Assert.Contains("閒置", text);
    }

    [Fact]
    public void 與能力相符就說相符()
    {
        var (text, sev) = GpuPcieDecoder.JudgeLink(3, 16, 3, 16);
        Assert.Contains("相符", text);
        Assert.Equal(Severity.Good, sev);
    }

    [Fact]
    public void 讀不到鏈路資訊時不下判斷()
    {
        var (text, sev) = GpuPcieDecoder.JudgeLink(0, 0, 0, 0);
        Assert.Contains("讀不到", text);
        Assert.Equal(Severity.Neutral, sev);
    }
}
