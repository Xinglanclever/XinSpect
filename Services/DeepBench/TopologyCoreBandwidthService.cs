using System.Diagnostics;
using System.Globalization;

namespace XinSpect;

public interface ITopologyCoreBandwidthEngine
{
    Task<TopologyCoreBandwidthMeasurement> MeasureAsync(
        TopologyCoreBandwidthContext context,
        CancellationToken cancellationToken);
}

public sealed record TopologyCoreBandwidthSettings(
    int ChunkLongs,
    int WarmupRounds,
    int MeasureRounds);

public sealed record TopologyCoreBandwidthContext(
    IReadOnlyList<ProcessorRef> Processors,
    DeepBenchRunProfile Profile,
    TopologyCoreBandwidthSettings Settings,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public sealed record TopologyCoreBandwidthPoint(
    ProcessorRef Source,
    ProcessorRef Target,
    double GigabytesPerSecond);

public sealed record TopologyCoreBandwidthMeasurement(
    IReadOnlyList<TopologyCoreBandwidthPoint> Points,
    int PinFailureCount = 0);

public sealed class TopologyCoreBandwidthValidationException(string message)
    : InvalidOperationException(message);

/// <summary>
/// 核心到核心頻寬矩陣：每個 ordered LP pair 用兩條釘選執行緒在共享 managed buffer
/// 上做寫入／讀回搬運。量測的是本程式實際達成的 coherence/cache path 頻寬。
/// </summary>
public sealed class TopologyCoreBandwidthService(
    Func<IReadOnlyList<ProcessorRef>>? processorProvider = null,
    ITopologyCoreBandwidthEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "topology.core-bandwidth";

    public static string[] Limitations { get; } =
    [
        "量的是本程式在兩個邏輯處理器之間達成的共享 buffer 搬運頻寬，不是 DRAM STREAM 絕對上限。",
        "buffer 可能命中 L2／L3；結果反映 coherence 與 cache path，不外推 mesh、ring 或 Infinity Fabric 絕對頻寬。",
        "任何釘選失敗的 pair 都會剔除；沒有有效釘選就不偽裝成核心到核心量測，也不補值。",
        "使用者模式親和性會受行程 affinity、群組拓撲與系統排程影響；多處理器群組路徑未在實機全面驗證。",
    ];

    private readonly Func<IReadOnlyList<ProcessorRef>> _processorProvider =
        processorProvider ?? CpuAffinity.AllLogicalProcessors;
    private readonly ITopologyCoreBandwidthEngine _engine =
        engine ?? new WindowsTopologyCoreBandwidthEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "列舉邏輯處理器矩陣"));
        IReadOnlyList<ProcessorRef> processors = _processorProvider();
        if (processors.Count < 2)
        {
            return Unsupported(context, started);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TopologyCoreBandwidthSettings settings = GetSettings(context.Profile);
            var engineContext = new TopologyCoreBandwidthContext(
                processors,
                context.Profile,
                settings,
                context.Progress,
                cancellationToken);
            TopologyCoreBandwidthMeasurement measurement = await _engine
                .MeasureAsync(engineContext, cancellationToken)
                .ConfigureAwait(false);
            ValidateMeasurement(measurement);
            return CreateResult(context, started, processors, settings, measurement);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (TopologyCoreBandwidthValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static TopologyCoreBandwidthSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(65_536, 2, 12),
        DeepBenchRunProfile.Full => new(131_072, 4, 32),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void ValidateMeasurement(TopologyCoreBandwidthMeasurement measurement)
    {
        if (measurement.Points.Count == 0)
            throw new TopologyCoreBandwidthValidationException("沒有任何核心到核心頻寬點位；不輸出空結果。");
        if (measurement.PinFailureCount < 0)
            throw new TopologyCoreBandwidthValidationException("釘選失敗計數不可為負。");

        HashSet<(ProcessorRef Source, ProcessorRef Target)> pairs = new();
        foreach (TopologyCoreBandwidthPoint point in measurement.Points)
        {
            if (point.Source.Equals(point.Target))
                throw new TopologyCoreBandwidthValidationException("核心到核心頻寬不可使用相同邏輯處理器。");
            if (!pairs.Add((point.Source, point.Target)))
                throw new TopologyCoreBandwidthValidationException("ordered LP pair 重複；不推算重複矩陣。");
            if (!double.IsFinite(point.GigabytesPerSecond) || point.GigabytesPerSecond <= 0)
                throw new TopologyCoreBandwidthValidationException("核心頻寬出現非有限或非正數樣本；整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        IReadOnlyList<ProcessorRef> processors,
        TopologyCoreBandwidthSettings settings,
        TopologyCoreBandwidthMeasurement measurement)
    {
        bool multiGroup = processors.Select(item => item.Group).Distinct().Count() > 1;
        double[] samples = measurement.Points.Select(point => point.GigabytesPerSecond).ToArray();
        var points = measurement.Points
            .Select(point => new DeepBenchMetricPoint(
                point.GigabytesPerSecond,
                new Dictionary<string, string>
                {
                    ["fromLp"] = point.Source.Index.ToString(CultureInfo.InvariantCulture),
                    ["toLp"] = point.Target.Index.ToString(CultureInfo.InvariantCulture),
                },
                [point.GigabytesPerSecond]))
            .ToArray();

        var conditions = new List<string>
        {
            $"實測 {measurement.Points.Count} 對 ordered LP pair；chunk {settings.ChunkLongs * 8L / 1024:0} KiB、warmup {settings.WarmupRounds} 回、量測 {settings.MeasureRounds} 回。",
        };
        if (measurement.PinFailureCount > 0)
        {
            conditions.Add($"釘選失敗 {measurement.PinFailureCount} 對；已剔除，不補值。");
        }
        if (multiGroup)
        {
            conditions.Add("偵測到多處理器群組；使用明確 thread group affinity，但此路徑未在實機全面驗證。");
        }

        var limitations = new List<string>(Limitations);
        if (measurement.PinFailureCount > 0)
        {
            limitations.Insert(0, $"釘選失敗的 {measurement.PinFailureCount} 對已剔除；不補值、不假裝有效釘選。");
        }

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{processors.Count} LP 全矩陣；共享 managed buffer 搬運；{settings.MeasureRounds} 量測回/pair",
            [new DeepBenchMetric(
                "topology.core-bandwidth.gbps",
                "Core-to-core bandwidth",
                "GB/s",
                true,
                "pinned producer/consumer shared-buffer matrix",
                samples,
                points)],
            conditions,
            limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [], ["需要至少兩個可釘選邏輯處理器。"], DeepBenchFailureKind.Unsupported, "邏輯處理器少於兩個。");

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後不輸出部分搬運矩陣補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["矩陣不完整時不推算缺失 pair。"], kind, error);
}

/// <summary>Windows 使用者模式實作；只使用 thread group affinity，不碰 MSR 或驅動。</summary>
public sealed class WindowsTopologyCoreBandwidthEngine : ITopologyCoreBandwidthEngine
{
    public Task<TopologyCoreBandwidthMeasurement> MeasureAsync(
        TopologyCoreBandwidthContext context,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Measure(context, cancellationToken), cancellationToken);
    }

    private static TopologyCoreBandwidthMeasurement Measure(
        TopologyCoreBandwidthContext context,
        CancellationToken cancellationToken)
    {
        var points = new List<TopologyCoreBandwidthPoint>();
        int pinFailures = 0;
        IReadOnlyList<ProcessorRef> processors = context.Processors;
        int pairCount = processors.Count * (processors.Count - 1);
        int completed = 0;

        foreach (ProcessorRef source in processors)
        {
            foreach (ProcessorRef target in processors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Equals(target)) continue;

                context.Progress.Report(new DeepBenchProgress(
                    TopologyCoreBandwidthService.TestId,
                    completed,
                    pairCount,
                    0.05 + 0.93 * completed / pairCount,
                    $"LP {source.Label(CpuAffinity.IsMultiGroup)} → {target.Label(CpuAffinity.IsMultiGroup)}"));
                (TopologyCoreBandwidthPoint? point, bool pinFailed) =
                    MeasurePair(source, target, context.Settings, cancellationToken);
                if (pinFailed) pinFailures++;
                else if (point is not null) points.Add(point);
                completed++;
            }
        }

        context.Progress.Report(new DeepBenchProgress(
            TopologyCoreBandwidthService.TestId,
            pairCount,
            pairCount,
            0.98,
            $"核心搬運矩陣完成；{points.Count} pair"));
        return new TopologyCoreBandwidthMeasurement(points, pinFailures);
    }

    private static (TopologyCoreBandwidthPoint? Point, bool PinFailed) MeasurePair(
        ProcessorRef source,
        ProcessorRef target,
        TopologyCoreBandwidthSettings settings,
        CancellationToken cancellationToken)
    {
        long[] buffer = new long[settings.ChunkLongs * 2];
        using var writeDone = new Barrier(2);
        using var roundDone = new Barrier(2);
        using var ready = new CountdownEvent(2);
        using var gate = new ManualResetEventSlim(false);
        var producer = new PinnedTransferWorker(
            source, buffer, writeDone, roundDone, ready, gate, settings, producer: true, cancellationToken);
        var consumer = new PinnedTransferWorker(
            target, buffer, writeDone, roundDone, ready, gate, settings, producer: false, cancellationToken);
        var producerThread = new Thread(producer.Run)
        {
            IsBackground = true,
            Name = $"XinSpect bandwidth producer G{source.Group}LP{source.Index}",
        };
        var consumerThread = new Thread(consumer.Run)
        {
            IsBackground = true,
            Name = $"XinSpect bandwidth consumer G{target.Group}LP{target.Index}",
        };

        producerThread.Start();
        consumerThread.Start();
        try
        {
            ready.Wait(cancellationToken);
            if (producer.PinFailed || consumer.PinFailed)
            {
                gate.Set();
                producerThread.Join();
                consumerThread.Join();
                cancellationToken.ThrowIfCancellationRequested();
                return (null, true);
            }

            long timestamp = Stopwatch.GetTimestamp();
            gate.Set();
            producerThread.Join();
            consumerThread.Join();
            cancellationToken.ThrowIfCancellationRequested();

            Exception? workerError = producer.Error ?? consumer.Error;
            if (producer.PinFailed || consumer.PinFailed) return (null, true);
            if (workerError is not null)
                throw new TopologyCoreBandwidthValidationException($"核心搬運 worker 結果不可信：{workerError.Message}");
            double throughput = producer.Throughput > 0 ? producer.Throughput : consumer.Throughput;
            if (!double.IsFinite(throughput) || throughput <= 0)
                throw new TopologyCoreBandwidthValidationException("核心搬運計時異常；不輸出該 pair。");

            return (new TopologyCoreBandwidthPoint(source, target, throughput), false);
        }
        finally
        {
            gate.Set();
            producerThread.Join();
            consumerThread.Join();
        }
    }

    private sealed class PinnedTransferWorker(
        ProcessorRef processor,
        long[] buffer,
        Barrier writeDone,
        Barrier roundDone,
        CountdownEvent ready,
        ManualResetEventSlim gate,
        TopologyCoreBandwidthSettings settings,
        bool producer,
        CancellationToken cancellationToken)
    {
        public Exception? Error { get; private set; }
        public bool PinFailed { get; private set; }
        public double Throughput { get; private set; }

        public void Run()
        {
            using CpuAffinity.Pin pin = CpuAffinity.Pinned(processor);
            PinFailed = !pin.Ok;
            ready.Signal();
            try
            {
                gate.Wait(cancellationToken);
                if (!pin.Ok)
                {
                    return;
                }

                if (producer) Produce();
                else Consume();
            }
            catch (Exception exception)
            {
                Error = exception;
            }
        }

        private void Produce()
        {
            long timestamp = 0;
            int totalRounds = settings.WarmupRounds + settings.MeasureRounds;
            for (int round = 0; round < totalRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (round == settings.WarmupRounds) timestamp = Stopwatch.GetTimestamp();
                int slot = (round & 1) * settings.ChunkLongs;
                for (int index = 0; index < settings.ChunkLongs; index++)
                    buffer[slot + index] = Payload(round, index);
                WaitPhase(writeDone);
                WaitPhase(roundDone);
            }

            RecordThroughput(timestamp);
        }

        private void Consume()
        {
            long timestamp = 0;
            int totalRounds = settings.WarmupRounds + settings.MeasureRounds;
            for (int round = 0; round < totalRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WaitPhase(writeDone);
                if (round == settings.WarmupRounds) timestamp = Stopwatch.GetTimestamp();
                int slot = (round & 1) * settings.ChunkLongs;
                long checksum = 0;
                for (int index = 0; index < settings.ChunkLongs; index++)
                {
                    long actual = Volatile.Read(ref buffer[slot + index]);
                    long expected = Payload(round, index);
                    if (actual != expected)
                        throw new TopologyCoreBandwidthValidationException("共享 buffer 讀回 sentinel 與寫入不符。");
                    checksum ^= actual;
                }

                _ = checksum;
                WaitPhase(roundDone);
            }

            RecordThroughput(timestamp);
        }

        private void RecordThroughput(long timestamp)
        {
            double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
            if (elapsedSeconds <= 0)
                throw new TopologyCoreBandwidthValidationException("核心搬運計時時間異常。");
            double bytes = (double)settings.MeasureRounds * settings.ChunkLongs * sizeof(long);
            Throughput = bytes / elapsedSeconds / 1_000_000_000d;
        }

        private void WaitPhase(Barrier barrier)
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15), cancellationToken))
                throw new TopologyCoreBandwidthValidationException("核心搬運同步逾時；worker 進度不一致。");
        }

        private static long Payload(int round, int index) =>
            unchecked((long)((uint)index * 2862933555777941757UL + (uint)round * 3037000493UL));
    }
}
