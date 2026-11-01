namespace XinSpect;

/// <summary>
/// 監控工程規劃的純計算核心（v3.13）：NVR 容量、接入頻寬、PoE 預算、交換機背板／包轉發、
/// 光纖衰減、攝影機壓降、佈線用線量、RAID 可用容量、無線覆蓋、UPS 續航與機櫃 U 數。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一支不做任何量測。</b>所有輸入都是使用者宣告的設計參數（頻道數、碼率、線長、瓦數…），
/// 輸出是公式推導的結果。畫面與報告都必須寫明「這是設計推算，不是本機量到的值」——
/// 這個專案對「推測冒充量測」的立場，在這一頁同樣成立。
/// </para>
/// <para>
/// <b>每個數字都要附假設。</b>例如光纖衰減用「接頭 0.5 dB、熔接 0.15 dB」是<b>最壞情況</b>的工程慣例，
/// 不是量到的插入損失；PoE 壓降假設攝影機是定功率負載、線材 20 °C 的銅電阻。
/// 使用者可以覆寫係數，但預設值一律取保守端——寧可算得緊，不要在驗收時才發現饋電不足。
/// </para>
/// <para>
/// 所有方法對不合法輸入<b>不丟例外</b>：回傳零值並在 Verdict 說明哪裡不足，讓畫面能如實顯示，
/// 而不是讓一個 TextBox 的空白把整頁打紅。
/// </para>
/// </remarks>
public static class SurveillancePlanning
{
    /// <summary>
    /// 1 GbE 在 64 bytes 最小訊框下的理論轉發率（Mpps）。
    /// IEEE 802.3：最小訊框 64 bytes ＋ 前導碼/SFD 8 bytes ＋ 訊框間隔 12 bytes ＝ 84 bytes ＝ 672 bits；
    /// 10<sup>9</sup> ÷ 672 ≈ 1.488 Mpps。
    /// </summary>
    public const double MppsPerGbps = 1.488;

    /// <summary>標準網路線箱長（305 m ＝ 1000 ft）。</summary>
    public const double BoxLengthMeters = 305;

    /// <summary>光纖接頭最壞情況插入損失（dB／個）。</summary>
    public const double ConnectorLossDb = 0.5;

    /// <summary>光纖熔接最壞情況損失（dB／處）。</summary>
    public const double SpliceLossDb = 0.15;

    /// <summary>光纖驗收慣例要求的剩餘裕度（dB）。</summary>
    public const double RequiredFiberMarginDb = 3.0;

    // ── 共用 ────────────────────────────────────────────────────────────────

    /// <summary>RAID 等級。RAID 50／60 以 <c>GroupSize</c> 決定每組幾顆。</summary>
    public enum RaidLevel { Raid0, Raid1, Raid5, Raid6, Raid10, Raid50, Raid60 }

    /// <summary>PoE 供電標準；決定 PD 端可用瓦數、PD 最低輸入電壓與使用幾對線。</summary>
    public enum PoeStandard { Af, At, BtType3, BtType4 }

    /// <summary>光纖波長（決定預設衰減係數）。</summary>
    public enum FiberWavelength { Nm850, Nm1310, Nm1550 }

    /// <summary>RAID 等級的顯示名稱。</summary>
    public static string RaidName(RaidLevel level) => level switch
    {
        RaidLevel.Raid0 => "RAID 0（等量）",
        RaidLevel.Raid1 => "RAID 1（鏡像）",
        RaidLevel.Raid5 => "RAID 5（單同位元）",
        RaidLevel.Raid6 => "RAID 6（雙同位元）",
        RaidLevel.Raid10 => "RAID 10（鏡像＋等量）",
        RaidLevel.Raid50 => "RAID 50（RAID 5 群組）",
        RaidLevel.Raid60 => "RAID 60（RAID 6 群組）",
        _ => "—",
    };

    /// <summary>可容忍同時壞幾顆（不含熱備援）。輸入不足時回 0。</summary>
    [SpecRef("RAID parity 推導：RAID 0 無冗餘、RAID 1／10 每組 1 顆、RAID 5／50 每組 1 顆、RAID 6／60 每組 2 顆。"
           + "熱備援不是冗餘，壞掉時才算進去，所以先從總顆數扣除。")]
    public static int RaidFaultTolerance(RaidLevel level) => level switch
    {
        RaidLevel.Raid0 => 0,
        RaidLevel.Raid1 or RaidLevel.Raid10 or RaidLevel.Raid5 or RaidLevel.Raid50 => 1,
        RaidLevel.Raid6 or RaidLevel.Raid60 => 2,
        _ => 0,
    };

    /// <summary>
    /// RAID 可用「資料碟」顆數（不含熱備援、不含檔案系統保留）。
    /// </summary>
    /// <param name="disks">實體碟總數（含熱備援）。</param>
    /// <param name="level">RAID 等級。</param>
    /// <param name="hotSpares">熱備援顆數。</param>
    /// <param name="groupSize">RAID 50／60 的每組顆數（其他等級忽略）。</param>
    /// <remarks>
    /// <b>回 0 代表這個組態不成立</b>（例如 RAID 5 只給 1 顆、RAID 6 只給 2 顆）。
    /// 呼叫端要把 0 當成「無法計算」而不是「零容量」——兩者在畫面上必須長得不一樣。
    /// </remarks>
    public static int RaidUsableDisks(int disks, RaidLevel level, int hotSpares = 0, int groupSize = 6)
    {
        int n = disks - Math.Max(0, hotSpares);
        if (n <= 0) return 0;
        return level switch
        {
            RaidLevel.Raid0 => n,
            RaidLevel.Raid1 => n / 2,
            RaidLevel.Raid5 => n >= 2 ? n - 1 : 0,
            RaidLevel.Raid6 => n >= 3 ? n - 2 : 0,
            RaidLevel.Raid10 => n >= 4 ? n / 2 : 0,
            RaidLevel.Raid50 => RaidGroups(n, groupSize, 1),
            RaidLevel.Raid60 => RaidGroups(n, groupSize, 2),
            _ => 0,
        };
    }

    private static int RaidGroups(int n, int groupSize, int parity)
    {
        int g = Math.Max(3, groupSize);
        int groups = n / g;
        if (groups == 0) return 0;
        return groups * (g - parity);
    }

    // ── 1. NVR 儲存容量 ─────────────────────────────────────────────────────

    public readonly record struct NvrPlan(
        int Cameras, double BitrateMbps, double HoursPerDay, double RecordingDuty,
        int RetentionDays, int Disks, double DiskTb, RaidLevel Level,
        int HotSpares, int GroupSize, double FilesystemOverheadPercent);

    public readonly record struct NvrResult(
        double GbPerCameraPerDay, double TotalGbPerDay, double RawTb,
        double UsableTb, double DaysAchievable, double ShortfallTb,
        int UsableDisks, string Verdict, bool Attention);

    /// <summary>
    /// 每支攝影機每天寫入多少 GB：<c>Mbps × 有效秒數 ÷ 8 ÷ 1000</c>（十進位 GB）。
    /// </summary>
    /// <remarks>
    /// 24 小時全錄、1 Mbps 時是 10.8 GB／天——這個數字可以直接背下來當量級檢查。
    /// 「有效秒數」＝ 每日錄影時數 × 錄影比例（移動偵測只錄一部分時間時填 0.3～0.5），
    /// 不是用「碼率 × 24 小時」硬算，否則移動偵測的案場會多估兩三倍。
    /// </remarks>
    [SpecRef("H.264/H.265 碼率為位元率：GB/day ＝ Mbps × 10^6 × 有效秒數 ÷ 8 ÷ 10^9；"
           + "以十進位 GB（10^9 bytes）計，與硬碟標示容量同一單位。")]
    public static double RecordingGbPerCameraPerDay(double bitrateMbps, double hoursPerDay, double recordingDuty)
    {
        double hours = Math.Clamp(hoursPerDay, 0, 24);
        double duty = Math.Clamp(recordingDuty, 0, 1);
        if (bitrateMbps <= 0) return 0;
        return bitrateMbps * hours * 3600 * duty / 8.0 / 1000.0;
    }

    /// <summary>NVR 容量推算：原始需求、RAID 後可用容量、可錄天數、缺口。</summary>
    [SpecRef("容量 ＝ 逐機每日 GB × 頻道數 × 保留天數；可用容量 ＝ 資料碟顆數 × 單碟 TB ×（1 − 檔案系統保留）；"
           + "可錄天數 ＝ 可用 TB × 1000 ÷ 每日 GB。RAID 可用顆數由 parity 推導。")]
    public static NvrResult PlanNvr(NvrPlan p)
    {
        double perCam = RecordingGbPerCameraPerDay(p.BitrateMbps, p.HoursPerDay, p.RecordingDuty);
        int cameras = Math.Max(0, p.Cameras);
        int days = Math.Max(0, p.RetentionDays);
        double daily = perCam * cameras;

        if (cameras == 0 || p.BitrateMbps <= 0 || p.RetentionDays <= 0)
            return new NvrResult(perCam, daily, 0, 0, 0, 0, 0, "輸入不足：頻道數、碼率與保留天數都要大於 0。", false);

        double rawTb = daily * days / 1000.0;
        int usableDisks = RaidUsableDisks(p.Disks, p.Level, p.HotSpares, p.GroupSize);
        double fs = Math.Clamp(p.FilesystemOverheadPercent, 0, 50) / 100.0;
        double usableTb = usableDisks * Math.Max(0, p.DiskTb) * (1 - fs);

        if (usableDisks == 0)
            return new NvrResult(perCam, daily, rawTb, 0, 0, rawTb, 0,
                $"RAID 組態不成立（{p.Disks} 顆、{RaidName(p.Level)}、熱備援 {p.HotSpares}）。", true);

        double achieved = daily > 0 ? usableTb * 1000 / daily : 0;
        double shortfall = Math.Max(0, rawTb - usableTb);

        string verdict;
        bool attention;
        if (shortfall > 0.001)
        {
            verdict = $"容量不足：{RaidName(p.Level)} 後可用 {usableTb:0.##} TB，"
                    + $"需要 {rawTb:0.##} TB，還差 {shortfall:0.##} TB（約可錄 {achieved:0.#} 天，目標 {days} 天）。";
            attention = true;
        }
        else if (usableTb > rawTb * 1.5 && rawTb > 0)
        {
            verdict = $"容量明顯過剩：可用 {usableTb:0.##} TB，需求 {rawTb:0.##} TB"
                    + $"（可錄 {achieved:0.#} 天，目標 {days} 天）——可考慮減少碟數或延長保留。";
            attention = false;
        }
        else
        {
            verdict = $"容量足夠：可用 {usableTb:0.##} TB，需求 {rawTb:0.##} TB，"
                    + $"可錄 {achieved:0.#} 天（目標 {days} 天）。";
            attention = false;
        }

        return new NvrResult(perCam, daily, rawTb, usableTb, achieved, shortfall, usableDisks, verdict, attention);
    }

    // ── 2. 監控接入頻寬 ─────────────────────────────────────────────────────

    public readonly record struct IngressPlan(
        int Cameras, double BitrateMbps, double ProtocolOverheadPercent,
        double NvrIngestMbps, double UplinkGbps);

    public readonly record struct IngressResult(
        double PayloadMbps, double TotalMbps, double UplinkMbps,
        double UplinkUtilizationPercent, double NvrUtilizationPercent,
        string Verdict, bool Attention);

    /// <summary>
    /// 接入頻寬：所有攝影機碼率加總（含 RTP/RTSP/TCP 封包開銷）對上 NVR 接入與上行埠。
    /// </summary>
    [SpecRef("IEEE 802.3 線速與 RTP/RTSP over TCP 的封包開銷；開銷預設 15%（保守值，實際隨訊框大小與傳輸層而變），"
           + "可覆寫。上行使用率 ＝ 監控負載 ÷ 上行線速。")]
    public static IngressResult PlanIngress(IngressPlan p)
    {
        int cameras = Math.Max(0, p.Cameras);
        double payload = cameras * Math.Max(0, p.BitrateMbps);
        double overhead = 1 + Math.Clamp(p.ProtocolOverheadPercent, 0, 100) / 100.0;
        double total = payload * overhead;
        double uplinkMbps = Math.Max(0, p.UplinkGbps) * 1000;
        double uplinkUse = uplinkMbps > 0 ? total / uplinkMbps * 100 : 0;
        double nvrUse = p.NvrIngestMbps > 0 ? total / p.NvrIngestMbps * 100 : 0;

        if (cameras == 0 || p.BitrateMbps <= 0)
            return new IngressResult(payload, total, uplinkMbps, 0, 0, "輸入不足：頻道數與碼率都要大於 0。", false);

        var notes = new List<string>();
        bool attention = false;
        if (p.NvrIngestMbps > 0 && total > p.NvrIngestMbps)
        {
            notes.Add($"超過 NVR 接入能力（{p.NvrIngestMbps:0.#} Mbps）");
            attention = true;
        }
        if (uplinkMbps > 0 && total > uplinkMbps)
        {
            notes.Add($"超過上行線速（{uplinkMbps:0.#} Mbps）");
            attention = true;
        }
        else if (uplinkUse >= 80)
        {
            notes.Add($"上行使用率 {uplinkUse:0}%，已接近上限");
            attention = true;
        }

        string verdict = notes.Count > 0
            ? string.Join("；", notes) + "。"
            : $"頻寬足夠：監控負載 {total:0.#} Mbps，上行使用率 {uplinkUse:0.#}%、NVR 接入使用率 {nvrUse:0.#}%。";
        return new IngressResult(payload, total, uplinkMbps, uplinkUse, nvrUse, verdict, attention);
    }

    // ── 3. PoE 功率預算 ─────────────────────────────────────────────────────

    public readonly record struct PoePlan(
        int Cameras, double WattsPerCamera, int PoePorts, double BudgetWatts,
        PoeStandard Standard, double ReservePercent);

    public readonly record struct PoeResult(
        double LoadWatts, double WithReserveWatts, double HeadroomWatts,
        int PortsShort, int OverClassCount, double PdMaxWatts,
        string Verdict, bool Attention);

    /// <summary>該標準的 PD 端最高可用瓦數（不是 PSE 端輸出）。</summary>
    [SpecRef("IEEE 802.3af：PSE 15.4 W／PD 12.95 W；802.3at：PSE 30 W／PD 25.5 W；"
           + "802.3bt Type 3：PSE 60 W／PD 51 W；Type 4：PSE 90 W／PD 71.3 W。"
           + "線損由 PSE 承擔，所以 PD 可用瓦數低於 PSE 輸出。")]
    public static double PoePdMaxWatts(PoeStandard s) => s switch
    {
        PoeStandard.Af => 12.95,
        PoeStandard.At => 25.5,
        PoeStandard.BtType3 => 51.0,
        PoeStandard.BtType4 => 71.3,
        _ => 12.95,
    };

    /// <summary>該標準的 PSE 每埠最大輸出瓦數。</summary>
    public static double PoePseMaxWatts(PoeStandard s) => s switch
    {
        PoeStandard.Af => 15.4,
        PoeStandard.At => 30.0,
        PoeStandard.BtType3 => 60.0,
        PoeStandard.BtType4 => 90.0,
        _ => 15.4,
    };

    /// <summary>PoE 總預算：負載、保留量、餘裕、埠數與單埠超額。</summary>
    [SpecRef("PoE 預算＝Σ 每埠 PD 瓦數 ×（1＋保留）；交換機預算是指 PSE 端輸出，"
           + "必須同時滿足總瓦數與每埠瓦數，且埠數不得少於裝置數。")]
    public static PoeResult PlanPoe(PoePlan p)
    {
        int cameras = Math.Max(0, p.Cameras);
        double load = cameras * Math.Max(0, p.WattsPerCamera);
        double reserve = 1 + Math.Clamp(p.ReservePercent, 0, 100) / 100.0;
        double withReserve = load * reserve;
        double pdMax = PoePdMaxWatts(p.Standard);
        int overClass = 0;
        if (p.WattsPerCamera > pdMax) overClass = cameras;

        int portsShort = Math.Max(0, cameras - Math.Max(0, p.PoePorts));
        double headroom = p.BudgetWatts - withReserve;

        if (cameras == 0 || p.WattsPerCamera <= 0)
            return new PoeResult(load, withReserve, headroom, portsShort, overClass, pdMax,
                "輸入不足：頻道數與每機瓦數都要大於 0。", false);

        var notes = new List<string>();
        bool attention = false;
        if (portsShort > 0)
        {
            notes.Add($"PoE 埠數不足 {portsShort} 埠");
            attention = true;
        }
        if (overClass > 0)
        {
            notes.Add($"每機 {p.WattsPerCamera:0.#} W 超過 {p.Standard} 的 PD 上限 {pdMax:0.#} W");
            attention = true;
        }
        if (headroom < 0)
        {
            notes.Add($"預算不足，還差 {-headroom:0.#} W（含 {p.ReservePercent:0}% 保留）");
            attention = true;
        }

        string verdict = notes.Count > 0
            ? string.Join("；", notes) + "。"
            : $"預算足夠：負載 {withReserve:0.#} W（含保留），餘裕 {headroom:0.#} W。";
        return new PoeResult(load, withReserve, headroom, portsShort, overClass, pdMax, verdict, attention);
    }

    // ── 4. 交換機背板與包轉發 ───────────────────────────────────────────────

    public readonly record struct SwitchPlan(
        int AccessPorts, double PortSpeedGbps, int UplinkPorts, double UplinkGbps,
        double RatedBackplaneGbps, double RatedMpps);

    public readonly record struct SwitchResult(
        double RequiredBackplaneGbps, double RequiredMpps,
        double UplinkOversubscription, bool BackplaneShort, bool MppsShort,
        string Verdict, bool Attention);

    /// <summary>
    /// 交換機背板與包轉發：全雙工背板 ＝ 埠數 × 線速 × 2；包轉發率以 64 bytes 最小訊框計算。
    /// </summary>
    /// <remarks>
    /// 背板與包轉發是兩個不同的上限：背板不夠會塞車，包轉發不夠則小封包（大量攝影機心跳、
    /// ONVIF 查詢）會先掉。只比其中一個，等於只看一半。
    /// </remarks>
    [SpecRef("背板頻寬（Gbps）＝ 埠數 × 每埠線速 × 2（全雙工）；包轉發率（Mpps）＝ 埠數 × 每埠線速 × 1.488"
           + "（64 bytes 最小訊框）。上行超訂比 ＝ 接入總頻寬 ÷ 上行總頻寬。")]
    public static SwitchResult PlanSwitch(SwitchPlan p)
    {
        int ports = Math.Max(0, p.AccessPorts);
        double speed = Math.Max(0, p.PortSpeedGbps);
        double requiredBackplane = ports * speed * 2;
        double requiredMpps = ports * speed * MppsPerGbps;
        double access = ports * speed;
        double uplink = Math.Max(0, p.UplinkGbps) * Math.Max(0, p.UplinkPorts);
        double oversub = uplink > 0 ? access / uplink : 0;

        if (ports == 0 || speed <= 0)
            return new SwitchResult(0, 0, oversub, false, false, "輸入不足：接入埠數與埠速都要大於 0。", false);

        bool backplaneShort = p.RatedBackplaneGbps > 0 && requiredBackplane > p.RatedBackplaneGbps;
        bool mppsShort = p.RatedMpps > 0 && requiredMpps > p.RatedMpps;

        var notes = new List<string>();
        if (backplaneShort)
            notes.Add($"標示背板 {p.RatedBackplaneGbps:0.#} Gbps 低於全雙工需求 {requiredBackplane:0.#} Gbps");
        if (mppsShort)
            notes.Add($"標示包轉發 {p.RatedMpps:0.##} Mpps 低於需求 {requiredMpps:0.##} Mpps");
        if (p.RatedBackplaneGbps <= 0 && p.RatedMpps <= 0)
            notes.Add("未填標示背板／包轉發率——只算需求，無從判斷這台夠不夠");
        if (oversub >= 4 && uplink > 0)
            notes.Add($"上行超訂 {oversub:0.#}:1，尖峰時段可能排隊");

        bool attention = backplaneShort || mppsShort || (oversub >= 4 && uplink > 0);
        string verdict = notes.Count > 0
            ? string.Join("；", notes) + "。"
            : $"規格相符：需求 {requiredBackplane:0.#} Gbps／{requiredMpps:0.##} Mpps，"
              + $"上行超訂 {oversub:0.#}:1。";
        return new SwitchResult(requiredBackplane, requiredMpps, oversub, backplaneShort, mppsShort, verdict, attention);
    }

    // ── 5. 光纖衰減與鏈路裕度 ───────────────────────────────────────────────

    public readonly record struct FiberPlan(
        double LengthKm, FiberWavelength Wavelength, double AttenuationDbPerKm,
        int Connectors, int Splices, double TxPowerDbm, double RxSensitivityDbm);

    public readonly record struct FiberResult(
        double AttenuationDb, double ConnectorLossDb, double SpliceLossDb,
        double LinkBudgetDb, double MarginDb, double RequiredMarginDb,
        string Verdict, bool Attention);

    /// <summary>波長的預設衰減係數（dB/km，最壞情況）。</summary>
    [SpecRef("IEC 60793／ITU-T G.652：單模 1310 nm ≤0.35 dB/km、1550 nm ≤0.22 dB/km；"
           + "OM3/OM4 多模 850 nm ≤3.5 dB/km。實際值依光纜規格與施工環境，可由使用者覆寫。")]
    public static double DefaultAttenuationDbPerKm(FiberWavelength w) => w switch
    {
        FiberWavelength.Nm850 => 3.5,
        FiberWavelength.Nm1310 => 0.35,
        FiberWavelength.Nm1550 => 0.22,
        _ => 0.35,
    };

    /// <summary>
    /// 光纖鏈路裕度 ＝（發射功率 − 接收靈敏度）−（光纜衰減 ＋ 接頭損失 ＋ 熔接損失）。
    /// </summary>
    [SpecRef("鏈路預算 ＝ TX − RX 靈敏度；損失 ＝ 長度 × 衰減係數 ＋ 接頭數 × 0.5 dB ＋ 熔接數 × 0.15 dB；"
           + "驗收慣例要求至少 3 dB 裕度（溫度、老化、彎曲與維修都吃裕度）。")]
    public static FiberResult PlanFiber(FiberPlan p)
    {
        double coef = p.AttenuationDbPerKm > 0 ? p.AttenuationDbPerKm : DefaultAttenuationDbPerKm(p.Wavelength);
        double fiberLoss = Math.Max(0, p.LengthKm) * coef;
        double conn = Math.Max(0, p.Connectors) * ConnectorLossDb;
        double splice = Math.Max(0, p.Splices) * SpliceLossDb;
        double totalLoss = fiberLoss + conn + splice;
        double budget = p.TxPowerDbm - p.RxSensitivityDbm;

        if (p.LengthKm <= 0)
            return new FiberResult(fiberLoss, conn, splice, budget, budget, RequiredFiberMarginDb,
                "輸入不足：光纜長度要大於 0。", false);

        double margin = budget - totalLoss;
        string verdict;
        bool attention;
        if (budget <= 0)
        {
            verdict = $"鏈路預算不成立：TX {p.TxPowerDbm:0.#} dBm 未高於接收靈敏度 {p.RxSensitivityDbm:0.#} dBm。";
            attention = true;
        }
        else if (margin < RequiredFiberMarginDb)
        {
            verdict = $"裕度不足：預算 {budget:0.#} dB、損失 {totalLoss:0.#} dB，只剩 {margin:0.#} dB"
                    + $"（驗收要求 ≥ {RequiredFiberMarginDb:0.#} dB）——考慮換單模、縮短距離或減少接頭。";
            attention = true;
        }
        else
        {
            verdict = $"裕度足夠：預算 {budget:0.#} dB、損失 {totalLoss:0.#} dB，剩餘 {margin:0.#} dB。";
            attention = false;
        }
        return new FiberResult(fiberLoss, conn, splice, budget, margin, RequiredFiberMarginDb, verdict, attention);
    }

    // ── 6. 攝影機端壓降 ─────────────────────────────────────────────────────

    public readonly record struct DropPlan(
        double PseVoltage, double CameraWatts, double LengthMeters,
        double ResistanceOhmPerMeter, int Pairs, double CameraMinVoltage);

    public readonly record struct DropResult(
        double LoopResistanceOhm, double CurrentAmps, double DropVolts,
        double CameraVoltage, double CableLossWatts, double PseWatts,
        string Verdict, bool Attention);

    /// <summary>常見 AWG 銅導體電阻（Ω/m、20 °C、單線）。</summary>
    [SpecRef("IEC 60228／ASTM B258：20 °C 銅導體直流電阻（Ω/km）：24 AWG 87.5、23 AWG 70.0、22 AWG 55.0、"
           + "21 AWG 43.7、20 AWG 34.8、18 AWG 21.9、16 AWG 13.7。數值換算為 Ω/m。")]
    public static double CopperOhmPerMeter(int awg) => awg switch
    {
        26 => 0.137,
        24 => 0.0875,
        23 => 0.0700,
        22 => 0.0550,
        21 => 0.0437,
        20 => 0.0348,
        18 => 0.0219,
        16 => 0.0137,
        _ => 0.0875,
    };

    /// <summary>該 PoE 標準在纜線裡用幾對線（af／at 兩對、bt 四對）。</summary>
    public static int PoePairs(PoeStandard s) => s is PoeStandard.BtType3 or PoeStandard.BtType4 ? 4 : 2;

    /// <summary>該標準的 PD 最低輸入電壓。</summary>
    [SpecRef("IEEE 802.3af／at：PD 輸入 36–57 V；802.3bt Type 3：42.5–57 V；Type 4：41.1–57 V。"
           + "低於下限時 PD 必須拒絕受電（不是「可能不穩」）。")]
    public static double PoeMinVoltage(PoeStandard s) => s switch
    {
        PoeStandard.Af or PoeStandard.At => 36.0,
        PoeStandard.BtType3 => 42.5,
        PoeStandard.BtType4 => 41.1,
        _ => 36.0,
    };

    /// <summary>
    /// 攝影機端壓降：迴路電阻 ＝ 2 × 線長 × 單線電阻 ÷ 對數，解 <c>V² − Vpse·V + P·R = 0</c>。
    /// </summary>
    /// <remarks>
    /// 不能直接拿「P ÷ Vpse」當電流再乘 R：電流本身取決於攝影機端電壓，兩者互相牽制。
    /// 解二次式的較大根才是穩定工作點；判別式小於 0 代表這條線根本供不起這顆攝影機
    /// （不是「壓降很大」，是無解）。
    /// </remarks>
    [SpecRef("PoE 迴路：V_cam ＝ V_pse − I·R_loop、I ＝ P ÷ V_cam，消去 I 得 V_cam² − V_pse·V_cam ＋ P·R_loop ＝ 0；"
           + "R_loop ＝ 2·L·R_wire ÷ pairs（同一極性的線併聯）。")]
    public static DropResult PlanDrop(DropPlan p)
    {
        int pairs = Math.Max(1, p.Pairs);
        double loopR = 2 * Math.Max(0, p.LengthMeters) * Math.Max(0, p.ResistanceOhmPerMeter) / pairs;
        double pwr = Math.Max(0, p.CameraWatts);
        double vpse = Math.Max(0, p.PseVoltage);

        if (p.LengthMeters <= 0 || pwr <= 0 || vpse <= 0)
            return new DropResult(loopR, 0, 0, 0, 0, 0, "輸入不足：線長、瓦數與供電電壓都要大於 0。", false);

        double disc = vpse * vpse - 4 * pwr * loopR;
        if (disc <= 0)
        {
            // 無解：這條線的迴路電阻大到任何電壓都送不到 P 瓦。
            return new DropResult(loopR, 0, vpse, 0, 0, 0,
                $"這條線供不起 {pwr:0.#} W：迴路電阻 {loopR:0.##} Ω 使二次式無解（線太長或線徑太細）。", true);
        }

        double vcam = (vpse + Math.Sqrt(disc)) / 2;
        double current = pwr / vcam;
        double drop = vpse - vcam;
        double cableLoss = current * current * loopR;
        double pseWatts = pwr + cableLoss;

        string verdict;
        bool attention;
        if (vcam < p.CameraMinVoltage)
        {
            verdict = $"壓降過大：攝影機端只剩 {vcam:0.#} V，低於 {p.CameraMinVoltage:0.#} V 下限"
                    + $"（壓降 {drop:0.#} V、線損 {cableLoss:0.##} W）——縮短線長或改用更粗的線。";
            attention = true;
        }
        else if (pseWatts > PoePseMaxWatts(PoeStandard.At))
        {
            verdict = $"攝影機端 {vcam:0.#} V 可接受，但 PSE 需要供 {pseWatts:0.##} W"
                    + $"（線損 {cableLoss:0.##} W），超過 802.3at 每埠上限 30 W。";
            attention = true;
        }
        else
        {
            verdict = $"壓降可接受：攝影機端 {vcam:0.#} V（壓降 {drop:0.#} V、線損 {cableLoss:0.##} W），"
                    + $"PSE 端需 {pseWatts:0.##} W。";
            attention = false;
        }
        return new DropResult(loopR, current, drop, vcam, cableLoss, pseWatts, verdict, attention);
    }

    // ── 7. 佈線用線量 ───────────────────────────────────────────────────────

    public readonly record struct CablingPlan(
        int Runs, double AverageRunMeters, double SlackPercent,
        double BoxLengthMeters, double PerRunServiceLoopMeters);

    public readonly record struct CablingResult(
        double TotalMeters, int Boxes, double BoxTotalMeters,
        double SpareMeters, double UtilizationPercent,
        string Verdict, bool Attention);

    /// <summary>
    /// 佈線用線量：總長 ＝ 迴路數 ×（平均長度 ＋ 每路預留） ×（1 ＋ 餘裕），再換算成整箱數。
    /// </summary>
    /// <remarks>
    /// 箱數<b>無條件進位</b>：線是一箱一箱買的，0.2 箱不會賣你。餘裕預設 10% 是施工慣例
    /// （轉彎、上下樓、失敗重拉），不是精確值；每路再留 2 m 給兩端設備箱。
    /// </remarks>
    [SpecRef("TIA-568 水平佈線：單一水平纜線通道長度上限 90 m（另加兩端跳線共 10 m）；"
           + "箱長 305 m（1000 ft）為標準包裝。總長與箱數為算術推導。")]
    public static CablingResult PlanCabling(CablingPlan p)
    {
        int runs = Math.Max(0, p.Runs);
        double boxLen = p.BoxLengthMeters > 0 ? p.BoxLengthMeters : BoxLengthMeters;
        double perRun = Math.Max(0, p.AverageRunMeters) + Math.Max(0, p.PerRunServiceLoopMeters);
        double total = runs * perRun * (1 + Math.Clamp(p.SlackPercent, 0, 100) / 100.0);
        int boxes = total > 0 ? (int)Math.Ceiling(total / boxLen) : 0;
        double boxTotal = boxes * boxLen;
        double spare = boxTotal - total;
        double util = boxTotal > 0 ? total / boxTotal * 100 : 0;

        if (runs == 0 || p.AverageRunMeters <= 0)
            return new CablingResult(0, 0, 0, 0, 0, "輸入不足：迴路數與平均長度都要大於 0。", false);

        var notes = new List<string>();
        bool attention = false;
        if (p.AverageRunMeters > 90)
        {
            notes.Add($"平均 {p.AverageRunMeters:0.#} m 已超過 TIA-568 水平通道 90 m 上限");
            attention = true;
        }
        if (util < 70)
            notes.Add($"整箱利用率只有 {util:0}%，剩 {spare:0.#} m 下一個案場可用");

        string verdict = notes.Count > 0
            ? string.Join("；", notes) + "。"
            : $"需要 {total:0.#} m，買 {boxes} 箱（{boxTotal:0} m），剩 {spare:0.#} m。";
        return new CablingResult(total, boxes, boxTotal, spare, util, verdict, attention);
    }

    // ── 8. RAID 可用容量 ────────────────────────────────────────────────────

    public readonly record struct RaidPlan(
        int Disks, double DiskTb, RaidLevel Level, int HotSpares,
        int GroupSize, double FilesystemOverheadPercent);

    public readonly record struct RaidResult(
        double RawTb, double UsableTb, int UsableDisks, int FaultTolerance,
        double EfficiencyPercent, string Verdict, bool Attention);

    /// <summary>單獨看一組 RAID 的可用容量與容錯。</summary>
    [SpecRef("可用容量 ＝ 資料碟 × 單碟 TB ×（1 − 檔案系統保留）；容錯顆數由 parity 推導；"
           + "效率 ＝ 可用 ÷ 原始。熱備援不計入可用容量。")]
    public static RaidResult PlanRaid(RaidPlan p)
    {
        int disks = Math.Max(0, p.Disks);
        double raw = disks * Math.Max(0, p.DiskTb);
        int usableDisks = RaidUsableDisks(disks, p.Level, p.HotSpares, p.GroupSize);
        double fs = Math.Clamp(p.FilesystemOverheadPercent, 0, 50) / 100.0;
        double usable = usableDisks * Math.Max(0, p.DiskTb) * (1 - fs);

        if (disks == 0 || p.DiskTb <= 0)
            return new RaidResult(0, 0, 0, 0, 0, "輸入不足：碟數與單碟容量都要大於 0。", false);
        if (usableDisks == 0)
            return new RaidResult(raw, 0, 0, 0, 0,
                $"{disks} 顆（熱備援 {p.HotSpares}）不足以建 {RaidName(p.Level)}。", true);

        double eff = raw > 0 ? usable / raw * 100 : 0;
        int ft = RaidFaultTolerance(p.Level);
        string verdict = $"{RaidName(p.Level)}：可用 {usable:0.##} TB（原始 {raw:0.##} TB，效率 {eff:0.#}%），"
                       + $"可同時壞 {ft} 顆，熱備援 {Math.Max(0, p.HotSpares)} 顆。";
        return new RaidResult(raw, usable, usableDisks, ft, eff, verdict, false);
    }

    // ── 9. 無線覆蓋 ─────────────────────────────────────────────────────────

    public readonly record struct WirelessPlan(
        int Cameras, double BitrateMbps, double PerApThroughputMbps,
        double CoverageRadiusMeters, double AreaSquareMeters, double OverlapPercent);

    public readonly record struct WirelessResult(
        int ApsByCapacity, int ApsByArea, int ApsRequired,
        double AreaPerApSquareMeters, string Verdict, bool Attention);

    /// <summary>
    /// 無線 AP 數量：取「頻寬需要幾台」與「覆蓋需要幾台」的較大者。
    /// </summary>
    /// <remarks>
    /// 兩者都要算，因為它們是不同的瓶頸：會議室裡一台 AP 的覆蓋夠，但十支 4 MP 攝影機
    /// 同時上傳就塞住；反過來，一支攝影機的頻寬一台 AP 綽綽有餘，但倉庫角落收不到訊號。
    /// </remarks>
    [SpecRef("每台 AP 的有效吞吐為使用者宣告值（預設依頻段與通道寬度：2.4 GHz 20 MHz 約 70 Mbps、"
           + "5 GHz 80 MHz 約 400 Mbps）；覆蓋面積 ＝ π·r²·(1 − 重疊比例)。"
           + "這些是設計估算，不是現場量測。")]
    public static WirelessResult PlanWireless(WirelessPlan p)
    {
        int cameras = Math.Max(0, p.Cameras);
        double load = cameras * Math.Max(0, p.BitrateMbps);
        double perAp = Math.Max(0, p.PerApThroughputMbps);
        double radius = Math.Max(0, p.CoverageRadiusMeters);
        double area = Math.Max(0, p.AreaSquareMeters);
        double overlap = Math.Clamp(p.OverlapPercent, 0, 90) / 100.0;

        int byCapacity = perAp > 0 ? (int)Math.Ceiling(load / perAp) : 0;
        double cellArea = Math.PI * radius * radius * (1 - overlap);
        int byArea = cellArea > 0 && area > 0 ? (int)Math.Ceiling(area / cellArea) : 0;
        int required = Math.Max(byCapacity, byArea);

        if (cameras == 0 || p.BitrateMbps <= 0)
            return new WirelessResult(0, 0, 0, cellArea, "輸入不足：頻道數與碼率都要大於 0。", false);
        if (perAp <= 0 && cellArea <= 0)
            return new WirelessResult(0, 0, 0, 0, "輸入不足：每台 AP 吞吐與覆蓋半徑至少填一個。", false);

        string verdict = required == 0
            ? "輸入不足：無法推算 AP 數量。"
            : (byCapacity >= byArea
                ? $"需要 {required} 台 AP（頻寬瓶頸：{load:0.#} Mbps ÷ 每台 {perAp:0.#} Mbps）。"
                : $"需要 {required} 台 AP（覆蓋瓶頸：{area:0} m² ÷ 每台約 {cellArea:0} m²）。");
        return new WirelessResult(byCapacity, byArea, required, cellArea, verdict, byCapacity > 0 && load > perAp);
    }

    // ── 10. UPS 續航與機櫃 U 數 ────────────────────────────────────────────

    public readonly record struct UpsPlan(
        double LoadWatts, double BatteryWh, double InverterEfficiencyPercent,
        double DepthOfDischargePercent);

    public readonly record struct UpsResult(
        double UsableWh, double RuntimeMinutes, string Verdict, bool Attention);

    /// <summary>
    /// UPS 續航：<c>電池 Wh × 逆變效率 × 放電深度 ÷ 負載 W × 60</c>。
    /// </summary>
    /// <remarks>
    /// 不要把「標示 VA」當成瓦數：VA × 功率因數（常見 0.6–0.9）才是 W。
    /// 鉛酸電池也不該放到 100%（深度放電會大幅縮短壽命），預設 80%。
    /// 這兩件事是 UPS 標示最常被誤讀的地方。
    /// </remarks>
    [SpecRef("續航（min）＝ Wh × 逆變效率 × 放電深度 ÷ 負載 W × 60；"
           + "VA→W 需乘功率因數（本工具要求直接輸入 W，不從 VA 猜）。")]
    public static UpsResult PlanUps(UpsPlan p)
    {
        double eff = Math.Clamp(p.InverterEfficiencyPercent, 1, 100) / 100.0;
        double dod = Math.Clamp(p.DepthOfDischargePercent, 1, 100) / 100.0;
        double usable = Math.Max(0, p.BatteryWh) * eff * dod;
        double load = Math.Max(0, p.LoadWatts);

        if (load <= 0 || p.BatteryWh <= 0)
            return new UpsResult(usable, 0, "輸入不足：負載瓦數與電池容量都要大於 0。", false);

        double minutes = usable / load * 60;
        string verdict = minutes < 5
            ? $"續航只有 {minutes:0.#} 分鐘——不足以安全關機 NVR 與交換機。"
            : minutes < 15
                ? $"續航 {minutes:0.#} 分鐘，僅夠短暫停電；建議加電池或降低負載。"
                : $"續航約 {minutes:0.#} 分鐘（可用 {usable:0} Wh）。";
        return new UpsResult(usable, minutes, verdict, minutes < 5);
    }

    /// <summary>機櫃 U 數：裝置 U 數加總＋保留 U 數；超出可用 U 時如實說。</summary>
    public static (int UsedU, int FreeU, string Verdict, bool Attention) PlanRack(
        int DeviceU, int ReservedU, int RackU)
    {
        int used = Math.Max(0, DeviceU) + Math.Max(0, ReservedU);
        int free = Math.Max(0, RackU) - used;
        if (RackU <= 0)
            return (used, 0, "輸入不足：機櫃 U 數要大於 0。", false);
        if (free < 0)
            return (used, 0, $"超出 {-free} U：裝置與保留共 {used} U，機櫃只有 {RackU} U。", true);
        return (used, free, $"使用 {used} U、剩 {free} U（機櫃 {RackU} U）。", false);
    }
}
