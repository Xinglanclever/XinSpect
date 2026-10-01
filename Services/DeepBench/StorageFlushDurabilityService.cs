using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

public interface IFlushDurabilityEngine
{
    Task<StorageFlushDurabilityMeasurement> MeasureAsync(FlushDurabilityContext context, CancellationToken cancellationToken);
}

public sealed record FlushDurabilityContext(
    string TempFilePath,
    long DataLengthBytes,
    long FlushBlockBytes,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress,
    IDiskIoFileSystem FileSystem);

public sealed record StorageFlushDurabilityPoint(
    long DataLengthBytes,
    long FlushBlockBytes,
    IReadOnlyList<double> WriteThroughputSamples,
    IReadOnlyList<double> FlushLatenciesUs,
    IReadOnlyList<double> ReadbackThroughputSamples,
    long FlushCount,
    IReadOnlyList<long> MismatchOffsets);

public sealed record StorageFlushDurabilityMeasurement(IReadOnlyList<StorageFlushDurabilityPoint> Points);

/// <summary>
/// Flush 持久化深測：固定暫存檔逐區塊寫入、每區塊呼叫 FlushToDisk，再重新開檔讀回比對。
/// 只量測 Windows 檔案 API 可觀察的成本與讀回一致性，不模擬斷電，也不宣稱裝置耐用性。
/// </summary>
public sealed class StorageFlushDurabilityService(
    string root,
    long tempBudgetBytes,
    IDiskIoFileSystem? fileSystem = null,
    IFlushDurabilityEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "storage.flush-durability";
    public const long FlushBlockBytes = 1024L * 1024;
    private const long ReserveBytes = 8L * 1024 * 1024 * 1024;
    private const long MinimumBudgetBytes = 64L * 1024 * 1024;
    private const int MaxReportedMismatches = 32;

    private readonly string _root = root;
    private readonly long _budgetBytes = tempBudgetBytes;
    private readonly IDiskIoFileSystem _fileSystem = fileSystem ?? new WindowsFlushDurabilityFileSystem();
    private readonly IFlushDurabilityEngine _engine = engine ?? new FlushToDiskEngine();

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
                FlushDurabilityWorkload workload = GetWorkload(context.Profile);
                context.Progress.Report(new DeepBenchProgress(Id, 0, workload.Rounds, 0.02, "準備 Flush 圖樣"));
                FlushDurabilityContext engineContext = new(
                    plan.TempFilePath,
                    workload.DataLengthBytes,
                    FlushBlockBytes,
                    context.Profile,
                    context.Progress,
                    _fileSystem);
                StorageFlushDurabilityMeasurement measurement = await _engine
                    .MeasureAsync(engineContext, cancellationToken)
                    .ConfigureAwait(false);
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
                    $"{workload.Rounds} round(s)；資料長度 {workload.DataLengthBytes / 1024.0 / 1024.0:0} MiB；每 {FlushBlockBytes / 1024.0 / 1024.0:0} MiB FlushToDisk",
                    metrics,
                    [
                        $"唯一暫存檔：{plan.TempFilePath}；啟動前已揭露路徑。",
                        $"每輪逐 {FlushBlockBytes / 1024.0 / 1024.0:0} MiB 區塊寫入並 FlushToDisk，關閉後重新開檔逐區塊讀回比對。",
                        mismatchCount == 0 ? "所有已讀回區塊逐位元組一致。" : $"偵測到 {mismatchCount} 個不一致位置。",
                    ],
                    [
                        "這是 Windows 檔案 API 可觀察的 FlushToDisk 成本與讀回驗證；檔案系統與裝置快取行為包含在內。",
                        "FlushToDisk 不模擬斷電；此結果不是斷電測試、耐用性認證、MemTest86 或儲存裝置認證。",
                        "結果只代表本機此次暫存檔路徑，不代表整碟其他區域或未來寫入。",
                    ],
                    mismatchCount == 0 ? DeepBenchFailureKind.None : DeepBenchFailureKind.Unstable,
                    mismatchError);
            }
        }
        catch (OperationCanceledException)
        {
            result = Cancelled(context, started);
        }
        catch (InvalidFlushDurabilityException exception)
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

    internal static FlushDurabilityWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(1, 8L * 1024 * 1024),
        DeepBenchRunProfile.Full => new(2, 32L * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
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

    private static void ValidateMeasurement(StorageFlushDurabilityMeasurement measurement)
    {
        if (measurement.Points.Count == 0) throw new InvalidFlushDurabilityException("沒有任何 Flush round；不輸出空結果。");
        foreach (StorageFlushDurabilityPoint point in measurement.Points)
        {
            if (point.DataLengthBytes <= 0 || point.FlushBlockBytes <= 0)
            {
                throw new InvalidFlushDurabilityException("Flush 資料長度與區塊長度必須大於零。");
            }

            long expectedFlushCount = (point.DataLengthBytes + point.FlushBlockBytes - 1) / point.FlushBlockBytes;
            if (point.FlushCount != expectedFlushCount)
            {
                throw new InvalidFlushDurabilityException($"Flush 次數不符：實測 {point.FlushCount}，資料長度需要 {expectedFlushCount}。");
            }

            if (point.WriteThroughputSamples.Count == 0 ||
                point.FlushLatenciesUs.Count != point.FlushCount ||
                point.ReadbackThroughputSamples.Count == 0)
            {
                throw new InvalidFlushDurabilityException("每個 round 必須有寫入、逐區塊 Flush 與讀回樣本。");
            }

            if (point.WriteThroughputSamples.Any(value => !double.IsFinite(value) || value <= 0) ||
                point.FlushLatenciesUs.Any(value => !double.IsFinite(value) || value <= 0) ||
                point.ReadbackThroughputSamples.Any(value => !double.IsFinite(value) || value <= 0))
            {
                throw new InvalidFlushDurabilityException("Flush 樣本出現非有限或非正數值；整場拒收，不改成零。");
            }

            if (point.MismatchOffsets.Any(offset => offset < 0 || offset >= point.DataLengthBytes))
            {
                throw new InvalidFlushDurabilityException("不一致位置超出資料範圍。");
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(IReadOnlyList<StorageFlushDurabilityPoint> points) =>
    [
        CreateMetric("storage.flush-durability.write-mibps", "Write throughput", "MiB/s", true, points, point => point.WriteThroughputSamples),
        CreateMetric("storage.flush-durability.flush-latency-us", "FlushToDisk latency", "µs", false, points, point => point.FlushLatenciesUs),
        CreateMetric("storage.flush-durability.readback-mibps", "Readback throughput", "MiB/s", true, points, point => point.ReadbackThroughputSamples),
        CreateMetric("storage.flush-durability.flush-count", "Flush count", "count", false, points, point => [point.FlushCount]),
        CreateMetric("storage.flush-durability.mismatch-count", "Mismatch count", "count", false, points, point => [point.MismatchOffsets.Count]),
    ];

    private static DeepBenchMetric CreateMetric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        IReadOnlyList<StorageFlushDurabilityPoint> points,
        Func<StorageFlushDurabilityPoint, IReadOnlyList<double>> selector)
    {
        double[] aggregate = points.SelectMany(selector).ToArray();
        return new DeepBenchMetric(
            id,
            title,
            unit,
            higherIsBetter,
            $"{points.Count} flush round(s)",
            aggregate,
            points.Select(point => new DeepBenchMetricPoint(
                selector(point).Average(),
                new Dictionary<string, string>
                {
                    ["dataBytes"] = point.DataLengthBytes.ToString(CultureInfo.InvariantCulture),
                    ["flushBlockBytes"] = point.FlushBlockBytes.ToString(CultureInfo.InvariantCulture),
                },
                selector(point))).ToArray());
    }

    private static string CreateMismatchError(IReadOnlyList<StorageFlushDurabilityPoint> points)
    {
        IEnumerable<string> summaries = points
            .Where(point => point.MismatchOffsets.Count > 0)
            .Select(point => $"round: {point.MismatchOffsets.Count} 個錯誤；前 {Math.Min(point.MismatchOffsets.Count, 8)} 個 offset={string.Join(", ", point.MismatchOffsets.Take(8))}");
        return "FlushToDisk 後讀回不一致；不修復、不補值。" + string.Join("；", summaries);
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後關閉並刪除暫存檔。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不從未完成 Flush round 推算。"], kind, error);

    private sealed class WindowsFlushDurabilityFileSystem : IDiskIoFileSystem
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

    private sealed class FlushToDiskEngine : IFlushDurabilityEngine
    {
        public async Task<StorageFlushDurabilityMeasurement> MeasureAsync(
            FlushDurabilityContext context,
            CancellationToken cancellationToken)
        {
            if (context.DataLengthBytes % context.FlushBlockBytes != 0)
            {
                throw new InvalidFlushDurabilityException("資料長度必須是 Flush 區塊長度的整數倍。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(context.TempFilePath)!);
            context.FileSystem.Create(context.TempFilePath);
            int rounds = StorageFlushDurabilityService.GetWorkload(context.Profile).Rounds;
            var points = new List<StorageFlushDurabilityPoint>(rounds);
            byte[] expected = GC.AllocateUninitializedArray<byte>((int)context.DataLengthBytes);
            byte[] actual = GC.AllocateUninitializedArray<byte>((int)context.DataLengthBytes);

            for (int round = 0; round < rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Progress.Report(new DeepBenchProgress(
                    StorageFlushDurabilityService.TestId,
                    round,
                    rounds,
                    0.05 + 0.85 * round / rounds,
                    $"Flush round {round + 1}"));

                StorageFlushDurabilityPoint point = await MeasureRoundAsync(
                    context.TempFilePath,
                    expected,
                    actual,
                    context.FlushBlockBytes,
                    round,
                    cancellationToken).ConfigureAwait(false);
                points.Add(point);
            }

            return new StorageFlushDurabilityMeasurement(points);
        }

        private static async Task<StorageFlushDurabilityPoint> MeasureRoundAsync(
            string path,
            byte[] expected,
            byte[] actual,
            long blockBytes,
            int round,
            CancellationToken cancellationToken)
        {
            int blockLength = (int)blockBytes;
            FillPattern(expected, round, blockLength);
            var flushLatencies = new List<double>(expected.Length / blockLength);
            long writeStarted = Stopwatch.GetTimestamp();

            using (SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None))
            {
                for (long offset = 0; offset < expected.Length; offset += blockBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RandomAccess.WriteAsync(
                        handle,
                        expected.AsMemory((int)offset, blockLength),
                        offset,
                        cancellationToken).ConfigureAwait(false);
                    long flushStarted = Stopwatch.GetTimestamp();
                    RandomAccess.FlushToDisk(handle);
                    flushLatencies.Add(Stopwatch.GetElapsedTime(flushStarted).TotalMilliseconds * 1000.0);
                }
            }

            TimeSpan writeElapsed = Stopwatch.GetElapsedTime(writeStarted);
            long readStarted = Stopwatch.GetTimestamp();
            using (SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                for (long offset = 0; offset < expected.Length; offset += blockBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await RandomAccess.ReadAsync(
                        handle,
                        actual.AsMemory((int)offset, blockLength),
                        offset,
                        cancellationToken).ConfigureAwait(false);
                    if (read != blockLength)
                    {
                        throw new IOException($"Flush 讀回長度不足：offset={offset}, read={read}, expected={blockLength}。");
                    }
                }
            }

            TimeSpan readElapsed = Stopwatch.GetElapsedTime(readStarted);
            List<long> mismatches = StorageWriteIntegrityService.FindMismatches(expected, actual, 0, MaxReportedMismatches);
            double mib = expected.Length / (1024.0 * 1024.0);
            long flushCount = expected.Length / blockLength;
            return new StorageFlushDurabilityPoint(
                expected.Length,
                blockLength,
                [mib / writeElapsed.TotalSeconds],
                [.. flushLatencies],
                [mib / readElapsed.TotalSeconds],
                flushCount,
                [.. mismatches]);
        }

        private static void FillPattern(byte[] target, int round, int blockLength)
        {
            for (int offset = 0; offset < target.Length; offset += blockLength)
            {
                uint seed = (uint)(round * 0x10000 + offset / blockLength + 1);
                for (int index = 0; index < blockLength; index++)
                {
                    target[offset + index] = (byte)(((seed * 2654435761u) ^ ((uint)index * 40503u + 0x9E3779B9u)) >> 24);
                }
            }
        }
    }
}

internal sealed class InvalidFlushDurabilityException : InvalidOperationException
{
    public InvalidFlushDurabilityException(string message) : base(message) { }
}

public readonly record struct FlushDurabilityWorkload(int Rounds, long DataLengthBytes);
