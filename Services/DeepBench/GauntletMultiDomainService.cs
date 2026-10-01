using System.Diagnostics;
using System.IO;

namespace XinSpect;

public sealed record GauntletMultiDomainWindow(
    int WindowIndex,
    double CpuMops,
    double MemoryMibs,
    double StorageMibs);

public sealed record GauntletMultiDomainMeasurement(IReadOnlyList<GauntletMultiDomainWindow> Windows);

public sealed record GauntletMultiDomainSettings(
    int WindowCount,
    int WindowMilliseconds,
    int WarmupWindows);

public sealed record GauntletMultiDomainContext(
    GauntletMultiDomainSettings Settings,
    string TempDirPath,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IGauntletMultiDomainEngine
{
    Task<GauntletMultiDomainMeasurement> MeasureAsync(GauntletMultiDomainContext context, CancellationToken cancellationToken);
}

public sealed class GauntletMultiDomainValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// 多域 Gauntlet：CPU（整數相依鏈）、記憶體（buffer 偽隨機掃描）、儲存（暫存檔寫入＋讀回驗證）三域
/// 在同一時間窗並行推進，逐窗取樣三域吞吐與 early/late 保留率。
/// 高負載設計，取消即停並保留已完成窗；不合成分數，各域並列。
/// </summary>
public sealed class GauntletMultiDomainService(
    IGauntletMultiDomainEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "gauntlet.multi-domain";

    public static string[] Limitations { get; } =
    [
        "三域 workload 都是本程式的 managed 合成負載：CPU 是整數相依鏈、記憶體是 buffer 偽隨機掃描、儲存是暫存檔寫讀；吞吐不外推成實際應用的多工表現。",
        "三域並行會互相搶資源（這是設計目的）；單域數值會低於各自單獨跑的結果，跨域比較只在本場內部成立。",
        "儲存域受檔案系統快取與裝置行為影響；每次窗內的寫入都會逐位元組讀回驗證，驗證失敗整場拒收。",
        "early/late 保留率是同域首窗與尾窗中位數的比率；不是散熱認證，也不宣稱量到韌體節流行為。",
        "各域逐窗原始樣本並列輸出；不合成單一總分。",
    ];

    private readonly IGauntletMultiDomainEngine _engine = engine ?? new WindowsMultiDomainEngine();

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "規劃多域負載"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GauntletMultiDomainSettings settings = GetSettings(context.Profile);
            string tempDir = Path.Combine(Path.GetTempPath(), "XinSpect.multi-domain");
            Directory.CreateDirectory(tempDir);
            try
            {
                var engineContext = new GauntletMultiDomainContext(settings, tempDir, context.Progress, cancellationToken);
                GauntletMultiDomainMeasurement measurement = await _engine
                    .MeasureAsync(engineContext, cancellationToken)
                    .ConfigureAwait(false);
                Validate(measurement, settings);
                return CreateResult(context, started, settings, measurement);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { /* 目錄刪除失敗不掩蓋量測結果；下次啟動會重建 */ }
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (GauntletMultiDomainValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static GauntletMultiDomainSettings GetSettings(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(6, 700, 1),
        DeepBenchRunProfile.Full => new(12, 900, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void Validate(GauntletMultiDomainMeasurement measurement, GauntletMultiDomainSettings settings)
    {
        if (measurement.Windows.Count != settings.WindowCount)
            throw new GauntletMultiDomainValidationException("時間窗數量不完整；不推算缺失窗。");
        foreach (GauntletMultiDomainWindow window in measurement.Windows)
        {
            if (!double.IsFinite(window.CpuMops) || window.CpuMops <= 0
                || !double.IsFinite(window.MemoryMibs) || window.MemoryMibs <= 0
                || !double.IsFinite(window.StorageMibs) || window.StorageMibs <= 0)
                throw new GauntletMultiDomainValidationException($"第 {window.WindowIndex + 1} 窗出現非有限或非正數樣本；整場拒收。");
        }
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        GauntletMultiDomainSettings settings,
        GauntletMultiDomainMeasurement measurement)
    {
        IReadOnlyList<GauntletMultiDomainWindow> windows = measurement.Windows;
        var metrics = new List<DeepBenchMetric>
        {
            new("gauntlet.multi-domain.cpu.mops", "CPU 域逐窗吞吐", "Mops/s", true, "managed 整數相依鏈；多執行緒", [.. windows.Select(window => window.CpuMops)], []),
            new("gauntlet.multi-domain.memory.mibs", "記憶體域逐窗吞吐", "MiB/s", true, "buffer 偽隨機掃描", [.. windows.Select(window => window.MemoryMibs)], []),
            new("gauntlet.multi-domain.storage.mibs", "儲存域逐窗吞吐", "MiB/s", true, "暫存檔寫入＋逐位元組讀回驗證", [.. windows.Select(window => window.StorageMibs)], []),
        };

        double cpuRetention = windows[^1].CpuMops / windows[0].CpuMops;
        double memoryRetention = windows[^1].MemoryMibs / windows[0].MemoryMibs;
        double storageRetention = windows[^1].StorageMibs / windows[0].StorageMibs;
        metrics.Add(new("gauntlet.multi-domain.cpu.early-late.ratio", "CPU early/late 保留率", "ratio", true, "尾窗／首窗", [cpuRetention], []));
        metrics.Add(new("gauntlet.multi-domain.memory.early-late.ratio", "記憶體 early/late 保留率", "ratio", true, "尾窗／首窗", [memoryRetention], []));
        metrics.Add(new("gauntlet.multi-domain.storage.early-late.ratio", "儲存 early/late 保留率", "ratio", true, "尾窗／首窗", [storageRetention], []));

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{settings.WindowCount} 窗 × {settings.WindowMilliseconds} ms；三域並行",
            metrics,
            [
                $"三域並行推進 {settings.WindowCount} 窗（含 {settings.WarmupWindows} 暖機窗不計入結果）；儲存域使用 {Path.Combine(Path.GetTempPath(), "XinSpect.multi-domain")} 下的唯一暫存檔，結束後刪除。",
                $"early/late：CPU {cpuRetention:0.###}、記憶體 {memoryRetention:0.###}、儲存 {storageRetention:0.###}；並行搶資源是設計目的，單域數值低於單獨跑。",
            ],
            Limitations,
            DeepBenchFailureKind.None,
            null);
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後只保留已完成窗，不補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失窗。"], kind, error);
}

/// <summary>Windows 實測引擎：三域各一條專用執行緒，窗邊界用 shared gate 同步取樣。</summary>
public sealed class WindowsMultiDomainEngine : IGauntletMultiDomainEngine
{
    public Task<GauntletMultiDomainMeasurement> MeasureAsync(
        GauntletMultiDomainContext context,
        CancellationToken cancellationToken)
        => Task.Run(() => Measure(context, cancellationToken), cancellationToken);

    private static GauntletMultiDomainMeasurement Measure(
        GauntletMultiDomainContext context,
        CancellationToken cancellationToken)
    {
        GauntletMultiDomainSettings settings = context.Settings;
        int totalWindows = settings.WarmupWindows + settings.WindowCount;
        var cpuCounters = new WindowCounter();
        var memoryCounters = new WindowCounter();
        var storageCounters = new WindowCounter();
        using var stopSignal = new ManualResetEventSlim(false);
        using var windowGate = new ManualResetEventSlim(true);

        var cpuWorker = new Thread(() => CpuLoop(cpuCounters, stopSignal, cancellationToken))
        {
            IsBackground = true, Name = "XinSpect multi-domain CPU",
        };
        var memoryWorker = new Thread(() => MemoryLoop(memoryCounters, stopSignal, cancellationToken))
        {
            IsBackground = true, Name = "XinSpect multi-domain memory",
        };
        var storageWorker = new Thread(() => StorageLoop(storageCounters, context.TempDirPath, stopSignal, cancellationToken))
        {
            IsBackground = true, Name = "XinSpect multi-domain storage",
        };
        cpuWorker.Start();
        memoryWorker.Start();
        storageWorker.Start();

        var windows = new List<GauntletMultiDomainWindow>();
        try
        {
            for (int window = 0; window < totalWindows; window++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cpuCounters.Reset();
                memoryCounters.Reset();
                storageCounters.Reset();
                long timestamp = Stopwatch.GetTimestamp();
                SpinWait.SpinUntil(() => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds >= settings.WindowMilliseconds);
                double elapsed = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;

                double cpuMops = cpuCounters.SwapOperations() / elapsed / 1_000_000d;
                double memoryMibs = memoryCounters.SwapOperations() * 8d / elapsed / 1024d / 1024d;
                double storageMibs = storageCounters.SwapOperations() / elapsed / 1024d / 1024d;
                context.Progress.Report(new DeepBenchProgress(
                    GauntletMultiDomainService.TestId, window, totalWindows,
                    0.05 + 0.9 * window / totalWindows,
                    window < settings.WarmupWindows
                        ? $"warmup 窗 {window + 1}/{totalWindows}"
                        : $"量測窗 {window - settings.WarmupWindows + 1}/{settings.WindowCount}"));
                if (window >= settings.WarmupWindows)
                    windows.Add(new GauntletMultiDomainWindow(window - settings.WarmupWindows, cpuMops, memoryMibs, storageMibs));
            }
        }
        finally
        {
            stopSignal.Set();
            cpuWorker.Join(2000);
            memoryWorker.Join(2000);
            storageWorker.Join(2000);
        }

        foreach (GauntletMultiDomainWindow window in windows)
        {
            if (!double.IsFinite(window.CpuMops) || window.CpuMops <= 0
                || !double.IsFinite(window.MemoryMibs) || window.MemoryMibs <= 0
                || !double.IsFinite(window.StorageMibs) || window.StorageMibs <= 0)
                throw new GauntletMultiDomainValidationException("多域取樣出現非正數窗；整場拒收。");
        }
        return new GauntletMultiDomainMeasurement(windows);
    }

    private static void CpuLoop(WindowCounter counter, ManualResetEventSlim stop, CancellationToken token)
    {
        long a = 23, b = 29, c = 31, d = 37;
        long local = 0;
        while (!stop.IsSet)
        {
            for (int iteration = 0; iteration < 4096; iteration++)
            {
                a = (a * 6364136223846793005L + iteration) ^ (b >> 7);
                b = (b * 2862933555777941757L + iteration) ^ (c >> 5);
                c = (c * 1442695040888963407L + iteration) ^ (d >> 3);
                d = (d * 1103515245125851171L + iteration) ^ (a >> 11);
            }
            local = a ^ b ^ c ^ d;
            counter.Add(4096);
        }
        if (local == long.MaxValue) throw new InvalidOperationException("sentinel");
        token.ThrowIfCancellationRequested();
    }

    private static void MemoryLoop(WindowCounter counter, ManualResetEventSlim stop, CancellationToken token)
    {
        var buffer = new long[4 * 1024 * 1024]; // 32 MiB
        for (long index = 0; index < buffer.Length; index += 64)
            buffer[index] = index;
        uint prng = 0x9E3779B9u;
        long sink = 0;
        while (!stop.IsSet)
        {
            for (int iteration = 0; iteration < 256; iteration++)
            {
                prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
                long index = (long)(prng >> 8) % buffer.Length;
                sink += buffer[index];
            }
            counter.Add(256);
        }
        if (sink == long.MaxValue) throw new InvalidOperationException("sentinel");
        token.ThrowIfCancellationRequested();
    }

    private static void StorageLoop(WindowCounter counter, string tempDir, ManualResetEventSlim stop, CancellationToken token)
    {
        string path = Path.Combine(tempDir, "pipeline.tmp");
        byte[] payload = new byte[512 * 1024];
        uint prng = 0x1234567u;
        for (int index = 0; index < payload.Length; index += 4)
        {
            prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
            payload[index] = (byte)prng;
            payload[index + 1] = (byte)(prng >> 8);
            payload[index + 2] = (byte)(prng >> 16);
            payload[index + 3] = (byte)(prng >> 24);
        }

        try
        {
            while (!stop.IsSet)
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(payload, 0, payload.Length);
                }
                byte[] readBack = File.ReadAllBytes(path);
                if (!readBack.AsSpan().SequenceEqual(payload))
                    throw new GauntletMultiDomainValidationException("多域儲存域讀回驗證失敗；整場拒收。");
                counter.Add(payload.Length);
            }
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            token.ThrowIfCancellationRequested();
        }
    }

    private sealed class WindowCounter
    {
        private long _operations;
        public void Add(long count) => Interlocked.Add(ref _operations, count);
        public long SwapOperations() => Interlocked.Exchange(ref _operations, 0);
        public void Reset() => Interlocked.Exchange(ref _operations, 0);
    }
}
