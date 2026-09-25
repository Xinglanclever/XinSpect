using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 驗機規則：命中、不命中，以及「缺一邊」時必須判成無法判定。
/// </summary>
/// <remarks>
/// 每條規則都要三種測試：相符、矛盾、缺一邊。第三種最重要——二手驗機的情境下，
/// 「這台機器讀不到 SMART」本身就是資訊，不能靜靜地當作通過。
/// 另外每個「矛盾」都必須附得出正當成因（<c>BenignCause</c>）：工具的職責到「指出對不上」為止，
/// 再往前一步就是替使用者認定賣家有惡意。
/// </remarks>
public class VerifyRulesTests
{
    internal static VerifyFact Text(FactId id, string value) => new(
        id, FactCatalog.Name(id), value, null, "", FactSource.Smbios,
        "SMBIOS Type 17", false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    internal static VerifyFact Num(FactId id, double n, string unit = "") => new(
        id, FactCatalog.Name(id), n.ToString("0.##"), n, unit, FactSource.Smbios,
        "SMBIOS Type 17", false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    internal static VerifyFinding One(string ruleId, params VerifyFact[] facts)
        => VerifyEngine.Run(new VerifyFacts(facts)).Single(x => x.Id == ruleId);

    [Fact]
    public void 缺事實時_由引擎判為無法判定_並指出缺哪一個()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2));      // 故意不給製造商與料號
        Assert.Equal(VerifyVerdict.Unread, f.Verdict);
        Assert.Equal(Severity.Neutral, f.Severity);
        Assert.Contains(FactCatalog.Name(FactId.DimmManufacturers), f.Explanation);
        Assert.Empty(f.Evidence);
    }

    [Fact]
    public void R_MEM_01_同廠同料號判為相符()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|Micron"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ|MTA8ATF1G64AZ"));
        Assert.Equal(VerifyVerdict.Match, f.Verdict);
        Assert.Equal(Severity.Good, f.Severity);
    }

    [Fact]
    public void R_MEM_01_不同料號判為矛盾_且必須附上正當成因()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|SK Hynix"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ|HMA81GU6JJR8N"));
        Assert.Equal(VerifyVerdict.Conflict, f.Verdict);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));   // 混批常常只是使用者自己加的
        Assert.Equal(2, f.Evidence.Length);
    }

    [Fact]
    public void R_MEM_01_只有一條模組時無從混批_判為相符()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 1),
            Text(FactId.DimmManufacturers, "Micron"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ"));
        Assert.Equal(VerifyVerdict.Match, f.Verdict);
    }

    // ── R-MEM-02：序號異常。全 0／全 F 是韌體沒燒序號或序號被抹掉；重複則是不可能的事 ──

    [Theory]
    [InlineData("0000000000000000|1234ABCD", VerifyVerdict.Conflict)]
    [InlineData("FFFFFFFF|1234ABCD", VerifyVerdict.Conflict)]
    [InlineData("1234ABCD|1234ABCD", VerifyVerdict.Conflict)]
    [InlineData("1234ABCD|5678EF01", VerifyVerdict.Match)]
    public void R_MEM_02_序號異常(string serials, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-02", Num(FactId.DimmCount, 2),
            Text(FactId.DimmSerials, serials)).Verdict);

    [Fact]
    public void R_MEM_02_序號重複時判定為較嚴重()
    {
        var f = One("R-MEM-02", Num(FactId.DimmCount, 2), Text(FactId.DimmSerials, "1234ABCD|1234ABCD"));
        Assert.Equal(Severity.Serious, f.Severity);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));
    }

    // ── R-MEM-03：實際運行速度低於標稱（多數情況是沒開 XMP，不是模組的問題）──

    [Theory]
    [InlineData(3200, 3200, VerifyVerdict.Match)]
    [InlineData(3200, 2133, VerifyVerdict.Conflict)]
    public void R_MEM_03_實際速度低於標稱(double rated, double configured, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-03",
            Num(FactId.DimmSpeedMts, rated, "MT/s"),
            Num(FactId.DimmConfiguredMts, configured, "MT/s")).Verdict);

    // ── R-MEM-04：陣列宣稱與實際安裝對不上 ──

    [Theory]
    [InlineData(32768, 65536, 4, 2, VerifyVerdict.Match)]
    [InlineData(32768, 16384, 4, 2, VerifyVerdict.Conflict)]   // 安裝量超過陣列宣稱上限
    [InlineData(32768, 65536, 2, 4, VerifyVerdict.Conflict)]   // 模組數多於插槽數
    public void R_MEM_04_陣列宣稱與實際對不上(
        double totalMiB, double maxMiB, double slots, double dimms, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-04",
            Num(FactId.DimmSizeTotalMiB, totalMiB, "MiB"), Num(FactId.ArrayMaxCapacityMiB, maxMiB, "MiB"),
            Num(FactId.ArraySlotCount, slots), Num(FactId.DimmCount, dimms)).Verdict);

    [Fact]
    public void 記憶體四條規則_缺任一依賴都由引擎判為無法判定()
    {
        var empty = VerifyEngine.Run(new VerifyFacts([]));
        Assert.All(empty.Where(x => x.Id.StartsWith("R-MEM-")),
            x => Assert.Equal(VerifyVerdict.Unread, x.Verdict));
        Assert.Equal(4, empty.Count(x => x.Id.StartsWith("R-MEM-")));
    }

    // ── 儲存裝置：每顆碟各跑一次，故要指定 VerifyScope.Disk ──────────────────

    private static VerifyFinding Disk(string ruleId, params VerifyFact[] facts)
        => VerifyEngine.Run(new VerifyFacts(facts), VerifyScope.Disk).Single(x => x.Id == ruleId);

    [Fact]
    public void 儲存規則不在整機範圍跑_記憶體規則也不在碟的範圍跑()
    {
        var machine = VerifyEngine.Run(new VerifyFacts([]));
        var disk = VerifyEngine.Run(new VerifyFacts([]), VerifyScope.Disk);

        Assert.DoesNotContain(machine, x => x.Id.StartsWith("R-SSD-"));
        Assert.DoesNotContain(disk, x => x.Id.StartsWith("R-MEM-"));
        Assert.Equal(6, disk.Count);
    }

    [Theory]
    [InlineData(5000, 100, VerifyVerdict.Match)]        // 50 GiB/h：高但可能
    [InlineData(80000, 100, VerifyVerdict.Conflict)]    // 800 GiB/h：物理上說不通
    [InlineData(1000, 0, VerifyVerdict.Unread)]         // 通電小時是整數：0 代表不足一小時，算不出速率就不判
    public void R_SSD_01_通電小時與寫入量對帳(double writtenGiB, double hours, VerifyVerdict expected)
        => Assert.Equal(expected, Disk("R-SSD-01",
            Num(FactId.NvmeDataUnitsWritten, writtenGiB, "GiB"),
            Num(FactId.NvmePowerOnHours, hours, "小時")).Verdict);

    [Theory]
    [InlineData(0, 200_000, VerifyVerdict.Conflict)]    // 寫了 200 TiB 而壽命還是 0%
    [InlineData(3, 200_000, VerifyVerdict.Match)]
    [InlineData(0, 500, VerifyVerdict.Match)]           // 新碟寫得少，0% 是正常的
    public void R_SSD_02_已用壽命與寫入量對帳(double percentUsed, double writtenGiB, VerifyVerdict expected)
        => Assert.Equal(expected, Disk("R-SSD-02",
            Num(FactId.NvmePercentageUsed, percentUsed, "%"),
            Num(FactId.NvmeDataUnitsWritten, writtenGiB, "GiB")).Verdict);

    [Theory]
    [InlineData(50, 100, VerifyVerdict.Match)]
    [InlineData(150, 100, VerifyVerdict.Conflict)]      // 不安全關機不可能多於通電次數
    public void R_SSD_03_不安全關機不得多於通電次數(double unsafeCount, double cycles, VerifyVerdict expected)
        => Assert.Equal(expected, Disk("R-SSD-03",
            Num(FactId.NvmeUnsafeShutdowns, unsafeCount), Num(FactId.NvmePowerCycles, cycles)).Verdict);

    [Fact]
    public void R_SSD_03_物理上不可能的事沒有正當成因可寫()
        => Assert.Null(Disk("R-SSD-03",
            Num(FactId.NvmeUnsafeShutdowns, 150), Num(FactId.NvmePowerCycles, 100)).BenignCause);

    [Theory]
    [InlineData(0, VerifyVerdict.Match)]
    [InlineData(0b0000_1000, VerifyVerdict.Conflict)]   // bit3：介質已進入唯讀
    public void R_SSD_04_關鍵警告位元(double flags, VerifyVerdict expected)
        => Assert.Equal(expected, Disk("R-SSD-04", Num(FactId.NvmeCriticalWarning, flags)).Verdict);

    [Theory]
    [InlineData(1000, 1_953_525_168, VerifyVerdict.Match)]      // 1TB 碟的正常 LBA 數
    [InlineData(2000, 1_953_525_168, VerifyVerdict.Conflict)]   // 宣稱 2TB 但只定址得到 1TB
    public void R_SSD_05_宣稱容量與可定址容量(double claimedGB, double totalLba, VerifyVerdict expected)
        => Assert.Equal(expected, Disk("R-SSD-05",
            Num(FactId.DiskClaimedCapacityGB, claimedGB, "GB"), Num(FactId.AtaTotalLba, totalLba)).Verdict);

    [Theory]
    [InlineData(1, 0, VerifyVerdict.Match)]          // 固態且無機械屬性
    [InlineData(1, 1, VerifyVerdict.Conflict)]       // 自稱固態卻有起轉時間
    [InlineData(7200, 1, VerifyVerdict.Match)]       // 機械且有機械屬性
    [InlineData(7200, 0, VerifyVerdict.Conflict)]    // 自稱機械卻沒有機械屬性
    [InlineData(0, 0, VerifyVerdict.Match)]          // 未回報轉速：無從矛盾，不猜
    public void R_SSD_06_轉速宣稱與屬性集(double rate, double spinUp, VerifyVerdict expected)
        => Assert.Equal(expected, Disk("R-SSD-06",
            Num(FactId.AtaRotationRate, rate), Num(FactId.SmartSpinUpPresent, spinUp)).Verdict);

    [Fact]
    public void 儲存五條矛盾_都要附證據_物理不可能那條以外都要附正當成因()
    {
        var conflicts = new[]
        {
            Disk("R-SSD-01", Num(FactId.NvmeDataUnitsWritten, 80000, "GiB"), Num(FactId.NvmePowerOnHours, 100)),
            Disk("R-SSD-02", Num(FactId.NvmePercentageUsed, 0), Num(FactId.NvmeDataUnitsWritten, 200_000)),
            Disk("R-SSD-04", Num(FactId.NvmeCriticalWarning, 0b0000_1000)),
            Disk("R-SSD-05", Num(FactId.DiskClaimedCapacityGB, 2000), Num(FactId.AtaTotalLba, 1_953_525_168)),
            Disk("R-SSD-06", Num(FactId.AtaRotationRate, 1), Num(FactId.SmartSpinUpPresent, 1)),
        };

        Assert.All(conflicts, x =>
        {
            Assert.Equal(VerifyVerdict.Conflict, x.Verdict);
            Assert.NotEmpty(x.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(x.BenignCause));
        });
    }

    // ── 電池 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50000, 48000, VerifyVerdict.Match)]        // 96%
    [InlineData(50000, 40000, VerifyVerdict.Match)]        // 80%：剛好在門檻上
    [InlineData(50000, 35000, VerifyVerdict.Conflict)]     // 70%：明顯衰退
    [InlineData(50000, 0, VerifyVerdict.Unread)]           // 0 mWh 是讀不到，不是沒有容量
    [InlineData(0, 40000, VerifyVerdict.Unread)]
    public void R_BAT_01_電池衰退(double design, double full, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-BAT-01",
            Num(FactId.BatteryDesignCapacityMWh, design, "mWh"),
            Num(FactId.BatteryFullCapacityMWh, full, "mWh")).Verdict);

    [Theory]
    [InlineData(35000, Severity.Warning)]     // 70%
    [InlineData(20000, Severity.Serious)]     // 40%
    public void R_BAT_01_衰退越重判定越重(double full, Severity expected)
        => Assert.Equal(expected, One("R-BAT-01",
            Num(FactId.BatteryDesignCapacityMWh, 50000, "mWh"),
            Num(FactId.BatteryFullCapacityMWh, full, "mWh")).Severity);
}
