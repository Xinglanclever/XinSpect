using System.Globalization;
using System.IO;

namespace XinSpect;

public interface ISlcSustainedWriteEngine
{
    Task<SlcSustainedWriteMeasurement> MeasureAsync(SlcSustainedWriteContext context, CancellationToken cancellationToken);
}

public sealed record SlcSustainedWriteContext(
    string TempFilePath,
    long TargetBytes,
    IProgress<DeepBenchProgress> Progress,
    IDiskIoFileSystem FileSystem);

public sealed record SlcSustainedWriteMeasurement(IReadOnlyList<SlcSample> Samples);

/// <summary>
/// 把既有 SLC 快取耗盡曲線服務接入 Deep Bench。只重複使用同一條持續寫入量測核心；
/// 每秒取樣並 FlushToDisk，保留整條吞吐曲線與既有斷崖推論，不合成裝置快取尺寸。
/// </summary>
public sealed class SlcSustainedWriteAdapter(
    string root,
    long tempBudgetBytes,
    IDiskIoFileSystem? fileSystem = null,
    ISlcSustainedWriteEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "storage.slc-sustained-write";
    private const long ReserveBytes = 8L * 1024 * 1024 * 1024;
    private const long MinimumBudgetBytes = 64L * 1024 * 1024;
    private const long GiB = 1024L * 1024 * 1024;

    private readonly string _root = root;
    private readonly long _budgetBytes = tempBudgetBytes;
    private readonly IDiskIoFileSystem _fileSystem = fileSystem ?? new WindowsSlcFileSystem();
    private readonly ISlcSustainedWriteEngine _engine = engine ?? new ExistingSlcWriteEngine();

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
                long target = GetTargetBytes(context.Profile);
                long availableForSustainedWrite = plan.FreeSpaceAfterBudgetBytes - ReserveBytes;
                if (availableForSustainedWrite < target)
                {
                    result = Failed(
                        context,
                        started,
                        DeepBenchFailureKind.NotRun,
                        $"SLC 持續寫入需要至少 {target / (double)GiB:0} GiB 目標外空間，且仍保留 8 GB；目前預算後可安全寫入 {Math.Max(0, availableForSustainedWrite) / (double)GiB:0.00} GiB。");
                }
                else
                {
                    context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, $"準備 {target / (double)GiB:0} GiB 持續寫入"));
                    SlcSustainedWriteMeasurement measurement = await _engine.MeasureAsync(
                        new SlcSustainedWriteContext(plan.TempFilePath, target, context.Progress, _fileSystem),
                        cancellationToken).ConfigureAwait(false);
                    ValidateMeasurement(measurement);
                    result = CreateResult(context, started, plan, target, measurement);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = Cancelled(context, started);
        }
        catch (InvalidSlcSustainedWriteException exception)
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

        context.Progress.Report(new DeepBenchProgress(TestId, 1, 1, 1, "暫存檔已清理"));
        return result;
    }

    internal static long GetTargetBytes(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => 4L * 1024 * 1024 * 1024,
        DeepBenchRunProfile.Full => 16L * 1024 * 1024 * 1024,
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

    private static void ValidateMeasurement(SlcSustainedWriteMeasurement measurement)
    {
        if (measurement.Samples.Count == 0) throw new InvalidSlcSustainedWriteException("沒有任何 SLC 持續寫入取樣；不輸出空結果。");
        double lastSeconds = -1;
        foreach (SlcSample sample in measurement.Samples)
        {
            if (!double.IsFinite(sample.Seconds) || sample.Seconds < 0 ||
                !double.IsFinite(sample.Mbps) || sample.Mbps <= 0)
            {
                throw new InvalidSlcSustainedWriteException("SLC 曲線樣本出現非有限或非正數吞吐；整場拒收，不改成零。");
            }

            if (sample.Seconds <= lastSeconds)
            {
                throw new InvalidSlcSustainedWriteException("SLC 曲線取樣時間必須遞增。");
            }

            lastSeconds = sample.Seconds;
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        DiskIoPlan plan,
        long targetBytes,
        SlcSustainedWriteMeasurement measurement)
    {
        double[] times = measurement.Samples.Select(sample => sample.Seconds).ToArray();
        double[] speeds = measurement.Samples.Select(sample => sample.Mbps).ToArray();
        var (peakMbps, peakSeconds, cliffSeconds, postMedian) = SlcMath.Analyze(times, speeds);
        string targetText = $"{targetBytes / (double)GiB:0} GiB";
        List<DeepBenchMetric> metrics =
        [
            new(
                TestId + ".mibps",
                "Sustained write curve",
                "MiB/s",
                true,
                $"{targetText}; {measurement.Samples.Count} one-second windows",
                speeds,
                measurement.Samples.Select(sample => new DeepBenchMetricPoint(
                    sample.Mbps,
                    new Dictionary<string, string>
                    {
                        ["timeSeconds"] = sample.Seconds.ToString("0.###", CultureInfo.InvariantCulture),
                    },
                    [sample.Mbps])).ToArray()),
            new(
                TestId + ".peak-mibps",
                "Peak sustained write",
                "MiB/s",
                true,
                $"peak @ {peakSeconds:0.###} s",
                [peakMbps],
                [new DeepBenchMetricPoint(
                    peakMbps,
                    new Dictionary<string, string>
                    {
                        ["timeSeconds"] = peakSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    },
                    [peakMbps])]),
            new(
                TestId + ".cliff-detected",
                "Cliff detected",
                "flag",
                false,
                "throughput < 35% of peak with a 6-sample post-window below 45%",
                [cliffSeconds >= 0 ? 1 : 0],
                [new DeepBenchMetricPoint(cliffSeconds >= 0 ? 1 : 0, new Dictionary<string, string>(), [cliffSeconds >= 0 ? 1 : 0])]),
            new(
                TestId + ".post-cliff-mibps",
                "Post-cliff median write",
                "MiB/s",
                true,
                cliffSeconds >= 0 ? $"after {cliffSeconds:0.###} s" : "no cliff observed",
                cliffSeconds >= 0 ? [postMedian] : [],
                cliffSeconds >= 0
                    ? [new DeepBenchMetricPoint(postMedian, new Dictionary<string, string>(), [postMedian])]
                    : []),
        ];

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{targetText} 目標；暫存檔 {plan.TempFilePath}；4 MiB 循序寫入；每秒 FlushToDisk 並取樣",
            metrics,
            [
                $"唯一暫存檔：{plan.TempFilePath}；啟動前已揭露路徑。",
                $"目標寫入量 {targetText}；量到 {measurement.Samples.Count} 秒吞吐曲線。",
                "每個取樣窗先 FlushToDisk，量的是已送過 Flush 的寫入路徑。",
            ],
            [
                "斷崖是吞吐曲線推導出來的觀察，會受快取演算法、溫度、背景負載與主機狀態影響。",
                "未看到斷崖只代表本輪目標量內未出現；不是韌體快取尺寸認證，也不能推算未來寫入壽命。",
                "結果只代表本機此次暫存檔路徑，不代表整碟其他區域或不同檔案系統狀態。",
            ],
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後關閉並刪除暫存檔。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不從未完成曲線推算。"], kind, error);

    private sealed class WindowsSlcFileSystem : IDiskIoFileSystem
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

    private sealed class ExistingSlcWriteEngine : ISlcSustainedWriteEngine
    {
        public async Task<SlcSustainedWriteMeasurement> MeasureAsync(
            SlcSustainedWriteContext context,
            CancellationToken cancellationToken)
        {
            IProgress<DeepBenchProgress> outer = context.Progress;
            IProgress<(double, string)> reporter = new Progress<(double, string)>(item =>
                outer.Report(new DeepBenchProgress(
                    TestId,
                    0,
                    1,
                    0.05 + 0.85 * Math.Clamp(item.Item1, 0, 1),
                    item.Item2)));

            List<SlcSample> samples = await Task.Run(
                () => SlcCacheBenchService.WriteSustained(context.TempFilePath, context.TargetBytes, cancellationToken, reporter),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new SlcSustainedWriteMeasurement(samples);
        }
    }
}

internal sealed class InvalidSlcSustainedWriteException : InvalidOperationException
{
    public InvalidSlcSustainedWriteException(string message) : base(message) { }
}