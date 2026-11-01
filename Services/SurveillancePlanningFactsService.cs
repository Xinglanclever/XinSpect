using System.Globalization;

namespace XinSpect;

/// <summary>
/// 把「監控規劃」的存檔算成一組事實（<c>plan.cctv.*</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這不是量測。</b><see cref="FactTrustLevel.Derived"/> 且來源一律寫明
/// 「使用者輸入的設計參數＋公式推導」——報告裡看到這些數字時，讀者必須知道
/// 它們不是從這台機器讀到的。
/// </para>
/// <para>
/// 沒有存檔就<b>不產任何鍵</b>（不是產一堆 0）：使用者沒填過規劃，
/// 「NVR 可用 0 TB」會被讀成「算出來是零」，而不是「沒有這份規劃」。
/// </para>
/// </remarks>
public static class SurveillancePlanningFactsService
{
    private const string Category = "監控規劃";
    private const string Source = "監控規劃頁的使用者輸入 ＋ 公式推導（設計試算，非量測）";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
        => Collect(SurveillancePlanStore.Load(), at);

    public static IReadOnlyList<HardwareFact> Collect(SurveillancePlanDocument? doc, DateTimeOffset at)
    {
        if (doc is null) return [];

        var nvr = SurveillancePlanning.PlanNvr(new(
            doc.Cameras, doc.BitrateMbps, doc.HoursPerDay, doc.RecordingDuty,
            doc.RetentionDays, doc.NvrDisks, doc.NvrDiskTb, doc.NvrRaid, doc.NvrSpares, 6, doc.NvrFsPercent));
        var ingress = SurveillancePlanning.PlanIngress(new(
            doc.Cameras, doc.BitrateMbps, doc.ProtocolOverheadPercent, doc.NvrIngestMbps, doc.UplinkGbps));
        var poe = SurveillancePlanning.PlanPoe(new(
            doc.Cameras, doc.PoeWatts, doc.PoePorts, doc.PoeBudgetWatts, doc.PoeStandard, doc.PoeReservePercent));
        var sw = SurveillancePlanning.PlanSwitch(new(
            doc.SwitchPorts, doc.SwitchSpeedGbps, doc.SwitchUplinkPorts, doc.SwitchUplinkGbps,
            doc.SwitchRatedBackplaneGbps, doc.SwitchRatedMpps));
        var fiber = SurveillancePlanning.PlanFiber(new(
            doc.FiberKm, doc.FiberWavelength, 0, doc.FiberConnectors, doc.FiberSplices,
            doc.FiberTxDbm, doc.FiberRxDbm));
        var drop = SurveillancePlanning.PlanDrop(new(
            doc.DropVoltage, doc.DropWatts, doc.DropMeters,
            SurveillancePlanning.CopperOhmPerMeter(doc.DropAwg), doc.DropPairs, doc.DropMinVoltage));
        var cab = SurveillancePlanning.PlanCabling(new(
            doc.CablingRuns, doc.CablingAverageMeters, doc.CablingSlackPercent,
            doc.CablingBoxMeters, doc.CablingLoopMeters));
        var raid = SurveillancePlanning.PlanRaid(new(
            doc.RaidDisks, doc.RaidDiskTb, doc.RaidLevel, doc.RaidSpares, doc.RaidGroupSize, doc.RaidFsPercent));
        var wireless = SurveillancePlanning.PlanWireless(new(
            doc.Cameras, doc.BitrateMbps, doc.WirelessPerApMbps, doc.WirelessRadiusMeters,
            doc.WirelessAreaSquareMeters, doc.WirelessOverlapPercent));
        var ups = SurveillancePlanning.PlanUps(new(
            doc.UpsLoadWatts, doc.UpsBatteryWh, doc.UpsEfficiencyPercent, doc.UpsDepthOfDischargePercent));
        var (_, rackFree, _, _) = SurveillancePlanning.PlanRack(
            doc.RackDeviceU, doc.RackReservedU, doc.RackTotalU);

        return
        [
            Num("plan.cctv.cameras", "規劃攝影機數", doc.Cameras, "支", at),
            Num("plan.cctv.nvr.usable_tb", "NVR 可用容量", Math.Round(nvr.UsableTb, 2), "TB", at),
            Num("plan.cctv.nvr.days", "NVR 可錄天數", Math.Round(nvr.DaysAchievable, 1), "天", at),
            Num("plan.cctv.nvr.shortfall_tb", "NVR 容量缺口", Math.Round(nvr.ShortfallTb, 2), "TB", at),
            Num("plan.cctv.ingress.mbps", "監控接入負載", Math.Round(ingress.TotalMbps, 1), "Mbps", at),
            Num("plan.cctv.poe.load_w", "PoE 負載（含保留）", Math.Round(poe.WithReserveWatts, 1), "W", at),
            Num("plan.cctv.poe.headroom_w", "PoE 預算餘裕", Math.Round(poe.HeadroomWatts, 1), "W", at),
            Num("plan.cctv.switch.backplane_gbps", "交換機背板需求", Math.Round(sw.RequiredBackplaneGbps, 1), "Gbps", at),
            Num("plan.cctv.switch.mpps", "交換機包轉發需求", Math.Round(sw.RequiredMpps, 2), "Mpps", at),
            Num("plan.cctv.fiber.margin_db", "光纖鏈路裕度", Math.Round(fiber.MarginDb, 1), "dB", at),
            Num("plan.cctv.drop.camera_v", "攝影機端電壓", Math.Round(drop.CameraVoltage, 1), "V", at),
            Num("plan.cctv.cabling.boxes", "佈線需用箱數", cab.Boxes, "箱", at),
            Num("plan.cctv.raid.usable_tb", "RAID 可用容量", Math.Round(raid.UsableTb, 2), "TB", at),
            Num("plan.cctv.wireless.aps", "無線 AP 數量", wireless.ApsRequired, "台", at),
            Num("plan.cctv.ups.runtime_min", "UPS 續航", Math.Round(ups.RuntimeMinutes, 1), "分鐘", at),
            Num("plan.cctv.rack.free_u", "機櫃剩餘 U 數", rackFree, "U", at),
        ];
    }

    private static HardwareFact Num(string key, string name, double value, string unit, DateTimeOffset at)
        => new(key, Category, name, value.ToString("0.##", CultureInfo.InvariantCulture), unit,
            Source, FactTrustLevel.Derived, false, at, value, FactAvailability.Present);
}
