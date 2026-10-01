using System.Diagnostics;
using System.Numerics;

namespace XinSpect;

/// <summary>
/// NPU / 算力頁的計算基準：用 SIMD（Vector256/128 FMA）跑一輪平行浮點運算，
/// 量出每秒 GFLOPS。這是 CPU 的 SIMD 吞吐量——不是 NPU 算力（NPU 算力表已在
/// NpuDetectionService 查表估計）。放在一起可看出「NPU 廠標 TOPS vs CPU 實測 GFLOPS」的差距。
/// 純 managed 程式碼、零特權、不啟動子行程。
/// </summary>
public sealed record ComputeBenchResult(double Gflops, int Threads, int VectorWidth, double ElapsedMs);

public static class NpuComputeBenchService
{
    private const int ArraySize = 4_000_000;   // ~16 MB float，夠大不受 L2 影響
    private const int Iterations = 50;

    /// <summary>執行平行 SIMD FMA 基準，回傳 GFLOPS。</summary>
    public static ComputeBenchResult Run()
    {
        var a = new float[ArraySize];
        var b = new float[ArraySize];
        var c = new float[ArraySize];
        // 初始化避免被編譯器折疊
        for (int i = 0; i < ArraySize; i++) { a[i] = 1.0f; b[i] = 2.0f; c[i] = 0.5f; }

        int vw = Vector<float>.Count; // 通常 8（AVX2）
        long flopsPerIter = (long)ArraySize * 2L * vw; // FMA = 2 FLOPs/lane

        var sw = Stopwatch.StartNew();
        Parallel.For(0, Iterations, _ =>
        {
            int vecEnd = ArraySize - (ArraySize % vw);
            for (int i = 0; i < vecEnd; i += vw)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                var vc = new Vector<float>(c, i);
                (va * vb + vc).CopyTo(c, i);
            }
        });
        sw.Stop();

        double totalFlops = (double)flopsPerIter * Iterations;
        double gflops = totalFlops / sw.Elapsed.TotalSeconds / 1e9;
        return new ComputeBenchResult(gflops, Environment.ProcessorCount, vw, sw.Elapsed.TotalMilliseconds);
    }
}
