using System.Diagnostics;
using System.Numerics;

namespace XinSpect;

/// <summary>
/// NPU / 算力頁的 CPU 計算基準：用 System.Numerics.Vector 跑平行浮點乘加，
/// 量出每秒 GFLOPS。這是 CPU 的 SIMD 吞吐量——不是 NPU 算力（NPU 算力表已在
/// NpuDetectionService 查表估計）。放在一起可看出「NPU 廠標 TOPS vs CPU 實測 GFLOPS」的差距。
/// 純 managed 程式碼、零特權、不啟動子行程。
/// </summary>
public sealed record ComputeBenchResult(double Gflops, int Threads, int VectorWidth, double ElapsedMs);

public static class NpuComputeBenchService
{
    private const int ArraySize = 4_000_000;   // ~16 MB float，夠大不受 L2 影響
    private const int Iterations = 50;

    /// <summary>執行平行 FP32 乘加基準，回傳 GFLOPS；是否融合為 FMA 由 JIT 決定。</summary>
    public static ComputeBenchResult Run()
    {
        var a = new float[ArraySize];
        var b = new float[ArraySize];
        var c = new float[ArraySize];
        // 初始化避免被編譯器折疊
        for (int i = 0; i < ArraySize; i++) { a[i] = 1.0f; b[i] = 2.0f; c[i] = 0.5f; }

        int vw = Vector<float>.Count; // 通常 8（AVX2）
        long flopsPerIter = CountFlops(ArraySize, 1); // 每個陣列元素一乘一加；不可再乘向量 lane 數

        var sw = Stopwatch.StartNew();
        // 各分區只寫自己的 c 範圍；舊版以 iteration 平行，讓所有 worker 同時寫整個 c。
        int blocks = (ArraySize + vw - 1) / vw;
        Parallel.For(0, Environment.ProcessorCount, worker =>
        {
            int start = (int)((long)blocks * worker / Environment.ProcessorCount) * vw;
            int end = (int)Math.Min(ArraySize, (long)blocks * (worker + 1) / Environment.ProcessorCount * vw);
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                int i = start;
                for (; i + vw <= end; i += vw)
                {
                    var va = new Vector<float>(a, i);
                    var vb = new Vector<float>(b, i);
                    var vc = new Vector<float>(c, i);
                    (va * vb + vc).CopyTo(c, i);
                }
                for (; i < end; i++) c[i] = a[i] * b[i] + c[i];
            }
        });
        sw.Stop();

        double totalFlops = (double)flopsPerIter * Iterations;
        double gflops = totalFlops / sw.Elapsed.TotalSeconds / 1e9;
        return new ComputeBenchResult(gflops, Environment.ProcessorCount, vw, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>純計數：乘與加各一 FLOP；向量化不會增加已計入的元素數。</summary>
    public static long CountFlops(int elements, int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elements);
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        return checked((long)elements * iterations * 2);
    }
}
