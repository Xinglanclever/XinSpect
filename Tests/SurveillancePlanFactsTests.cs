using System.IO;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 監控規劃存檔與事實（3.13）：存的是輸入、算的是結果；
/// 沒有存檔就不產鍵，存了就要在事實集裡看得到同一組數字。
/// </summary>
public class SurveillancePlanFactsTests
{
    private static SurveillancePlanDocument Doc() => new(
        Cameras: 8, BitrateMbps: 4, HoursPerDay: 24, RecordingDuty: 1,
        RetentionDays: 30, NvrDisks: 4, NvrDiskTb: 4, NvrRaidIndex: 2, NvrSpares: 0, NvrFsPercent: 5,
        ProtocolOverheadPercent: 15, NvrIngestMbps: 200, UplinkGbps: 1,
        PoeWatts: 12.95, PoePorts: 8, PoeBudgetWatts: 120, PoeStandardIndex: 1, PoeReservePercent: 20,
        SwitchPorts: 24, SwitchSpeedGbps: 1, SwitchUplinkPorts: 2, SwitchUplinkGbps: 1,
        SwitchRatedBackplaneGbps: 56, SwitchRatedMpps: 41.66,
        FiberKm: 2, FiberWavelengthIndex: 1, FiberConnectors: 4, FiberSplices: 2, FiberTxDbm: 0, FiberRxDbm: -20,
        DropVoltage: 57, DropWatts: 25.5, DropMeters: 100, DropAwgIndex: 1, DropPairs: 2, DropMinVoltage: 36,
        CablingRuns: 12, CablingAverageMeters: 40, CablingSlackPercent: 10, CablingBoxMeters: 305, CablingLoopMeters: 2,
        RaidDisks: 8, RaidDiskTb: 8, RaidLevelIndex: 3, RaidSpares: 1, RaidGroupSize: 6, RaidFsPercent: 5,
        WirelessPerApMbps: 400, WirelessRadiusMeters: 30, WirelessAreaSquareMeters: 2000, WirelessOverlapPercent: 20,
        UpsLoadWatts: 150, UpsBatteryWh: 900, UpsEfficiencyPercent: 90, UpsDepthOfDischargePercent: 80,
        RackDeviceU: 20, RackReservedU: 4, RackTotalU: 24,
        SavedAtUtc: "2026-11-01 12:00:00");

    [Fact]
    public void 沒有存檔時不產任何鍵_不是產一堆零()
        => Assert.Empty(SurveillancePlanningFactsService.Collect(null, DateTimeOffset.UtcNow));

    [Fact]
    public void 存檔後的事實鍵都是合法鍵_而且全部落在plan家族()
    {
        var facts = SurveillancePlanningFactsService.Collect(Doc(), DateTimeOffset.UtcNow);
        Assert.Equal(16, facts.Count);
        foreach (var f in facts)
        {
            Assert.StartsWith("plan.cctv.", f.Key);
            Assert.Null(KeyCatalogPolicy.CheckKeyName(f.Key));
            Assert.Equal(FactAvailability.Present, f.Availability);
            Assert.Equal(FactTrustLevel.Derived, f.Trust);
            Assert.Contains("非量測", f.Source);
        }
    }

    [Fact]
    public void 存檔後的事實值與計算核心同一組數字()
    {
        var at = DateTimeOffset.UtcNow;
        var facts = SurveillancePlanningFactsService.Collect(Doc(), at)
            .ToDictionary(f => f.Key, f => f, StringComparer.Ordinal);

        // 8 路 × 4 Mbps、30 天、RAID5 4 顆 4 TB、檔案系統保留 5% → 11.4 TB、約 33 天
        Assert.Equal(11.4, facts["plan.cctv.nvr.usable_tb"].NumericValue!.Value, 2);
        Assert.Equal(33.0, facts["plan.cctv.nvr.days"].NumericValue!.Value, 1);
        Assert.Equal(0, facts["plan.cctv.nvr.shortfall_tb"].NumericValue!.Value, 2);
        Assert.Equal(36.8, facts["plan.cctv.ingress.mbps"].NumericValue!.Value, 1);
        Assert.Equal(124.3, facts["plan.cctv.poe.load_w"].NumericValue!.Value, 1);
        Assert.Equal(-4.3, facts["plan.cctv.poe.headroom_w"].NumericValue!.Value, 1);
        Assert.Equal(48, facts["plan.cctv.switch.backplane_gbps"].NumericValue!.Value, 1);
        Assert.Equal(35.71, facts["plan.cctv.switch.mpps"].NumericValue!.Value, 2);
        Assert.Equal(17, facts["plan.cctv.fiber.margin_db"].NumericValue!.Value, 1);
        Assert.Equal(52.8, facts["plan.cctv.drop.camera_v"].NumericValue!.Value, 1);
        Assert.Equal(2, facts["plan.cctv.cabling.boxes"].NumericValue!.Value, 0);
        Assert.Equal(38, facts["plan.cctv.raid.usable_tb"].NumericValue!.Value, 2);
        Assert.Equal(1, facts["plan.cctv.wireless.aps"].NumericValue!.Value, 0);
        Assert.Equal(259.2, facts["plan.cctv.ups.runtime_min"].NumericValue!.Value, 1);
        Assert.Equal(0, facts["plan.cctv.rack.free_u"].NumericValue!.Value, 0);
    }

    [Fact]
    public void 存檔可來回讀寫_壞檔視為沒有存檔()
    {
        string path = Path.Combine(Path.GetTempPath(), $"plan-{Guid.NewGuid():N}.json");
        SurveillancePlanStore.PathOverride = path;
        SurveillancePlanStore.ResetMemory();
        try
        {
            SurveillancePlanStore.Save(Doc());
            SurveillancePlanStore.ResetMemory();
            var loaded = SurveillancePlanStore.Load();
            Assert.NotNull(loaded);
            Assert.Equal(8, loaded!.Cameras);
            Assert.Equal(30, loaded.RetentionDays);

            File.WriteAllText(path, "{ this is not json");
            SurveillancePlanStore.ResetMemory();
            Assert.Null(SurveillancePlanStore.Load());
        }
        finally
        {
            SurveillancePlanStore.PathOverride = null;
            SurveillancePlanStore.ResetMemory();
            try { File.Delete(path); } catch { }
        }
    }
}
