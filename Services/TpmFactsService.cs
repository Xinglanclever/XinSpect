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
}
