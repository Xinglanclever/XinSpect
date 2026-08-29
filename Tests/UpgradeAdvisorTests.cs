using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 升級建議規則引擎測試：驗證兩條鐵則——沒有資料就不下結論、預期效益只寫經驗範圍——
/// 以及各規則的觸發條件、優先度排序與瓶頸判定。純函式，不碰硬體。
/// </summary>
public class UpgradeAdvisorTests
{
    /// <summary>一台「什麼都沒讀到」的機器：所有欄位維持預設。</summary>
    private static UpgradeFacts Blank() => new();

    /// <summary>一台讀值健全、樣本充足、沒有任何問題的機器（作為各規則的對照基準）。</summary>
    private static UpgradeFacts Healthy() => new()
    {
        MemTotalGb = 32,
        MemModules = 2,
        MemChannelsText = "雙通道",
        MemSpeedMhz = 3200,
        MemRatedMhz = 3200,
        MemLoadP95 = 45,
        CpuName = "測試處理器",
        CpuCores = 8,
        CpuThreads = 16,
        CpuLoadP95 = 30,
        CpuTempP95 = 62,
        CpuTempMax = 71,
        GpuName = "測試顯示卡",
        HasGpu = true,
        HasDiscreteGpu = true,
        GpuVramGb = 12,
        GpuLoadP95 = 25,
        GpuTempP95 = 60,
        SystemDiskIsHdd = false,
        DiskCount = 1,
        HddCount = 0,
        SystemFreePercent = 55,
        WorstDiskLife = 97,
        MaxDiskTempC = 42,
        PowerPlan = "平衡",
        HistoryMinutes = 720,
    };

    private static bool Has(UpgradeReport r, string titleFragment)
        => r.Items.Any(i => i.Title.Contains(titleFragment));

    private static UpgradeSuggestion Pick(UpgradeReport r, string titleFragment)
        => r.Items.First(i => i.Title.Contains(titleFragment));

    // ── 鐵則一：沒有資料就不下結論 ────────────────────────────────

    [Fact]
    public void NoData_ProducesNoSuggestions()
    {
        var r = UpgradeAdvisor.Analyze(Blank());
        Assert.False(r.HasItems);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void NoData_BottleneckIsUndecided()
    {
        var r = UpgradeAdvisor.Analyze(Blank());
        Assert.Equal("尚無定論", r.Bottleneck);
        Assert.Equal(Severity.Neutral, r.BottleneckSeverity);
    }

    [Fact]
    public void NoData_ConfidenceSaysWhatIsMissing()
    {
        var c = UpgradeAdvisor.Analyze(Blank()).Confidence;
        Assert.Contains("沒有歷史樣本", c);
        Assert.Contains("未偵測到顯示卡讀值", c);
        Assert.Contains("無法判斷 XMP", c);
        Assert.Contains("磁碟未提供剩餘壽命", c);
        Assert.Contains("未在天梯榜單命中", c);
        Assert.Contains("非本機實測", c);   // 預期效益的性質必須寫明
    }

    [Fact]
    public void HealthyMachine_HasNoSuggestionsAndNoBottleneck()
    {
        var r = UpgradeAdvisor.Analyze(Healthy());
        Assert.False(r.HasItems);
        Assert.Equal("無明顯瓶頸", r.Bottleneck);
        Assert.Equal(Severity.Good, r.BottleneckSeverity);
    }

    [Fact]
    public void MemoryRules_SkippedWhenCapacityUnknown()
    {
        var f = Blank();
        f.MemModules = 1;            // 單支，但容量沒讀到
        f.MemTotalGb = 0;
        Assert.False(Has(UpgradeAdvisor.Analyze(f), "雙通道"));
    }

    [Fact]
    public void LongTermRules_SkippedWhenSampleTooShort()
    {
        var f = Healthy();
        f.HistoryMinutes = 20;       // 少於 30 分鐘
        f.CpuLoadP95 = 99;
        f.GpuLoadP95 = 10;
        Assert.False(f.HasLongTerm);
        Assert.False(Has(UpgradeAdvisor.Analyze(f), "處理器是主要瓶頸"));
    }

    [Fact]
    public void RankingRules_SkippedWhenNotMatched()
    {
        var f = Healthy();
        f.CpuRank = 0; f.CpuRankTotal = 0;
        f.GpuRank = 0; f.GpuRankTotal = 0;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.DoesNotContain(r.Items, i => i.Title.Contains("天梯"));
    }

    // ── 儲存規則 ──────────────────────────────────────────────────

    [Fact]
    public void DyingDisk_OutranksEverythingElse()
    {
        var f = Healthy();
        f.WorstDiskLife = 6;
        f.SystemDiskIsHdd = true;      // 同時有另一條 92 分的建議
        var r = UpgradeAdvisor.Analyze(f);

        var top = r.Items[0];
        Assert.Contains("壽終", top.Title);
        Assert.Equal(Severity.Critical, top.Severity);
        Assert.Equal(100, top.Score);
        Assert.Contains("6", top.Evidence);
        Assert.Equal("磁碟壽命", r.Bottleneck);
    }

    [Fact]
    public void HealthWarning_OnlyWhenLifeIsNotAlreadyCritical()
    {
        var f = Healthy();
        f.DiskHealthWarning = true;
        f.DiskHealthDetail = "測試磁碟：重新配置磁區數偏高";
        var r = UpgradeAdvisor.Analyze(f);
        Assert.Contains("重新配置磁區數偏高", Pick(r, "健康狀態異常").Evidence);

        f.WorstDiskLife = 5;           // 壽命告警優先，健康告警不再重複列
        Assert.False(Has(UpgradeAdvisor.Analyze(f), "健康狀態異常"));
    }

    [Fact]
    public void SystemHdd_SuggestsNvmeAndIsTheBottleneck()
    {
        var f = Healthy();
        f.SystemDiskIsHdd = true;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.True(Has(r, "NVMe 固態硬碟"));
        Assert.Equal("系統碟", r.Bottleneck);
    }

    [Fact]
    public void MixedDisks_SuggestsMovingHotDataOnly()
    {
        var f = Healthy();
        f.DiskCount = 3; f.HddCount = 1; f.SystemDiskIsHdd = false;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.True(Has(r, "搬到固態硬碟"));
        Assert.False(Has(r, "系統碟換成"));
    }

    [Theory]
    [InlineData(6, "接近用盡")]
    [InlineData(15, "偏緊")]
    public void LowFreeSpace_PicksTheRightSeverity(double percent, string fragment)
    {
        var f = Healthy();
        f.SystemFreePercent = percent;
        Assert.True(Has(UpgradeAdvisor.Analyze(f), fragment));
    }

    [Fact]
    public void UnknownFreeSpace_SaysNothing()
    {
        var f = Healthy();
        f.SystemFreePercent = -1;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.False(Has(r, "接近用盡"));
        Assert.False(Has(r, "偏緊"));
    }

    [Fact]
    public void HotDisk_SuggestsHeatsink()
    {
        var f = Healthy();
        f.MaxDiskTempC = 72;
        Assert.Contains("72", Pick(UpgradeAdvisor.Analyze(f), "散熱片").Evidence);
    }

    // ── 記憶體規則 ────────────────────────────────────────────────

    [Fact]
    public void EightGigabytes_SuggestsSixteen()
    {
        var f = Healthy();
        f.MemTotalGb = 8; f.MemModules = 2;
        var s = Pick(UpgradeAdvisor.Analyze(f), "加到 16 GB");
        Assert.Equal(72, s.Score);                  // 沒有長期壓力時的分數
        Assert.Equal(Severity.Warning, s.Severity);
    }

    [Fact]
    public void EightGigabytesUnderPressure_ScoresHigher()
    {
        var f = Healthy();
        f.MemTotalGb = 8; f.MemModules = 2; f.MemLoadP95 = 91;
        var s = Pick(UpgradeAdvisor.Analyze(f), "加到 16 GB");
        Assert.Equal(90, s.Score);
        Assert.Equal(Severity.Serious, s.Severity);
        Assert.Contains("91", s.Evidence);
    }

    [Fact]
    public void SixteenGigabytes_OnlySuggestsThirtyTwoUnderPressure()
    {
        var f = Healthy();
        f.MemTotalGb = 16; f.MemModules = 2; f.MemLoadP95 = 50;
        Assert.False(Has(UpgradeAdvisor.Analyze(f), "加到 32 GB"));

        f.MemLoadP95 = 89;
        Assert.True(Has(UpgradeAdvisor.Analyze(f), "加到 32 GB"));
    }

    [Fact]
    public void SingleModule_WeighsHeavierWithoutDiscreteGpu()
    {
        var f = Healthy();
        f.MemModules = 1; f.MemTotalGb = 16;

        f.HasDiscreteGpu = true;
        var withGpu = Pick(UpgradeAdvisor.Analyze(f), "雙通道");
        Assert.Equal(74, withGpu.Score);
        Assert.Equal(Severity.Warning, withGpu.Severity);

        f.HasDiscreteGpu = false;
        var onIgpu = Pick(UpgradeAdvisor.Analyze(f), "雙通道");
        Assert.Equal(86, onIgpu.Score);
        Assert.Equal(Severity.Serious, onIgpu.Severity);
        Assert.Contains("內顯", onIgpu.Gain);
    }

    [Fact]
    public void UnderclockedMemory_SuggestsXmp()
    {
        var f = Healthy();
        f.MemSpeedMhz = 2133; f.MemRatedMhz = 3200;
        var s = Pick(UpgradeAdvisor.Analyze(f), "XMP");
        Assert.Equal("免費", s.Cost);
        Assert.Contains("2133", s.Evidence);
        Assert.Contains("3200", s.Evidence);
    }

    [Fact]
    public void MemoryAtRatedSpeed_SaysNothingAboutXmp()
    {
        var f = Healthy();
        f.MemSpeedMhz = 3200; f.MemRatedMhz = 3200;
        Assert.False(Has(UpgradeAdvisor.Analyze(f), "XMP"));
    }

    // ── 散熱規則 ──────────────────────────────────────────────────

    [Fact]
    public void Throttling_ComesBeforeAnyHardwareSwap()
    {
        var f = Healthy();
        f.ThrottleSeen = true;
        f.CpuTempMax = 100;
        var r = UpgradeAdvisor.Analyze(f);
        var s = Pick(r, "熱降頻");
        Assert.Equal(94, s.Score);
        Assert.Contains("100", s.Evidence);
        Assert.Equal("散熱", r.Bottleneck);
    }

    [Fact]
    public void Throttling_AdvisesDifferentlyOnLaptops()
    {
        var f = Healthy();
        f.ThrottleSeen = true;

        f.IsLaptop = false;
        Assert.Contains("機殼風道", Pick(UpgradeAdvisor.Analyze(f), "熱降頻").Action);

        f.IsLaptop = true;
        Assert.Contains("出風口", Pick(UpgradeAdvisor.Analyze(f), "熱降頻").Action);
    }

    [Fact]
    public void HotCpuWithoutThrottling_SuggestsBetterCooling()
    {
        var f = Healthy();
        f.CpuTempP95 = 91;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.True(Has(r, "加強處理器散熱"));
        Assert.False(Has(r, "熱降頻"));
    }

    [Fact]
    public void HotGpu_SuggestsGpuCooling()
    {
        var f = Healthy();
        f.GpuTempP95 = 86;
        Assert.Contains("86", Pick(UpgradeAdvisor.Analyze(f), "顯示卡散熱").Evidence);
    }

    // ── 運算瓶頸規則 ──────────────────────────────────────────────

    [Fact]
    public void IntegratedGraphicsOnly_SuggestsDiscreteCard()
    {
        var f = Healthy();
        f.HasDiscreteGpu = false;
        f.GpuName = "測試內顯";
        var s = Pick(UpgradeAdvisor.Analyze(f), "加一張獨立顯示卡");
        Assert.Contains("測試內顯", s.Evidence);
        Assert.Equal("高", s.Cost);
    }

    [Fact]
    public void NoGpuReading_DoesNotSuggestAddingOne()
    {
        var f = Healthy();
        f.HasGpu = false; f.HasDiscreteGpu = false;
        Assert.False(Has(UpgradeAdvisor.Analyze(f), "加一張獨立顯示卡"));
    }

    [Fact]
    public void CpuSaturatedGpuIdle_BlamesTheCpu()
    {
        var f = Healthy();
        f.CpuLoadP95 = 95; f.GpuLoadP95 = 22;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.True(Has(r, "處理器是主要瓶頸"));
        Assert.False(Has(r, "顯示卡是主要瓶頸"));
        Assert.Equal("處理器", r.Bottleneck);
    }

    [Fact]
    public void GpuSaturatedCpuIdle_BlamesTheGpu()
    {
        var f = Healthy();
        f.GpuLoadP95 = 96; f.CpuLoadP95 = 35;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.True(Has(r, "顯示卡是主要瓶頸"));
        Assert.Equal("顯示卡", r.Bottleneck);
    }

    [Fact]
    public void BothSaturated_ReportsWholeMachineShortfall()
    {
        var f = Healthy();
        f.CpuLoadP95 = 93; f.GpuLoadP95 = 94;
        var r = UpgradeAdvisor.Analyze(f);
        Assert.Contains("同時吃滿", r.Bottleneck);
        Assert.False(Has(r, "處理器是主要瓶頸"));   // 兩邊都滿時不指單一元件
        Assert.False(Has(r, "顯示卡是主要瓶頸"));
    }

    [Fact]
    public void SmallVramUnderLoad_SaysCapacityFirst()
    {
        var f = Healthy();
        f.GpuLoadP95 = 97; f.CpuLoadP95 = 40; f.GpuVramGb = 4;
        var s = Pick(UpgradeAdvisor.Analyze(f), "顯示記憶體偏小");
        Assert.Contains("無法單獨加裝", s.Action);
    }

    // ── 天梯與系統設定 ────────────────────────────────────────────

    [Fact]
    public void LowRankedCpu_MentionsRankAndTotal()
    {
        var f = Healthy();
        f.CpuRank = 900; f.CpuRankTotal = 1000;
        var s = Pick(UpgradeAdvisor.Analyze(f), "處理器在天梯偏後段");
        Assert.Contains("900", s.Evidence);
        Assert.Contains("1000", s.Evidence);
        Assert.Contains("僅供參考", s.Action);
    }

    [Fact]
    public void MidRankedHardware_IsNotFlagged()
    {
        var f = Healthy();
        f.CpuRank = 300; f.CpuRankTotal = 1000;
        f.GpuRank = 250; f.GpuRankTotal = 900;
        Assert.DoesNotContain(UpgradeAdvisor.Analyze(f).Items, i => i.Title.Contains("天梯"));
    }

    [Fact]
    public void PowerSavingPlan_IsFreePerformance()
    {
        var f = Healthy();
        f.PowerPlan = "節能";
        var s = Pick(UpgradeAdvisor.Analyze(f), "電源計劃");
        Assert.Equal("免費", s.Cost);
        Assert.Contains("節能", s.Evidence);
    }

    // ── 排序與呈現 ────────────────────────────────────────────────

    [Fact]
    public void Items_AreSortedByDescendingScore()
    {
        var f = Healthy();
        f.SystemDiskIsHdd = true;
        f.MemTotalGb = 8; f.MemModules = 1;
        f.PowerPlan = "節能";
        f.MemSpeedMhz = 2133; f.MemRatedMhz = 3200;
        var scores = UpgradeAdvisor.Analyze(f).Items.Select(i => i.Score).ToList();

        Assert.True(scores.Count > 3);
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);
    }

    [Theory]
    [InlineData(95, "最優先")]
    [InlineData(72, "建議")]
    [InlineData(50, "可考慮")]
    [InlineData(20, "選配")]
    public void PriorityText_TracksScore(int score, string expected)
    {
        var s = new UpgradeSuggestion
        {
            Part = UpgradePart.System, Title = "測試", Severity = Severity.Neutral, Score = score,
            Gain = "—", Evidence = "—", Action = "—",
        };
        Assert.Equal(expected, s.PriorityText);
    }

    [Theory]
    [InlineData(UpgradePart.Storage, "儲存")]
    [InlineData(UpgradePart.Memory, "記憶體")]
    [InlineData(UpgradePart.Cpu, "處理器")]
    [InlineData(UpgradePart.Gpu, "顯示卡")]
    [InlineData(UpgradePart.Cooling, "散熱")]
    [InlineData(UpgradePart.System, "系統")]
    public void PartText_IsLocalised(UpgradePart part, string expected)
    {
        var s = new UpgradeSuggestion
        {
            Part = part, Title = "測試", Severity = Severity.Neutral, Score = 1,
            Gain = "—", Evidence = "—", Action = "—",
        };
        Assert.Equal(expected, s.PartText);
    }

    [Fact]
    public void EverySuggestion_CarriesEvidenceActionAndGain()
    {
        var f = Healthy();
        f.SystemDiskIsHdd = true;
        f.WorstDiskLife = 4;
        f.MemTotalGb = 8; f.MemModules = 1;
        f.MemSpeedMhz = 2133; f.MemRatedMhz = 3200;
        f.ThrottleSeen = true;
        f.SystemFreePercent = 8;
        f.MaxDiskTempC = 70;
        f.PowerPlan = "節能";
        f.HasDiscreteGpu = false;
        f.CpuRank = 990; f.CpuRankTotal = 1000;

        var r = UpgradeAdvisor.Analyze(f);
        Assert.True(r.Count >= 8);
        foreach (var s in r.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(s.Action));
            Assert.False(string.IsNullOrWhiteSpace(s.Gain));
            Assert.False(string.IsNullOrWhiteSpace(s.Cost));
            Assert.StartsWith("預期效益：", s.GainText);
            Assert.StartsWith("花費：", s.CostText);
        }
    }

    [Theory]
    [InlineData(0, "沒有歷史樣本")]
    [InlineData(45, "約 45 分鐘")]
    [InlineData(180, "約 3 小時")]
    [InlineData(4320, "約 3 天")]
    public void Confidence_DescribesSampleCoverage(int minutes, string fragment)
    {
        var f = Healthy();
        f.HistoryMinutes = minutes;
        Assert.Contains(fragment, UpgradeAdvisor.Analyze(f).Confidence);
    }
}
