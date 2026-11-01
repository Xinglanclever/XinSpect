using Xunit;
using XinSpect;
using static XinSpect.SurveillancePlanning;

namespace XinSpect.Tests;

/// <summary>
/// 監控工程規劃的計算核心。這一組測試釘住的是「公式與單位」，不是 UI：
/// 每個期望值都用紙筆算得出來（註解裡留算式），任何一個係數被改動都會紅燈。
/// </summary>
public class SurveillancePlanningTests
{
    private const double Tol = 0.01;

    // ── 錄影量 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 每支攝影機每日錄影量_1Mbps全錄是10點8GB()
    {
        // 1 Mbps × 86400 s ÷ 8 ÷ 1000 = 10.8 GB（十進位）
        Assert.Equal(10.8, RecordingGbPerCameraPerDay(1, 24, 1), 3);
        Assert.Equal(43.2, RecordingGbPerCameraPerDay(4, 24, 1), 3);
    }

    [Fact]
    public void 移動偵測只錄一半時間_不能當成24小時全錄()
    {
        // 4 Mbps × 8 h × 3600 × 0.5 ÷ 8 ÷ 1000 = 7.2 GB
        Assert.Equal(7.2, RecordingGbPerCameraPerDay(4, 8, 0.5), 3);
        // 時數與比例都會被夾在合法範圍，超量輸入不會算出負值或爆量
        Assert.Equal(43.2, RecordingGbPerCameraPerDay(4, 48, 2), 3);
        Assert.Equal(0, RecordingGbPerCameraPerDay(4, -1, 1));
    }

    [Fact]
    public void 碼率為零_錄影量為零而不是例外()
        => Assert.Equal(0, RecordingGbPerCameraPerDay(0, 24, 1));

    // ── RAID 可用顆數 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(6, RaidLevel.Raid0, 0, 6, 6)]
    [InlineData(6, RaidLevel.Raid1, 0, 6, 3)]
    [InlineData(6, RaidLevel.Raid5, 0, 6, 5)]
    [InlineData(6, RaidLevel.Raid6, 0, 6, 4)]
    [InlineData(8, RaidLevel.Raid10, 0, 6, 4)]
    [InlineData(8, RaidLevel.Raid50, 0, 4, 6)]   // 2 組 ×(4−1)
    [InlineData(12, RaidLevel.Raid60, 0, 6, 8)]  // 2 組 ×(6−2)
    [InlineData(6, RaidLevel.Raid5, 1, 6, 4)]    // 熱備援先扣，再扣 parity
    public void RAID可用資料碟數依等級推導(int disks, RaidLevel level, int spares, int group, int expected)
        => Assert.Equal(expected, RaidUsableDisks(disks, level, spares, group));

    [Theory]
    [InlineData(1, RaidLevel.Raid5, 0, 0)]    // 至少要 2 顆
    [InlineData(2, RaidLevel.Raid6, 0, 0)]    // 至少要 3 顆
    [InlineData(3, RaidLevel.Raid10, 0, 0)]   // 至少要 4 顆
    [InlineData(3, RaidLevel.Raid50, 4, 0)]   // 湊不成一組
    [InlineData(1, RaidLevel.Raid5, 1, 0)]    // 熱備援把碟吃光
    public void 組態不成立時回零而不是負數(int disks, RaidLevel level, int spares, int expected)
        => Assert.Equal(expected, RaidUsableDisks(disks, level, spares));

    // ── NVR 容量 ────────────────────────────────────────────────────────────

    [Fact]
    public void NVR容量_八路4Mbps三十天_RAID5四顆4TB剛好超過需求()
    {
        // 每日 43.2×8 = 345.6 GB；30 天 = 10.368 TB
        // RAID5 4 顆 → 3 顆可用 ×4 TB = 12 TB → 12,000 ÷ 345.6 ≈ 34.7 天
        var r = PlanNvr(new NvrPlan(8, 4, 24, 1.0, 30, 4, 4, RaidLevel.Raid5, 0, 6, 0));

        Assert.Equal(43.2, r.GbPerCameraPerDay, 3);
        Assert.Equal(345.6, r.TotalGbPerDay, 3);
        Assert.Equal(10.368, r.RawTb, 3);
        Assert.Equal(12, r.UsableTb, 3);
        Assert.Equal(34.72, r.DaysAchievable, 2);
        Assert.Equal(0, r.ShortfallTb, 3);
        Assert.False(r.Attention);
        Assert.Contains("足夠", r.Verdict);
    }

    [Fact]
    public void NVR容量不足時要說出差多少TB與實際可錄幾天()
    {
        // 需求 10.368 TB；RAID5 2 顆 4 TB → 1 顆可用 ×4 = 4 TB → 差 6.368 TB
        var r = PlanNvr(new NvrPlan(8, 4, 24, 1.0, 30, 2, 4, RaidLevel.Raid5, 0, 6, 0));

        Assert.True(r.Attention);
        Assert.Equal(4, r.UsableTb, 3);
        Assert.Equal(6.368, r.ShortfallTb, 3);
        Assert.Contains("不足", r.Verdict);
        Assert.Contains("11.6", r.Verdict);   // 4,000 ÷ 345.6 ≈ 11.6 天
    }

    [Fact]
    public void 檔案系統保留會從可用容量扣掉()
    {
        // 3 顆可用 ×4 TB × 0.95 = 11.4 TB
        var r = PlanNvr(new NvrPlan(8, 4, 24, 1.0, 30, 4, 4, RaidLevel.Raid5, 0, 6, 5));
        Assert.Equal(11.4, r.UsableTb, 3);
    }

    [Fact]
    public void NVR輸入不足時回零並說明_不丟例外()
    {
        var r = PlanNvr(new NvrPlan(0, 0, 0, 0, 0, 0, 0, RaidLevel.Raid5, 0, 6, 0));
        Assert.Equal(0, r.DaysAchievable);
        Assert.Contains("輸入不足", r.Verdict);
    }

    // ── 接入頻寬 ────────────────────────────────────────────────────────────

    [Fact]
    public void 接入頻寬含協定開銷_八路4Mbps加15趴是36點8()
    {
        var r = PlanIngress(new IngressPlan(8, 4, 15, 100, 1));
        Assert.Equal(32, r.PayloadMbps, 3);
        Assert.Equal(36.8, r.TotalMbps, 3);
        Assert.Equal(3.68, r.UplinkUtilizationPercent, 2);
        Assert.False(r.Attention);
        Assert.Contains("足夠", r.Verdict);
    }

    [Fact]
    public void 超過NVR接入能力要指名是NVR而不是上行()
    {
        var r = PlanIngress(new IngressPlan(8, 4, 15, 20, 10));
        Assert.True(r.Attention);
        Assert.Contains("NVR", r.Verdict);
        Assert.DoesNotContain("上行線速", r.Verdict);
    }

    [Fact]
    public void 上行使用率達到八成就要提醒()
    {
        var r = PlanIngress(new IngressPlan(8, 4, 15, 1000, 0.046));
        Assert.True(r.Attention);
        Assert.Contains("接近上限", r.Verdict);
    }

    // ── PoE 預算 ────────────────────────────────────────────────────────────

    [Fact]
    public void PoE預算含保留_八路12點95瓦加20趴是124點32()
    {
        var r = PlanPoe(new PoePlan(8, 12.95, 8, 120, PoeStandard.At, 20));
        Assert.Equal(103.6, r.LoadWatts, 3);
        Assert.Equal(124.32, r.WithReserveWatts, 3);
        Assert.Equal(-4.32, r.HeadroomWatts, 3);
        Assert.True(r.Attention);
        Assert.Contains("不足", r.Verdict);
    }

    [Fact]
    public void 每機瓦數超過PD上限要單獨指出而不是只扣總預算()
    {
        // 30 W 的需求在 802.3at 只能算 25.5 W（PD 端），這不是「預算不夠」而是「規格不對」
        var r = PlanPoe(new PoePlan(4, 30, 8, 500, PoeStandard.At, 0));
        Assert.Equal(4, r.OverClassCount);
        Assert.True(r.Attention);
        Assert.Contains("PD 上限", r.Verdict);
    }

    [Fact]
    public void PoE埠數不足要說少幾埠()
    {
        var r = PlanPoe(new PoePlan(24, 5, 16, 500, PoeStandard.Af, 0));
        Assert.Equal(8, r.PortsShort);
        Assert.Contains("埠數不足 8", r.Verdict);
    }

    [Theory]
    [InlineData(PoeStandard.Af, 12.95, 15.4, 2)]
    [InlineData(PoeStandard.At, 25.5, 30.0, 2)]
    [InlineData(PoeStandard.BtType3, 51.0, 60.0, 4)]
    [InlineData(PoeStandard.BtType4, 71.3, 90.0, 4)]
    public void PoE標準的PD與PSE上限與線對數要對得上規格(
        PoeStandard s, double pd, double pse, int pairs)
    {
        Assert.Equal(pd, PoePdMaxWatts(s), 2);
        Assert.Equal(pse, PoePseMaxWatts(s), 2);
        Assert.Equal(pairs, PoePairs(s));
    }

    // ── 交換機背板與包轉發 ──────────────────────────────────────────────────

    [Fact]
    public void 背板是全雙工_24埠1G要48Gbps與35點7Mpps()
    {
        // 24 × 1 × 2 = 48 Gbps；24 × 1 × 1.488 = 35.712 Mpps
        var r = PlanSwitch(new SwitchPlan(24, 1, 2, 1, 56, 41.66));
        Assert.Equal(48, r.RequiredBackplaneGbps, 3);
        Assert.Equal(35.712, r.RequiredMpps, 3);
        Assert.False(r.BackplaneShort);
        Assert.False(r.MppsShort);
        Assert.Equal(12, r.UplinkOversubscription, 2);   // 24 ÷ 2
        Assert.True(r.Attention);                        // 超訂 ≥ 4 要提醒
        Assert.Contains("超訂", r.Verdict);
    }

    [Fact]
    public void 標示規格低於需求要分別指出背板與包轉發()
    {
        var r = PlanSwitch(new SwitchPlan(24, 1, 2, 1, 20, 10));
        Assert.True(r.BackplaneShort);
        Assert.True(r.MppsShort);
        Assert.Contains("背板", r.Verdict);
        Assert.Contains("包轉發", r.Verdict);
    }

    [Fact]
    public void 沒填標示規格時只算需求_不假裝判斷得出來()
    {
        var r = PlanSwitch(new SwitchPlan(8, 2.5, 1, 10, 0, 0));
        Assert.False(r.BackplaneShort);
        Assert.Contains("未填", r.Verdict);
    }

    // ── 光纖衰減 ────────────────────────────────────────────────────────────

    [Fact]
    public void 光纖衰減_兩公里單模1310加四接頭兩熔接是3點0dB()
    {
        // 2×0.35 + 4×0.5 + 2×0.15 = 0.7 + 2.0 + 0.3 = 3.0 dB；預算 0 −(−20)=20 dB
        var r = PlanFiber(new FiberPlan(2, FiberWavelength.Nm1310, 0, 4, 2, 0, -20));
        Assert.Equal(0.7, r.AttenuationDb, 3);
        Assert.Equal(2.0, r.ConnectorLossDb, 3);
        Assert.Equal(0.3, r.SpliceLossDb, 3);
        Assert.Equal(20, r.LinkBudgetDb, 3);
        Assert.Equal(17, r.MarginDb, 3);
        Assert.False(r.Attention);
    }

    [Fact]
    public void 長距離單模裕度不足要說換單模或縮短_而28公里1310本來就夠()
    {
        // 30 km ×0.35 = 10.5 + 2.0 + 0.3 = 12.8；預算 8 → 裕度 −4.8
        var bad = PlanFiber(new FiberPlan(30, FiberWavelength.Nm1310, 0, 4, 2, 0, -8));
        Assert.True(bad.Attention);
        Assert.Contains("裕度不足", bad.Verdict);
    }

    [Fact]
    public void 波長預設係數_多模850最壞3點5_單模1550是0點22()
    {
        Assert.Equal(3.5, DefaultAttenuationDbPerKm(FiberWavelength.Nm850));
        Assert.Equal(0.35, DefaultAttenuationDbPerKm(FiberWavelength.Nm1310));
        Assert.Equal(0.22, DefaultAttenuationDbPerKm(FiberWavelength.Nm1550));
    }

    // ── 攝影機壓降 ──────────────────────────────────────────────────────────

    [Fact]
    public void 壓降_57V25點5W100米24AWG兩對_迴路8點75歐姆()
    {
        // R_loop = 2 × 100 × 0.0875 ÷ 2 = 8.75 Ω
        // V = (57 + √(57² − 4×25.5×8.75)) ÷ 2 = (57 + 48.544) ÷ 2 ≈ 52.77 V
        var r = PlanDrop(new DropPlan(57, 25.5, 100, CopperOhmPerMeter(24), 2, 36));
        Assert.Equal(8.75, r.LoopResistanceOhm, 3);
        Assert.Equal(52.77, r.CameraVoltage, 2);
        Assert.Equal(4.23, r.DropVolts, 2);
        Assert.False(r.Attention);
        Assert.Contains("可接受", r.Verdict);
    }

    [Fact]
    public void 長線讓PSE超過30W_即使攝影機端電壓還夠也要提醒()
    {
        // 300 m：R=26.25 Ω，V≈40.45 V（仍高於 36 V），但 PSE 需 ≈35.9 W > 30 W
        var r = PlanDrop(new DropPlan(57, 25.5, 300, CopperOhmPerMeter(24), 2, 36));
        Assert.True(r.CameraVoltage >= 36);
        Assert.True(r.PseWatts > 30);
        Assert.True(r.Attention);
        Assert.Contains("30 W", r.Verdict);
    }

    [Fact]
    public void 線太長或太細_二次式無解時要說供不起而不是給一個假電壓()
    {
        var r = PlanDrop(new DropPlan(57, 25.5, 1000, CopperOhmPerMeter(24), 2, 36));
        Assert.True(r.Attention);
        Assert.Equal(0, r.CameraVoltage);
        Assert.Contains("供不起", r.Verdict);
    }

    [Fact]
    public void 四對線的迴路電阻是兩對的一半()
    {
        double two = PlanDrop(new DropPlan(57, 60, 100, 0.0875, 2, 42.5)).LoopResistanceOhm;
        double four = PlanDrop(new DropPlan(57, 60, 100, 0.0875, 4, 42.5)).LoopResistanceOhm;
        Assert.Equal(two / 2, four, 6);
    }

    // ── 佈線用線量 ──────────────────────────────────────────────────────────

    [Fact]
    public void 佈線量_12路40米加2米預留與10趴餘裕_要買兩箱()
    {
        // 12 × 42 × 1.1 = 554.4 m → ceil(554.4 ÷ 305) = 2 箱（610 m）
        var r = PlanCabling(new CablingPlan(12, 40, 10, 305, 2));
        Assert.Equal(554.4, r.TotalMeters, 2);
        Assert.Equal(2, r.Boxes);
        Assert.Equal(610, r.BoxTotalMeters, 2);
        Assert.Equal(55.6, r.SpareMeters, 2);
        Assert.Equal(90.9, r.UtilizationPercent, 1);
        Assert.False(r.Attention);
    }

    [Fact]
    public void 平均長度超過90米要提醒水平通道上限()
    {
        var r = PlanCabling(new CablingPlan(4, 95, 10, 305, 2));
        Assert.True(r.Attention);
        Assert.Contains("90 m", r.Verdict);
    }

    [Fact]
    public void 整箱利用率偏低要如實說剩多少()
    {
        var r = PlanCabling(new CablingPlan(2, 30, 0, 305, 0));
        Assert.Equal(1, r.Boxes);
        Assert.Contains("利用率", r.Verdict);
    }

    // ── RAID 可用容量 ───────────────────────────────────────────────────────

    [Fact]
    public void RAID6八顆8TB一顆熱備援_可用40TB效率62點5趴()
    {
        // 8−1=7 → 7−2=5 顆 × 8 TB = 40 TB；原始 64 TB
        var r = PlanRaid(new RaidPlan(8, 8, RaidLevel.Raid6, 1, 6, 0));
        Assert.Equal(64, r.RawTb, 3);
        Assert.Equal(40, r.UsableTb, 3);
        Assert.Equal(5, r.UsableDisks);
        Assert.Equal(2, r.FaultTolerance);
        Assert.Equal(62.5, r.EfficiencyPercent, 2);
        Assert.False(r.Attention);
    }

    [Fact]
    public void RAID組態不成立時可用容量是零_但原始容量照算()
    {
        var r = PlanRaid(new RaidPlan(2, 8, RaidLevel.Raid6, 0, 6, 0));
        Assert.Equal(16, r.RawTb, 3);
        Assert.Equal(0, r.UsableTb, 3);
        Assert.True(r.Attention);
        Assert.Contains("不足以", r.Verdict);
    }

    // ── 無線覆蓋 ────────────────────────────────────────────────────────────

    [Fact]
    public void 無線AP數取頻寬與覆蓋的較大者_覆蓋瓶頸要說是覆蓋()
    {
        // 20 路 × 8 Mbps = 160 Mbps ÷ 70 = 3 台；5,000 m² ÷ (π×25²×0.75)=1,472.6 → 4 台
        var r = PlanWireless(new WirelessPlan(20, 8, 70, 25, 5000, 25));
        Assert.Equal(3, r.ApsByCapacity);
        Assert.Equal(4, r.ApsByArea);
        Assert.Equal(4, r.ApsRequired);
        Assert.Contains("覆蓋瓶頸", r.Verdict);
    }

    [Fact]
    public void 頻寬瓶頸時要說是頻寬()
    {
        // 10 路 × 10 Mbps = 100 ÷ 70 = 2 台；小面積只需 1 台
        var r = PlanWireless(new WirelessPlan(10, 10, 70, 30, 500, 20));
        Assert.Equal(2, r.ApsRequired);
        Assert.Contains("頻寬瓶頸", r.Verdict);
    }

    [Fact]
    public void 無線輸入不足時回零_不給一個看起來合理的台數()
    {
        var r = PlanWireless(new WirelessPlan(0, 0, 0, 0, 0, 0));
        Assert.Equal(0, r.ApsRequired);
        Assert.Contains("輸入不足", r.Verdict);
    }

    // ── UPS 與機櫃 ──────────────────────────────────────────────────────────

    [Fact]
    public void UPS續航_900Wh九十趴效率八十趴放電深度150W是259分鐘()
    {
        // 900 × 0.9 × 0.8 = 648 Wh ÷ 150 W × 60 = 259.2 min
        var r = PlanUps(new UpsPlan(150, 900, 90, 80));
        Assert.Equal(648, r.UsableWh, 3);
        Assert.Equal(259.2, r.RuntimeMinutes, 2);
        Assert.False(r.Attention);
    }

    [Fact]
    public void UPS續航不到五分鐘要提醒不足以安全關機()
    {
        var r = PlanUps(new UpsPlan(800, 90, 90, 80));
        Assert.True(r.Attention);
        Assert.Contains("不足以", r.Verdict);
    }

    [Fact]
    public void 機櫃U數_裝置加保留超過可用U要如實說超出多少()
    {
        var ok = PlanRack(20, 4, 24);
        Assert.Equal(24, ok.UsedU);
        Assert.Equal(0, ok.FreeU);
        Assert.False(ok.Attention);

        var over = PlanRack(30, 4, 24);
        Assert.True(over.Attention);
        Assert.Contains("超出 10 U", over.Verdict);
    }

    // ── 共通契約 ────────────────────────────────────────────────────────────

    [Fact]
    public void 所有計算器對全零輸入都只回輸入不足_不丟例外()
    {
        Assert.Contains("輸入不足", PlanNvr(default).Verdict);
        Assert.Contains("輸入不足", PlanIngress(default).Verdict);
        Assert.Contains("輸入不足", PlanPoe(default).Verdict);
        Assert.Contains("輸入不足", PlanSwitch(default).Verdict);
        Assert.Contains("輸入不足", PlanFiber(default).Verdict);
        Assert.Contains("輸入不足", PlanDrop(default).Verdict);
        Assert.Contains("輸入不足", PlanCabling(default).Verdict);
        Assert.Contains("輸入不足", PlanRaid(default).Verdict);
        Assert.Contains("輸入不足", PlanWireless(default).Verdict);
        Assert.Contains("輸入不足", PlanUps(default).Verdict);
    }
}
