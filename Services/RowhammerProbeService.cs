using System.Diagnostics;

namespace XinSpect;

/// <summary>一次記憶體壓力探測的結果。Flips＞0 表示在<b>自擁有的</b>緩衝區內偵測到位元翻轉。</summary>
public sealed record RowhammerProbeResult(uint AllocatedBytes, uint Iterations, int Flips, long ElapsedMs);

/// <summary>
/// WP22 記憶體壓力探測（**危險項**）：在<b>自擁有的</b>連續緩衝區內反覆讀寫已知樣本並逐位元組驗證——
/// 本服務只驗證自己配置的頁面；usermode 無法保證翻轉被限制在這些頁面。
/// <list type="bullet">
/// <item>**兩級風險開關**：兩級均啟用才可開始，每輪與壓力區段重新檢查；還原寫入許可不適用。</item>
/// <item>**同意閘門**：<c>userConsent</c> 必須明確為 true——沒有同意一律拒跑（丟例外，不降級）。</item>
/// <item>**誠實命名**：usermode 無法執行 clflush，這是壓力探測、**非保證觸發 Rowhammer**——
///       不能宣稱「驗證了 Rowhammer 抗性」，只宣稱「壓力下自擁有頁面無翻轉」。</item>
/// <item>**為什麼還標危險**：相鄰實體列可能屬於其他處理程序——單靠 usermode 無法完全隔離，
///       理論上存在波及風險。建議使用專用測試機。</item>
/// </list>
/// GUI 以 <see cref="DangerNotice"/> 標註並逐次同意；服務入口再次檢查。沒有自動執行或 CLI 入口。
/// </summary>
public static class RowhammerProbeService
{
    public const string DangerNotice =
        "⚠ 危險操作：記憶體壓力探測會對記憶體進行高頻率反覆讀寫，可能損壞資料（含其他處理程序的資料——" +
        "usermode 無法完全隔離相鄰實體列）。建議使用專用測試機，不要在存放重要資料的機器上執行。" +
        "本探測非保證觸發 Rowhammer（usermode 無 clflush）；結果只代表壓力下自擁有頁面的翻轉觀察。";

    // 以整 MiB 接收；1 MiB 已涵蓋 0x1000／0x2000 熱點，2047 MiB 是整 MiB 換算後仍可由 int 表達的上限。
    // 這是位址與整數邊界，不代表可用記憶體或硬體安全；配置失敗仍由呼叫端如實回報。
    internal const uint MinTargetMegabytes = 1;
    internal const uint MaxTargetMegabytes = 2047;

    /// <summary>只驗證大小並換算長度；不配置緩衝區、不執行壓力探測。</summary>
    internal static int ValidateTargetMegabytes(uint targetMegabytes)
    {
        if (targetMegabytes < MinTargetMegabytes || targetMegabytes > MaxTargetMegabytes)
            throw new ArgumentOutOfRangeException(nameof(targetMegabytes), targetMegabytes,
                $"緩衝區大小須為 {MinTargetMegabytes} 至 {MaxTargetMegabytes} MiB。");

        return checked((int)(targetMegabytes * 1024UL * 1024UL));
    }

    public const string GateNotice = "需先依序啟用「進階診斷」與「硬體寫入、合成威脅與危險記憶體壓力」兩級風險開關，並對本次操作明確同意。";

    internal static void EnsureAdmission(bool userConsent)
    {
        if (!userConsent)
            throw new InvalidOperationException("記憶體壓力探測需要本次明確同意才會執行。" + DangerNotice);
        EnsureHighRiskPolicy();
    }

    private static void EnsureHighRiskPolicy()
    {
        // CanWriteHardware 可能含還原操作許可；該許可不能啟動或繼續壓力測試。
        if (!HardwareOperationGate.IsHighRiskModeEnabled)
            throw new InvalidOperationException(GateNotice + DangerNotice);
    }

    // 僅 friend 測試組件可注入。AsyncLocal 隔離呼叫脈絡；不修改生產 runner 或 gate。
    private sealed record ProbeExecution(Func<int, byte[]> Allocate, Func<byte[], uint, RowhammerProbeResult> Run);
    private static readonly System.Threading.AsyncLocal<ProbeExecution?> TestExecution = new();
    private static readonly ProbeExecution ProductionExecution = new(
        length => GC.AllocateUninitializedArray<byte>(length, pinned: false), RunPressure);

    internal static IDisposable UseTestExecution(Func<int, byte[]> allocate,
        Func<byte[], uint, RowhammerProbeResult> run)
    {
        ArgumentNullException.ThrowIfNull(allocate);
        ArgumentNullException.ThrowIfNull(run);
        var prior = TestExecution.Value;
        TestExecution.Value = new ProbeExecution(allocate, run);
        return new TestExecutionScope(prior);
    }

    private sealed class TestExecutionScope(ProbeExecution? prior) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TestExecution.Value = prior;
        }
    }

    /// <summary>每次呼叫均要求明確同意與兩級即時開關；配置與 worker 前各檢查一次。</summary>
    public static RowhammerProbeResult RunConsentedProbe(bool userConsent, uint targetMegabytes = 256)
    {
        EnsureAdmission(userConsent);
        int bufferLength = ValidateTargetMegabytes(targetMegabytes);
        var execution = TestExecution.Value ?? ProductionExecution;
        var buffer = execution.Allocate(bufferLength);
        EnsureAdmission(userConsent);
        var result = execution.Run(buffer, (uint)bufferLength);
        EnsureAdmission(userConsent);
        return result;
    }

    private static RowhammerProbeResult RunPressure(byte[] buffer, uint bytes)
    {
        var sw = Stopwatch.StartNew();
        uint iterations = 0;
        int flips = 0;
        try
        {
            unsafe
            {
                fixed (byte* basePtr = buffer)
                {
                    // 兩個相距 4 KiB 的錘擊點（同頁面群內），其餘位元組為驗證區
                    byte* hotA = basePtr + 0x1000;
                    byte* hotB = basePtr + 0x2000;
                    for (uint round = 0; round < 64 && flips == 0; round++)
                    {
                        EnsureHighRiskPolicy();
                        FillPattern(buffer, (byte)round);
                        EnsureHighRiskPolicy();
                        // 高頻讀寫兩個熱點（壓力來源）
                        for (int i = 0; i < 200_000; i++)
                        {
                            // 熱點迴圈每 4096 次重核；填樣／驗證仍以區段邊界合作式停止，非硬取消。
                            if ((i & 4095) == 0) EnsureHighRiskPolicy();
                            *hotA = (byte)(i & 0xFF);
                            *hotB = (byte)(~i & 0xFF);
                            volatile_ = *hotA; volatile_ = *hotB;
                        }
                        EnsureHighRiskPolicy();
                        flips = VerifyPattern(buffer, (byte)round,
                            skipOffsets: new HashSet<int> { 0x1000, 0x2000 }); // 熱點是被錘擊覆寫的，不在驗證範圍
                        iterations++;
                    }
                }
            }
        }
        finally
        {
            sw.Stop();
        }
        return new RowhammerProbeResult(bytes, iterations, flips, sw.ElapsedMilliseconds);
    }

    private static int volatile_; // 讀取副作用接收（避免被 JIT 消除）

    /// <summary>多輪聚合結果：每輪獨立配置與驗證，TotalFlips 為各輪之和。</summary>
    public sealed record MultiRoundResult(int Rounds, ulong AllocatedBytesPerRound, int TotalFlips, bool AnyFlip, long ElapsedMs);

    /// <summary>
    /// **多輪模式**（使用者核准的多輪測試）：連跑 N 輪單輪探測並聚合——
    /// <b>多輪測試進行中、不保證可用</b>：usermode 無 clflush，任何一輪零翻轉都不代表
    /// 記憶體具備 Rowhammer 抗性；同意閘門與單輪同一道。
    /// </summary>
    public static MultiRoundResult RunMultiRound(bool userConsent, int rounds = 10, uint targetMegabytes = 256)
    {
        EnsureAdmission(userConsent);
        int bufferLength = ValidateTargetMegabytes(targetMegabytes);
        var sw = Stopwatch.StartNew();
        int totalFlips = 0;
        bool anyFlip = false;
        for (int i = 0; i < rounds; i++)
        {
            var r = RunConsentedProbe(userConsent, targetMegabytes);
            totalFlips += r.Flips;
            anyFlip |= r.Flips > 0;
        }
        sw.Stop();
        EnsureAdmission(userConsent);
        return new MultiRoundResult(rounds, (ulong)bufferLength, totalFlips, anyFlip, sw.ElapsedMilliseconds);
    }

    /// <summary>多輪結果格式化——必定帶「多輪測試」「不保證可用」「未經過校驗」三重標註。</summary>
    public static string FormatMultiRound(MultiRoundResult result)
    {
        string verdict = result.AnyFlip
            ? $"{result.TotalFlips} 個位元組翻轉（跨 {result.Rounds} 輪）——請立即檢查資料完整性"
            : $"{result.Rounds} 輪全部翻轉 0 位元組";
        return $"【多輪測試・不保證可用】{verdict}、每輪 {result.AllocatedBytesPerRound / (1024 * 1024)} MiB、" +
               $"總耗時 {result.ElapsedMs} ms。多輪零翻轉<b>不代表</b>記憶體具備 Rowhammer 抗性。" +
               "【未經過校驗】結果未對照任何參考實作。 " + DangerNotice;
    }

    /// <summary>把探測結果轉成人可讀文字。<b>必定帶「未經過校驗」標註</b>——結果沒有對照過任何
    /// 參考實作（usermode 無 clflush、無法與 TestMem5／正規 rowhammer tester 交叉驗證），只能當參考。</summary>
    public static string FormatResult(RowhammerProbeResult result)
    {
        string verdict = result.Flips == 0
            ? $"壓力下自擁有頁面翻轉 0 位元組"
            : $"壓力下自擁有頁面翻轉 {result.Flips} 位元組——請立即檢查資料完整性";
        return $"{verdict}：{result.Iterations} 輪、耗時 {result.ElapsedMs} ms、" +
               $"緩衝 {result.AllocatedBytes / (1024 * 1024)} MiB。" +
               "【未經過校驗】本結果沒有對照過任何參考實作，且非保證觸發 Rowhammer（usermode 無 clflush）——僅供參考，不作為記憶體可靠性的結論。" +
               DangerNotice;
    }

    /// <summary>以種子填入可重現樣本（xorshift 派生，逐位元組）。</summary>
    public static unsafe void FillPattern(Span<byte> region, byte seed)
    {
        uint state = 0x9E3779B9u ^ (uint)(seed << 24 | seed << 16 | seed << 8 | seed);
        for (int i = 0; i < region.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            region[i] = (byte)(state >> 24);
        }
    }

    /// <summary>逐位元組驗證樣本；回傳翻轉位元組數（0＝完好）。skipOffsets＝刻意覆寫的偏移（如錘擊熱點），不計翻轉。</summary>
    public static unsafe int VerifyPattern(ReadOnlySpan<byte> region, byte seed, IReadOnlySet<int>? skipOffsets = null)
    {
        uint state = 0x9E3779B9u ^ (uint)(seed << 24 | seed << 16 | seed << 8 | seed);
        int flips = 0;
        for (int i = 0; i < region.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            if (region[i] != (byte)(state >> 24) && (skipOffsets is null || !skipOffsets.Contains(i))) flips++;
        }
        return flips;
    }
}
