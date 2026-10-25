using System.Diagnostics;
using System.Numerics;

namespace XinSpect;

/// <summary>
/// U10-TS-0018／0019「memcpy／memset 帶寬（SIMD vs CRT vs 逐位元組）」：
/// 同一緩衝上量三種實作的搬運／填充帶寬。判別碼家族前綴：<see cref="KernelPrefix"/>。
/// </summary>
/// <remarks>
/// <para>
/// 量的是：<c>Array.Copy</c>／<c>Array.Fill</c>（CRT memmove/memset 路徑——FSRM／ERMS 機器上
/// 會選 REP MOVSB，本測試照實量 managed 呼叫到的實作，不自行判定微碼路徑）、
/// <c>Vector&lt;byte&gt;</c> 迴圈（SIMD 路徑）、逐位元組迴圈（最差基準）三者的 GB/s 對照。
/// FSRM 的有無由 CPUID 位元（CpuIdService 特徵表）判讀，本服務只提供「CRT 路徑實測值」這把證據。
/// </para>
/// <para>
/// 誠實界線：①<b>使用者明示觸發才跑</b>（掛在 DeepBench 入口模式），不自動跑；
/// ②量的是本行程 managed 呼叫達成的帶寬，不是記憶體控制器上限；
/// ③工作集預設 16 MiB（DRAM 層級）——這是 STREAM 域的形狀，本服務的差異面是
/// <b>搬運核（memcpy）與填充核（memset）的實作對照</b>，與 memory.stream-bandwidth 的
/// 讀／複製／加／三元四型態不重疊也不重測；
/// ④量測預算寫進常數（<see cref="PerKernelBudgetMs"/>），每核超過即停並標註截斷。
/// </para>
/// </remarks>
public static class CopyKernelBandwidthService
{
    private const string Category = "處理器";
    public const string KernelPrefix = "ts.copy.kernel.";
    public const string Source = "managed Array.Copy/Array.Fill（CRT）與 Vector<byte> 迴圈（本行程自己的記憶體；零特權）";

    /// <summary>量測預算：單一核×實作的時長上限（毫秒）。超過即停並標註截斷。</summary>
    public const int PerKernelBudgetMs = 200;

    /// <summary>預設工作集（MiB）：DRAM 層級，對照 STREAM 域的形狀。</summary>
    public const int WorkingSetMib = 16;

    /// <summary>搬運核名稱：memcpy／memset。</summary>
    public static readonly string[] Kernels = { "memcpy", "memset" };

    /// <summary>實作名稱：crt（Array.Copy／Array.Fill）／simd（Vector&lt;byte&gt;）／byte（逐位元組）。</summary>
    public static readonly string[] Implementations = { "crt", "simd", "byte" };

    /// <summary>一次取樣：核名稱、實作、搬運位元組數、耗時（Stopwatch 刻）。</summary>
    public sealed record KernelSample(string Kernel, string Implementation, long Bytes, long Ticks, bool Truncated);

    /// <summary>收集搬運核帶寬。測試以注入的樣本取代真實負載，測試不跑長負載。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<KernelSample>>? probe = null)
    {
        try
        {
            IReadOnlyList<KernelSample> samples = probe is { } p ? p() : RealCollect();
            return [.. samples.Select(s => BuildFact(at, s))];
        }
        catch (Exception ex)
        {
            return [new HardwareFact(KernelPrefix + "probe", Category, "搬運核帶寬", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "量測失敗：" + ex.Message)];
        }
    }

    /// <summary>純函式：一次樣本 → 一把事實。鍵形 <c>ts.copy.kernel.{memcpy|memset}.{crt|simd|byte}</c>。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, KernelSample sample)
    {
        string key = $"{KernelPrefix}{sample.Kernel.ToLowerInvariant()}.{sample.Implementation.ToLowerInvariant()}";
        string name = $"{sample.Kernel} 帶寬（{sample.Implementation}）";
        if (sample.Bytes <= 0 || sample.Ticks <= 0)
            return new HardwareFact(key, Category, name, "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "搬運量或耗時非正值——樣本不可信，不推算");
        double gbps = sample.Bytes / (sample.Ticks / (double)Stopwatch.Frequency) / 1e9;
        string truncation = sample.Truncated ? $"（達 {PerKernelBudgetMs} ms 預算即停——樣本為截斷窗）" : "";
        string implNote = sample.Implementation switch
        {
            "crt" => "Array.Copy/Array.Fill 走 CRT memmove/memset；FSRM/ERMS 機器上為 REP MOVSB 路徑（照實量 managed 呼叫到的實作，不自行判定微碼選路）",
            "simd" => "Vector<byte> 迴圈（SIMD 路徑）",
            _ => "逐位元組迴圈（最差基準）",
        };
        return new HardwareFact(key, Category, name,
            $"{gbps:0.##} GB/s（工作集 {WorkingSetMib} MiB）{truncation}；{implNote}",
            "GB/s", Source, FactTrustLevel.Measured, false, at, gbps, FactAvailability.Present, null);
    }

    // ── 真實負載（使用者明示觸發才會走到）────────────────────────────────────

    private static IReadOnlyList<KernelSample> RealCollect()
    {
        byte[] src = new byte[WorkingSetMib * 1024 * 1024];
        byte[] dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4096) src[i] = 0xA5;   // 打散全零頁

        var samples = new List<KernelSample>();
        samples.Add(Measure("memcpy", "crt", () => { Array.Copy(src, dst, src.Length); return src.Length; }));
        samples.Add(Measure("memcpy", "simd", () => SimdCopy(src, dst)));
        samples.Add(Measure("memcpy", "byte", () => { ByteCopy(src, dst); return src.Length; }));
        samples.Add(Measure("memset", "crt", () => { Array.Fill(dst, (byte)0x5A); return dst.Length; }));
        samples.Add(Measure("memset", "simd", () => SimdFill(dst)));
        samples.Add(Measure("memset", "byte", () => { ByteFill(dst); return dst.Length; }));
        return samples;
    }

    private static KernelSample Measure(string kernel, string impl, Func<long> block)
    {
        block();   // 暖機（讓 JIT 與 CRT 選路先完成，不計時）
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        bool truncated = false;
        while (sw.ElapsedMilliseconds < PerKernelBudgetMs)
        {
            bytes += block();
            truncated = true;
        }
        sw.Stop();
        return new KernelSample(kernel, impl, bytes, sw.ElapsedTicks, truncated);
    }

    private static long SimdCopy(byte[] src, byte[] dst)
    {
        int w = Vector<byte>.Count;
        int vecEnd = src.Length - (src.Length % w);
        for (int i = 0; i < vecEnd; i += w) new Vector<byte>(src, i).CopyTo(dst, i);
        return vecEnd;
    }

    private static long SimdFill(byte[] dst)
    {
        int w = Vector<byte>.Count;
        var v = new Vector<byte>((byte)0x5A);
        int vecEnd = dst.Length - (dst.Length % w);
        for (int i = 0; i < vecEnd; i += w) v.CopyTo(dst, i);
        return vecEnd;
    }

    private static void ByteCopy(byte[] src, byte[] dst)
    {
        for (int i = 0; i < src.Length; i++) dst[i] = src[i];
    }

    private static void ByteFill(byte[] dst)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = 0x5A;
    }
}
