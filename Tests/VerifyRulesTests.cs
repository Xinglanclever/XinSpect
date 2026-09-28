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

    // ── R-MEM-05：宣稱 ECC 與模組實際位元寬度對不上（工作站二手機關鍵）──

    [Theory]
    [InlineData(3, 0, VerifyVerdict.Match)]      // 宣稱無 ECC + 沒有 ECC 位元 → 一致
    [InlineData(6, 1, VerifyVerdict.Match)]      // 宣稱多位元 ECC + 有 ECC 位元 → 一致
    [InlineData(6, 0, VerifyVerdict.Conflict)]   // 宣稱 ECC 卻沒有 ECC 位元 → 買到假 ECC
    [InlineData(3, 1, VerifyVerdict.Match)]      // 有 ECC 位元卻宣稱無:韌體常態,單向規則不判矛盾
    [InlineData(4, 0, VerifyVerdict.Match)]      // 同位元(4)不算 ECC,無位元 → 一致
    public void R_MEM_05_ECC宣稱與位元寬度(double eccType, double bits, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-05",
            Num(FactId.MemEccType, eccType), Num(FactId.MemEccBitsPresent, bits)).Verdict);

    [Fact]
    public void 記憶體五條規則_缺任一依賴都由引擎判為無法判定()
    {
        var empty = VerifyEngine.Run(new VerifyFacts([]));
        Assert.All(empty.Where(x => x.Id.StartsWith("R-MEM-")),
            x => Assert.Equal(VerifyVerdict.Unread, x.Verdict));
        Assert.Equal(5, empty.Count(x => x.Id.StartsWith("R-MEM-")));
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

    // ── R-CPU-06:ring0 逐核微碼版本一致性(唯讀 MSR 0x8B)──

    [Theory]
    [InlineData("0x2F|0x2F|0x2F|0x2F", VerifyVerdict.Match)]      // 全核同版
    [InlineData("0x2F", VerifyVerdict.Match)]                    // 單核也算一致
    [InlineData("0x2F|0x2F|0x2E|0x2F", VerifyVerdict.Conflict)]  // 一核落後 = 載入失敗/竄改
    public void R_CPU_06_逐核微碼一致性(string revs, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-CPU-06", Text(FactId.CpuMicrocodePerCore, revs)).Verdict);

    [Fact]
    public void R_CPU_06_不一致時判定為較嚴重且附成因()
    {
        var f = One("R-CPU-06", Text(FactId.CpuMicrocodePerCore, "0x2F|0x2E"));
        Assert.Equal(Severity.Serious, f.Severity);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));
    }

    [Fact]
    public void R_CPU_06_讀不到微碼時由引擎判無法判定()
        => Assert.Equal(VerifyVerdict.Unread, One("R-CPU-06", Num(FactId.DimmCount, 1)).Verdict);

    // ── R-CPU-02：工程樣品跡象（品牌字串含 ES／QS，或 "Genuine Intel CPU 0000"）──

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-7980XE CPU @ 2.60GHz", VerifyVerdict.Match)]  // XE 不是 ES
    [InlineData("Genuine Intel(R) CPU 0000 @ 2.00GHz", VerifyVerdict.Conflict)]     // 0000 = 工程樣品
    [InlineData("Intel(R) Xeon(R) CPU E5-2699 ES", VerifyVerdict.Conflict)]         // ES = engineering sample
    [InlineData("Intel(R) Xeon(R) Gold 6248 QS", VerifyVerdict.Conflict)]           // QS = qualification sample
    public void R_CPU_02_工程樣品標記(string brand, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-CPU-02", Text(FactId.CpuBrandString, brand)).Verdict);

    [Fact]
    public void R_CPU_02_矛盾時附正當成因()
        => Assert.False(string.IsNullOrWhiteSpace(
            One("R-CPU-02", Text(FactId.CpuBrandString, "Genuine Intel(R) CPU 0000")).BenignCause));

    // ── R-CPU-03：快取層級異常（L3 為 0；非混合架構時 L2 總量須能被核心數整除）──

    [Theory]
    [InlineData(25952256, 18874368, 18, 0, VerifyVerdict.Match)]      // 本機：L3 24.75M、L2 18M÷18核=1M
    [InlineData(0, 18874368, 18, 0, VerifyVerdict.Conflict)]          // L3=0：桌機／HEDT 不該沒有 L3
    [InlineData(25952256, 10000000, 18, 0, VerifyVerdict.Conflict)]   // 非混合但 L2 不能被核心數整除
    [InlineData(0, 18874368, 18, 1, VerifyVerdict.Conflict)]          // 混合架構仍查 L3=0
    [InlineData(25952256, 10000000, 18, 1, VerifyVerdict.Match)]      // 混合架構：P/E 核 L2 不同，不查整除
    public void R_CPU_03_快取層級異常(double l3, double l2Total, double cores, double hybrid, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-CPU-03",
            Num(FactId.CpuL3Bytes, l3), Num(FactId.CpuL2TotalBytes, l2Total),
            Num(FactId.CpuPhysicalCores, cores), Num(FactId.CpuIsHybrid, hybrid)).Verdict);

    // ── R-CPU-04：虛擬層存在時，以下 MSR／CPUID 讀值不可全信 ──

    [Theory]
    [InlineData(0, VerifyVerdict.Match)]
    [InlineData(1, VerifyVerdict.Conflict)]
    public void R_CPU_04_虛擬層存在(double present, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-CPU-04", Num(FactId.HypervisorPresent, present)).Verdict);

    // ── R-CPU-05：矽晶倍頻推算的基礎頻率 vs 宣稱基礎頻率（改標剋星）──

    [Theory]
    [InlineData(2600, 2600, VerifyVerdict.Match)]      // 完全一致
    [InlineData(2591, 2600, VerifyVerdict.Match)]      // BCLK 量測誤差內（本機實值）
    [InlineData(2600, 3600, VerifyVerdict.Conflict)]   // 宣稱基頻遠高於矽晶倍頻能支撐的
    public void R_CPU_05_矽晶基頻與宣稱基頻(double silicon, double claimed, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-CPU-05",
            Num(FactId.CpuSiliconBaseMhz, silicon, "MHz"),
            Num(FactId.CpuBrandClaimedMhz, claimed, "MHz")).Verdict);

    [Fact]
    public void R_CPU_05_矛盾時附證據與正當成因()
    {
        var f = One("R-CPU-05", Num(FactId.CpuSiliconBaseMhz, 2600, "MHz"),
            Num(FactId.CpuBrandClaimedMhz, 3600, "MHz"));
        Assert.Equal(VerifyVerdict.Conflict, f.Verdict);
        Assert.Equal(2, f.Evidence.Length);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));
    }

    // ── R-LNK-01：現行 PCIe 鏈路寬度低於裝置能力（寬度不足不會自己好；速度低多為閒置省電，不判矛盾）──

    [Theory]
    [InlineData(16, 16, 3, 3, VerifyVerdict.Match)]      // 滿寬滿速
    [InlineData(16, 16, 1, 3, VerifyVerdict.Match)]      // 寬度滿、速度低：閒置降速屬正常，不判矛盾
    [InlineData(8, 16, 3, 3, VerifyVerdict.Conflict)]    // 寬度只有一半：走線／分流／M.2 佔道
    [InlineData(4, 16, 1, 3, VerifyVerdict.Conflict)]    // 寬度不足（速度也低，但判定看寬度）
    public void R_LNK_01_PCIe鏈路寬度(double curW, double maxW, double curS, double maxS, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-LNK-01",
            Num(FactId.PcieCurWidth, curW), Num(FactId.PcieMaxWidth, maxW),
            Num(FactId.PcieCurSpeed, curS), Num(FactId.PcieMaxSpeed, maxS)).Verdict);

    [Fact]
    public void R_LNK_01_寬度不足時附四項證據與正當成因()
    {
        var f = One("R-LNK-01", Num(FactId.PcieCurWidth, 8), Num(FactId.PcieMaxWidth, 16),
            Num(FactId.PcieCurSpeed, 3), Num(FactId.PcieMaxSpeed, 3));
        Assert.Equal(VerifyVerdict.Conflict, f.Verdict);
        Assert.Equal(4, f.Evidence.Length);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));
    }
}
