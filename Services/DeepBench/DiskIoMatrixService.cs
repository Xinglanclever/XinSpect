using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

public enum DiskIoMatrixKind
{
    QdLadder,
    MixedReadWrite
}

/// <summary>啟動前必須揭露的磁碟深測計畫：只允許一個固定暫存檔名，且預算後仍需保留 8 GiB。 </summary>
public sealed record DiskIoPlan(
    string StorageRoot,
    string TempFilePath,
    long TempBudgetBytes,
    long AvailableFreeSpaceBytes,
    long FreeSpaceAfterBudgetBytes);

public interface IDiskIoFileSystem
{
    long GetAvailableFreeSpace(string root);
    bool Exists(string path);
    void Create(string path);
    void Delete(string path);
}

public interface IDiskIoEngine
{
    Task<DiskIoMeasurement> MeasureAsync(DiskIoEngineContext context, CancellationToken cancellationToken);
}

public sealed record DiskIoEngineContext(
    string TempFilePath,
    long TempBudgetBytes,
    long DataLengthBytes,
    DeepBenchRunProfile Profile,
    DiskIoMatrixKind Kind,
    IProgress<DeepBenchProgress> Progress,
    IDiskIoFileSystem FileSystem);

public sealed record DiskIoPointMeasurement(
    int BlockBytes,
    int QueueDepth,
    int ReadPercent,
    IReadOnlyList<double> IopsSamples,
    IReadOnlyList<double> ThroughputSamples,
    IReadOnlyList<double> LatencySamplesUs);

public sealed record DiskIoMeasurement(IReadOnlyList<DiskIoPointMeasurement> Points);

internal sealed class InvalidDiskIoMeasurementException : InvalidOperationException
{
    public InvalidDiskIoMeasurementException(string message) : base(message) { }
}

/// <summary>
/// 磁碟 QD 階梯與 4K 混合讀寫深測。資料面使用 FILE_FLAG_NO_BUFFERING、WriteThrough、
/// 磁區對齊原生記憶體與多個 async worker；控制面保證只建立 XinSpect.deepbench.tmp，
/// 且結束、例外、取消都會關閉並刪除。
/// </summary>
public sealed class DiskIoMatrixService : IDeepBenchTest
{
    public const string QdTestId = "storage.qd-ladder";
    public const string MixedTestId = "storage.mixed-rw";
    private const string TempFileName = "XinSpect.deepbench.tmp";
    private const long ReserveBytes = 8L * 1024 * 1024 * 1024;
    private const long MinimumBudgetBytes = 64L * 1024 * 1024;
    private const int Alignment = 4096;
    private static readonly FileOptions NoBuffering = (FileOptions)0x20000000;

    private readonly DiskIoMatrixKind _kind;
    private readonly string _root;
    private readonly long _budgetBytes;
    private readonly IDiskIoFileSystem _fileSystem;
    private readonly IDiskIoEngine _engine;
    private readonly string _id;

    public DiskIoMatrixService(
        DiskIoMatrixKind kind,
        string root,
        long tempBudgetBytes,
        IDiskIoFileSystem? fileSystem = null,
        IDiskIoEngine? engine = null)
    {
        _kind = kind;
        _root = root;
        _budgetBytes = tempBudgetBytes;
        _fileSystem = fileSystem ?? new WindowsDiskIoFileSystem();
        _engine = engine ?? new UnbufferedDiskIoEngine();
        _id = kind == DiskIoMatrixKind.QdLadder ? QdTestId : MixedTestId;
    }

    public string Id => _id;

    public static DiskIoPlan CreatePlan(string storageRoot, long tempBudgetBytes, long availableFreeSpaceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        string root = Path.GetFullPath(storageRoot);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        return new DiskIoPlan(
            root,
            Path.Combine(root, TempFileName),
            tempBudgetBytes,
            availableFreeSpaceBytes,
            availableFreeSpaceBytes - tempBudgetBytes);
    }

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
            plan = CreatePlan(_root, _budgetBytes, free);
            string? guardError = ValidatePlan(plan);
            if (guardError is not null)
            {
                result = Failed(context, started, DeepBenchFailureKind.NotRun, guardError);
            }
            else
            {
                context.Progress.Report(new DeepBenchProgress(Id, 0, 1, 0.02, "配置唯一暫存檔"));
                long dataLength = plan.TempBudgetBytes / Alignment * Alignment;
                var engineContext = new DiskIoEngineContext(
                    plan.TempFilePath, plan.TempBudgetBytes, dataLength, context.Profile, _kind, context.Progress, _fileSystem);
                DiskIoMeasurement measurement = await _engine.MeasureAsync(engineContext, cancellationToken).ConfigureAwait(false);
                ValidateMeasurement(measurement);
                IReadOnlyList<DeepBenchMetric> metrics = CreateMetrics(_kind, measurement.Points);
                result = new DeepBenchTestResult(
                    Id,
                    context.SessionId,
                    context.Profile,
                    started,
                    DateTime.UtcNow,
                    $"{_kind switch { DiskIoMatrixKind.QdLadder => "QD ladder", _ => "4K mixed R/W" }}；暫存檔 {plan.TempFilePath}；預算 {plan.TempBudgetBytes / 1024.0 / 1024.0:0} MiB；預算後可用 {plan.FreeSpaceAfterBudgetBytes / 1024.0 / 1024 / 1024:0.0} GB；未緩衝直接 I/O",
                    metrics,
                    [
                        $"唯一暫存檔：{TempFileName}；啟動前已揭露 {plan.TempFilePath}。",
                        $"資料長度 {dataLength / 1024.0 / 1024.0:0} MiB；預算後仍保留至少 8 GB。",
                        "每個點位保留多輪 IOPS/MiB/s 樣本與逐筆完成延遲。",
                    ],
                    [
                        "FILE_FLAG_NO_BUFFERING + WriteThrough 繞過 OS 檔案快取；裝置內建快取、驅動與檔案系統行為仍屬實機路徑。",
                        "佇列以 async worker 維持，不是核心原生 io_uring/iocompletion 生成器；尾端與排程會包含在實測內。",
                        "結果只在本次暫存檔與本機路徑上成立，不代表整碟其他區域。",
                    ],
                    DeepBenchFailureKind.None,
                    null);
            }
        }
        catch (OperationCanceledException)
        {
            result = Cancelled(context, started);
        }
        catch (InvalidDiskIoMeasurementException exception)
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

    private static string? ValidatePlan(DiskIoPlan plan)
    {
        if (Path.GetFileName(plan.TempFilePath) != TempFileName) return "暫存檔名固定為 XinSpect.deepbench.tmp。";
        if (plan.TempBudgetBytes < MinimumBudgetBytes) return $"暫存預算至少 {MinimumBudgetBytes / 1024.0 / 1024:0} MiB。";
        if (plan.TempBudgetBytes % Alignment != 0) return $"暫存預算必須是 {Alignment} 位元組的倍數。";
        if (plan.FreeSpaceAfterBudgetBytes < ReserveBytes)
        {
            return $"可用空間不足：預算 {plan.TempBudgetBytes / 1024.0 / 1024:0} MiB 後仍必須保留 8 GB（目前只會剩 {plan.FreeSpaceAfterBudgetBytes / 1024.0 / 1024 / 1024:0.00} GB）。";
        }
        return null;
    }

    private static void ValidateMeasurement(DiskIoMeasurement measurement)
    {
        if (measurement.Points.Count == 0) throw new InvalidDiskIoMeasurementException("沒有任何磁碟點位；不輸出空結果。");
        foreach (DiskIoPointMeasurement point in measurement.Points)
        {
            if (point.BlockBytes <= 0 || point.BlockBytes % Alignment != 0) throw new InvalidDiskIoMeasurementException("區塊大小必須為 4096 對齊。");
            if (point.QueueDepth <= 0) throw new InvalidDiskIoMeasurementException("佇列深度必須大於零。");
            if (point.ReadPercent is < 0 or > 100) throw new InvalidDiskIoMeasurementException("讀取比例必須在 0–100。");
            if (point.IopsSamples.Count == 0 || point.ThroughputSamples.Count == 0 || point.LatencySamplesUs.Count == 0)
                throw new InvalidDiskIoMeasurementException("每個點位都需要 IOPS、吞吐與延遲樣本。");
            if (point.IopsSamples.Any(value => !double.IsFinite(value) || value <= 0) ||
                point.ThroughputSamples.Any(value => !double.IsFinite(value) || value <= 0) ||
                point.LatencySamplesUs.Any(value => !double.IsFinite(value) || value <= 0))
            {
                throw new InvalidDiskIoMeasurementException($"點位 block={point.BlockBytes}, QD={point.QueueDepth}, read={point.ReadPercent}% 出現非有限或非正數吞吐/延遲；整場拒收，不改成零。");
            }
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(DiskIoMatrixKind kind, IReadOnlyList<DiskIoPointMeasurement> points)
    {
        string prefix = kind == DiskIoMatrixKind.QdLadder ? "storage.qd.read" : "storage.mixed";
        return
        [
            CreateMetric(prefix + ".iops", "IOPS", "IOPS", true, points, point => point.IopsSamples),
            CreateMetric(prefix + ".mibps", "Throughput", "MiB/s", true, points, point => point.ThroughputSamples),
            CreateMetric(prefix + ".latency_us", "Completion latency", "µs", false, points, point => point.LatencySamplesUs),
        ];
    }

    private static DeepBenchMetric CreateMetric(
        string id, string title, string unit, bool higherIsBetter,
        IReadOnlyList<DiskIoPointMeasurement> points,
        Func<DiskIoPointMeasurement, IReadOnlyList<double>> samples)
    {
        double[] aggregate = points.SelectMany(samples).ToArray();
        return new DeepBenchMetric(
            id,
            title,
            unit,
            higherIsBetter,
            $"{points.Count} point matrix; raw finite samples retained",
            aggregate,
            points.Select(point => new DeepBenchMetricPoint(
                samples(point).Average(),
                CreateAxes(point),
                samples(point))).ToArray());
    }

    private static IReadOnlyDictionary<string, string> CreateAxes(DiskIoPointMeasurement point)
    {
        var axes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["blockBytes"] = point.BlockBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["queueDepth"] = point.QueueDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["readPercent"] = point.ReadPercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return axes;
    }

    private DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(_id, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後關閉並刪除暫存檔。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(_id, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失點位。"], kind, error);


    private sealed class WindowsDiskIoFileSystem : IDiskIoFileSystem
    {
        public long GetAvailableFreeSpace(string root)
        {
            string fullPath = Path.GetFullPath(root);
            string? volume = Path.GetPathRoot(fullPath) ?? throw new IOException($"無法解析磁碟根目錄：{root}");
            return new DriveInfo(volume).AvailableFreeSpace;
        }
        public bool Exists(string path) => File.Exists(path);
        public void Create(string path)
        {
            using var _ = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        public void Delete(string path) => File.Delete(path);
    }

    private sealed class UnbufferedDiskIoEngine : IDiskIoEngine
    {
        private const int OperationsPerWorkerPerRound = 256;
        private const int SampleRounds = 2;
        private static readonly int[] FullQueueDepths = [1, 2, 4, 8, 16, 32, 64];
        private static readonly int[] QuickQueueDepths = [1, 4, 16];
        private static readonly int[] MixedReadPercents = [100, 70, 50, 30, 0];

        public async Task<DiskIoMeasurement> MeasureAsync(DiskIoEngineContext context, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(context.TempFilePath)!);
            await PrepareFileAsync(context, cancellationToken).ConfigureAwait(false);
            var configs = GetConfigurations(context.Kind, context.Profile);
            var points = new List<DiskIoPointMeasurement>(configs.Count);
            for (int i = 0; i < configs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (int block, int queueDepth, int readPercent) = configs[i];
                context.Progress.Report(new DeepBenchProgress(
                    context.Kind == DiskIoMatrixKind.QdLadder ? QdTestId : MixedTestId,
                    i, configs.Count, 0.1 + 0.8 * i / configs.Count,
                    $"block {block / 1024}K ・ QD {queueDepth} ・ R {readPercent}%"));
                points.Add(await MeasurePointAsync(context.TempFilePath, context.DataLengthBytes, block, queueDepth, readPercent, cancellationToken).ConfigureAwait(false));
            }
            return new DiskIoMeasurement(points);
        }

        private static List<(int BlockBytes, int QueueDepth, int ReadPercent)> GetConfigurations(DiskIoMatrixKind kind, DeepBenchRunProfile profile)
        {
            if (kind == DiskIoMatrixKind.MixedReadWrite)
            {
                return MixedReadPercents.Select(read => (4096, 16, read)).ToList();
            }
            int[] blocks = [4096, 128 * 1024];
            int[] queues = profile == DeepBenchRunProfile.Quick ? QuickQueueDepths : FullQueueDepths;
            return (from block in blocks from queue in queues select (block, queue, 100)).ToList();
        }

        private static async Task PrepareFileAsync(DiskIoEngineContext context, CancellationToken cancellationToken)
        {
            const int chunkBytes = 1024 * 1024;
            using var buffer = new AlignedBufferManager(chunkBytes);
            Random.Shared.NextBytes(buffer.Memory.Span);
            using SafeFileHandle handle = File.OpenHandle(
                context.TempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                NoBuffering | FileOptions.WriteThrough,
                context.DataLengthBytes);
            for (long offset = 0; offset < context.DataLengthBytes; offset += chunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RandomAccess.WriteAsync(handle, buffer.Memory, offset, cancellationToken).ConfigureAwait(false);
            }
            RandomAccess.FlushToDisk(handle);
        }

        private static async Task<DiskIoPointMeasurement> MeasurePointAsync(
            string path, long dataLength, int blockBytes, int queueDepth, int readPercent, CancellationToken cancellationToken)
        {
            int bufferLength = blockBytes * queueDepth;
            using var buffer = new AlignedBufferManager(bufferLength);
            Random.Shared.NextBytes(buffer.Memory.Span);
            var iops = new List<double>(SampleRounds);
            var throughput = new List<double>(SampleRounds);
            var latencies = new List<double>(queueDepth * OperationsPerWorkerPerRound * SampleRounds);
            FileOptions options = readPercent == 100
                ? NoBuffering
                : NoBuffering | FileOptions.WriteThrough;
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                readPercent == 100 ? FileAccess.Read : FileAccess.ReadWrite,
                FileShare.None,
                options);
            for (int round = 0; round < SampleRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long timestamp = Stopwatch.GetTimestamp();
                double[][] rounds = await Task.WhenAll(Enumerable.Range(0, queueDepth)
                    .Select(worker => WorkerAsync(handle, buffer.Memory.Slice(worker * blockBytes, blockBytes), dataLength, blockBytes, worker, queueDepth, OperationsPerWorkerPerRound, readPercent, round, cancellationToken))
                    .ToArray()).ConfigureAwait(false);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
                long operations = queueDepth * OperationsPerWorkerPerRound;
                if (elapsed.TotalSeconds <= 0) throw new InvalidDiskIoMeasurementException("磁碟計時輪時間異常。");
                iops.Add(operations / elapsed.TotalSeconds);
                throughput.Add(operations * blockBytes / 1024.0 / 1024.0 / elapsed.TotalSeconds);
                latencies.AddRange(rounds.SelectMany(values => values));
            }
            return new DiskIoPointMeasurement(blockBytes, queueDepth, readPercent, iops, throughput, latencies);
        }

        private static async Task<double[]> WorkerAsync(
            SafeFileHandle handle,
            Memory<byte> memory,
            long dataLength,
            int blockBytes,
            int worker,
            int queueDepth,
            int operationsPerWorker,
            int readPercent,
            int round,
            CancellationToken cancellationToken)
        {
            long blocks = dataLength / blockBytes;
            var values = new List<double>(operationsPerWorker);
            for (int operation = 0; operation < operationsPerWorker; operation++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int global = round * queueDepth * operationsPerWorker + worker * operationsPerWorker + operation;
                long offset = (long)(Mix((ulong)global) % (ulong)blocks) * blockBytes;
                bool read = readPercent == 100 || (readPercent > 0 && Mix((ulong)global ^ 0x9E3779B97F4A7C15UL) % 100 < (ulong)readPercent);
                long started = Stopwatch.GetTimestamp();
                if (read)
                {
                    int readBytes = await RandomAccess.ReadAsync(handle, memory, offset, cancellationToken).ConfigureAwait(false);
                    if (readBytes != blockBytes) throw new InvalidDiskIoMeasurementException("未緩衝讀取長度不等於區塊大小。");
                }
                else
                {
                    await RandomAccess.WriteAsync(handle, memory, offset, cancellationToken).ConfigureAwait(false);
                }
                values.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000.0);
            }
            return values.ToArray();
        }

        private static ulong Mix(ulong value)
        {
            value ^= value >> 33;
            value *= 0xFF51AFD7ED558CCDUL;
            value ^= value >> 33;
            value *= 0xC4CEB9FE1A85EC53UL;
            value ^= value >> 33;
            return value;
        }
    }

    private sealed unsafe class AlignedBufferManager(int length) : MemoryManager<byte>
    {
        private readonly void* _pointer = NativeMemory.AlignedAlloc((nuint)length, (nuint)Alignment);
        private bool _disposed;

        public new Memory<byte> Memory => CreateMemory(0, length);

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(_pointer, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new((byte*)_pointer + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            NativeMemory.AlignedFree(_pointer);
            _disposed = true;
        }
    }
}

