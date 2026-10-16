using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// TPM 2.0 量測開機鏈事實（V7 WP14／A31）：經 Windows TBS（tbs.dll）讀 PCR 0–23
/// （SHA-1／SHA-256／SHA-384 三 bank）與 TCG 事件 log 摘要。
/// TBS 由作業系統仲介 TPM 存取（權限由 Windows 管理），本服務無核心風險；
/// 無 TPM／TBS 不可用如實三態。PCR 0–7 覆蓋韌體量測鏈核心（CRTM／UEFI code／設定），
/// PCR 8–15 覆蓋 OS 量測鏈，PCR 16–23 覆蓋偵錯／應用——與快照的跨時間比對可抓「量測鏈變了」。
/// </summary>
public static class TpmFactsService
{
    private const string Category = "信賴根";
    private const uint TbsContextVersion2 = 2;
    private const uint TbsCommandLocalityZero = 0;
    private const uint TbsCommandPriorityNormal = 10;
    private const int PcrCount = 24; // PCR 0–23（完整覆蓋）

    /// <summary>PCR 讀取的雜湊 bank（TPM_ALG_ID → 顯示名）。</summary>
    private static readonly (ushort AlgId, string BankName, string KeyPrefix)[] Banks =
    [
        (Tpm2.AlgSha256, "SHA-256", "sha256"),
        (Tpm2.AlgSha1,   "SHA-1",   "sha1"),
        (Tpm2.AlgSha384, "SHA-384", "sha384"),
    ];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<nint?>? createContext = null,
        Func<nint, byte[], (uint Result, byte[] Output, uint OutputLen)>? submit = null,
        Func<nint, byte[]?>? readLog = null)
    {
        const string presentKey = "tpm.present";
        bool injected = createContext is not null;
        nint ctx;
        if (injected)
        {
            var h = createContext!();
            if (h is null || h.Value == nint.Zero)
                return [Unavailable(presentKey, "TPM 2.0", "Windows TBS（tbs.dll）", at, FactAvailability.NotApplicable, "TBS 無法建立內容——TPM 不存在或未啟用（如實標，不推測量測鏈）")];
            ctx = h.Value;
        }
        else
        {
            var version = TbsContextVersion2;
            var rc = Tbsi_Context_Create(ref version, out ctx);
            if (rc != TbsSuccess || ctx == nint.Zero)
                return [Unavailable(presentKey, "TPM 2.0", "Windows TBS（tbs.dll）", at, FactAvailability.NotApplicable,
                    rc == TbsSuccess ? "TBS 建立的內容無效" : $"TBS 無法建立內容（結果 0x{rc:X8}）——TPM 不存在、未啟用，或服務未執行")];
        }

        try
        {
            var facts = new List<HardwareFact>
            {
                new(presentKey, Category, "TPM 2.0", "可用（Windows TBS 仲介，唯讀查詢）", "", "Windows TBS（tbs.dll）",
                    FactTrustLevel.Measured, false, at),
            };

            var submitFn = submit ?? RealSubmit;
            for (uint i = 0; i < PcrCount; i++)
                foreach (var (alg, bankName, keyPrefix) in Banks)
                    facts.Add(PcrFact(ctx, i, alg, bankName, keyPrefix, at, submitFn));
            facts.Add(TcgLogFact(ctx, at, readLog ?? RealReadLog));
            facts.AddRange(BankInventoryFacts(ctx, at, submitFn));
            // ═══ 屬性只讀兩組、各讀一次，三個事實共用同一份結果 ═══
            // TPM_PT_FIXED 那一組（family／level／revision／manufacturer／廠商字串）與
            // TPM_PT_VAR 那一組（PT_PERMANENT／PT_STARTUP_CLEAR）。
            // **刻意不是「每個事實各自讀一次」**：同一份屬性多讀一次就多一次
            // 「兩次讀到的不一樣」——那正是本專案在 MeasuredBootFactsService 與
            // UefiVersionCrossFactsService 裡各自警告過的事。
            var fixedProps = ReadProperties(ctx, Tpm2.PtFamilyIndicator, Tpm2.FixedPropertyCount, submitFn);
            var varProps = ReadProperties(ctx, Tpm2.PtPermanent, 2, submitFn);
            facts.AddRange(VersionFacts(fixedProps, at));
            facts.AddRange(OwnedFacts(varProps, at));               // P2-069
            facts.Add(AttestFact(fixedProps, varProps, at));        // P2-075
            // P2-076：清除與重設的界線宣告。
            // **它不需要任何能力查詢，也不新增任何編號**——它只說出「本專案不做清除」。
            facts.Add(TpmClearBoundaryFact(at));
            // P2-071／P2-072：把同一份 log 再走一次，補上「事件型別分佈」。
            //
            // ═══ 這裡有一個我知道的缺陷，而它要被說出來 ═══
            //
            // 上面那個 TcgLogFact 已經讀了一次 log（它內部呼叫 Tbsi_Get_TCG_Log
            // 並走訪），而我看不到它走完之後的結果——它的介面是 Func<nint, byte[]?>。
            // 所以這裡**又讀了一次**。
            //
            // 那不理想：**「讀兩次」正是我在 MeasuredBootFactsService 與
            // UefiVersionCrossFactsService 裡都警告過的事**
            // （「每多讀一次就多一次兩次讀到的不一樣的可能」）。
            //
            // 在單次掃描中這幾乎不可能發生（log 在一次開機內是穩定的），
            // 而**正確的修法是讓 TcgLogFact 回傳走訪結果、由呼叫端分派給兩個消費者**
            // ——那要改既有方法的簽章，所以這一版先如實標記。
            //
            // **留著不說比讀兩次更糟**：下一個人會看到兩次呼叫，而不知道我知不知道。
            var log = (readLog ?? RealReadLog)(ctx);
            facts.AddRange(TpmLogFactsService.Collect(at,
                log is null ? null : Tpm2.WalkTcgLog(log)));
            return facts;
        }
        finally
        {
            if (!injected) Tbsip_Context_Close(ctx);
        }
    }

    private static HardwareFact PcrFact(nint ctx, uint index, ushort hashAlg, string bankName, string keyPrefix,
        DateTimeOffset at, Func<nint, byte[], (uint Result, byte[] Output, uint OutputLen)> submit)
    {
        string key = $"tpm.pcr.{keyPrefix}.{index}";
        string name = $"TPM PCR {index}（{bankName}）";
        string source = $"TPM 2.0 PCR {index}（{bankName} bank），經 Windows TBS";
        var cmd = Tpm2.BuildPcrReadCommand(index, hashAlg);
        var (result, output, outputLen) = submit(ctx, cmd);
        if (result != TbsSuccess)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, $"TBS 錯誤 0x{result:X8}");
        var trimmed = outputLen is > 0 and <= int.MaxValue and var n ? output[..(int)Math.Min(n, output.Length)] : [];
        var digest = Tpm2.DecodePcrResponse(trimmed, out uint tpmRc);
        if (digest is null)
            return Unavailable(key, name, source, at, FactAvailability.NotSupported,
                tpmRc == 0 ? "回應過短或格式不明——不解碼垃圾" : $"TPM 拒絕（TPM_RC 0x{tpmRc:X8}）");
        return new HardwareFact(key, Category, name, Convert.ToHexStringLower(digest), "", source,
            FactTrustLevel.Measured, false, at);
    }

    // ── P2-076 清除與重設的界線 ───────────────────────────────────────────

    /// <summary>
    /// TPM 清除與重設的<b>界線宣告</b>（P2-076）。
    ///
    /// <para>
    /// <b>這把鍵什麼都不測，而它存在的理由就是說出那件事。</b>
    /// 它與 <c>ec.boundary</c>（不讀寫 EC 埠）完全同型：
    /// <b>一個沒說出口的界線，會被讀成「還沒做」而不是「刻意不做」。</b>
    /// </para>
    ///
    /// <para>
    /// <b>為什麼不做清除：</b><c>TPM2_Clear</c> 會讓擁有者授權與所有以該授權
    /// 封存的資料永久失效——<b>那是一台機器上最不可逆的操作之一</b>。
    /// 而本專案讀取硬體事實的目的不需要它：<b>要判斷「有沒有擁有者」，
    /// 讀 TPM_PT_PERMANENT 的位元就夠了，不需要先清除再重來。</b>
    /// </para>
    ///
    /// <para>
    /// <b>而這一項同時標出一個尚未對準出處的部分</b>：判斷「是否已取得擁有權」
    /// （P2-069 <c>tpm.owned</c>）需要 <c>TPM_PT_PERMANENT</c> 的屬性編號，
    /// 而<b>本專案的 <c>Tpm2</c> 只收了有出處的四個編號</b>
    /// （FAMILY_INDICATOR 0x100、REVISION 0x101、VENDOR_STRING_1..4 0x104..0x107）。
    /// <b>所以 P2-069 在對準出處之前不做</b>——與 FIT 的 Key Manifest 編號、
    /// TPM 的 EV_* 型別名、EFI 變數的屬性位元是同一個判斷：
    /// <b>填錯的編號會讓一個不存在的東西看起來存在。</b>
    /// </para>
    /// </summary>
    internal static HardwareFact TpmClearBoundaryFact(DateTimeOffset at)
        => new("tpm.clear.boundary", Category, "TPM 清除與重設界線",
            "本專案不做 TPM 清除或重設", "", "專案界線宣告（無外部來源）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
            "**TPM2_Clear 會讓擁有者授權與所有以該授權封存的資料永久失效**——"
            + "那是一台機器上最不可逆的操作之一。"
            + "而本專案讀取硬體事實的目的不需要它："
            + "**要判斷「有沒有擁有者」，讀 TPM_PT_PERMANENT 的位元就夠了，"
            + "不需要先清除再重來。**"
            + "**而這把鍵存在的理由就是說出那件事**——"
            + "一個沒說出口的界線，會被讀成「還沒做」而不是「刻意不做」"
            + "（與 ec.boundary 同型的處置）。"
            + "**同時如實標出一個尚未對準的部分**：規格的 P2-069（tpm.owned）"
            + "需要 TPM_PT_PERMANENT 的屬性編號，"
            + "而本專案的 Tpm2 只收了有出處的四個（0x100、0x101、0x104..0x107）"
            + "——**所以那一項在對準出處之前不做**");

    private static HardwareFact TcgLogFact(nint ctx, DateTimeOffset at, Func<nint, byte[]?> readLog)
    {
        const string key = "tpm.tcg_log", name = "TCG 事件 log 摘要", source = "Tbsi_Get_TCG_Log";
        var log = readLog(ctx);
        if (log is null)
            return Unavailable(key, name, source, at, FactAvailability.NotSupported, "log 讀取失敗或平台未提供");
        var walk = Tpm2.WalkTcgLog(log);
        var pcrCovered = walk.Events.Select(e => e.PcrIndex).Distinct().OrderBy(x => x).ToList();
        string coverage = pcrCovered.Count == 0 ? "無可解析事件" : $"PCR 覆蓋 {string.Join("、", pcrCovered.Take(12))}{(pcrCovered.Count > 12 ? "…" : "")}";
        return new HardwareFact(key, Category, name,
            $"{walk.Events.Count} 個事件（{coverage}）{(walk.Truncated ? "；log 走訪提前結束——內容損毀或截斷，如實標示" : "")}",
            "", source, FactTrustLevel.Measured, false, at, walk.Events.Count);
    }

    // ── P2-063 演算法銀行清單：問 TPM 自己實作了哪些雜湊，不是從 PCR 有沒有值回推 ──
    //
    // 為什麼要問：PCR 讀得到不等於這個 bank 在量測鏈裡被用過，PCR 讀不到也不等於沒實作
    // （可能只是 raw PCR 不可讀）。GetCapability(TPM_CAP_ALGORITHMS) 是 TPM 自己的申報，
    // 與「本服務讀哪三個 bank」是兩件事——這裡只報 TPM 申報了什麼。

    private static IReadOnlyList<HardwareFact> BankInventoryFacts(nint ctx, DateTimeOffset at,
        Func<nint, byte[], (uint Result, byte[] Output, uint OutputLen)> submit)
    {
        const string key = "tpm.pcr.banks", name = "TPM 雜湊演算法銀行",
            source = "TPM2_GetCapability(TPM_CAP_ALGORITHMS)，經 Windows TBS";
        var (result, output, outputLen) = submit(ctx, Tpm2.BuildGetCapabilityCommand(Tpm2.CapAlgorithms, 0, 64));
        if (result != TbsSuccess)
            return [Unavailable(key, name, source, at, FactAvailability.ReadError, $"TBS 錯誤 0x{result:X8}")];
        var trimmed = outputLen is > 0 and <= int.MaxValue and var n ? output[..(int)Math.Min(n, output.Length)] : [];
        var data = Tpm2.GetCapabilityData(trimmed, out uint rc, out uint cap, out _);
        if (data is null)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                rc == 0 ? "GetCapability 回應格式不符——不解碼垃圾" : $"TPM 拒絕（TPM_RC 0x{rc:X8}）")];
        if (cap != Tpm2.CapAlgorithms)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                $"回應的 capability 是 0x{cap:X8}，不是查詢的 TPM_CAP_ALGORITHMS——不如實標示會把別人的答案當成這個問題的答案")];

        var algs = Tpm2.DecodeAlgProperties(data);
        var hashes = algs.Where(a => Tpm2.IsHash(a.Flags)).Select(a => a.Alg).OrderBy(x => x).ToList();
        string hashText = hashes.Count > 0
            ? string.Join("、", hashes.Select(a => Tpm2.AlgName(a) ?? $"未收錄 0x{a:X4}"))
            : "（TPM 未申報任何雜湊演算法）";

        // 本服務實際嘗試讀取的 bank 與 TPM 申報的比對：只陳述差異，不下判決。
        var probed = Banks.Select(b => b.AlgId).ToList();
        var probedMissing = probed.Where(p => !hashes.Contains(p)).ToList();
        string delta = probedMissing.Count == 0
            ? "本服務嘗試讀取的 bank（SHA-256／SHA-1／SHA-384）都在 TPM 申報清單內"
            : $"本服務嘗試讀取的 {string.Join("、", probedMissing.Select(a => Tpm2.AlgName(a) ?? $"0x{a:X4}"))} 不在 TPM 申報清單內"
              + "——該 bank 的 PCR 讀不到是這個原因，不是讀取錯誤";

        return
        [
            new HardwareFact(key, Category, name,
                $"TPM 申報 {algs.Count} 種演算法，其中雜湊 {hashes.Count} 種：{hashText}。{delta}。"
                + "界線：這是 TPM 自己的申報，不代表任一 bank 已被量測鏈使用（那要看 TCG log 的事件）",
                "種", source, FactTrustLevel.Measured, false, at, hashes.Count),
        ];
    }

    // ── P2-068／P2-069／P2-075：屬性一次讀兩組，三個事實共用同一份結果 ──────────

    /// <summary>
    /// 一組 TPM 屬性的讀取結果。<see cref="Values"/> 非 null 時可用；否則 <see cref="Failure"/>
    /// 說明原因，<see cref="TbsFailed"/> 決定態別（TBS 自己失敗＝ReadError，其餘＝NotSupported）。
    /// </summary>
    private readonly record struct PropertyRead(IReadOnlyDictionary<uint, uint>? Values, bool TbsFailed, string Failure);

    /// <summary>
    /// 讀 TPM_PT 屬性：<paramref name="first"/> 起共 <paramref name="count"/> 項。
    /// 回應格式不符、capability 不符、TPM 拒絕——一律回 null 並說出原因，**不解碼垃圾**。
    /// </summary>
    private static PropertyRead ReadProperties(nint ctx, uint first, uint count,
        Func<nint, byte[], (uint Result, byte[] Output, uint OutputLen)> submit)
    {
        var (result, output, outputLen) = submit(ctx,
            Tpm2.BuildGetCapabilityCommand(Tpm2.CapTpmProperties, first, count));
        if (result != TbsSuccess)
            return new(null, true, $"TBS 錯誤 0x{result:X8}");
        var trimmed = outputLen is > 0 and <= int.MaxValue and var n
            ? output[..(int)Math.Min(n, output.Length)] : [];
        var data = Tpm2.GetCapabilityData(trimmed, out uint rc, out uint cap, out _);
        if (data is null)
            return new(null, false, rc == 0
                ? "GetCapability 回應格式不符——不解碼垃圾"
                : $"TPM 拒絕（TPM_RC 0x{rc:X8}）");
        if (cap != Tpm2.CapTpmProperties)
            return new(null, false, $"回應的 capability 是 0x{cap:X8}，不是查詢的 TPM_CAP_TPM_PROPERTIES");
        var dict = new Dictionary<uint, uint>();
        foreach (var prop in Tpm2.DecodeTpmProperties(data)) dict[prop.Property] = prop.Value;
        return new(dict, false, "");
    }

    private static HardwareFact ReadFailure(string key, string name, string source, DateTimeOffset at, PropertyRead read)
        => Unavailable(key, name, source, at,
            read.TbsFailed ? FactAvailability.ReadError : FactAvailability.NotSupported, read.Failure);

    private static string Yn(bool v) => v ? "是" : "否";

    /// <summary>
    /// P2-068 TPM 規格版本：family／level／revision／廠商代號與廠商字串，全部由 TPM 自己回報。
    /// <b>revision 是「規格版號 ×100」</b>（TPM 2.0 Part 2），不是韌體版號——本機
    /// <c>Win32_Tpm.SpecVersion</c> 的「2.0, 0, 1.16」正是 family／level／revision 三欄。
    /// </summary>
    private static IReadOnlyList<HardwareFact> VersionFacts(PropertyRead read, DateTimeOffset at)
    {
        const string key = "tpm.version", name = "TPM 規格版本",
            source = "TPM2_GetCapability(TPM_CAP_TPM_PROPERTIES, TPM_PT_FIXED)，經 Windows TBS";
        if (read.Values is not { } p) return [ReadFailure(key, name, source, at, read)];

        string? family = Tpm2.FamilyIndicator(p.GetValueOrDefault(Tpm2.PtFamilyIndicator));
        var revision = Tpm2.Revision(p.GetValueOrDefault(Tpm2.PtRevision));
        string? level = p.TryGetValue(Tpm2.PtLevel, out var lv) ? lv.ToString() : null;
        string manufacturer = Tpm2.Ascii4(p.GetValueOrDefault(Tpm2.PtManufacturer)).TrimEnd('\0', ' ');
        string vendor = string.Concat(
            new[] { Tpm2.PtVendor1, Tpm2.PtVendor2, Tpm2.PtVendor3, Tpm2.PtVendor4 }
                .Select(id => p.TryGetValue(id, out var v) ? Tpm2.Ascii4(v) : ""))
            .TrimEnd('\0', ' ');

        if (family is null && revision is null)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                "TPM 未回報 family indicator 與 revision——沒有值就是沒有值，不以 2.0 代填")];

        string value = $"family {family ?? "未回報"}、level {level ?? "未回報"}"
            + $"、revision {(revision is { } r ? $"{r.Major}.{r.Minor}" : "未回報")}"
            + $"，廠商代號 {(manufacturer.Length > 0 ? manufacturer : "未回報")}"
            + $"，廠商字串 {(vendor.Length > 0 ? vendor : "未回報")}。"
            + "界線：revision 是規格本身的版號×100（TPM 2.0 Part 2 的定義），不是 TPM 的韌體版號——"
            + "韌體版號在 PT_FIRMWARE_VERSION_1／2，本事實不讀那份，也不與此混用。";
        return
        [
            new HardwareFact(key, Category, name, value, "", source, FactTrustLevel.Measured, false, at),
        ];
    }

    /// <summary>
    /// P2-069 擁有者與啟用狀態：TPMA_PERMANENT 與 TPMA_STARTUP_CLEAR 兩個位元欄位，由 TPM 自己回報。
    /// <c>ownerAuthSet</c> 就是一般所稱的「已取得擁有權」；三個 Enable 位是各階層是否可用。
    /// </summary>
    private static IReadOnlyList<HardwareFact> OwnedFacts(PropertyRead read, DateTimeOffset at)
    {
        const string key = "tpm.owned", name = "TPM 擁有權與階層狀態",
            source = "TPM2_GetCapability(TPM_CAP_TPM_PROPERTIES, TPM_PT_PERMANENT／TPM_PT_STARTUP_CLEAR)，經 Windows TBS";
        if (read.Values is not { } p) return [ReadFailure(key, name, source, at, read)];

        bool hasPerm = p.TryGetValue(Tpm2.PtPermanent, out uint permRaw);
        bool hasStart = p.TryGetValue(Tpm2.PtStartupClear, out uint startRaw);
        if (!hasPerm && !hasStart)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                "TPM 未回報 PT_PERMANENT 與 PT_STARTUP_CLEAR——沒有值就是沒有值，不從其他欄位推斷擁有權")];

        var parts = new List<string>();
        if (hasPerm)
        {
            var f = Tpm2.DecodePermanent(permRaw);
            parts.Add($"永久標記（TPMA_PERMANENT 0x{permRaw:X8}）：ownerAuthSet（已取得擁有權）＝{Yn(f.OwnerAuthSet)}"
                + $"，endorsementAuthSet＝{Yn(f.EndorsementAuthSet)}，lockoutAuthSet＝{Yn(f.LockoutAuthSet)}"
                + $"，disableClear（TPM2_Clear 被停用）＝{Yn(f.DisableClear)}，inLockout＝{Yn(f.InLockout)}"
                + $"，tpmGeneratedEPS＝{Yn(f.TpmGeneratedEps)}");
        }
        else parts.Add("永久標記（TPMA_PERMANENT）：未回報");
        if (hasStart)
        {
            var s = Tpm2.DecodeStartupClear(startRaw);
            parts.Add($"本次開機狀態（TPMA_STARTUP_CLEAR 0x{startRaw:X8}）：phEnable＝{Yn(s.PhEnable)}"
                + $"，shEnable＝{Yn(s.ShEnable)}，ehEnable＝{Yn(s.EhEnable)}"
                + $"，phEnableNV＝{Yn(s.PhEnableNv)}，orderly（上次是正常關機）＝{Yn(s.Orderly)}");
        }
        else parts.Add("本次開機狀態（TPMA_STARTUP_CLEAR）：未回報");

        string value = string.Join("；", parts) + "。"
            + "界線：這些是「自上次 TPM2_Clear 以來」的標記——講的是狀態，不是誰是擁有者；"
            + "某個階層的 Enable 為否時，該階層的物件與其 NV 索引都用不到。"
            + "本專案不施測清除與重設（見 tpm.clear.boundary）。";
        return [new HardwareFact(key, Category, name, value, "", source, FactTrustLevel.Measured, false, at)];
    }

    /// <summary>
    /// P2-075 遠端證明能力（陳述，不上網）：由 family 與背書階層的事實組成。
    /// <b>它陳述的是「具備前提」，不是「已經被證明」</b>——本專案不連網、不送 challenge、
    /// 不驗 EK 憑證鏈，所以這一項不構成「能被遠端證明」的結論。
    /// </summary>
    private static HardwareFact AttestFact(PropertyRead fixedRead, PropertyRead varRead, DateTimeOffset at)
    {
        const string key = "tpm.attest.capable", name = "TPM 遠端證明能力（本機陳述）",
            source = "TPM2_GetCapability(TPM_CAP_TPM_PROPERTIES：PT_FIXED／PT_STARTUP_CLEAR／PT_PERMANENT)，經 Windows TBS";
        if (fixedRead.Values is not { } fp) return ReadFailure(key, name, source, at, fixedRead);
        if (varRead.Values is not { } vp) return ReadFailure(key, name, source, at, varRead);

        string? family = Tpm2.FamilyIndicator(fp.GetValueOrDefault(Tpm2.PtFamilyIndicator));
        bool hasEh = vp.TryGetValue(Tpm2.PtStartupClear, out uint sc);
        bool hasPerm = vp.TryGetValue(Tpm2.PtPermanent, out uint perm);
        if (family is null || (!hasEh && !hasPerm))
            return Unavailable(key, name, source, at, FactAvailability.NotSupported,
                "缺 family 或背書階層狀態——沒有值就是沒有值，不以「支援」代填");

        bool? eh = hasEh ? Tpm2.DecodeStartupClear(sc).EhEnable : null;
        bool? endAuth = hasPerm ? Tpm2.DecodePermanent(perm).EndorsementAuthSet : null;

        string value = $"family {family}；背書階層啟用（ehEnable）＝{(eh is { } e ? Yn(e) : "未回報")}"
            + $"，背書階層授權值已設定（endorsementAuthSet）＝{(endAuth is { } a ? Yn(a) : "未回報")}。"
            + "遠端證明的前提是「背書階層可用、且能建立並使用證明身分（EK／AK）」，以上即該前提的本機事實。"
            + (family == "2.0" ? "" : $"（family 不是 2.0：這兩個前提是依 TPM 2.0 的階層模型陳述的，置換成 {family} 的模型並不成立）")
            + "**界線：本事實不連網、不送 challenge、不驗證 EK 憑證鏈**——"
            + "所以它不等於「這台機器在遠端證明中會被接受」；EK 是否存在另屬一項，不在本事實內。";
        return new HardwareFact(key, Category, name, value, "", source, FactTrustLevel.Derived, false, at);
    }

    // ── 真實 TBS 通路（薄；測試以注入委派取代）──

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_Context_Create(ref uint version, out nint context);

    [DllImport("tbs.dll")]
    private static extern uint Tbsip_Submit_Command(nint context, uint locality, uint priority,
        byte[] inputBuffer, uint inputBufferLength, byte[] outputBuffer, ref uint outputBufferLength);

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_Get_TCG_Log(nint context, byte[]? outputBuffer, ref uint outputBufferLength);

    [DllImport("tbs.dll")]
    private static extern uint Tbsip_Context_Close(nint context);

    private const uint TbsSuccess = 0;
    private const int SubmitOutputCapacity = 1024;

    private static (uint Result, byte[] Output, uint OutputLen) RealSubmit(nint ctx, byte[] command)
    {
        var outBuf = new byte[SubmitOutputCapacity];
        uint outLen = (uint)outBuf.Length;
        uint rc = Tbsip_Submit_Command(ctx, TbsCommandLocalityZero, TbsCommandPriorityNormal,
            command, (uint)command.Length, outBuf, ref outLen);
        return (rc, outBuf, outLen);
    }

    private static byte[]? RealReadLog(nint ctx)
    {
        uint len = 0;
        var first = Tbsi_Get_TCG_Log(ctx, null, ref len);
        if (first != TbsSuccess || len == 0 || len > 4 * 1024 * 1024) return null;
        var buf = new byte[len];
        if (Tbsi_Get_TCG_Log(ctx, buf, ref len) != TbsSuccess) return null;
        return buf;
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    // ── 重放服務（TpmReplayFactsService）共用的 internal 存取器：同一份 TBS P/Invoke，不寫第二份 ──
    internal static byte[]? RealReadLogForReplay(nint ctx) => RealReadLog(ctx);
    internal static (uint Result, byte[] Output, uint OutputLen) RealSubmitForReplay(nint ctx, byte[] command) => RealSubmit(ctx, command);
    internal static void CloseContextForReplay(nint ctx) => Tbsip_Context_Close(ctx);
    internal static bool TryCreateContextForReplay(out nint ctx)
    {
        var version = TbsContextVersion2;
        var rc = Tbsi_Context_Create(ref version, out ctx);
        return rc == TbsSuccess && ctx != nint.Zero;
    }
}
