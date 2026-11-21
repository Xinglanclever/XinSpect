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
    internal const string NativeOwnershipUnavailableReason = "Flush 原生量測未執行：尚未驗證重開物件所有權；未進行檔案 I/O 或負載。";
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
        DiskIoPlan? plan = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_engine is FlushToDiskEngine)
                return Failed(context, started, DeepBenchFailureKind.NotRun, NativeOwnershipUnavailableReason);
            long free = _fileSystem.GetAvailableFreeSpace(_root);
            var candidatePlan = DiskIoMatrixService.CreatePlan(_root, _budgetBytes, free);
            string? guardError = ValidatePlan(candidatePlan);
            if (guardError is not null)
            {
                result = Failed(context, started, DeepBenchFailureKind.NotRun, guardError);
            }
            else
            {
                plan = candidatePlan;
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
                cancellationToken.ThrowIfCancellationRequested();
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
                        "注入引擎自行管理其測試資源；服務不依路徑刪檔。",
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
        context.Progress.Report(new DeepBenchProgress(Id, 1, 1, 1, "Flush 測試流程已結束。"));
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
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消是請求；注入引擎須自行等待已發出 I/O 結束並管理資源。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

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
        public void Create(string path) => throw new NotSupportedException(NativeOwnershipUnavailableReason);
        public void Delete(string path) => throw new NotSupportedException(NativeOwnershipUnavailableReason);
    }

    // Native workload removed until an identity-safe close/reopen contract is verified.
    private sealed class FlushToDiskEngine : IFlushDurabilityEngine
    {
        public Task<StorageFlushDurabilityMeasurement> MeasureAsync(
            FlushDurabilityContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(NativeOwnershipUnavailableReason);
        }
    }

}

internal sealed class InvalidFlushDurabilityException : InvalidOperationException
{
    public InvalidFlushDurabilityException(string message) : base(message) { }
}

public readonly record struct FlushDurabilityWorkload(int Rounds, long DataLengthBytes);
