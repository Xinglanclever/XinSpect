using System.Diagnostics;

namespace XinSpect;

public sealed record DramPatternResult(long BytesTested, int PatternsPassed, int Mismatches, long ElapsedMicroseconds);

public interface IDramPatternIntegrityEngine
{
    DramPatternResult Run(int maxBytes, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Bounded process-private DRAM pattern write/read validation; never accesses physical addresses.</summary>
public sealed class DramPatternIntegrityService(IDramPatternIntegrityEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "memory.dram-pattern-integrity";
    public const int MaxBufferBytes = 16 * 1024 * 1024;

    private readonly IDramPatternIntegrityEngine _engine = engine ?? new ManagedDramPatternIntegrityEngine();
    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        if (!HardwareOperationGate.CanRunAdvancedDiagnostics)
            return Failure(context, started, DeepBenchFailureKind.Unstable,
                "DRAM pattern test requires advanced diagnostics mode.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DramPatternResult result = await Task.Run(
                () => _engine.Run(MaxBufferBytes, new Progress<double>(fraction =>
                    context.Progress.Report(new DeepBenchProgress(TestId,
                        (int)Math.Round(fraction * ManagedDramPatternIntegrityEngine.PatternCount),
                        ManagedDramPatternIntegrityEngine.PatternCount, fraction, "寫入／讀回驗證"))), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            bool passed = result.Mismatches == 0 && result.PatternsPassed == ManagedDramPatternIntegrityEngine.PatternCount;
            var metrics = new DeepBenchMetric[]
            {
                new("memory.dram-pattern.bytes", "程序私有配置驗證容量", "bytes", false,
                    "managed byte array; no physical-address access", [result.BytesTested], []),
                new("memory.dram-pattern.patterns", "完整往返圖樣", "patterns", false,
                    "zero/ones/alternating/address-derived/complement/random-seed", [result.PatternsPassed], []),
                new("memory.dram-pattern.mismatches", "讀回不符位元組", "bytes", false,
                    "exact byte comparison", [result.Mismatches], []),
                new("memory.dram-pattern.elapsed.us", "執行時間", "µs", false,
                    "bounded process-private buffer", [result.ElapsedMicroseconds], []),
            };
            return new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow,
                passed ? $"{result.PatternsPassed} 個圖樣往返一致" : $"發現 {result.Mismatches} 個不符位元組",
                metrics, ["資料只寫入及讀回本程序配置的暫存陣列。", "不直接偵測實體 DRAM/ECC 故障，不寫實體位址、不注入 ECC 錯誤。"],
                ["最多配置 16 MiB；執行時間受記憶體速度與背景負載影響。"],
                passed ? DeepBenchFailureKind.None : DeepBenchFailureKind.Unstable,
                passed ? null : "Pattern read-back mismatch.");
        }
        catch (OperationCanceledException)
        {
            return Failure(context, started, DeepBenchFailureKind.Cancelled, "使用者取消 DRAM pattern test。");
        }
        catch (Exception exception)
        {
            return Failure(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static byte PatternByte(int pattern, int index)
    {
        uint x = unchecked((uint)index * 0x9E3779B9u + (uint)pattern * 0x85EBCA6Bu);
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        byte random = (byte)x;
        return pattern switch
        {
            0 => 0x00,
            1 => 0xFF,
            2 => (index & 1) == 0 ? (byte)0xAA : (byte)0x55,
            3 => (byte)index,
            4 => (byte)~index,
            _ => random,
        };
    }

    private static DeepBenchTestResult Failure(DeepBenchRunContext context, DateTime started,
        DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["只使用程序私有記憶體；不代表 ECC 注入或實體 DRAM 故障診斷。"], kind, error);
}

public sealed class ManagedDramPatternIntegrityEngine : IDramPatternIntegrityEngine
{
    public const int PatternCount = 6;

    public DramPatternResult Run(int maxBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int length = Math.Clamp(maxBytes, 1, DramPatternIntegrityService.MaxBufferBytes);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
        long started = Stopwatch.GetTimestamp();
        int mismatches = 0;

        for (int pattern = 0; pattern < PatternCount; pattern++)
        {
            for (int offset = 0; offset < buffer.Length; offset++)
            {
                if ((offset & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                buffer[offset] = DramPatternIntegrityService.PatternByte(pattern, offset);
            }

            for (int offset = 0; offset < buffer.Length; offset++)
            {
                if ((offset & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (buffer[offset] != DramPatternIntegrityService.PatternByte(pattern, offset)) mismatches++;
            }

            progress?.Report((pattern + 1d) / PatternCount);
        }

        return new((long)length * PatternCount, PatternCount, mismatches,
            checked((long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000d)));
    }
}
