using System.Diagnostics;
using System.Runtime.InteropServices;
namespace XinSpect;

public enum HardwareRandomSource { Rdrand, RdSeed }

public interface IHardwareRandomEngine
{
    bool RdrandSupported { get; }
    bool RdSeedSupported { get; }

    Task<HardwareRandomMeasurement> MeasureAsync(
        HardwareRandomSource source,
        int valuesPerRound,
        int rounds,
        CancellationToken cancellationToken);
}

public sealed record HardwareRandomSample(long Values, long Retries, double Seconds);
public sealed record HardwareRandomMeasurement(IReadOnlyList<HardwareRandomSample> Samples);

/// <summary>
/// RDRAND / RDSEED 深測。分開量兩個硬體亂數指令的吞吐與 retry 率；
/// 結果只描述本機此次 CPU/API 觀察值，不做熵源品質或密碼學安全認證。
/// </summary>
public sealed class HardwareRandomBenchAdapter(IHardwareRandomEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "cpu.rdrand-rdseed";

    private readonly IHardwareRandomEngine _engine = engine ?? new X86HardwareRandomEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_engine.RdrandSupported || !_engine.RdSeedSupported)
            {
                string missing = !_engine.RdrandSupported && !_engine.RdSeedSupported
                    ? "RDRAND 與 RDSEED 都不支援"
                    : !_engine.RdrandSupported ? "RDRAND 不支援" : "RDSEED 不支援";
                return Failed(context, started, DeepBenchFailureKind.Unsupported, $"{missing}；本項不使用軟體亂數代替。");
            }

            (int rdrandValues, int rdSeedValues, int rounds) = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.03, "RDRAND 量測"));
            HardwareRandomMeasurement rdrand = await _engine.MeasureAsync(
                HardwareRandomSource.Rdrand, rdrandValues, rounds, cancellationToken).ConfigureAwait(false);
            ValidateMeasurement(rdrand, rdrandValues, rounds);

            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.52, "RDSEED 量測"));
            HardwareRandomMeasurement rdSeed = await _engine.MeasureAsync(
                HardwareRandomSource.RdSeed, rdSeedValues, rounds, cancellationToken).ConfigureAwait(false);
            ValidateMeasurement(rdSeed, rdSeedValues, rounds);

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.98, "整理原始樣本"));
            return CreateResult(context, started, rdrand, rdSeed, rounds);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (InvalidHardwareRandomMeasurementException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static (int RdrandValues, int RdSeedValues, int Rounds) GetWorkload(DeepBenchRunProfile profile) =>
        profile switch
        {
            DeepBenchRunProfile.Quick => (500_000, 100_000, 3),
            DeepBenchRunProfile.Full => (1_000_000, 200_000, 7),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

    private static void ValidateMeasurement(
        HardwareRandomMeasurement measurement,
        long expectedValuesPerRound,
        int expectedRounds)
    {
        if (measurement.Samples.Count != expectedRounds)
        {
            throw new InvalidHardwareRandomMeasurementException($"量測輪數不符：需要 {expectedRounds}，收到 {measurement.Samples.Count}。");
        }

        foreach (HardwareRandomSample sample in measurement.Samples)
        {
            if (sample.Values != expectedValuesPerRound || sample.Retries < 0 ||
                !double.IsFinite(sample.Seconds) || sample.Seconds <= 0 ||
                !double.IsFinite(expectedValuesPerRound / sample.Seconds / 1_000_000.0) ||
                expectedValuesPerRound / sample.Seconds <= 0)
            {
                throw new InvalidHardwareRandomMeasurementException("硬體亂數樣本出現非有限或非正數吞吐；整場拒收，不改成零。");
            }
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        HardwareRandomMeasurement rdrand,
        HardwareRandomMeasurement rdSeed,
        int rounds)
    {
        List<DeepBenchMetric> metrics =
        [
            CreateThroughputMetric("cpu.rdrand.throughput-mvals", "RDRAND throughput", rdrand),
            CreateRetryMetric("cpu.rdrand.retry-ratio", "RDRAND retry ratio", rdrand),
            CreateThroughputMetric("cpu.rdseed.throughput-mvals", "RDSEED throughput", rdSeed),
            CreateRetryMetric("cpu.rdseed.retry-ratio", "RDSEED retry ratio", rdSeed),
        ];

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"RDRAND / RDSEED 32-bit；各 {rounds} 輪分段量測",
            metrics,
            [
                "RDRAND 與 RDSEED 分開計時；吞吐只統計成功取得的 32-bit 值。",
                "retry ratio 是指令回報未取得值的重試次數除以成功值數。",
            ],
            [
                "吞吐與 retry 包含 .NET intrinsic、迴圈與 Stopwatch 可觀察成本；不是 ISA 表定 latency。",
                "這不是熵源品質認證、不是密碼學安全性評分，也不推算長期熵供應能力。",
                "RDSEED 在低熵狀態可能變慢或重試；背景負載、VM 設定與 CPU 狀態都會影響結果。",
            ],
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchMetric CreateThroughputMetric(
        string id,
        string title,
        HardwareRandomMeasurement measurement)
    {
        double[] samples = measurement.Samples
            .Select(sample => sample.Values / sample.Seconds / 1_000_000.0)
            .ToArray();
        return new(
            id,
            title,
            "Mvalues/s",
            true,
            $"{measurement.Samples.Count} rounds; 32-bit values",
            samples,
            samples.Select((value, index) => new DeepBenchMetricPoint(
                value,
                new Dictionary<string, string>
                {
                    ["round"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                [value])).ToArray());
    }

    private static DeepBenchMetric CreateRetryMetric(
        string id,
        string title,
        HardwareRandomMeasurement measurement)
    {
        double[] samples = measurement.Samples
            .Select(sample => sample.Retries / (double)sample.Values)
            .ToArray();
        return new(
            id,
            title,
            "ratio",
            false,
            $"retries / generated values; {measurement.Samples.Count} rounds",
            samples,
            samples.Select((value, index) => new DeepBenchMetricPoint(
                value,
                new Dictionary<string, string>
                {
                    ["round"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                [value])).ToArray());
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後不補量。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不從未完成量測推算。"], kind, error);
}

internal sealed class InvalidHardwareRandomMeasurementException(string message) : Exception(message);

/// <summary>x64 硬體亂數預設引擎；連續失敗會明確終止，不無限阻塞也不改用軟體亂數。</summary>
public sealed class X86HardwareRandomEngine : IHardwareRandomEngine
{
    private const int MaxConsecutiveFailures = 10_000;

    public bool RdrandSupported => NativeHardwareRandom.IsRdrandSupported;
    public bool RdSeedSupported => NativeHardwareRandom.IsRdSeedSupported;

    public async Task<HardwareRandomMeasurement> MeasureAsync(
        HardwareRandomSource source,
        int valuesPerRound,
        int rounds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(valuesPerRound, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        return await Task.Run(() => Measure(source, valuesPerRound, rounds, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static HardwareRandomMeasurement Measure(
        HardwareRandomSource source,
        int valuesPerRound,
        int rounds,
        CancellationToken cancellationToken)
    {
        var samples = new List<HardwareRandomSample>(rounds);
        ulong checksum = 0;
        for (int round = 0; round < rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long timestamp = Stopwatch.GetTimestamp();
            long retries = 0;
            for (int valueIndex = 0; valueIndex < valuesPerRound; valueIndex++)
            {
                int consecutiveFailures = 0;
                uint value = 0;
                while (!TryNext(source, out value))
                {
                    retries++;
                    consecutiveFailures++;
                    System.Runtime.Intrinsics.X86.X86Base.Pause();
                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        throw new InvalidOperationException($"{source} 連續 {MaxConsecutiveFailures} 次未取得值；不再阻塞。");
                    }
                }

                checksum ^= value;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
            if (elapsed.TotalSeconds <= 0)
            {
                throw new InvalidOperationException($"{source} 計時輪時間異常。");
            }

            samples.Add(new HardwareRandomSample(valuesPerRound, retries, elapsed.TotalSeconds));
        }

        _ = checksum == 0 ? 1UL : checksum;
        return new HardwareRandomMeasurement(samples);
    }

    private static bool TryNext(HardwareRandomSource source, out uint value) => source switch
    {
        HardwareRandomSource.Rdrand => NativeHardwareRandom.TryRdrand32(out value),
        HardwareRandomSource.RdSeed => NativeHardwareRandom.TryRdSeed32(out value),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}

/// <summary>
/// .NET 沒有公開 RDRAND/RDSEED intrinsic；這裡只生成固定的 16-byte x64 讀值 thunk，
/// 執行前檢查 CPUID，不載入外部程式、不改用軟體亂數。
/// </summary>
internal static unsafe class NativeHardwareRandom
{
    // rdrand eax; jnc fail; mov [rcx],eax; mov eax,1; ret; fail: xor eax,eax; ret
    private static readonly byte[] RdrandThunk =
    [
        0x0F, 0xC7, 0xF0, 0x73, 0x08,
        0x89, 0x01, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,
        0x31, 0xC0, 0xC3
    ];

    // rdseed eax; jnc fail; mov [rcx],eax; mov eax,1; ret; fail: xor eax,eax; ret
    private static readonly byte[] RdSeedThunk =
    [
        0x0F, 0xC7, 0xF8, 0x73, 0x08,
        0x89, 0x01, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,
        0x31, 0xC0, 0xC3
    ];

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TryRandom32Delegate(uint* value);

    private static readonly TryRandom32Delegate? Rdrand32 = CreateDelegate(RdrandThunk, IsRdrandSupported);
    private static readonly TryRandom32Delegate? RdSeed32 = CreateDelegate(RdSeedThunk, IsRdSeedSupported);

    internal static bool IsRdrandSupported => CpuIdFeature(1, 0, 2, 30);
    internal static bool IsRdSeedSupported => CpuIdFeature(7, 0, 1, 18);

    internal static bool TryRdrand32(out uint value) => Try(Rdrand32, out value);
    internal static bool TryRdSeed32(out uint value) => Try(RdSeed32, out value);

    private static bool Try(TryRandom32Delegate? thunk, out uint value)
    {
        value = 0;
        if (thunk is null) return false;
        fixed (uint* pointer = &value)
        {
            return thunk(pointer) != 0;
        }
    }

    private static bool CpuIdFeature(int leaf, int subLeaf, int register, int bit)
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return false;
        (int eax, int ebx, int ecx, int edx) = System.Runtime.Intrinsics.X86.X86Base.CpuId(leaf, subLeaf);
        int value = register switch
        {
            0 => eax,
            1 => ebx,
            2 => ecx,
            _ => edx,
        };
        return ((value >> bit) & 1) != 0;
    }

    private static TryRandom32Delegate? CreateDelegate(byte[] thunk, bool supported)
    {
        if (!supported) return null;

        IntPtr code = VirtualAlloc(
            IntPtr.Zero,
            (UIntPtr)Environment.SystemPageSize,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE);
        if (code == IntPtr.Zero) throw new OutOfMemoryException("無法配置硬體亂數執行頁。");

        try
        {
            Marshal.Copy(thunk, 0, code, thunk.Length);
            if (!FlushInstructionCache(GetCurrentProcess(), code, (UIntPtr)thunk.Length))
            {
                throw new InvalidOperationException("無法清除硬體亂數 thunk 指令快取。");
            }

            if (!VirtualProtect(code, (UIntPtr)Environment.SystemPageSize, PAGE_EXECUTE_READ, out nint _))
            {
                throw new InvalidOperationException("無法將硬體亂數 thunk 改為只讀執行。");
            }

            return Marshal.GetDelegateForFunctionPointer<TryRandom32Delegate>(code);
        }
        catch
        {
            VirtualFree(code, UIntPtr.Zero, MEM_RELEASE);
            throw;
        }
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READ = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out nint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
