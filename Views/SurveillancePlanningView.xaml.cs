using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using XinSpect;

namespace XinSpect;

/// <summary>
/// 監控工程規劃頁的後置程式：只做「讀輸入 → 呼叫 <see cref="SurveillancePlanning"/> → 排字」，
/// 計算全部在純函式裡（測試直接打那一支，不必開視窗）。
/// </summary>
public partial class SurveillancePlanningView : UserControl
{
    public SurveillancePlanningView()
    {
        InitializeComponent();
        Loaded += (_, _) => Recalculate();
    }

    private void Recalculate_Click(object sender, RoutedEventArgs e) => Recalculate();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildSummary());
            StatusText.Text = "已複製這份規劃的摘要。";
        }
        catch (Exception ex)
        {
            // 剪貼簿被別的程序鎖住是常見狀況，不讓它把整頁打紅。
            StatusText.Text = "複製失敗：" + ex.Message;
        }
    }

    private void Recalculate()
    {
        int cameras = Int(CameraCount, 0);
        double bitrate = Num(Bitrate, 0);
        double hours = Num(HoursPerDay, 24);
        double duty = Num(Duty, 1);

        ComputeNvr(cameras, bitrate, hours, duty);
        ComputeIngress(cameras, bitrate);
        ComputePoe(cameras);
        ComputeSwitch();
        ComputeFiber();
        ComputeDrop();
        ComputeCabling();
        ComputeRaid();
        ComputeWireless(cameras, bitrate);
        ComputeUps();

        StatusText.Text = "已依目前輸入重算；改完數字再按一次。";
    }

    // ── 各段計算 ────────────────────────────────────────────────────────────

    private void ComputeNvr(int cameras, double bitrate, double hours, double duty)
    {
        var r = SurveillancePlanning.PlanNvr(new(
            cameras, bitrate, hours, duty,
            Int(RetentionDays, 0), Int(NvrDisks, 0), Num(NvrDiskTb, 0),
            RaidFromIndex(NvrRaid.SelectedIndex), Int(NvrSpares, 0), 6, Num(NvrFs, 0)));

        NvrValues.Text = $"每支 {Fmt(r.GbPerCameraPerDay)} GB/日　全場 {Fmt(r.TotalGbPerDay)} GB/日　"
                       + $"原始需求 {Fmt(r.RawTb)} TB　可用 {Fmt(r.UsableTb)} TB（{r.UsableDisks} 顆資料碟）　"
                       + $"可錄 {Fmt(r.DaysAchievable)} 天";
        NvrVerdict.Text = r.Verdict;
        Mark(NvrVerdict, r.Attention);
    }

    private void ComputeIngress(int cameras, double bitrate)
    {
        var r = SurveillancePlanning.PlanIngress(new(
            cameras, bitrate, Num(ProtocolOverhead, 0), Num(NvrIngest, 0), Num(UplinkGbps, 0)));

        IngressValues.Text = $"原始 {Fmt(r.PayloadMbps)} Mbps　含開銷 {Fmt(r.TotalMbps)} Mbps　"
                           + $"上行使用率 {Fmt(r.UplinkUtilizationPercent)}%　NVR 接入使用率 {Fmt(r.NvrUtilizationPercent)}%";
        IngressVerdict.Text = r.Verdict;
        Mark(IngressVerdict, r.Attention);
    }

    private void ComputePoe(int cameras)
    {
        var r = SurveillancePlanning.PlanPoe(new(
            cameras, Num(PoeWatts, 0), Int(PoePorts, 0), Num(PoeBudget, 0),
            PoeFromIndex(PoeStandard.SelectedIndex), Num(PoeReserve, 0)));

        PoeValues.Text = $"PD 負載 {Fmt(r.LoadWatts)} W　含保留 {Fmt(r.WithReserveWatts)} W　"
                       + $"餘裕 {Fmt(r.HeadroomWatts)} W　PD 單埠上限 {Fmt(r.PdMaxWatts)} W";
        PoeVerdict.Text = r.Verdict;
        Mark(PoeVerdict, r.Attention);
    }

    private void ComputeSwitch()
    {
        var r = SurveillancePlanning.PlanSwitch(new(
            Int(SwPorts, 0), Num(SwSpeed, 0), Int(SwUplinkPorts, 0), Num(SwUplinkGbps, 0),
            Num(SwBackplane, 0), Num(SwMpps, 0)));

        SwitchValues.Text = $"全雙工背板需求 {Fmt(r.RequiredBackplaneGbps)} Gbps　包轉發需求 {Fmt(r.RequiredMpps)} Mpps　"
                          + $"上行超訂 {Fmt(r.UplinkOversubscription)}:1";
        SwitchVerdict.Text = r.Verdict;
        Mark(SwitchVerdict, r.Attention);
    }

    private void ComputeFiber()
    {
        var r = SurveillancePlanning.PlanFiber(new(
            Num(FiberKm, 0), WavelengthFromIndex(FiberWavelength.SelectedIndex), 0,
            Int(FiberConnectors, 0), Int(FiberSplices, 0), Num(FiberTx, 0), Num(FiberRx, -20)));

        FiberValues.Text = $"光纜 {Fmt(r.AttenuationDb)} dB ＋ 接頭 {Fmt(r.ConnectorLossDb)} dB ＋ 熔接 {Fmt(r.SpliceLossDb)} dB　"
                         + $"鏈路預算 {Fmt(r.LinkBudgetDb)} dB　裕度 {Fmt(r.MarginDb)} dB（要求 ≥ {Fmt(r.RequiredMarginDb)} dB）";
        FiberVerdict.Text = r.Verdict;
        Mark(FiberVerdict, r.Attention);
    }

    private void ComputeDrop()
    {
        int awg = AwgFromIndex(DropAwg.SelectedIndex);
        var r = SurveillancePlanning.PlanDrop(new(
            Num(DropVoltage, 0), Num(DropWatts, 0), Num(DropMeters, 0),
            SurveillancePlanning.CopperOhmPerMeter(awg), PairsFromIndex(DropPairs.SelectedIndex),
            Num(DropMinVoltage, 36)));

        DropValues.Text = $"迴路電阻 {Fmt(r.LoopResistanceOhm)} Ω　電流 {Fmt(r.CurrentAmps)} A　壓降 {Fmt(r.DropVolts)} V　"
                        + $"攝影機端 {Fmt(r.CameraVoltage)} V　線損 {Fmt(r.CableLossWatts)} W　PSE 需 {Fmt(r.PseWatts)} W";
        DropVerdict.Text = r.Verdict;
        Mark(DropVerdict, r.Attention);
    }

    private void ComputeCabling()
    {
        var r = SurveillancePlanning.PlanCabling(new(
            Int(CabRuns, 0), Num(CabAvg, 0), Num(CabSlack, 0), Num(CabBox, 305), Num(CabLoop, 0)));

        CablingValues.Text = $"總長 {Fmt(r.TotalMeters)} m　需要 {r.Boxes} 箱（{Fmt(r.BoxTotalMeters)} m）　"
                           + $"剩餘 {Fmt(r.SpareMeters)} m　整箱利用率 {Fmt(r.UtilizationPercent)}%";
        CablingVerdict.Text = r.Verdict;
        Mark(CablingVerdict, r.Attention);
    }

    private void ComputeRaid()
    {
        var r = SurveillancePlanning.PlanRaid(new(
            Int(RaidDisks, 0), Num(RaidDiskTb, 0), RaidFromIndex(RaidLevel.SelectedIndex),
            Int(RaidSpares, 0), Int(RaidGroup, 6), Num(RaidFs, 0)));

        RaidValues.Text = $"原始 {Fmt(r.RawTb)} TB　可用 {Fmt(r.UsableTb)} TB（{r.UsableDisks} 顆資料碟）　"
                        + $"效率 {Fmt(r.EfficiencyPercent)}%　容錯 {r.FaultTolerance} 顆";
        RaidVerdict.Text = r.Verdict;
        Mark(RaidVerdict, r.Attention);
    }

    private void ComputeWireless(int cameras, double bitrate)
    {
        var r = SurveillancePlanning.PlanWireless(new(
            cameras, bitrate, Num(WlPerAp, 0), Num(WlRadius, 0), Num(WlArea, 0), Num(WlOverlap, 0)));

        WirelessValues.Text = $"頻寬需要 {r.ApsByCapacity} 台　覆蓋需要 {r.ApsByArea} 台　"
                            + $"合計 {r.ApsRequired} 台　每台覆蓋約 {Fmt(r.AreaPerApSquareMeters)} m²";
        WirelessVerdict.Text = r.Verdict;
        Mark(WirelessVerdict, r.Attention);
    }

    private void ComputeUps()
    {
        var u = SurveillancePlanning.PlanUps(new(
            Num(UpsLoad, 0), Num(UpsWh, 0), Num(UpsEff, 90), Num(UpsDod, 80)));
        var rack = SurveillancePlanning.PlanRack(
            Int(RackDeviceU, 0), Int(RackReservedU, 0), Int(RackTotalU, 0));

        UpsValues.Text = $"可用 {Fmt(u.UsableWh)} Wh　續航 {Fmt(u.RuntimeMinutes)} 分鐘　"
                       + $"機櫃用 {rack.UsedU} U、剩 {rack.FreeU} U";
        UpsVerdict.Text = u.Verdict + "　" + rack.Verdict;
        Mark(UpsVerdict, u.Attention || rack.Attention);
    }

    // ── 摘要 ────────────────────────────────────────────────────────────────

    private string BuildSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"曦覽 XinSpect {AppInfo.ShortVersion} 監控工程規劃（設計試算，非量測）");
        sb.AppendLine();
        foreach (var (title, values, verdict) in new[]
        {
            ("NVR 容量", NvrValues, NvrVerdict),
            ("接入頻寬", IngressValues, IngressVerdict),
            ("PoE 預算", PoeValues, PoeVerdict),
            ("交換機", SwitchValues, SwitchVerdict),
            ("光纖", FiberValues, FiberVerdict),
            ("壓降", DropValues, DropVerdict),
            ("佈線", CablingValues, CablingVerdict),
            ("RAID", RaidValues, RaidVerdict),
            ("無線", WirelessValues, WirelessVerdict),
            ("UPS／機櫃", UpsValues, UpsVerdict),
        })
        {
            sb.AppendLine($"[{title}] {values.Text}");
            sb.AppendLine($"　→ {verdict.Text}");
        }
        return sb.ToString();
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────

    private static void Mark(TextBlock block, bool attention)
        => block.FontWeight = attention ? FontWeights.SemiBold : FontWeights.Normal;

    private static double Num(TextBox box, double fallback)
    {
        string text = box.Text?.Trim().Replace(",", "") ?? "";
        if (text.Length == 0) return fallback;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)) return v;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        return fallback;
    }

    private static int Int(TextBox box, int fallback)
        => (int)Math.Round(Num(box, fallback));

    private static string Fmt(double v) => v.ToString("#,0.##", CultureInfo.CurrentCulture);

    private static SurveillancePlanning.RaidLevel RaidFromIndex(int i) => i switch
    {
        0 => SurveillancePlanning.RaidLevel.Raid0,
        1 => SurveillancePlanning.RaidLevel.Raid1,
        2 => SurveillancePlanning.RaidLevel.Raid5,
        3 => SurveillancePlanning.RaidLevel.Raid6,
        4 => SurveillancePlanning.RaidLevel.Raid10,
        5 => SurveillancePlanning.RaidLevel.Raid50,
        6 => SurveillancePlanning.RaidLevel.Raid60,
        _ => SurveillancePlanning.RaidLevel.Raid5,
    };

    private static SurveillancePlanning.PoeStandard PoeFromIndex(int i) => i switch
    {
        0 => SurveillancePlanning.PoeStandard.Af,
        1 => SurveillancePlanning.PoeStandard.At,
        2 => SurveillancePlanning.PoeStandard.BtType3,
        3 => SurveillancePlanning.PoeStandard.BtType4,
        _ => SurveillancePlanning.PoeStandard.At,
    };

    private static SurveillancePlanning.FiberWavelength WavelengthFromIndex(int i) => i switch
    {
        0 => SurveillancePlanning.FiberWavelength.Nm850,
        1 => SurveillancePlanning.FiberWavelength.Nm1310,
        2 => SurveillancePlanning.FiberWavelength.Nm1550,
        _ => SurveillancePlanning.FiberWavelength.Nm1310,
    };

    /// <summary>下拉選單的順序：26／24／23／22／21／20／18／16 AWG。</summary>
    private static int AwgFromIndex(int i) => i switch
    {
        0 => 26, 1 => 24, 2 => 23, 3 => 22, 4 => 21, 5 => 20, 6 => 18, 7 => 16, _ => 24,
    };

    private static int PairsFromIndex(int i) => i == 1 ? 4 : 2;
}
