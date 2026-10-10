using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// TPM 事件 log 重放比對事實服務（P0-01／FW-002）。
/// <para>
/// 把兩個既有來源變第三者驗證：TCG log（Tbsi_Get_TCG_Log）逐事件模擬 extend → 期望值，
/// 與 TPM 實讀 PCR（SHA-256 bank）逐顆比對。log 缺／TBS 不可用＝NotApplicable；
/// <b>不一致＝事實不是「被入侵」判決</b>（可能是重放模型、log 不完整或平台未延伸某些事件）。
/// </para>
/// </summary>
public static class TpmReplayFactsService
{
    public const string Category = "信賴根";
    public const string SummaryKey = "tpm.replay.summary";
    private const uint TbsContextVersion2 = 2;

    private const string Source = "TCG log 重放（唯讀）× TPM PCR 實讀";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<nint?>? createContext = null,
        Func<nint, byte[], (uint Result, byte[] Output, uint OutputLen)>? submit = null,
        Func<nint, byte[]?>? readLog = null)
    {
        bool injected = createContext is not null;
        nint ctx;
        if (injected)
        {
            var h = createContext!();
            if (h is null || h.Value == nint.Zero)
                return [Unavailable(SummaryKey, at, FactAvailability.NotApplicable, "TBS 無法建立內容——TPM 不存在或未啟用；沒有 log 就沒有重放，不推測")];
            ctx = h.Value;
        }
        else
        {
            if (!TpmFactsAccess.TryCreateContextForReplay(out ctx))
                return [Unavailable(SummaryKey, at, FactAvailability.NotApplicable, "TBS 無法建立內容——TPM 不存在或未啟用；沒有 log 就沒有重放")];
        }

        try
        {
            var log = (readLog ?? TpmFactsAccess.RealReadLog)(ctx);
            if (log is null)
                return [Unavailable(SummaryKey, at, FactAvailability.NotSupported, "TCG log 讀取失敗或平台未提供——log 缺＝無法重放，不是『量測鏈完整』")];

            var walk = Tpm2.WalkTcgLog(log);
            var replay = TpmReplay.Run(walk.Events, Tpm2.AlgSha256);

            var submitFn = submit ?? TpmFactsAccess.RealSubmit;
            var facts = new List<HardwareFact>(26);
            int match = 0, mismatch = 0, uncovered = 0, readError = 0;
            var mismatchDetail = new List<string>();
            for (uint i = 0; i < replay.Count; i++)
            {
                var r = replay[(int)i];
                if (!r.Covered) { uncovered++; facts.Add(PcrFact(i, "log 無此 PCR 的事件", "", FactAvailability.NotApplicable, at)); continue; }
                var (result, output, outputLen) = submitFn(ctx, Tpm2.BuildPcrReadCommand(i, Tpm2.AlgSha256));
                if (result != 0 || Tpm2.DecodePcrResponse(
                        outputLen is > 0 and <= int.MaxValue and var n ? output[..(int)Math.Min(n, output.Length)] : [], out _) is not { } actual)
                { readError++; facts.Add(PcrFact(i, "PCR 實讀失敗——比對不成立，如實標", "", FactAvailability.ReadError, at)); continue; }
                bool same = r.Expected.AsSpan().SequenceEqual(actual);
                if (same) match++;
                else
                {
                    mismatch++;
                    if (mismatchDetail.Count < 6)
                        mismatchDetail.Add($"PCR{i}：" + TpmReplay.Judge(r.Expected, actual, "SHA-256"));
                }
                facts.Add(PcrFact(i,
                    same ? "SHA-256 一致" : "SHA-256 不一致（重放 vs 實測；不一致是事實，不是入侵判決）",
                    same ? "" : $"重放 0x{Convert.ToHexStringLower(r.Expected)}／實測 0x{Convert.ToHexStringLower(actual)}",
                    FactAvailability.Present, at));
            }

            string summary = $"事件 {walk.Events.Count} 個（log {(walk.Truncated ? "走訪提前結束——截斷或損毀如實標示" : "完整走訪")}）・"
                + $"SHA-256 bank 比對：一致 {match}／不一致 {mismatch}／log 未覆蓋 {uncovered}／實讀失敗 {readError}"
                + (mismatchDetail.Count > 0 ? "。" + string.Join("；", mismatchDetail) : "")
                + "。界線：重放模型（起始值／EV_NO_ACTION 不延伸）影響結果——不一致是事實，不是『被入侵』判決；"
                + "一致是強訊號（log 與 TPM 實讀互相印證）。";
            facts.Insert(0, new HardwareFact(SummaryKey, Category, "TPM log 重放比對", summary, "顆", Source,
                FactTrustLevel.Derived, false, at, match));
            return facts;
        }
        finally
        {
            if (!injected) TpmFactsAccess.Close(ctx);
        }
    }

    private static HardwareFact PcrFact(uint index, string value, string detail, FactAvailability avail, DateTimeOffset at) =>
        new($"tpm.replay.pcr.sha256.{index}", Category, $"TPM log 重放 PCR {index}（SHA-256）", value + detail, "", Source,
            FactTrustLevel.Derived, false, at, null, avail,
            avail == FactAvailability.Present ? null : value);

    private static HardwareFact Unavailable(string key, DateTimeOffset at, FactAvailability avail, string why) =>
        new(key, Category, "TPM log 重放比對", "", "顆", Source, FactTrustLevel.Unknown, false, at, null, avail, why);
}

/// <summary>把 TpmFactsService 的真實 TBS 通路（private）以 internal 存取器暴露給重放服務共用——不寫第二份。</summary>
internal static class TpmFactsAccess
{
    public static bool TryCreateContextForReplay(out nint ctx) => TpmFactsService.TryCreateContextForReplay(out ctx);
    public static byte[]? RealReadLog(nint ctx) => TpmFactsService.RealReadLogForReplay(ctx);
    public static (uint, byte[], uint) RealSubmit(nint ctx, byte[] cmd) => TpmFactsService.RealSubmitForReplay(ctx, cmd);
    public static void Close(nint ctx) => TpmFactsService.CloseContextForReplay(ctx);
}
