namespace XinSpect;

/// <summary>
/// ACPI 表深化第二波（Vol2 第 AC-004、AC-005、AC-011、AC-012、AC-013、AC-018 項）：
/// 電源資源 <c>_PRx</c>（<c>ac.pwres.summary</c>）、_DSM GUID 清單（<c>ac.dsm.summary</c>）、
/// BGRT 開機 logo 表（<c>ac.bgrt.deep</c>）、SPCR 序列主控台（<c>ac.spcr.deep</c>）、
/// DBG2 除錯埠（<c>ac.dbg2.deep</c>）、ACPI 表匯總頁（<c>ac.tables.deep</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一波只做「表頭層」的解碼，不碰 AML 本體。</b>_PRx 與 _DSM 的完整內容在 AML 位元組碼裡，
/// 要真的解出來需要一個評估層——本專案沒有，也不假裝有。所以這兩項做的是
/// <b>存在性與計數</b>：有幾個 PowerResource 物件、幾張表宣告了 _DSM，
/// 而不是「電源資源的實際狀態是什麼」。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 不做的事要寫在值裡——_PRx／_DSM 只到存在性，不宣稱解出內容；
/// ② 熱區門檻是<b>事實不是設定建議</b>（本專案不調散熱）；
/// ③ 表頭存在性不代表功能可用（BGRT 有表不等於開機 logo 有效）；
/// ④ 匯總頁要與既有 <c>ac.tables</c> 的逐表列舉分工：匯總談版本與校驗和的分布，
///    逐表在既有那條路上，不重複列。
/// </para>
/// </remarks>
public static class AcpiDeepFactsService
{
    private const string Cat = "ACPI";
    private const string Source = "EnumSystemFirmwareTables ＋ 表頭解析（唯讀）";

    /// <summary>_PRx 的最低重複次數——只有一個 PowerResource 的機器沒什麼好匯總的。</summary>
    public const int MinPowerResources = 2;

    // ── 鍵 ────────────────────────────────────────────────────────────────

    /// <summary>AC-004 電源資源 _PRx。</summary>
    public const string PowerResourceKey = "ac.pwres.summary";
    /// <summary>AC-005 _DSM GUID 清單。</summary>
    public const string DsmKey = "ac.dsm.summary";
    /// <summary>AC-011 BGRT 開機 logo 表。</summary>
    public const string BgrtKey = "ac.bgrt.deep";
    /// <summary>AC-012 SPCR 序列主控台。</summary>
    public const string SpcrKey = "ac.spcr.deep";
    /// <summary>AC-013 DBG2 除錯埠。</summary>
    public const string Dbg2Key = "ac.dbg2.deep";
    /// <summary>AC-018 ACPI 表匯總頁。</summary>
    public const string TablesKey = "ac.tables.deep";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（PowerResource 物件數, 引用它們的表數, 涵蓋的電源狀態數）。</summary>
    public sealed record PowerResourceState(int Objects, int ReferencingTables, int PowerStates);

    /// <summary>（宣告 _DSM 的表數, 不同的 GUID 數, GUID 全為 16 位元組的比例）。</summary>
    public sealed record DsmState(int Tables, int DistinctGuids, bool ShapesValid);

    /// <summary>（BGRT 表存在, 版本, 映像型別, 映像位址非零, 校驗和正確）。</summary>
    public sealed record BgrtState(bool Present, byte Version, byte ImageType, bool ImageNonZero, bool ChecksumOk);

    /// <summary>（SPCR 表存在, 介面型別, 中斷模式, 鮑率非零, 主控台數）。</summary>
    public sealed record SpcrState(bool Present, byte InterfaceType, string InterruptMode, bool BaudNonZero, int Consoles);

    /// <summary>（DBG2 表存在, 除錯裝置數, 埠型別名稱, 位址非零數）。</summary>
    public sealed record Dbg2State(bool Present, int Devices, string PortTypes, int NonZeroAddresses);

    /// <summary>（表總數, 不同簽章數, 校驗和正確數, 校驗和錯誤數, 最高版本表）。</summary>
    public sealed record TablesSummaryState(int Total, int DistinctSignatures, int ChecksumOk, int ChecksumBad, string HighestVersion);

    public delegate PowerResourceState? PowerResourceProbe();
    public delegate DsmState? DsmProbe();
    public delegate BgrtState? BgrtProbe();
    public delegate SpcrState? SpcrProbe();
    public delegate Dbg2State? Dbg2Probe();
    public delegate TablesSummaryState? TablesProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PowerResourceProbe? powerResource = null,
        DsmProbe? dsm = null,
        BgrtProbe? bgrt = null,
        SpcrProbe? spcr = null,
        Dbg2Probe? dbg2 = null,
        TablesProbe? tables = null)
        =>
        [
            PowerResourceFact(at, powerResource),
            DsmFact(at, dsm),
            BgrtFact(at, bgrt),
            SpcrFact(at, spcr),
            Dbg2Fact(at, dbg2),
            TablesSummaryFact(at, tables),
        ];

    // ── AC-004 電源資源 _PRx ──────────────────────────────────────────────

    public static HardwareFact PowerResourceFact(DateTimeOffset at, PowerResourceProbe? probe = null)
    {
        PowerResourceState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PowerResourceKey, "電源資源 _PRx", FactAvailability.NotApplicable,
                "本版不做 AML 評估層——_PRx 的內容在 AML 位元組碼裡，沒有評估層就沒有物件可數（不假裝解出來）");

        var v = s;
        if (v.Objects < MinPowerResources)
            return Unavailable(at, PowerResourceKey, "電源資源 _PRx", FactAvailability.NotApplicable,
                $"只找到 {v.Objects} 個 PowerResource 物件（門檻 {MinPowerResources} 個）——單一資源沒什麼好匯總的");

        return new HardwareFact(PowerResourceKey, Cat, "電源資源 _PRx",
            $"PowerResource 物件 {v.Objects} 個・引用它們的表 {v.ReferencingTables} 張・涵蓋電源狀態 {v.PowerStates} 種。" +
            "<b>只到存在性與計數</b>：_PRx 物件的內容（哪些裝置共用、切換順序）在 AML 位元組碼裡，" +
            "要解出來需要評估層——本專案沒有，所以這一項回答的是「這台機器的韌體宣告了幾個電源資源」，" +
            "不是「電源管理的實際行為」。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Objects);
    }

    // ── AC-005 _DSM GUID 清單 ────────────────────────────────────────────

    public static HardwareFact DsmFact(DateTimeOffset at, DsmProbe? probe = null)
    {
        DsmState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DsmKey, "_DSM GUID 清單", FactAvailability.NotApplicable,
                "本版不做 AML 評估層——_DSM 是裝置方法，沒有評估層就沒有 GUID 可列（查詢失敗如實說）");

        var v = s;
        if (v.Tables == 0)
            return Unavailable(at, DsmKey, "_DSM GUID 清單", FactAvailability.NotApplicable,
                "沒有表宣告 _DSM 方法——不適用，不是錯誤");

        string shape = v.ShapesValid
            ? "所有 GUID 皆為 16 位元組"
            : "<b>有 GUID 長度不合 16 位元組</b>——表可能有解析偏移錯或廠商自訂寫法，值得留意";
        return new HardwareFact(DsmKey, Cat, "_DSM GUID 清單",
            $"宣告 _DSM 的表 {v.Tables} 張・不同 GUID {v.DistinctGuids} 個・{shape}。" +
            "<b>只到清單與計數</b>：GUID 的<b>語意</b>（哪一個是亮度控制、哪一個是電源管理）不在表裡，" +
            "要靠規格書對照——本項不代查、不猜測 GUID 的用途；" +
            "只列 GUID 本身（那是公開的介面識別碼，不含機器或使用者資訊）。",
            "個", Source, FactTrustLevel.Reported, false, at, v.DistinctGuids);
    }

    // ── AC-011 BGRT ───────────────────────────────────────────────────────

    public static HardwareFact BgrtFact(DateTimeOffset at, BgrtProbe? probe = null)
    {
        BgrtState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || !s.Present)
            return Unavailable(at, BgrtKey, "BGRT 開機 logo 表", FactAvailability.NotApplicable,
                "韌體沒有提供 BGRT 表——不適用（少見但不代表有問題，許多主機板不填這張表）");

        const byte BgrtImageTypeBitmap = 0;
        var v = s;
        string typeName = v.ImageType == BgrtImageTypeBitmap ? "點陣圖" : $"型別 {v.ImageType}（未收錄）";
        return new HardwareFact(BgrtKey, Cat, "BGRT 開機 logo 表",
            $"版本 {v.Version}・映像型別 {typeName}・映像位址{(v.ImageNonZero ? "非零" : "<b>為零</b>")}・" +
            $"校驗和{(v.ChecksumOk ? "正確" : "<b>不正確</b>")}。" +
            "<b>有表不等於功能可用</b>：BGRT 描述開機時顯示的商標圖，但實際顯示由韌體與開機路徑決定——" +
            "本項只陳述表本身的存在與欄位，不宣稱開機畫面會出現什麼。",
            "", Source, FactTrustLevel.Reported, false, at);
    }

    // ── AC-012 SPCR ───────────────────────────────────────────────────────

    public static HardwareFact SpcrFact(DateTimeOffset at, SpcrProbe? probe = null)
    {
        SpcrState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || !s.Present)
            return Unavailable(at, SpcrKey, "SPCR 序列主控台", FactAvailability.NotApplicable,
                "韌體沒有提供 SPCR 表——不適用（桌上型主機板通常不填這張表，它主要給伺服器與嵌入式）");

        var v = s;
        return new HardwareFact(SpcrKey, Cat, "SPCR 序列主控台",
            $"介面型別 {v.InterfaceType}・中斷模式 {v.InterruptMode}・" +
            $"鮑率{(v.BaudNonZero ? "已設定" : "<b>未設定</b>")}・主控台 {v.Consoles} 個。" +
            "<b>配置事實</b>：SPCR 描述「韌體告訴作業系統的序列主控台」——" +
            "它是伺服器與嵌入式平台的第一手除錯管道；本項只讀配置，不開啟任何序列埠、不送任何字元。",
            "", Source, FactTrustLevel.Reported, false, at, v.Consoles);
    }

    // ── AC-013 DBG2 ───────────────────────────────────────────────────────

    public static HardwareFact Dbg2Fact(DateTimeOffset at, Dbg2Probe? probe = null)
    {
        Dbg2State? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || !s.Present)
            return Unavailable(at, Dbg2Key, "DBG2 除錯埠", FactAvailability.NotApplicable,
                "韌體沒有提供 DBG2 表——不適用，不是錯誤");

        var v = s;
        string addr = v.NonZeroAddresses == v.Devices
            ? "全部有非零位址"
            : $"{v.NonZeroAddresses}/{v.Devices} 個有非零位址";
        return new HardwareFact(Dbg2Key, Cat, "DBG2 除錯埠",
            $"除錯裝置 {v.Devices} 個・埠型別 {v.PortTypes}・{addr}。" +
            "<b>埠存在性</b>：DBG2 宣告核心除錯可用的實體埠；有宣告不等於埠接得出來——" +
            "本項只讀表，<b>不開啟、不寫入、不探測</b>任何除錯埠（那是另一種性質的操作）。",
            "個", Source, FactTrustLevel.Reported, false, at, v.Devices);
    }

    // ── AC-018 ACPI 表匯總頁 ──────────────────────────────────────────────

    public static HardwareFact TablesSummaryFact(DateTimeOffset at, TablesProbe? probe = null)
    {
        TablesSummaryState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, TablesKey, "ACPI 表匯總", FactAvailability.ReadError,
                "ACPI 表列舉失敗——讀不到就是不猜（多半是未提權或平台不支援）");

        var v = s;
        if (v.Total == 0)
            return Unavailable(at, TablesKey, "ACPI 表匯總", FactAvailability.NotApplicable,
                "沒有列舉到任何 ACPI 表——不適用，不是錯誤");

        string checksum = v.ChecksumBad == 0
            ? $"校驗和全部正確（{v.ChecksumOk} 張，0 是值）"
            : $"校驗和正確 {v.ChecksumOk}・<b>錯誤 {v.ChecksumBad}</b>——韌體表損毀或廠商未正確填寫，值得留意";
        return new HardwareFact(TablesKey, Cat, "ACPI 表匯總",
            $"表 {v.Total} 張・不同簽章 {v.DistinctSignatures} 個・{checksum}・最高版本 {v.HighestVersion}。" +
            "<b>與逐表列舉分工</b>：逐表的簽章、OEM 與位址在既有的 ACPI 表清單裡，" +
            "這一項只談「這一台機器上有幾種表、版本分布如何、校驗和有沒有問題」——" +
            "匯總視角看的是韌體整體健康，不是某一張表的內容。",
            "張", Source, FactTrustLevel.Reported, false, at, v.Total);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
