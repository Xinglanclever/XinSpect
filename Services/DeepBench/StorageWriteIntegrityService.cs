using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

public interface IWriteIntegrityEngine
{
    Task<StorageWriteIntegrityMeasurement> MeasureAsync(WriteIntegrityContext context, CancellationToken cancellationToken);
}

public sealed record WriteIntegrityContext(
    string TempFilePath,
    long DataLengthBytes,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    IDiskIoFileSystem FileSystem);

public sealed record StorageWriteIntegrityPoint(
    string Pattern,
    long DataLengthBytes,
    IReadOnlyList<double> WriteThroughputSamples,
    IReadOnlyList<double> VerifyThroughputSamples,
    IReadOnlyList<double> LatencySamplesUs,
    IReadOnlyList<long> MismatchOffsets);

public sealed record StorageWriteIntegrityMeasurement(IReadOnlyList<StorageWriteIntegrityPoint> Points);

/// <summary>
/// 寫入完整性深測：對唯一暫存檔寫入確定圖樣、FlushToDisk 後逐區塊讀回比對。
/// 只報告錯誤位置與摘要，不修復使用者資料；不模擬斷電，也不宣稱裝置認證。
/// </summary>
public sealed class StorageWriteIntegrityService(
    string root,
    long tempBudgetBytes,
    IDiskIoFileSystem? fileSystem = null,
    IWriteIntegrityEngine? engine = null) : IDeepBenchTest
{
    public enum PatternId
    {
        Fixed55,
        FixedAA,
        Prng,
    }

    public const string TestId = "storage.write-integrity";
    private const long ReserveBytes = 8L * 1024 * 1024 * 1024;
    private const long MinimumBudgetBytes = 64L * 1024 * 1024;
    private const int MaxReportedMismatches = 32;

    private readonly string _root = root;
    private readonly long _budgetBytes = tempBudgetBytes;
    private readonly IDiskIoFileSystem _fileSystem = fileSystem ?? new WindowsWriteIntegrityFileSystem();
    private readonly IWriteIntegrityEngine _engine = engine ?? new WriteThroughIntegrityEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        DeepBenchTestResult result;
        Exception? cleanupFailure = null;
        DiskIoPlan? plan = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long free = _fileSystem.GetAvailableFreeSpace(_root);
            plan = DiskIoMatrixService.CreatePlan(_root, _budgetBytes, free);
            string? guardError = ValidatePlan(plan);
            if (guardError is not null)
            {
                result = Failed(context, started, DeepBenchFailureKind.NotRun, guardError);
            }
            else
            {
                WriteIntegrityWorkload workload = GetWorkload(context.Profile);
                context.Progress.Report(new DeepBenchProgress(Id, 0, 1, 0.02, "準備完整性圖樣"));
                var engineContext = new WriteIntegrityContext(
                    plan.TempFilePath, workload.DataLengthBytes, context.Profile, context.Progress, _fileSystem);
                StorageWriteIntegrityMeasurement measurement = await _engine.MeasureAsync(engineContext, cancellationToken).ConfigureAwait(false);
                ValidateMeasurement(measurement);
                IReadOnlyList<DeepBenchMetric> metrics = CreateMetrics(measurement.Points);
                long mismatchCount = measurement.Points.Sum(point => point.MismatchOffsets.Count);
                string? mismatchError = mismatchCount == 0 ? null : CreateMismatchError(measurement.Points);
                result = new DeepBenchTestResult(
                    TestId,
                    context.SessionId,
                    context.Profile,
                    started,
                    DateTime.UtcNow,
                    $"{workload.Rounds} round(s)；資料長度 {workload.DataLengthBytes / 1024.0 / 1024.0:0} MiB；fixed 0x55 / 0xAA / PRNG",
                    metrics,
                    [
                        $"唯一暫存檔：{plan.TempFilePath}；啟動前已揭露路徑。",
                        "每輪使用固定 0x55、固定 0xAA 與確定性 PRNG 圖樣，寫入後 FlushToDisk 再逐區塊讀回比對。",
                        mismatchCount == 0 ? "所有已讀回區塊逐位元組一致。" : $"偵測到 {mismatchCount} 個不一致摘要位置。",
                    ],
                    [
                        "這是 Windows 檔案 API 可觀察路徑；裝置內建快取、韌體處理與檔案系統行為仍會包含在內。",
                        "FlushToDisk 不模擬斷電；此結果不是斷電級耐用性測試、MemTest86 或儲存裝置認證。",
                        "結果只在本次暫存檔與本機路徑上成立，不代表整碟其他區域。",
                    ],
                    mismatchCount == 0 ? DeepBenchFailureKind.None : DeepBenchFailureKind.Unstable,
                    mismatchError);
            }
        }
        catch (OperationCanceledException)
        {
            result = Cancelled(context, started);
        }
        catch (InvalidWriteIntegrityException exception)
        {
            result = Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            result = Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
        finally
        {
            if (plan is not null)
            {
                try
                {
                    if (_fileSystem.Exists(plan.TempFilePath)) _fileSystem.Delete(plan.TempFilePath);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
        }

        if (cleanupFailure is not null)
        {
            result = result with
            {
                FailureKind = DeepBenchFailureKind.PlatformError,
                Error = $"量測結束但暫存檔刪除失敗：{cleanupFailure.Message}",
                Limitations = [.. result.Limitations, "請手動確認 XinSpect.deepbench.tmp 是否仍存在。"],
            };
        }

        context.Progress.Report(new DeepBenchProgress(Id, 1, 1, 1, "暫存檔已清理"));
        return result;
    }

    internal static WriteIntegrityWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1, 8L * 1024 * 1024),
        DeepBenchRunProfile.Full => new(2, 32L * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static byte[] CreatePattern(PatternId pattern, int length, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        return Enumerable.Range(0, length).Select(index => PatternByte(pattern, (uint)index, (uint)seed)).ToArray();
    }

    internal static List<long> FindMismatches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, long baseOffset, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (expected.Length != actual.Length) throw new ArgumentException("期望與實際長度不同。");
        var mismatches = new List<long>();
        for (int i = 0; i < expected.Length && mismatches.Count < maxCount; i++)
        {
            if (expected[i] != actual[i]) mismatches.Add(baseOffset + i);
        }

        return mismatches;
    }

    private static byte PatternByte(PatternId pattern, uint index, uint seed) => pattern switch
    {
        PatternId.Fixed55 => 0x55,
        PatternId.FixedAA => 0xAA,
        PatternId.Prng => (byte)(((seed * 2654435761u) ^ (index * 40503u + 0x9E3779B9u)) >> 24),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, null),
    };

    private static string? ValidatePlan(DiskIoPlan plan)
    {
        if (Path.GetFileName(plan.TempFilePath) != "XinSpect.deepbench.tmp") return "暫存檔名固定為 XinSpect.deepbench.tmp。";
        if (plan.TempBudgetBytes < MinimumBudgetBytes) return $"暫存預算至少 {MinimumBudgetBytes / 1024 / 1024:0} MiB。";
        if (plan.FreeSpaceAfterBudgetBytes < ReserveBytes)
        {
            return $"可用空間不足：預算 {plan.TempBudgetBytes / 1024 / 1024:0} MiB 後仍必須保留 8 GB（目前只會剩 {plan.FreeSpaceAfterBudgetBytes / 1024 / 1024 / 1024:0.00} GB）。";
        }

        return null;
    }

    private static void ValidateMeasurement(StorageWriteIntegrityMeasurement measurement)
    {
        if (measurement.Points.Count == 0) throw new InvalidWriteIntegrityException("沒有任何完整性圖樣；不輸出空結果。");
        foreach (StorageWriteIntegrityPoint point in measurement.Points)
        {
            if (point.DataLengthBytes <= 0) throw new InvalidWriteIntegrityException("圖樣資料長度必須大於零。");
            if (point.WriteThroughputSamples.Count == 0 || point.VerifyThroughputSamples.Count == 0 || point.LatencySamplesUs.Count == 0)
            {
                throw new InvalidWriteIntegrityException($"圖樣 {point.Pattern} 缺少寫入、驗證或延遲樣本。");
            }

            if (point.WriteThroughputSamples.Any(value => !double.IsFinite(value) || value <= 0) ||
                point.VerifyThroughputSamples.Any(value => !double.IsFinite(value) || value <= 0) ||
                point.LatencySamplesUs.Any(value => !double.IsFinite(value) || value <= 0))
            {
                throw new InvalidWriteIntegrityException($"圖樣 {point.Pattern} 出現非有限或非正數樣本；整場拒收，不改成零。");
            }

            if (point.MismatchOffsets.Any(offset => offset < 0 || offset >= point.DataLengthBytes))
            {
                throw new InvalidWriteIntegrityException($"圖樣 {point.Pattern} 的錯誤位置超出資料範圍。");
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(IReadOnlyList<StorageWriteIntegrityPoint> points) =>
    [
        CreateMetric("storage.write-integrity.write-mibps", "Write throughput", "MiB/s", true, points, point => point.WriteThroughputSamples),
        CreateMetric("storage.write-integrity.verify-mibps", "Read and verify throughput", "MiB/s", true, points, point => point.VerifyThroughputSamples),
        CreateMetric("storage.write-integrity.latency-us", "Block verify latency", "µs", false, points, point => point.LatencySamplesUs),
        CreateMetric("storage.write-integrity.mismatch-count", "Mismatch count", "count", false, points, point => [point.MismatchOffsets.Count]),
    ];

    private static DeepBenchMetric CreateMetric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        IReadOnlyList<StorageWriteIntegrityPoint> points,
        Func<StorageWriteIntegrityPoint, IReadOnlyList<double>> selector)
    {
        double[] aggregate = points.SelectMany(selector).ToArray();
        return new DeepBenchMetric(
            id,
            title,
            unit,
            higherIsBetter,
            $"{points.Count} pattern rounds",
            aggregate,
            points.Select(point => new DeepBenchMetricPoint(
                selector(point).Average(),
                new Dictionary<string, string>
                {
                    ["pattern"] = point.Pattern,
                    ["dataBytes"] = point.DataLengthBytes.ToString(CultureInfo.InvariantCulture),
                },
                selector(point))).ToArray());
    }

    private static string CreateMismatchError(IReadOnlyList<StorageWriteIntegrityPoint> points)
    {
        IEnumerable<string> summaries = points
            .Where(point => point.MismatchOffsets.Count > 0)
            .Select(point => $"{point.Pattern}: {point.MismatchOffsets.Count} 個錯誤；前 {Math.Min(point.MismatchOffsets.Count, 8)} 個 offset={string.Join(", ", point.MismatchOffsets.Take(8))}");
        return "寫入後讀回不一致；不修復、不補值。" + string.Join("；", summaries);
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後關閉並刪除暫存檔。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不從未完成圖樣推算。"], kind, error);

    private sealed class WindowsWriteIntegrityFileSystem : IDiskIoFileSystem
    {
        public long GetAvailableFreeSpace(string root)
        {
            string fullPath = Path.GetFullPath(root);
            string? volume = Path.GetPathRoot(fullPath) ?? throw new IOException($"無法解析磁碟根目錄：{root}");
            return new DriveInfo(volume).AvailableFreeSpace;
        }

        public bool Exists(string path) => File.Exists(path);
        public void Create(string path) { using var _ = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None); }
        public void Delete(string path) => File.Delete(path);
    }

    private sealed class WriteThroughIntegrityEngine : IWriteIntegrityEngine
    {
        private const int BlockBytes = 1024 * 1024;

        public async Task<StorageWriteIntegrityMeasurement> MeasureAsync(WriteIntegrityContext context, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(context.TempFilePath)!);
            var points = new List<StorageWriteIntegrityPoint>();
            PatternId[] patterns = [PatternId.Fixed55, PatternId.FixedAA, PatternId.Prng];
            int rounds = GetWorkload(context.Profile).Rounds;
            byte[] expected = GC.AllocateUninitializedArray<byte>((int)context.DataLengthBytes);
            byte[] actual = GC.AllocateUninitializedArray<byte>((int)context.DataLengthBytes);

            for (int round = 0; round < rounds; round++)
            {
                for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PatternId pattern = patterns[patternIndex];
                    context.Progress.Report(new DeepBenchProgress(
                        TestId,
                        round * patterns.Length + patternIndex,
                        rounds * patterns.Length,
                        0.05 + 0.85 * (round * patterns.Length + patternIndex) / (rounds * patterns.Length),
                        $"{pattern} round {round + 1}"));

                    FillPattern(expected, pattern, round);
                    (double writeThroughput, double verifyThroughput, List<double> latencies, List<long> mismatches) =
                        await MeasureRoundAsync(context.TempFilePath, expected, actual, cancellationToken).ConfigureAwait(false);
                    points.Add(new StorageWriteIntegrityPoint(
                        pattern.ToStringInvariant(),
                        context.DataLengthBytes,
                        [writeThroughput],
                        [verifyThroughput],
                        [.. latencies],
                        [.. mismatches]));
                }
            }

            return new StorageWriteIntegrityMeasurement(points);
        }

        private static async Task<(double Write, double Verify, List<double> Latencies, List<long> Mismatches)> MeasureRoundAsync(
            string path, byte[] expected, byte[] actual, CancellationToken cancellationToken)
        {
            long timestamp = Stopwatch.GetTimestamp();
            using (SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.WriteThrough))
            {
                for (long offset = 0; offset < expected.Length; offset += BlockBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int length = Math.Min(BlockBytes, expected.Length - (int)offset);
                    await RandomAccess.WriteAsync(handle, expected.AsMemory((int)offset, length), offset, cancellationToken).ConfigureAwait(false);
                }

                RandomAccess.FlushToDisk(handle);
            }

            TimeSpan writeElapsed = Stopwatch.GetElapsedTime(timestamp);
            timestamp = Stopwatch.GetTimestamp();
            var latencies = new List<double>(expected.Length / BlockBytes);
            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                for (long offset = 0; offset < expected.Length; offset += BlockBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int length = Math.Min(BlockBytes, expected.Length - (int)offset);
                    long blockStarted = Stopwatch.GetTimestamp();
                    int read = await RandomAccess.ReadAsync(handle, actual.AsMemory((int)offset, length), offset, cancellationToken).ConfigureAwait(false);
                    if (read != length) throw new IOException($"完整性讀取長度不足：offset={offset}, read={read}, expected={length}。");
                    latencies.Add(Stopwatch.GetElapsedTime(blockStarted).TotalMilliseconds * 1000.0);
                }
            }

            List<long> mismatches = FindMismatches(expected, actual, 0, MaxReportedMismatches);
            TimeSpan verifyElapsed = Stopwatch.GetElapsedTime(timestamp);
            double mib = expected.Length / (1024.0 * 1024.0);
            return (mib / writeElapsed.TotalSeconds, mib / verifyElapsed.TotalSeconds, latencies, mismatches);
        }

        private static void FillPattern(byte[] target, PatternId pattern, int round)
        {
            for (int block = 0; block < target.Length; block += BlockBytes)
            {
                int length = Math.Min(BlockBytes, target.Length - block);
                uint seed = (uint)(round * 0x10000 + block / BlockBytes + 1);
                for (int i = 0; i < length; i++)
                {
                    target[block + i] = PatternByte(pattern, (uint)i, seed);
                }
            }
        }
    }
}

internal sealed class InvalidWriteIntegrityException : InvalidOperationException
{
    public InvalidWriteIntegrityException(string message) : base(message) { }
}

public readonly record struct WriteIntegrityWorkload(int Rounds, long DataLengthBytes);

internal static class PatternIdExtensions
{
    public static string ToStringInvariant(this StorageWriteIntegrityService.PatternId pattern) => pattern switch
    {
        StorageWriteIntegrityService.PatternId.Fixed55 => "fixed-0x55",
        StorageWriteIntegrityService.PatternId.FixedAA => "fixed-0xaa",
        StorageWriteIntegrityService.PatternId.Prng => "prng",
        _ => pattern.ToString(),
    };
}
