using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// U10-TS-0003／0005／0006（吞吐面）「向量寬度階梯吞吐」：純量／128／256／512 位元 FP32
/// 乘加鏈的單執行緒與多執行緒 GFLOPS。判別碼家族前綴：<see cref="FamilyPrefix"/>。
/// </summary>
/// <remarks>
/// <para>
/// 量的是：同一條暫存器駐留的 FP32 乘加鏈，在四種寬度下本機 .NET/x64 JIT 可達的吞吐。
/// 寬度階梯放在一起看，才能看出「向量越寬、每 FLOP 越省」以及 AVX-512 機上的寬度效應。
/// 純 managed、零特權、只跑本行程自己的執行緒與暫存器。
/// </para>
/// <para>
/// 誠實界線：①<b>使用者明示觸發才跑</b>（掛在 DeepBench 入口模式），本服務不自動跑、
/// 不在掃描路徑上出現；②量的是本行程 JIT 編譯出的程式碼吞吐，不是 CPU 規格峰值——
/// 與 NpuComputeBenchService（多核 FP32 SIMD GFLOPS）同立場，本服務補的是
/// <b>純量／128／256／512 的寬度對照與單執行緒相位</b>，不重複該服務的既有多核形狀；
/// ③乘與加各計 1 FLOP（FMA 融合與否不改變計數，只改變實際達成率）；
/// ④AVX-512 在 CPUID 不支援的機器<b>如實 NotSupported，不降級到 256 位元冒充</b>；
/// ⑤量測預算寫進常數（<see cref="PerWidthBudgetMs"/>），超過即停並標註截斷；
/// ⑥降頻效應（AVX-512 負載下的頻率變化）由 CeilingService 的向量授權降頻窗收口，
/// 本服務只量吞吐，不重測頻率。
/// </para>
/// </remarks>
public static class VectorWidthThroughputService
{
    private const string Category = "處理器";
    public const string FamilyPrefix = "ts.vector.flops.";
    public const string Source = "managed 純量／Vector128/256/512<float> 乘加鏈（本行程自己的執行緒；零特權）";

    /// <summary>量測預算：單一寬度×單/多執行緒相位的時長上限（毫秒）。超過即停並標註截斷。</summary>
    public const int PerPhaseBudgetMs = 300;

    /// <summary>暫存器駐留的累加器條數（每條一個向量／純量）。</summary>
    public const int AccumulatorCount = 4;

    /// <summary>一次取樣：寬度（scalar/128/256/512）、是否多執行緒、FLOP 計數、耗時（Stopwatch 刻）。</summary>
    public sealed record WidthSample(string Width, bool MultiThread, long Flops, long Ticks, bool Truncated);

    /// <summary>收集寬度階梯吞吐。測試以注入的樣本取代真實負載，測試不跑長負載。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<WidthSample>>? probe = null,
        bool avx512Supported = false)
    {
        if (probe is { } p)
        {
            try { return [.. p().Select(s => BuildFact(at, s))]; }
            catch (Exception ex)
            {
                return [new HardwareFact(FamilyPrefix + "probe", Category, "向量寬度吞吐", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "量測失敗：" + ex.Message)];
            }
        }
        return RealCollect(at, avx512Supported);
    }

    /// <summary>純函式：一次樣本 → 一把事實。鍵形 <c>ts.vector.flops.{寬度}.{st|mt}</c>。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, WidthSample sample)
    {
        string phase = sample.MultiThread ? "mt" : "st";
        string key = $"{FamilyPrefix}{sample.Width}.{phase}";
        if (sample.Flops <= 0 || sample.Ticks <= 0)
            return new HardwareFact(key, Category, $"向量吞吐 {sample.Width} ×{phase}", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "FLOP 計數或耗時非正值——樣本不可信，不推算");
        double gflops = sample.Flops / (sample.Ticks / (double)Stopwatch.Frequency) / 1e9;
        string truncation = sample.Truncated ? $"（達 {PerPhaseBudgetMs} ms 預算即停——樣本為截斷窗）" : "";
        return new HardwareFact(key, Category, $"向量吞吐 {sample.Width} ×{phase}",
            $"{gflops:0.##} GFLOPS{truncation}；FP32 乘加鏈、{AccumulatorCount} 條累加器，乘與加各計 1 FLOP",
            "GFLOPS", Source, FactTrustLevel.Measured, false, at, gflops, FactAvailability.Present, null);
    }

    /// <summary>純函式：AVX-512 不支援時的如實回報。不降級、不冒充。</summary>
    public static HardwareFact BuildUnsupportedFact(DateTimeOffset at, bool multiThread) =>
        new($"{FamilyPrefix}512.{(multiThread ? "mt" : "st")}", Category, "向量吞吐 512",
            "", "", Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
            "CPUID 無 AVX-512F——512 位元相位不執行，也不降級到 256 位元冒充");

    // ── 真實負載（使用者明示觸發才會走到）────────────────────────────────────

    private static IReadOnlyList<HardwareFact> RealCollect(DateTimeOffset at, bool avx512Supported)
    {
        var samples = new List<WidthSample>();
        samples.Add(Measure("scalar", multiThread: false, ScalarBlock));
        samples.Add(Measure("scalar", multiThread: true, ScalarBlock));
        if (Vector128.IsHardwareAccelerated)
        {
            samples.Add(Measure("128", multiThread: false, Vector128Block));
            samples.Add(Measure("128", multiThread: true, Vector128Block));
        }
        if (Vector256.IsHardwareAccelerated)
        {
            samples.Add(Measure("256", multiThread: false, Vector256Block));
            samples.Add(Measure("256", multiThread: true, Vector256Block));
        }
        var facts = new List<HardwareFact>();
        foreach (WidthSample s in samples) facts.Add(BuildFact(at, s));
        if (!avx512Supported)
        {
            facts.Add(BuildUnsupportedFact(at, multiThread: false));
            facts.Add(BuildUnsupportedFact(at, multiThread: true));
        }
        else
        {
            facts.Add(BuildFact(at, Measure("512", multiThread: false, Vector512Block)));
            facts.Add(BuildFact(at, Measure("512", multiThread: true, Vector512Block)));
        }
        return facts;
    }

    /// <summary>跑一個相位：先暖機，再於預算內累積區塊，回傳樣本。</summary>
    private static WidthSample Measure(string width, bool multiThread, Func<long> blockFlops)
    {
        blockFlops();   // 暖機（讓 JIT 先編譯，不計時）
        var sw = Stopwatch.StartNew();
        long flops = 0;
        bool truncated = false;
        if (multiThread)
        {
            object gate = new();
            int workers = Math.Max(1, Environment.ProcessorCount);
            Parallel.For(0, workers, _ =>
            {
                long local = 0;
                while (sw.ElapsedMilliseconds < PerPhaseBudgetMs)
                {
                    local += blockFlops();
                }
                lock (gate) flops += local;
            });
            truncated = true;   // 平行相位以預算窗為界，一律是截斷窗
        }
        else
        {
            while (sw.ElapsedMilliseconds < PerPhaseBudgetMs)
            {
                flops += blockFlops();
                truncated = true;
            }
        }
        sw.Stop();
        return new WidthSample(width, multiThread, flops, sw.ElapsedTicks, truncated);
    }

    private static long ScalarBlock()
    {
        const int inner = 512;
        const int outer = 64;
        float m = 1.0000001f, s = 0.0000001f;
        float r0 = 0.5f, r1 = 0.25f, r2 = 0.75f, r3 = 0.125f;
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                r0 = r0 * m + s; r1 = r1 * m + s; r2 = r2 * m + s; r3 = r3 * m + s;
            }
        }
        // 讀回累加器防止消除；FLOP = 4 條鏈 × 2（乘+加）× inner × outer
        return (long)AccumulatorCount * 2 * inner * outer + (r0 + r1 + r2 + r3 == 3.7f ? 1 : 0);
    }

    private static long Vector128Block()
    {
        const int inner = 512;
        const int outer = 64;
        var m = Vector128.Create(1.0000001f); var s = Vector128.Create(0.0000001f);
        var r0 = Vector128.Create(0.5f); var r1 = Vector128.Create(0.25f);
        var r2 = Vector128.Create(0.75f); var r3 = Vector128.Create(0.125f);
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                r0 = r0 * m + s; r1 = r1 * m + s; r2 = r2 * m + s; r3 = r3 * m + s;
            }
        }
        float acc = Vector128.Dot(r0, Vector128<float>.One) + Vector128.Dot(r1, Vector128<float>.One)
                  + Vector128.Dot(r2, Vector128<float>.One) + Vector128.Dot(r3, Vector128<float>.One);
        return (long)AccumulatorCount * Vector128<float>.Count * 2 * inner * outer + (acc == 3.7f ? 1 : 0);
    }

    private static long Vector256Block()
    {
        const int inner = 512;
        const int outer = 64;
        var m = Vector256.Create(1.0000001f); var s = Vector256.Create(0.0000001f);
        var r0 = Vector256.Create(0.5f); var r1 = Vector256.Create(0.25f);
        var r2 = Vector256.Create(0.75f); var r3 = Vector256.Create(0.125f);
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                r0 = r0 * m + s; r1 = r1 * m + s; r2 = r2 * m + s; r3 = r3 * m + s;
            }
        }
        float acc = Vector256.Dot(r0, Vector256<float>.One) + Vector256.Dot(r1, Vector256<float>.One)
                  + Vector256.Dot(r2, Vector256<float>.One) + Vector256.Dot(r3, Vector256<float>.One);
        return (long)AccumulatorCount * Vector256<float>.Count * 2 * inner * outer + (acc == 3.7f ? 1 : 0);
    }

    private static long Vector512Block()
    {
        const int inner = 512;
        const int outer = 64;
        var m = Vector512.Create(1.0000001f); var s = Vector512.Create(0.0000001f);
        var r0 = Vector512.Create(0.5f); var r1 = Vector512.Create(0.25f);
        var r2 = Vector512.Create(0.75f); var r3 = Vector512.Create(0.125f);
        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                r0 = r0 * m + s; r1 = r1 * m + s; r2 = r2 * m + s; r3 = r3 * m + s;
            }
        }
        float acc = Vector512.Dot(r0, Vector512<float>.One) + Vector512.Dot(r1, Vector512<float>.One)
                  + Vector512.Dot(r2, Vector512<float>.One) + Vector512.Dot(r3, Vector512<float>.One);
        return (long)AccumulatorCount * Vector512<float>.Count * 2 * inner * outer + (acc == 3.7f ? 1 : 0);
    }
}
