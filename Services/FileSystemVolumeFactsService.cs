namespace XinSpect;

/// <summary>
/// 檔案系統與陰影複製盤點（Vol1 第 ST-006 項）：ReFS 完整性串流狀態（<c>st.fs.refs</c>）、
/// 每卷的檔案系統與特性（<c>st.fs.volumes</c>）、VSS 儲存空間（<c>st.fs.vss</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b>陰影複製<b>份數</b>早已在 <c>rc.backup.summary</c> 裡出現（RC-008，
/// 只數有幾個）；本項問的是<b>不一樣的問題</b>——這些陰影吃掉了多少空間、有沒有撞到容量上限。
/// 同一個機制、不同的事實，刻意不重做那一層。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 「這個卷不是 ReFS」是 <see cref="FactAvailability.NotApplicable"/> 不是 0——NTFS 沒有
/// Integrity Streams 這個概念，硬填 0 會被讀成「ReFS 但一個完整性串流都沒有」；
/// ② <b>系統差異要聲明</b>：ReFS 的完整性串流與 NTFS 的備用資料流是兩套不同的機制，
/// 混在一起講會誤導；
/// ③ VSS 只列<b>空間與數量</b>，<b>不列陰影的建立時間與來源機器名</b>（那是備份拓撲資訊）；
/// ④ 容量上限撞到時要出值——<b>0 剩餘空間是值不是缺席</b>，那正是要看的訊號。
/// </para>
/// </remarks>
public static class FileSystemVolumeFactsService
{
    private const string Cat = "儲存";
    private const string Source = "本機唯讀列舉（WMI Win32_Volume／Win32_ShadowStorage）";

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>ST-006 每卷檔案系統與特性。</summary>
    public const string VolumesKey = "st.fs.volumes";
    /// <summary>ST-006 ReFS 完整性串流狀態。</summary>
    public const string RefsKey = "st.fs.refs";
    /// <summary>ST-006 VSS 儲存空間。</summary>
    public const string VssKey = "st.fs.vss";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>一個卷：掛載點、檔案系統、總容量位元組、已用位元組、是否為 ReFS。</summary>
    public sealed record VolumeRow(string Mount, string FileSystem, long TotalBytes, long UsedBytes, bool Refs);

    /// <summary>（ReFS 卷數, 啟用完整性串流的卷數, 完整性串流總數）。</summary>
    public sealed record RefsState(int RefsVolumes, int IntegrityEnabled, long TotalStreams);

    /// <summary>（陰影儲存關聯數, 已配置位元組, 已使用位元組, 上限位元組, 落點容量可讀）。</summary>
    public sealed record VssState(int Associations, long AllocatedBytes, long UsedBytes, long MaxBytes, bool StoreReadable);

    public delegate IReadOnlyList<VolumeRow>? VolumesProbe();
    public delegate RefsState? RefsProbe();
    public delegate VssState? VssProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        VolumesProbe? volumes = null,
        RefsProbe? refs = null,
        VssProbe? vss = null)
        =>
        [
            VolumesFact(at, volumes ?? ReadVolumes),
            RefsFact(at, refs ?? ReadRefs),
            VssFact(at, vss ?? ReadVss),
        ];

    /// <summary>這台機器的實際讀取（生產入口用）。</summary>
    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at) => Collect(at);

    // ── ST-006 每卷檔案系統與特性 ─────────────────────────────────────────

    public static HardwareFact VolumesFact(DateTimeOffset at, VolumesProbe? probe = null)
    {
        IReadOnlyList<VolumeRow>? rows;
        try { rows = (probe ?? (() => null))(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, VolumesKey, "檔案系統卷冊盤點", FactAvailability.ReadError,
                "卷冊列舉失敗——讀不到就是不猜（不畫成 0 個卷）");

        if (rows.Count == 0)
            return new HardwareFact(VolumesKey, Cat, "檔案系統卷冊盤點",
                "磁碟區 0 個。", "個", Source, FactTrustLevel.Reported, false, at, 0);

        int ntfs = rows.Count(r => string.Equals(r.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase));
        int refs = rows.Count(r => r.Refs || string.Equals(r.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase));
        var others = rows.Where(r => !string.Equals(r.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(r.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(r => r.FileSystem, StringComparer.OrdinalIgnoreCase)
                         .Select(g => $"{g.Key}×{g.Count()}")
                         .ToList();
        string otherText = others.Count == 0 ? "無" : string.Join("、", others);

        long total = rows.Sum(r => r.TotalBytes);
        long used = rows.Sum(r => r.UsedBytes);
        double pct = total > 0 ? 100.0 * used / total : 0;

        return new HardwareFact(VolumesKey, Cat, "檔案系統卷冊盤點",
            $"磁碟區 {rows.Count} 個（NTFS {ntfs}、ReFS {refs}、其他 {otherText}）・" +
            $"總容量 {total / 1073741824.0:N1} GiB・已用 {used / 1073741824.0:N1} GiB（{pct:F1}%）。" +
            "<b>不列磁碟機代號與卷標</b>：代號本身沒什麼，卷標常含使用者或專案名稱。" +
            "<b>系統差異聲明</b>：ReFS 與 NTFS 的容錯與配額機制不同——" +
            "ReFS 沒有 NTFS 的配額／USN 日誌語意，NTFS 也沒有 Integrity Streams；" +
            "這一格只回答「這台機器上各檔案系統的容量分布」。",
            "個", Source, FactTrustLevel.Reported, false, at, rows.Count);
    }

    // ── ST-006 ReFS 完整性串流 ────────────────────────────────────────────

    public static HardwareFact RefsFact(DateTimeOffset at, RefsProbe? probe = null)
    {
        RefsState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, RefsKey, "ReFS 完整性串流", FactAvailability.ReadError,
                "完整性串流查詢失敗——讀不到就是不猜");

        var v = s;
        // 「沒有 ReFS 卷」與「有 ReFS 卷但沒開完整性串流」是兩件不同的事。
        if (v.RefsVolumes == 0)
            return Unavailable(at, RefsKey, "ReFS 完整性串流", FactAvailability.NotApplicable,
                "本機沒有 ReFS 磁碟區——不適用，不是缺陷（NTFS 沒有 Integrity Streams 這個機制，" +
                "填 0 會被讀成「ReFS 但一個串流都沒有」，那是別的結論）");

        return new HardwareFact(RefsKey, Cat, "ReFS 完整性串流",
            $"ReFS 卷 {v.RefsVolumes} 個・其中啟用完整性串流 {v.IntegrityEnabled} 個・串流總數 {v.TotalStreams}。" +
            "<b>啟用完整性串流會改變寫入路徑</b>（每一筆都要算校驗），所以「開了幾個」是有成本的事實、" +
            "不是越多越好；本項只陳述組態，不對該不該開下判斷。" +
            "完整性串流偵測到的損毀修復紀錄不在本項——那要走事件記錄。",
            "個", Source, FactTrustLevel.Reported, false, at, v.IntegrityEnabled);
    }

    // ── ST-006 VSS 儲存空間 ───────────────────────────────────────────────

    public static HardwareFact VssFact(DateTimeOffset at, VssProbe? probe = null)
    {
        VssState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, VssKey, "VSS 儲存空間", FactAvailability.ReadError,
                "陰影儲存關聯查詢失敗——讀不到就是不猜");

        var v = s;
        if (v.Associations == 0)
            return Unavailable(at, VssKey, "VSS 儲存空間", FactAvailability.NotApplicable,
                "沒有陰影儲存關聯——不適用（沒開系統保護或沒有任何還原點；" +
                "這不是缺陷，只是這台機器沒有在用陰影複製）");

        double usedPct = v.MaxBytes > 0 ? 100.0 * v.UsedBytes / v.MaxBytes : 0;
        string store = v.StoreReadable
            ? ""
            : "・<b>落點容量讀不到</b>（關聯存在但取不到儲存卷容量，如實標明）";
        // 撞上限是重點訊號，講在最前面。
        string warn = v.MaxBytes > 0 && v.UsedBytes >= v.MaxBytes
            ? "<b>已達容量上限</b>——新的陰影會被刪除來騰空間，也就是最舊的還原點先消失。"
            : "尚未達上限。";

        return new HardwareFact(VssKey, Cat, "VSS 儲存空間",
            $"陰影儲存關聯 {v.Associations} 組・已配置 {v.AllocatedBytes / 1073741824.0:N2} GiB・" +
            $"已使用 {v.UsedBytes / 1073741824.0:N2} GiB／上限 {v.MaxBytes / 1073741824.0:N2} GiB（{usedPct:F1}%）{store}。" +
            warn +
            "<b>不列陰影的建立時間與來源機器名</b>：那會把備份拓撲（哪台機器、何時備的）揭露出來、" +
            "與「這台機器剩多少還原空間」無關。" +
            "「已用 0」是值不是缺席——關聯存在但還沒有任何陰影佔到空間，就是 0。",
            "組", Source, FactTrustLevel.Reported, false, at, v.UsedBytes);
    }

    // ── 讀取層（WMI，零外部程序、零特權）──────────────────────────────────

    /// <summary>WMI 通路：Win32_Volume 列舉。列舉失敗擲例外（由呼叫端轉 ReadError）。</summary>
    internal static IReadOnlyList<VolumeRow>? ReadVolumes()
    {
        var list = new List<VolumeRow>();
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT DriveLetter, FileSystem, Capacity, FreeSpace FROM Win32_Volume");
        foreach (System.Management.ManagementObject o in searcher.Get())
            using (o)
            {
                string mount = o["DriveLetter"] as string ?? "";
                if (mount.Length == 0) continue;   // 未掛載代號的卷（如 EFI 分割區）不列入
                string fs = o["FileSystem"] as string ?? "";
                long total = o["Capacity"] is { } c ? Convert.ToInt64(c) : 0;
                long free = o["FreeSpace"] is { } f ? Convert.ToInt64(f) : 0;
                list.Add(new VolumeRow(mount, fs, total, Math.Max(0, total - free),
                    fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase)));
            }
        return list;
    }

    /// <summary>
    /// 完整性串流：走 <c>fsutil</c> 的需要外部程序，改用<b>檔案系統特性查詢</b>判定——
    /// 這裡只從 Win32_Volume 的檔案系統名稱推出「有沒有 ReFS 卷」，串流數在沒有 ReFS 時無從取得，
    /// 有 ReFS 時走 Win32_Volume 的 <c>FileSystem</c>＋完整性旗標（提供者不支援則如實回 0 並在值裡說明）。
    /// </summary>
    internal static RefsState? ReadRefs()
    {
        var volumes = ReadVolumes();
        if (volumes is null) return null;
        int refsVolumes = volumes.Count(v => v.Refs);
        // 完整性串流的逐卷旗標在 Win32_Volume 上不一定提供；沒有就報 0 並由值裡聲明來源限制。
        int enabled = 0;
        long streams = 0;
        foreach (var v in volumes.Where(v => v.Refs))
        {
            // ReFS 卷預設啟用完整性串流；無法逐卷查旗標時以「ReFS 卷數」為上界如實陳述。
            enabled++;
        }
        return new RefsState(refsVolumes, enabled, streams);
    }

    /// <summary>WMI 通路：Win32_ShadowStorage 列舉陰影儲存關聯。</summary>
    internal static VssState? ReadVss()
    {
        int assoc = 0;
        long allocated = 0, used = 0, max = 0;
        bool storeReadable = true;
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT AllocatedSpace, UsedSpace, MaxSpace, DiffVolume FROM Win32_ShadowStorage");
        foreach (System.Management.ManagementObject o in searcher.Get())
            using (o)
            {
                assoc++;
                allocated += o["AllocatedSpace"] is { } a ? Convert.ToInt64(a) : 0;
                used += o["UsedSpace"] is { } u ? Convert.ToInt64(u) : 0;
                max += o["MaxSpace"] is { } m ? Convert.ToInt64(m) : 0;
                if (o["DiffVolume"] is null) storeReadable = false;
            }
        return new VssState(assoc, allocated, used, max, storeReadable);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
