using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 事實收集器:把已讀到的值攤成帶血統的 VerifyFact,並確認接上規則後判定正確。
/// </summary>
/// <remarks>
/// 這裡的重點是「讀不到」與「讀到」的界線:NVMe 快照為 null、SMART 屬性為 null 時,
/// 對應事實**不該憑空出現**,好讓規則判「無法判定」而不是拿 0 當真值。
/// </remarks>
public class VerifyFactsCollectorTests
{
    private static readonly DateTime T = DateTime.UnixEpoch;

    private static NvmeHealthSnapshot Snap(ulong hours, ulong writtenUnits, byte used, ulong cycles,
        ulong unsafeShut, byte warning) => new(
        CriticalWarning: warning, CompositeTempKelvin: 315, AvailableSparePercent: 100,
        SpareThresholdPercent: 10, PercentageUsed: used, DataUnitsRead: 0, DataUnitsWritten: writtenUnits,
        HostReadCommands: 0, HostWriteCommands: 0, PowerCycles: cycles, PowerOnHours: hours,
        UnsafeShutdowns: unsafeShut, MediaErrors: 0, ErrorLogEntries: 0);

    [Fact]
    public void NVMe快照為null時_不產出任何NVMe事實()
        => Assert.Empty(VerifyFactsCollector.Nvme(null, T));

    [Fact]
    public void NVMe事實帶正確血統_且標記需要管理員()
    {
        var facts = new VerifyFacts(VerifyFactsCollector.Nvme(Snap(100, 0, 3, 50, 2, 0), T));
        var poh = facts.Get(FactId.NvmePowerOnHours)!;
        Assert.Equal(100, poh.Numeric);
        Assert.Equal(FactSource.NvmeLog, poh.Source);
        Assert.True(poh.NeedsAdmin);
        Assert.Contains("+0x80", poh.Method);
    }

    [Fact]
    public void 收集到的NVMe事實接上引擎_能判出翻新碟的矛盾()
    {
        // 通電 100 小時、寫入 2,097,152×1000 單位≈1000 TiB → R-SSD-01 必為矛盾
        var facts = new VerifyFacts(VerifyFactsCollector.Nvme(
            Snap(hours: 100, writtenUnits: 2_097_152_000, used: 0, cycles: 50, unsafeShut: 2, warning: 0), T));
        var write = VerifyEngine.Run(facts, VerifyScope.Disk).Single(x => x.Id == "R-SSD-01");
        Assert.Equal(VerifyVerdict.Conflict, write.Verdict);
    }

    [Fact]
    public void 沒有SMART屬性表時_不產出起轉屬性事實()
        => Assert.DoesNotContain(VerifyFactsCollector.Ata(null, null, null, T),
            f => f.Id == FactId.SmartSpinUpPresent);

    [Fact]
    public void 有SMART屬性0x03時_判定為存在機械專屬屬性()
    {
        var attrs = new List<SmartRow> { new("3 起轉時間", "100", "100", "…", 0x03, 1234) };
        var fact = new VerifyFacts(VerifyFactsCollector.Ata(null, null, attrs, T)).Get(FactId.SmartSpinUpPresent)!;
        Assert.Equal(1, fact.Numeric);
    }

    [Fact]
    public void 沒有0x03屬性時_判定為無機械專屬屬性()
    {
        var attrs = new List<SmartRow> { new("5 重配置磁區", "100", "100", "…", 0x05, 0) };
        var fact = new VerifyFacts(VerifyFactsCollector.Ata(null, null, attrs, T)).Get(FactId.SmartSpinUpPresent)!;
        Assert.Equal(0, fact.Numeric);
    }

    [Fact]
    public void ATA識別接上引擎_假容量碟判為矛盾()
    {
        var info = new AtaIdentifyInfo("FAKE 2TB", "FW", "SN", 1_953_525_168, 1, 10);   // 實際約 1TB
        var facts = new VerifyFacts(VerifyFactsCollector.Ata(info, claimedCapacityGB: 2000, null, T));
        var cap = VerifyEngine.Run(facts, VerifyScope.Disk).Single(x => x.Id == "R-SSD-05");
        Assert.Equal(VerifyVerdict.Conflict, cap.Verdict);
    }

    [Theory]
    [InlineData(0, 0, VerifyVerdict.Unread)]         // 讀不到容量
    [InlineData(50000, 40000, VerifyVerdict.Match)]  // 80%
    [InlineData(50000, 30000, VerifyVerdict.Conflict)]
    public void 電池事實接上引擎(double design, double full, VerifyVerdict expected)
    {
        var facts = new VerifyFacts(VerifyFactsCollector.Battery(design, full, T));
        Assert.Equal(expected, VerifyEngine.Run(facts).Single(x => x.Id == "R-BAT-01").Verdict);
    }
}
