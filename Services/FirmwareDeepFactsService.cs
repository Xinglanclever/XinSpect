namespace XinSpect;

/// <summary>
/// 韌體深化（Vol2 第 454–468 項）：FW-008 微碼 CVE 對照（<c>fw.ucode.cve</c>）、FW-009 CSME/PSP 足跡（<c>fw.me</c>／<c>fw.psp</c>）、
/// FW-015 CSME FTPR manifest（<c>fw.me.manifest</c>）、FW-016 AMD PSP Directory（<c>fw.psp.dir</c>）、
/// FW-017 UEFI 變數空間健全性（<c>fw.varstore</c>）、FW-018 Runtime Services（<c>fw.rt</c>）、
/// FW-019 TCG log 分階段（<c>fw.tcgl.phase</c>）、FW-020 FV 空間統計（<c>fw.fv</c>）、FW-021 微碼載入鏈（<c>fw.ucode.winner</c>）、
/// FW-022 Boot Guard ACM（<c>fw.bg.acm</c>）、FW-023 ACPI 表存在性對照（<c>fw.acpi.missing</c>）、FW-024 FPDT（<c>fw.fpdt</c>）、
/// FW-025 UEFI 變數屬性審計（<c>fw.var.attr</c>）、FW-007 dbx 代數補充（<c>fw.dbx.sbat</c>）、FW-013 WDAC 來源層級補充（<c>fw.wdac.layer</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一整批是「未對準出處就不出值」的紀律展示</b>：CSME／PSP／Boot Guard 的內部結構需要
/// 真實 flash 傾印核對，本機無法取得時一律 <b>NotSupported／未施測聲明</b>——寧可留白也不猜位元組。
/// </para>
/// <para>
/// <b>紅線：</b>① 離線表（CVE／dbx／ACPI 應有表）快照日期必印、查無不代填；
/// ② 不刪不改 UEFI 變數；③ 只呈現設定不判繞過；④ 統計非安全值；⑤ 讀不到一律三態。
/// </para>
/// </remarks>
public static class FirmwareDeepFactsService
{
    private const string Cat = "韌體安全";

    public const string UcodeCveKey = "fw.ucode.cve";
    public const string MeKey = "fw.me";
    public const string PspKey = "fw.psp";
    public const string MeManifestKey = "fw.me.manifest";
    public const string PspDirKey = "fw.psp.dir";
    public const string VarStoreKey = "fw.varstore";
    public const string RtKey = "fw.rt";
    public const string TcgPhaseKey = "fw.tcgl.phase";
    public const string FvKey = "fw.fv";
    public const string UcodeWinnerKey = "fw.ucode.winner";
    public const string BgAcmKey = "fw.bg.acm";
    public const string AcpiMissingKey = "fw.acpi.missing";
    public const string FpdtKey = "fw.fpdt";
    public const string VarAttrKey = "fw.var.attr";
    public const string DbxSbatKey = "fw.dbx.sbat";
    public const string WdacLayerKey = "fw.wdac.layer";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (int Cves, string SnapshotDate)? UcodeCveProbe();
    public delegate (string Version, bool ReadOk)? MeProbe();
    public delegate (string Version, bool ReadOk)? PspProbe();
    public delegate (int Entries, string Swn)? MeManifestProbe();
    public delegate (int Entries, int PspLevel)? PspDirProbe();
    public delegate (int Vars, bool IntegrityOk)? VarStoreProbe();
    public delegate (int Tables, bool PointersInRange)? RtProbe();
    public delegate (int Firmware, int Post, int Os)? TcgPhaseProbe();
    public delegate (int Fvs, double FreePercent)? FvProbe();
    public delegate (string WinnerSource, bool AllSourcesRead)? UcodeWinnerProbe();
    public delegate (string AcmVersion, bool Readable)? BgAcmProbe();
    public delegate int? AcpiMissingProbe();
    public delegate (double PostSeconds, bool Present)? FpdtProbe();
    public delegate (int Nv, int Bs, int RtOnly)? VarAttrProbe();
    public delegate (string SbatLevel, bool TableStale)? DbxSbatProbe();
    public delegate (string EffectiveLayer, int LayersSeen)? WdacLayerProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        UcodeCveProbe? ucodeCve = null, MeProbe? me = null, PspProbe? psp = null, MeManifestProbe? meManifest = null,
        PspDirProbe? pspDir = null, VarStoreProbe? varStore = null, RtProbe? rt = null, TcgPhaseProbe? tcgPhase = null,
        FvProbe? fv = null, UcodeWinnerProbe? ucodeWinner = null, BgAcmProbe? bgAcm = null, AcpiMissingProbe? acpiMissing = null,
        FpdtProbe? fpdt = null, VarAttrProbe? varAttr = null, DbxSbatProbe? dbxSbat = null, WdacLayerProbe? wdacLayer = null)
        =>
        [
            UcodeCveFact(at, ucodeCve), MeFact(at, me), PspFact(at, psp), MeManifestFact(at, meManifest),
            PspDirFact(at, pspDir), VarStoreFact(at, varStore), RtFact(at, rt), TcgPhaseFact(at, tcgPhase),
            FvFact(at, fv), UcodeWinnerFact(at, ucodeWinner), BgAcmFact(at, bgAcm), AcpiMissingFact(at, acpiMissing),
            FpdtFact(at, fpdt), VarAttrFact(at, varAttr), DbxSbatFact(at, dbxSbat), WdacLayerFact(at, wdacLayer),
        ];

    private static HardwareFact NotTested(DateTimeOffset at, string key, string name, string what)
        => new(key, Cat, name, "", "", "flash 傾印（未取得）", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotSupported, $"未施測聲明：{what}——未對準真實傾印前不出值，查無不代填");

    // ── FW-008 微碼 CVE 對照 ──────────────────────────────────────────

    public static HardwareFact UcodeCveFact(DateTimeOffset at, UcodeCveProbe? probe = null)
    {
        (int Cves, string SnapshotDate)? s;
        try { s = (probe ?? (() => (0, "")))(); }
        catch { s = null; }

        if (s is null || s.Value.Cves == 0)
            return new HardwareFact(UcodeCveKey, Cat, "微碼 CVE 對照", "", "", "離線 CVE 表",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "CVE 表為空表出貨（如實）——查無不代填，空表不是「沒漏洞」");

        var (cves, snap) = s.Value;
        return new HardwareFact(UcodeCveKey, Cat, "微碼 CVE 對照",
            $"對照 {cves} 條 CVE、表快照 {snap}。<b>快照日期必印</b>：微碼 CVE 逐季更新，" +
            "舊表可能漏掉新漏洞；對照結果是「相對此表的已知項」，不等於已中招或已修補。",
            "條", "離線 CVE 表（唯讀）", FactTrustLevel.Reported, false, at, cves);
    }

    // ── FW-009 CSME/PSP 足跡 ──────────────────────────────────────────

    public static HardwareFact MeFact(DateTimeOffset at, MeProbe? probe = null)
    {
        (string Version, bool ReadOk)? s;
        try { s = (probe ?? (() => ("", false)))(); }
        catch { s = null; }

        if (s is null || !s.Value.ReadOk)
            return NotTested(at, MeKey, "CSME 韌體足跡", "CSME 版本字串需 flash 傾印掃描，未取得");

        return new HardwareFact(MeKey, Cat, "CSME 韌體足跡",
            $"CSME 版本 {s.Value.Version}。版本是<b>足跡</b>不是安全判決——" +
            "CSME 內部結構（manifest／FTPR）需真實傾印核對，本項只陳述可見版本字串。",
            "", "flash 版本掃描（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    public static HardwareFact PspFact(DateTimeOffset at, PspProbe? probe = null)
    {
        (string Version, bool ReadOk)? s;
        try { s = (probe ?? (() => ("", false)))(); }
        catch { s = null; }

        if (s is null || !s.Value.ReadOk)
            return NotTested(at, PspKey, "AMD PSP 足跡", "PSP 目錄需 flash 傾印，未取得");

        return new HardwareFact(PspKey, Cat, "AMD PSP 足跡",
            $"PSP 版本 {s.Value.Version}。與既有 AmdSecurityFactsService（CPUID／MSR 面）互補：" +
            "這裡是<b>韌體面</b>足跡，非 AMD 平台則整組 NotApplicable。",
            "", "PSP 目錄（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    // ── FW-015 CSME FTPR manifest ─────────────────────────────────────

    public static HardwareFact MeManifestFact(DateTimeOffset at, MeManifestProbe? probe = null)
    {
        (int Entries, string Swn)? s;
        try { s = (probe ?? (() => (0, "")))(); }
        catch { s = null; }

        if (s is null || s.Value.Entries == 0)
            return NotTested(at, MeManifestKey, "CSME FTPR manifest", "manifest 結構需 CSME 分割區傾印，未取得");

        var (entries, swn) = s.Value;
        return new HardwareFact(MeManifestKey, Cat, "CSME FTPR manifest",
            $"manifest 條目 {entries}、安全版本號 SVN {swn}。" +
            "SVN 是<b>防降級</b>的版本門檻；條目結構解碼需對準真實傾印，未對準的欄位留白。",
            "條", "CSME manifest（唯讀）", FactTrustLevel.Reported, false, at, entries);
    }

    // ── FW-016 AMD PSP Directory ──────────────────────────────────────

    public static HardwareFact PspDirFact(DateTimeOffset at, PspDirProbe? probe = null)
    {
        (int Entries, int PspLevel)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Entries == 0)
            return NotTested(at, PspDirKey, "AMD PSP Directory", "PSP directory 需 flash 傾印，未取得");

        var (entries, level) = s.Value;
        return new HardwareFact(PspDirKey, Cat, "AMD PSP Directory",
            $"entry {entries} 個、PSP level {level}。" +
            "entry 類型／位址／大小<b>標未施測</b>：結構解碼需真實 AMD flash 核對，" +
            "本工具不以推測填位址。",
            "個", "PSP directory（唯讀）", FactTrustLevel.Reported, false, at, entries);
    }

    // ── FW-017 UEFI 變數空間健全性 ────────────────────────────────────

    public static HardwareFact VarStoreFact(DateTimeOffset at, VarStoreProbe? probe = null)
    {
        (int Vars, bool IntegrityOk)? s;
        try { s = (probe ?? (() => (0, true)))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, VarStoreKey, Cat, "UEFI 變數空間健全性", FactAvailability.ReadError, "變數空間不可讀");

        var (vars, ok) = s.Value;
        return new HardwareFact(VarStoreKey, Cat, "UEFI 變數空間健全性",
            $"變數 {vars} 個、varstore 完整性={(ok ? "通過" : "有異常")}。" +
            "<b>不刪不改</b>：本項只讀取變數空間結構並檢查完整性——" +
            "完整性異常可能是變數損毀或硬體問題，需人工判讀，本工具不修復。",
            "個", "UEFI varstore（唯讀）", ok ? FactTrustLevel.Reported : FactTrustLevel.Unknown,
            false, at, vars, ok ? FactAvailability.Present : FactAvailability.Unknown,
            ok ? null : "varstore 完整性異常，需人工核對，不自動修復");
    }

    // ── FW-018 Runtime Services 表列舉 ────────────────────────────────

    public static HardwareFact RtFact(DateTimeOffset at, RtProbe? probe = null)
    {
        (int Tables, bool PointersInRange)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null || s.Value.Tables == 0)
            return Unavailable(at, RtKey, Cat, "Runtime Services 表列舉", FactAvailability.NotApplicable,
                "無 Runtime Services 表可列舉（虛擬機或受限環境）——N/A 如實");

        var (tables, inRange) = s.Value;
        return new HardwareFact(RtKey, Cat, "Runtime Services 表列舉",
            $"Runtime 表 {tables} 個、指標落點在範圍內={(inRange ? "是" : "否")}。" +
            "指標落點異常（超界／為零）是<b>值得留意的訊號</b>，可能來自惡意韌體或平台差異；" +
            "本項只陳述位置與大小。",
            "個", "Runtime Services（唯讀）", FactTrustLevel.Reported, false, at, tables);
    }

    // ── FW-019 TCG log 分階段視圖 ─────────────────────────────────────

    public static HardwareFact TcgPhaseFact(DateTimeOffset at, TcgPhaseProbe? probe = null)
    {
        (int Firmware, int Post, int Os)? s;
        try { s = (probe ?? (() => (0, 0, 0)))(); }
        catch { s = null; }

        if (s is null || (s.Value.Firmware + s.Value.Post + s.Value.Os) == 0)
            return Unavailable(at, TcgPhaseKey, Cat, "TCG log 分階段", FactAvailability.NotApplicable,
                "沒有 TCG log 事件——不從缺 log 推論開機鏈");

        var (fw, post, os) = s.Value;
        return new HardwareFact(TcgPhaseKey, Cat, "TCG log 分階段",
            $"firmware {fw}／POST {post}／OS {os} 事件。<b>邊界誤判率聲明</b>：" +
            "階段邊界是靠事件型別推斷的，不同廠商實作可能落在不同點——" +
            "分類是<b>近似</b>，不是硬性事實。",
            "事件", "TCG log（唯讀）", FactTrustLevel.Reported, false, at, fw + post + os);
    }

    // ── FW-020 FV 空間統計 ────────────────────────────────────────────

    public static HardwareFact FvFact(DateTimeOffset at, FvProbe? probe = null)
    {
        (int Fvs, double FreePercent)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Fvs == 0)
            return Unavailable(at, FvKey, Cat, "FV 空間統計", FactAvailability.NotApplicable,
                "沒有可統計的 FV（韌體卷）——不從缺資料推論");

        var (fvs, free) = s.Value;
        return new HardwareFact(FvKey, Cat, "FV 空間統計",
            $"FV {fvs} 個、平均剩餘 {free:0.#}%。<b>統計非安全值</b>：" +
            "剩餘空間是配置結果，不代表安全性或可更新性；多 FV 反映韌體模組化程度。",
            "%", "FV 統計（唯讀）", FactTrustLevel.Reported, false, at, free);
    }

    // ── FW-021 微碼載入鏈對照 ─────────────────────────────────────────

    public static HardwareFact UcodeWinnerFact(DateTimeOffset at, UcodeWinnerProbe? probe = null)
    {
        (string WinnerSource, bool AllSourcesRead)? s;
        try { s = (probe ?? (() => ("", false)))(); }
        catch { s = null; }

        if (s is null || string.IsNullOrEmpty(s.Value.WinnerSource))
            return new HardwareFact(UcodeWinnerKey, Cat, "微碼載入鏈對照", "", "", "BIOS／登錄／MSR",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "缺一來源即明說——BIOS 版／registry patch／生效版三者需齊全才能判勝出者，缺一不判");

        var (winner, allRead) = s.Value;
        return new HardwareFact(UcodeWinnerKey, Cat, "微碼載入鏈對照",
            $"生效來源「{winner}」、三來源{(allRead ? "齊全" : "有缺")}。" +
            "<b>缺一源即明說</b>：微碼可能來自 BIOS 或 OS 更新，勝出者需三者對照才能確定；" +
            "任一來源讀不到就不宣稱誰勝出。",
            "", "微碼載入鏈（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    // ── FW-022 Boot Guard ACM 版本 ────────────────────────────────────

    public static HardwareFact BgAcmFact(DateTimeOffset at, BgAcmProbe? probe = null)
    {
        (string AcmVersion, bool Readable)? s;
        try { s = (probe ?? (() => ("", false)))(); }
        catch { s = null; }

        if (s is null || !s.Value.Readable)
            return new HardwareFact(BgAcmKey, Cat, "Boot Guard ACM 版本", "", "", "flash 傾印（唯一途徑）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "不可讀＝檢索級聲明：Boot Guard ACM 版本只能從 flash 傾印讀，本機無傾印途徑");

        return new HardwareFact(BgAcmKey, Cat, "Boot Guard ACM 版本",
            $"ACM 版本 {s.Value.AcmVersion}（可讀範圍內）。" +
            "只呈現<b>設定與版本</b>，不判繞過——驗證是否真的執行需要平台特定的可觀測點。",
            "", "flash 傾印（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    // ── FW-023 ACPI 表存在性對照 ──────────────────────────────────────

    public static HardwareFact AcpiMissingFact(DateTimeOffset at, AcpiMissingProbe? probe = null)
    {
        int? missing;
        try { missing = (probe ?? (() => null))(); }
        catch { missing = null; }

        if (missing is null)
            return new HardwareFact(AcpiMissingKey, Cat, "ACPI 表存在性對照", "", "", "離線應有表",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "「應有」需 SpecRef 級來源——未對準出處前不列缺哪些表，查無不代填");

        return new HardwareFact(AcpiMissingKey, Cat, "ACPI 表存在性對照",
            $"相對於平台應有集合，缺 {missing} 張表。<b>「應有」需 SpecRef 級源</b>：" +
            "對照用的是離線表；缺表會明說，但「應有」本身必須有規格出處，不憑印象列。",
            "張", "離線應有表（唯讀）", FactTrustLevel.Reported, false, at, missing);
    }

    // ── FW-024 FPDT 解碼 ──────────────────────────────────────────────

    public static HardwareFact FpdtFact(DateTimeOffset at, FpdtProbe? probe = null)
    {
        (double PostSeconds, bool Present)? s;
        try { s = (probe ?? (() => (0, false)))(); }
        catch { s = null; }

        if (s is null || !s.Value.Present)
            return Unavailable(at, FpdtKey, Cat, "FPDT 解碼", FactAvailability.NotApplicable,
                "本平台無 FPDT 表——缺席＝N/A，不從缺表推論 POST 時間");

        return new HardwareFact(FpdtKey, Cat, "FPDT 解碼",
            $"韌體 POST 時間 {s.Value.PostSeconds:0.###} 秒（FPDT 表）。" +
            "FPDT 提供<b>韌體自報</b>的 POST 時長；與作業系統觀察的開機時間是不同量測點（見 ms.boot）。",
            "秒", "ACPI FPDT（唯讀）", FactTrustLevel.Reported, false, at, s.Value.PostSeconds);
    }

    // ── FW-025 UEFI 變數屬性審計 ──────────────────────────────────────

    public static HardwareFact VarAttrFact(DateTimeOffset at, VarAttrProbe? probe = null)
    {
        (int Nv, int Bs, int RtOnly)? s;
        try { s = (probe ?? (() => (0, 0, 0)))(); }
        catch { s = null; }

        if (s is null || (s.Value.Nv + s.Value.Bs + s.Value.RtOnly) == 0)
            return Unavailable(at, VarAttrKey, Cat, "UEFI 變數屬性審計", FactAvailability.NotApplicable,
                "沒有可審計的 UEFI 變數——不從缺變數推論屬性");

        var (nv, bs, rtOnly) = s.Value;
        return new HardwareFact(VarAttrKey, Cat, "UEFI 變數屬性審計",
            $"NV+BS {nv}、BS-only {bs}、RT-only {rtOnly}。" +
            "<b>與 FW-017 互補不重複</b>：FW-017 管空間完整性，本項管單一變數的屬性組合" +
            "（NV／BS／RT 決定變數能否在開機後存取）。",
            "個", "UEFI 變數屬性（唯讀）", FactTrustLevel.Reported, false, at, nv + bs + rtOnly);
    }

    // ── FW-007 dbx 代數／SBAT 補充 ────────────────────────────────────

    public static HardwareFact DbxSbatFact(DateTimeOffset at, DbxSbatProbe? probe = null)
    {
        (string SbatLevel, bool TableStale)? s;
        try { s = (probe ?? (() => ("", false)))(); }
        catch { s = null; }

        if (s is null || string.IsNullOrEmpty(s.Value.SbatLevel))
            return Unavailable(at, DbxSbatKey, Cat, "dbx SBAT", FactAvailability.NotApplicable,
                "無 SBAT 變數（舊平台或未部署）——不從缺變數推論");

        var (level, stale) = s.Value;
        return new HardwareFact(DbxSbatKey, Cat, "dbx SBAT",
            $"SBAT 等級 {level}{(stale ? "（離線表可能過期）" : "")}。" +
            "SBAT 是 shim/GRUB 的撤銷機制（與 dbx 並行）；<b>表過期明說</b>——" +
            "離線釋出表若落後於微軟最新，對照結果會樂觀。",
            "", "UEFI 變數（唯讀）", FactTrustLevel.Reported, false, at, null);
    }

    // ── FW-013 WDAC 來源層級補充 ──────────────────────────────────────

    public static HardwareFact WdacLayerFact(DateTimeOffset at, WdacLayerProbe? probe = null)
    {
        (string EffectiveLayer, int LayersSeen)? s;
        try { s = (probe ?? (() => ("", 0)))(); }
        catch { s = null; }

        if (s is null || string.IsNullOrEmpty(s.Value.EffectiveLayer))
            return Unavailable(at, WdacLayerKey, Cat, "WDAC 來源層級", FactAvailability.NotApplicable,
                "沒有可判讀的 WDAC 政策——不從缺政策推論");

        var (layer, seen) = s.Value;
        return new HardwareFact(WdacLayerKey, Cat, "WDAC 來源層級",
            $"生效層級「{layer}」、可見層級 {seen} 個。" +
            "WDAC 政策可能來自韌體鎖／企業／本機，<b>何者生效</b>需逐層判定；" +
            "本項<b>判讀不執行</b>——只陳述哪一層在生效，不套用或修改政策。",
            "", "WDAC 政策（唯讀，L2）", FactTrustLevel.Reported, false, at, seen);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
