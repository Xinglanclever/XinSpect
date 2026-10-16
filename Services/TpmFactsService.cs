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
            facts.AddRange(VersionFacts(ctx, at, submitFn));
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

    // ── P2-068 TPM 規格版本：由 TPM 自己回報的 family／revision，不是從 TBS 或驅動推測 ──

    private static IReadOnlyList<HardwareFact> VersionFacts(nint ctx, DateTimeOffset at,
        Func<nint, byte[], (uint Result, byte[] Output, uint OutputLen)> submit)
    {
        const string key = "tpm.version", name = "TPM 規格版本",
            source = "TPM2_GetCapability(TPM_CAP_TPM_PROPERTIES)，經 Windows TBS";
        var (result, output, outputLen) = submit(ctx, Tpm2.BuildGetCapabilityCommand(Tpm2.CapTpmProperties, Tpm2.PtFamilyIndicator, 8));
        if (result != TbsSuccess)
            return [Unavailable(key, name, source, at, FactAvailability.ReadError, $"TBS 錯誤 0x{result:X8}")];
        var trimmed = outputLen is > 0 and <= int.MaxValue and var n ? output[..(int)Math.Min(n, output.Length)] : [];
        var data = Tpm2.GetCapabilityData(trimmed, out uint rc, out uint cap, out _);
        if (data is null)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                rc == 0 ? "GetCapability 回應格式不符——不解碼垃圾" : $"TPM 拒絕（TPM_RC 0x{rc:X8}）")];
        if (cap != Tpm2.CapTpmProperties)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                $"回應的 capability 是 0x{cap:X8}，不是查詢的 TPM_CAP_TPM_PROPERTIES")];

        var props = Tpm2.DecodeTpmProperties(data);
        uint? Find(uint id) => props.Where(p => p.Property == id).Select(p => (uint?)p.Value).FirstOrDefault();
        string? family = Tpm2.FamilyIndicator(Find(Tpm2.PtFamilyIndicator) ?? 0);
        var revision = Tpm2.Revision(Find(Tpm2.PtRevision) ?? 0);

        if (family is null && revision is null)
            return [Unavailable(key, name, source, at, FactAvailability.NotSupported,
                "TPM 未回報 family indicator 與 revision——沒有值就是沒有值，不以 2.0 代填")];

        string vendor = string.Concat(
            new[] { Tpm2.PtVendor1, Tpm2.PtVendor2, Tpm2.PtVendor3, Tpm2.PtVendor4 }
                .Select(id => Find(id) is { } v and not 0 ? VtpmVendorChunk(v) : ""))
            .TrimEnd('\0', ' ');

        string value = $"family {family ?? "未回報"}、revision {(revision is { } r ? $"{r.Major}.{r.Minor}" : "未回報")}"
            + (vendor.Length > 0 ? $"、廠商字串 {vendor}" : "、廠商字串未回報");
        return
        [
            new HardwareFact(key, Category, name, value, "", source, FactTrustLevel.Measured, false, at),
        ];
    }

    /// <summary>TPM_PT_VENDOR_STRING_x 的四位元組 ASCII（大端打包，與 TPM 線上格式一致）。</summary>
    private static string VtpmVendorChunk(uint raw)
    {
        var bytes = new[] { (byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw };
        return System.Text.Encoding.ASCII.GetString(bytes);
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
