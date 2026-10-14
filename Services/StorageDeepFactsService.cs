namespace XinSpect;

/// <summary>
/// 儲存深化第二批（Vol1 第 ST-007、ST-009、ST-013、ST-014、ST-016、ST-018 項）：
/// 控制器拓撲樹（<c>st.ctrl.tree</c>）、外接盒橋接晶片（<c>st.bridge.summary</c>）、
/// Storage Spaces 拓撲（<c>st.ss.summary</c>）、SSD 韌體已知問題表（<c>st.fw.known</c>）、
/// Get-PhysicalDisk 交叉核對（<c>st.xcheck.summary</c>）、DWPD 估計（<c>st.dwpd</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一組全部唯讀。</b>控制器模式、橋接晶片、儲存池拓撲都是列舉事實；韌體問題表是離線知識包；
/// 交叉核對只比對兩份既有讀值，不寫入任何一處。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 模式誤判要被聲明——RAID／直通／VMD 有韌體差異，讀不到一律三態；
/// ② 橋接晶片未收錄知識表時如實說「未收錄」，不用晶片型號猜；
/// ③ 無 Storage Spaces 是 NotApplicable 不是錯誤；
/// ④ 韌體問題表的<b>快照日期必印</b>——離線表會過期，日期是它唯一的新鮮度線索；
/// ⑤ 交叉核對不一致是<b>矛盾</b>不是錯誤（兩邊都可能對，差異本身就是要呈現的事實）。
/// </para>
/// </remarks>
public static class StorageDeepFactsService
{
    private const string Cat = "儲存";
    private const string Source = "本機唯讀列舉（WMI／IOCTL）＋離線知識表";

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>ST-007 控制器拓撲樹。</summary>
    public const string ControllerTreeKey = "st.ctrl.tree";
    /// <summary>ST-009 外接盒橋接晶片。</summary>
    public const string BridgeKey = "st.bridge.summary";
    /// <summary>ST-013 Storage Spaces 拓撲。</summary>
    public const string SpacesKey = "st.ss.summary";
    /// <summary>ST-014 SSD 韌體已知問題表。</summary>
    public const string FirmwareKnownKey = "st.fw.known";
    /// <summary>ST-016 Get-PhysicalDisk 交叉核對。</summary>
    public const string CrossCheckKey = "st.xcheck.summary";
    /// <summary>ST-018 DWPD 估計。</summary>
    public const string DwpdKey = "st.dwpd";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>一顆碟的控制器歸屬：控制器名稱、模式（RAID／AHCI／直通／VMD）、通道上的碟數。</summary>
    public sealed record ControllerRow(string Controller, string Mode, int Disks);

    /// <summary>一個外接裝置的橋接資訊：VID/PID、橋接晶片判定（null＝知識表未收錄）、走 UASP 與否。</summary>
    public sealed record BridgeRow(string VidPid, string? Chip, bool Uasp);

    /// <summary>（儲存池數, 實體碟數, 虛擬碟數, 韌性型別）。</summary>
    public sealed record SpacesState(int Pools, int Physical, int Virtual, string Resiliency);

    /// <summary>離線韌體問題表：快照日期＋表項數＋（這一版）命中數。</summary>
    public sealed record FirmwareTableState(string SnapshotDate, int Entries, int? Hits, string? HitDetail);

    /// <summary>WMI 與本工具在同一批碟上的列舉差異：獨有數、雙方共識數、差異樣本。</summary>
    public sealed record CrossCheckState(int WmiOnly, int ToolOnly, int Agreed, string Sample);

    /// <summary>（最舊碟的通電小時, 該碟累計寫入 GiB, 標稱 TBW GiB（null＝未知））。</summary>
    public sealed record DwpdInput(long PowerOnHours, double WrittenGiB, double? RatedTbwGiB);

    public delegate IReadOnlyList<ControllerRow>? ControllerProbe();
    public delegate IReadOnlyList<BridgeRow>? BridgeProbe();
    public delegate SpacesState? SpacesProbe();
    public delegate FirmwareTableState? FirmwareProbe();
    public delegate CrossCheckState? CrossCheckProbe();
    public delegate DwpdInput? DwpdProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ControllerProbe? controller = null,
        BridgeProbe? bridge = null,
        SpacesProbe? spaces = null,
        FirmwareProbe? firmware = null,
        CrossCheckProbe? crossCheck = null,
        DwpdProbe? dwpd = null)
        =>
        [
            ControllerTreeFact(at, controller),
            BridgeFact(at, bridge),
            SpacesFact(at, spaces),
            FirmwareKnownFact(at, firmware),
            CrossCheckFact(at, crossCheck),
            DwpdFact(at, dwpd),
        ];

    // ── ST-007 控制器拓撲樹 ───────────────────────────────────────────────

    public static HardwareFact ControllerTreeFact(DateTimeOffset at, ControllerProbe? probe = null)
    {
        IReadOnlyList<ControllerRow>? rows;
        try { rows = (probe ?? ReadControllers)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, ControllerTreeKey, "控制器拓撲樹", FactAvailability.ReadError,
                "控制器歸屬列舉失敗——讀不到就是不猜（控制器模式有韌體差異，猜錯比留白糟）");

        if (rows.Count == 0)
            return Unavailable(at, ControllerTreeKey, "控制器拓撲樹", FactAvailability.NotApplicable,
                "沒有列舉到任何儲存控制器——不適用，不是錯誤");

        string detail = string.Join("；", rows.Take(6).Select(r =>
            $"{r.Controller}（{r.Mode}，{r.Disks} 碟）"));
        string modes = string.Join("、", rows.Select(r => r.Mode).Distinct(StringComparer.Ordinal));
        return new HardwareFact(ControllerTreeKey, Cat, "控制器拓撲樹",
            $"控制器 {rows.Count} 個・碟 {rows.Sum(r => r.Disks)} 顆・模式：{modes}。{detail}" +
            (rows.Count > 6 ? $"；等 {rows.Count - 6} 個控制器" : "") + "。" +
            "<b>模式誤判聲明</b>：RAID／AHCI／直通／VMD 的舉報會因韌體與驅動而異——" +
            "本項陳述的是本機儲存堆疊當下告訴我們的那個模式，不是硬體手冊上的預期模式。",
            "個", Source, FactTrustLevel.Reported, false, at, rows.Count);
    }

    private static IReadOnlyList<ControllerRow>? ReadControllers()
    {
        try
        {
            var list = new List<ControllerRow>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\cimv2", "SELECT Name, DeviceID FROM Win32_SCSIController");
            foreach (System.Management.ManagementObject m in searcher.Get())
            {
                using (m)
                {
                    string name = m["Name"]?.ToString() ?? "（未具名）";
                    list.Add(new ControllerRow(name, "未回報", 0));
                }
            }
            return list.Count == 0 ? null : list;
        }
        catch { return null; }
    }

    // ── ST-009 外接盒橋接晶片 ─────────────────────────────────────────────

    public static HardwareFact BridgeFact(DateTimeOffset at, BridgeProbe? probe = null)
    {
        IReadOnlyList<BridgeRow>? rows;
        try { rows = (probe ?? (() => null))(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, BridgeKey, "外接盒橋接晶片", FactAvailability.ReadError,
                "橋接資訊列舉失敗——讀不到就是不猜");
        if (rows.Count == 0)
            return Unavailable(at, BridgeKey, "外接盒橋接晶片", FactAvailability.NotApplicable,
                "沒有外接儲存裝置——不適用，不是錯誤");

        int known = rows.Count(r => r.Chip is not null);
        int uasp = rows.Count(r => r.Uasp);
        string detail = string.Join("；", rows.Take(5).Select(r =>
            r.Chip is null ? $"{r.VidPid}（知識表未收錄）" : $"{r.VidPid}＝{r.Chip}{(r.Uasp ? "・UASP" : "")}"));
        return new HardwareFact(BridgeKey, Cat, "外接盒橋接晶片",
            $"外接裝置 {rows.Count} 個・知識表命中 {known} 個・走 UASP {uasp} 個。{detail}" +
            (rows.Count > 5 ? $"；等 {rows.Count - 5} 個" : "") + "。" +
            "<b>未收錄如實說</b>：認不出的 VID/PID 一律標「知識表未收錄」，不用相近型號猜——" +
            "猜錯會讓使用者按錯誤的相容性結論行動。",
            "個", Source, FactTrustLevel.Reported, false, at, rows.Count);
    }

    // ── ST-013 Storage Spaces 拓撲 ────────────────────────────────────────

    public static HardwareFact SpacesFact(DateTimeOffset at, SpacesProbe? probe = null)
    {
        SpacesState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SpacesKey, "Storage Spaces 拓撲", FactAvailability.ReadError,
                "儲存池查詢失敗——讀不到就是不猜");
        if (s.Pools == 0)
            return Unavailable(at, SpacesKey, "Storage Spaces 拓撲", FactAvailability.NotApplicable,
                "本機沒有 Storage Spaces 儲存池——不適用，不是錯誤（直連碟不需要儲存池）");

        var v = s;
        return new HardwareFact(SpacesKey, Cat, "Storage Spaces 拓撲",
            $"儲存池 {v.Pools} 個・實體碟 {v.Physical} 顆・虛擬碟 {v.Virtual} 顆・韌性 {v.Resiliency}。" +
            "<b>實體↔虛擬的對應</b>：虛擬碟的「容量」是所有成員碟分攤後的結果，" +
            "與單顆碟的健康數字不可直接相比；本項只呈現拓撲，不做容量預測。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Pools);
    }

    // ── ST-014 SSD 韌體已知問題表 ─────────────────────────────────────────

    public static HardwareFact FirmwareKnownFact(DateTimeOffset at, FirmwareProbe? probe = null)
    {
        FirmwareTableState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, FirmwareKnownKey, "SSD 韌體已知問題表", FactAvailability.NotApplicable,
                "本版沒有內建韌體問題離線表——沒有表就沒有可比對的東西，不假裝比對過");

        var v = s;
        string hits = v.Hits is { } h
            ? h == 0 ? "本機韌體未命中表項" : $"命中 {h} 項（{v.HitDetail ?? "詳見知識包"}）"
            : "本機韌體版本讀不到，無法比對——讀不到不等於未命中";
        return new HardwareFact(FirmwareKnownKey, Cat, "SSD 韌體已知問題表",
            $"表快照 {v.SnapshotDate}・表項 {v.Entries} 條・{hits}。" +
            "<b>快照日期必印</b>：離線表會過期，沒有日期就沒人分得出「沒問題」與「表太舊」。",
            "條", "離線知識表（快照＋本機韌體版本）", FactTrustLevel.Reported, false, at, v.Entries);
    }

    // ── ST-016 Get-PhysicalDisk 交叉核對 ──────────────────────────────────

    public static HardwareFact CrossCheckFact(DateTimeOffset at, CrossCheckProbe? probe = null)
    {
        CrossCheckState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CrossCheckKey, "Get-PhysicalDisk 交叉核對", FactAvailability.ReadError,
                "WMI 磁碟列舉失敗——讀不到就是不猜");

        var v = s;
        bool agree = v.WmiOnly == 0 && v.ToolOnly == 0;
        return new HardwareFact(CrossCheckKey, Cat, "Get-PhysicalDisk 交叉核對",
            agree
                ? $"兩邊一致：共識 {v.Agreed} 顆，沒有單邊獨有的碟。（差異 0 是值——不是「沒比對」。）"
                : $"共識 {v.Agreed} 顆・只有 WMI 看得到 {v.WmiOnly} 顆・只有本工具看得到 {v.ToolOnly} 顆。" +
                  $"差異樣本：{v.Sample}。<b>不一致是矛盾不是錯誤</b>——兩個來源都可能對（WMI 看的是它認得的物件，" +
                  "本工具看的是實體裝置），差異本身就是要呈現的事實，不是誰壞了。",
            "顆", "Win32_DiskDrive ↔ 本工具列舉（唯讀比對）", FactTrustLevel.Derived, false, at, v.Agreed);
    }

    // ── ST-018 DWPD 估計 ──────────────────────────────────────────────────

    public static HardwareFact DwpdFact(DateTimeOffset at, DwpdProbe? probe = null)
    {
        DwpdInput? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DwpdKey, "DWPD 估計", FactAvailability.ReadError,
                "通電時數或寫入量讀不到——沒有分子也沒有分母，不編一個 DWPD 出來");
        var v = s;
        if (v.PowerOnHours <= 0)
            return Unavailable(at, DwpdKey, "DWPD 估計", FactAvailability.NotApplicable,
                "通電時數為 0 或讀不到——時間分母不可用，算不出每日寫入量，不拿 1 小時硬除");

        double days = v.PowerOnHours / 24.0;
        double gibPerDay = v.WrittenGiB / days;
        double dwpd = v.RatedTbwGiB is { } tbw and > 0
            ? v.WrittenGiB / 1024.0 / tbw * (365.0 / days)
            : double.NaN;

        string rated = v.RatedTbwGiB is { } r and > 0
            ? $"標稱 TBW {r:0} GiB・已用 TBW 比例 {v.WrittenGiB / 1024.0 / r * 100:0.#}%・推算 DWPD {dwpd:0.###}"
            : "標稱 TBW 未知（未收錄此型號的表項）——只給每日寫入量，不給 DWPD 比值";
        return new HardwareFact(DwpdKey, Cat, "DWPD 估計",
            $"通電 {v.PowerOnHours} 小時（{days:0.#} 天）・累計寫入 {v.WrittenGiB:0.#} GiB・每日寫入 {gibPerDay:0.##} GiB。" +
            rated + "。<b>明標估計</b>：這是把裝置自報的累計值攤到它的通電時間上，" +
            "不是量測、也不是預測——換過工作負載的碟會被歷史平均拉平。",
            "GiB/日", "NVMe Health Log（唯讀，裝置自報累計值攤平）", FactTrustLevel.Derived, false, at, gibPerDay);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
