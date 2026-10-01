using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

public interface IStorageIocpEngine
{
    Task<StorageIocpMeasurement> MeasureAsync(StorageIocpContext context, CancellationToken cancellationToken);
}

public sealed record StorageIocpContext(
    string TempFilePath,
    long TempBudgetBytes,
    long DataLengthBytes,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress);

public sealed record StorageIocpPoint(
    int BlockBytes,
    int QueueDepth,
    IReadOnlyList<double> WriteIopsSamples,
    IReadOnlyList<double> WriteThroughputSamples,
    IReadOnlyList<double> WriteLatencySamplesUs,
    IReadOnlyList<double> ReadIopsSamples,
    IReadOnlyList<double> ReadThroughputSamples,
    IReadOnlyList<double> ReadLatencySamplesUs);

public sealed record StorageIocpMeasurement(IReadOnlyList<StorageIocpPoint> Points);

/// <summary>
/// 原生 Windows IOCP 深測：CreateIoCompletionPort + OVERLAPPED ReadFile/WriteFile，
/// 資料面走 NO_BUFFERING + WriteThrough。控制面只使用固定 XinSpect.iocp.tmp，
/// 預算後保留 8 GB，結束、例外與取消都會刪除暫存檔。
/// </summary>
public sealed class StorageIocpEngineService : IDeepBenchTest
{
    public const string TestId = "storage.iocp-engine";
    public const string TempFileName = "XinSpect.iocp.tmp";
    public const long ReserveBytes = 8L * 1024 * 1024 * 1024;
    public const long MinimumBudgetBytes = 64L * 1024 * 1024;
    public const int Alignment = 4096;

    public static string[] Limitations { get; } =
    [
        "資料面是 CreateIoCompletionPort 搭配原生 OVERLAPPED ReadFile／WriteFile；不是 .NET async worker 也不是 Linux io_uring。",
        "NO_BUFFERING + WriteThrough 繞過 Windows 檔案快取，但裝置內建快取、驅動排程與檔案系統仍屬實測路徑。",
        "寫入 sentinel 會在讀回階段驗證；只驗證每塊開頭，不做逐 byte 完整性驗證，持久性由 Flush durability 測項互補。",
        "結果只在本次固定暫存檔與指定磁碟上成立，不外推整碟、網路盤或不同電源狀態。",
    ];

    private readonly string _root;
    private readonly long _budgetBytes;
    private readonly IDiskIoFileSystem _fileSystem;
    private readonly IStorageIocpEngine _engine;

    public StorageIocpEngineService(
        string root,
        long tempBudgetBytes,
        IDiskIoFileSystem? fileSystem = null,
        IStorageIocpEngine? engine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _budgetBytes = tempBudgetBytes;
        _fileSystem = fileSystem ?? new WindowsDiskIoFileSystemAdapter();
        _engine = engine ?? new WindowsIocpStorageEngine();
    }

    public string Id => TestId;

    public static StorageIocpPlan CreatePlan(string storageRoot, long tempBudgetBytes, long availableFreeSpaceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        string root = Path.GetFullPath(storageRoot);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        return new StorageIocpPlan(
            root,
            Path.Combine(root, TempFileName),
            tempBudgetBytes,
            availableFreeSpaceBytes,
            availableFreeSpaceBytes - tempBudgetBytes);
    }

    public static string? ValidatePlan(StorageIocpPlan plan)
    {
        if (Path.GetFileName(plan.TempFilePath) != TempFileName) return $"暫存檔名固定為 {TempFileName}。";
        if (plan.TempBudgetBytes < MinimumBudgetBytes) return $"暫存預算至少 {MinimumBudgetBytes / 1024 / 1024:0} MiB。";
        if (plan.TempBudgetBytes % Alignment != 0) return $"暫存預算必須是 {Alignment} 位元組的倍數。";
        if (plan.FreeSpaceAfterBudgetBytes < ReserveBytes)
            return $"可用空間不足：預算後仍必須保留 8 GB（目前只會剩 {plan.FreeSpaceAfterBudgetBytes / 1024d / 1024 / 1024:0.00} GB）。";
        return null;
    }

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        DeepBenchTestResult result;
        Exception? cleanupFailure = null;
        StorageIocpPlan? plan = null;
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
                Directory.CreateDirectory(Path.GetDirectoryName(plan.TempFilePath)!);
                _fileSystem.Create(plan.TempFilePath);
                context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.02, "準備原生 IOCP 暫存檔"));
                long dataLength = plan.TempBudgetBytes / Alignment * Alignment;
                var engineContext = new StorageIocpContext(
                    plan.TempFilePath, plan.TempBudgetBytes, dataLength, context.Profile, context.Progress);
                StorageIocpMeasurement measurement = await _engine.MeasureAsync(engineContext, cancellationToken).ConfigureAwait(false);
                ValidateMeasurement(measurement);
                result = new DeepBenchTestResult(
                    TestId,
                    context.SessionId,
                    context.Profile,
                    started,
                    DateTime.UtcNow,
                    $"Windows IOCP；暫存檔 {plan.TempFilePath}；資料長度 {dataLength / 1024 / 1024:0} MiB；未緩衝直接 I/O",
                    CreateMetrics(measurement.Points),
                    [
                        $"唯一暫存檔：{TempFileName}；啟動前已揭露完整路徑。",
                        $"保留 {measurement.Points.Count} 個 block×QD 點位，寫入與讀回原始樣本分開保留。",
                        "每點位皆先以 IOCP 寫入並 FlushFileBuffers，再以 IOCP 讀回 sentinel。",
                    ],
                    Limitations,
                    DeepBenchFailureKind.None,
                    null);
            }
        }
        catch (OperationCanceledException)
        {
            result = Cancelled(context, started);
        }
        catch (StorageIocpValidationException exception)
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
                Error = $"量測結束但 IOCP 暫存檔刪除失敗：{cleanupFailure.Message}",
                Limitations = [.. result.Limitations, $"請手動確認 {TempFileName} 是否仍存在。"],
            };
        }

        context.Progress.Report(new DeepBenchProgress(TestId, 1, 1, 1, "IOCP 暫存檔已清理"));
        return result;
    }

    internal static (int BlockBytes, int QueueDepth, int Operations)[] GetConfigurations(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick =>
        [
            (4096, 1, 128),
            (4096, 16, 128),
            (65536, 1, 128),
            (65536, 16, 128),
        ],
        DeepBenchRunProfile.Full =>
        [
            (4096, 1, 256),
            (4096, 8, 256),
            (4096, 32, 256),
            (65536, 1, 256),
            (65536, 8, 256),
            (65536, 32, 256),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    private static void ValidateMeasurement(StorageIocpMeasurement measurement)
    {
        if (measurement.Points.Count == 0)
            throw new StorageIocpValidationException("沒有任何 IOCP 點位；不輸出空結果。");
        foreach (StorageIocpPoint point in measurement.Points)
        {
            if (point.BlockBytes <= 0 || point.BlockBytes % Alignment != 0)
                throw new StorageIocpValidationException("IOCP 區塊大小必須為 4096 對齊。");
            if (point.QueueDepth <= 0)
                throw new StorageIocpValidationException("IOCP queue depth 必須大於零。");
            if (point.WriteIopsSamples.Count == 0 || point.WriteThroughputSamples.Count == 0 ||
                point.ReadIopsSamples.Count == 0 || point.ReadThroughputSamples.Count == 0 ||
                point.WriteLatencySamplesUs.Count == 0 || point.ReadLatencySamplesUs.Count == 0)
                throw new StorageIocpValidationException("每個 IOCP 點位都需要寫入與讀回原始樣本。");
            if (!point.WriteIopsSamples.Concat(point.WriteThroughputSamples).Concat(point.WriteLatencySamplesUs)
                    .Concat(point.ReadIopsSamples).Concat(point.ReadThroughputSamples).Concat(point.ReadLatencySamplesUs)
                    .All(value => double.IsFinite(value) && value > 0))
                throw new StorageIocpValidationException($"IOCP block={point.BlockBytes}, QD={point.QueueDepth} 出現非有限或非正數樣本；整場拒收。");
        }
    }

    private static IReadOnlyList<DeepBenchMetric> CreateMetrics(IReadOnlyList<StorageIocpPoint> points) =>
    [
        CreateMetric("storage.iocp.write-iops", "IOCP write IOPS", "IOPS", true, points, point => point.WriteIopsSamples),
        CreateMetric("storage.iocp.write-mibps", "IOCP write throughput", "MiB/s", true, points, point => point.WriteThroughputSamples),
        CreateMetric("storage.iocp.write-latency-us", "IOCP write completion latency", "µs", false, points, point => point.WriteLatencySamplesUs),
        CreateMetric("storage.iocp.read-iops", "IOCP read IOPS", "IOPS", true, points, point => point.ReadIopsSamples),
        CreateMetric("storage.iocp.read-mibps", "IOCP read throughput", "MiB/s", true, points, point => point.ReadThroughputSamples),
        CreateMetric("storage.iocp.read-latency-us", "IOCP read completion latency", "µs", false, points, point => point.ReadLatencySamplesUs),
    ];

    private static DeepBenchMetric CreateMetric(
        string id,
        string title,
        string unit,
        bool higherIsBetter,
        IReadOnlyList<StorageIocpPoint> points,
        Func<StorageIocpPoint, IReadOnlyList<double>> selector)
    {
        double[] aggregate = points.SelectMany(selector).ToArray();
        return new DeepBenchMetric(
            id,
            title,
            unit,
            higherIsBetter,
            $"{points.Count} point matrix; GetQueuedCompletionStatus timings",
            aggregate,
            points.Select(point => new DeepBenchMetricPoint(
                selector(point).Average(),
                new Dictionary<string, string>
                {
                    ["blockBytes"] = point.BlockBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["queueDepth"] = point.QueueDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                selector(point))).ToArray());
    }

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消後關閉並刪除 IOCP 暫存檔。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失 IOCP 樣本。"], kind, error);
}

public sealed record StorageIocpPlan(
    string StorageRoot,
    string TempFilePath,
    long TempBudgetBytes,
    long AvailableFreeSpaceBytes,
    long FreeSpaceAfterBudgetBytes);

public sealed class StorageIocpValidationException(string message) : InvalidOperationException(message);

/// <summary>把 DiskIoMatrixService 的 Windows 檔案系統實作共用給 IOCP 控制面。</summary>
public sealed class WindowsDiskIoFileSystemAdapter : IDiskIoFileSystem
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

/// <summary>Windows 原生 IOCP engine；service 測試可注入假的 engine。</summary>
public sealed unsafe class WindowsIocpStorageEngine : IStorageIocpEngine
{
    private const int ErrorIoPending = 997;
    private const int WaitTimeout = 258;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileBegin = 0;

    public Task<StorageIocpMeasurement> MeasureAsync(StorageIocpContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() => Measure(context, cancellationToken), cancellationToken);
    }

    private static StorageIocpMeasurement Measure(StorageIocpContext context, CancellationToken cancellationToken)
    {
        (int block, int queueDepth, int operations)[] configurations =
            StorageIocpEngineService.GetConfigurations(context.Profile);
        var points = new List<StorageIocpPoint>(configurations.Length);
        for (int index = 0; index < configurations.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int blockBytes, int queueDepth, int operations) = configurations[index];
            context.Progress.Report(new DeepBenchProgress(
                StorageIocpEngineService.TestId,
                index,
                configurations.Length,
                0.05 + 0.9 * index / configurations.Length,
                $"IOCP block {blockBytes / 1024}K ・ QD {queueDepth}"));
            points.Add(MeasurePoint(context.TempFilePath, context.DataLengthBytes, blockBytes, queueDepth, operations));
        }

        return new StorageIocpMeasurement(points);
    }

    private static StorageIocpPoint MeasurePoint(
        string path,
        long dataLength,
        int blockBytes,
        int queueDepth,
        int operationsPerRound)
    {
        IntPtr file = CreateFileW(
            path,
            GenericReadWrite,
            0,
            IntPtr.Zero,
            2, // CREATE_ALWAYS
            FileFlagOverlapped | FileFlagNoBuffering | FileFlagWriteThrough,
            IntPtr.Zero);
        if (file == IntPtr.Zero)
            throw new StorageIocpValidationException($"CreateFile 失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}。");

        IntPtr port = CreateIoCompletionPort(file, IntPtr.Zero, 0, 0);
        if (port == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(file);
            throw new StorageIocpValidationException($"CreateIoCompletionPort 失敗，Win32 錯誤 {error}。");
        }

        byte[] buffers = new byte[queueDepth * blockBytes];
        var bufferHandles = new GCHandle[queueDepth];
        var overlapHandles = new GCHandle[queueDepth];
        var overlaps = new NativeOverlapped[queueDepth];
        var timestamps = new long[queueDepth];
        var writeIops = new List<double>(2);
        var writeThroughput = new List<double>(2);
        var writeLatencies = new List<double>(2 * operationsPerRound);
        var readIops = new List<double>(2);
        var readThroughput = new List<double>(2);
        var readLatencies = new List<double>(2 * operationsPerRound);
        try
        {
            if (!SetFilePointerEx(file, 0, out _, FileBegin) || !SetEndOfFile(file))
                throw new StorageIocpValidationException($"設定 IOCP 檔案長度失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}。");

            for (int slot = 0; slot < queueDepth; slot++)
            {
                bufferHandles[slot] = GCHandle.Alloc(buffers, GCHandleType.Pinned);
                overlapHandles[slot] = GCHandle.Alloc(overlaps, GCHandleType.Pinned);
            }

            nint overlapBase = overlapHandles[0].AddrOfPinnedObject();
            Dictionary<nint, int> overlapToSlot = new(queueDepth);
            for (int slot = 0; slot < queueDepth; slot++)
                overlapToSlot[overlapBase + slot * sizeof(NativeOverlapped)] = slot;

            RunPhase(
                file, port, overlapBase, overlapToSlot, bufferHandles[0].AddrOfPinnedObject(), timestamps,
                blockBytes, queueDepth, operationsPerRound, dataLength, write: true,
                iops: writeIops, throughput: writeThroughput, latencies: writeLatencies);
            if (!FlushFileBuffers(file))
                throw new StorageIocpValidationException($"FlushFileBuffers 失敗，Win32 錯誤 {Marshal.GetLastWin32Error()}。");
            RunPhase(
                file, port, overlapBase, overlapToSlot, bufferHandles[0].AddrOfPinnedObject(), timestamps,
                blockBytes, queueDepth, operationsPerRound, dataLength, write: false,
                iops: readIops, throughput: readThroughput, latencies: readLatencies);
        }
        finally
        {
            CloseHandle(port);
            CloseHandle(file);
            for (int slot = 0; slot < queueDepth; slot++)
            {
                if (overlapHandles[slot].IsAllocated) overlapHandles[slot].Free();
                if (bufferHandles[slot].IsAllocated) bufferHandles[slot].Free();
            }
        }

        return new StorageIocpPoint(
            blockBytes,
            queueDepth,
            writeIops,
            writeThroughput,
            writeLatencies,
            readIops,
            readThroughput,
            readLatencies);
    }

    private static void RunPhase(
        IntPtr file,
        IntPtr port,
        nint overlapBase,
        Dictionary<nint, int> overlapToSlot,
        nint bufferBase,
        long[] timestamps,
        int blockBytes,
        int queueDepth,
        int operationsPerRound,
        long dataLength,
        bool write,
        List<double> iops,
        List<double> throughput,
        List<double> latencies)
    {
        int totalOperations = 2 * operationsPerRound;
        int issued = 0;
        int completed = 0;
        long phaseTimestamp = Stopwatch.GetTimestamp();
        while (completed < totalOperations)
        {
            while (issued < totalOperations && issued - completed < queueDepth)
            {
                int round = issued / operationsPerRound;
                int localOperation = issued % operationsPerRound;
                int slot = issued % queueDepth;
                long blocks = dataLength / blockBytes;
                long offset = (long)(Mix((uint)round, (uint)localOperation) % (ulong)blocks) * blockBytes;
                nint buffer = bufferBase + slot * blockBytes;
                uint expected = SentinelWord(offset, round);
                if (write) *(uint*)buffer = expected;
                else *(uint*)buffer = 0;

                NativeOverlapped* pendingOverlap = (NativeOverlapped*)(overlapBase + slot * sizeof(NativeOverlapped));
                *pendingOverlap = default;
                pendingOverlap->OffsetLow = unchecked((int)(offset & 0xFFFFFFFFL));
                pendingOverlap->OffsetHigh = (int)(offset >> 32);
                timestamps[slot] = Stopwatch.GetTimestamp();
                bool accepted = write
                    ? WriteFile(file, (void*)buffer, (uint)blockBytes, out _, pendingOverlap)
                    : ReadFile(file, (void*)buffer, (uint)blockBytes, out _, pendingOverlap);
                if (!accepted)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorIoPending)
                        throw new StorageIocpValidationException($"IOCP {(write ? "WriteFile" : "ReadFile")} 提交失敗，Win32 錯誤 {error}。");
                }

                issued++;
            }

            if (!GetQueuedCompletionStatus(port, out uint bytes, out nuint completionKey, out NativeOverlapped* overlap, 5000))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == WaitTimeout)
                    throw new StorageIocpValidationException("IOCP completion 逾時；可能有 I/O 未完成，整場拒收。");
                throw new StorageIocpValidationException($"GetQueuedCompletionStatus 失敗，Win32 錯誤 {error}。");
            }

            int completedSlot = overlapToSlot[(nint)overlap];
            if (bytes != (uint)blockBytes)
                throw new StorageIocpValidationException("IOCP 完成位元組數不等於區塊大小。");
            if (!write)
            {
                int round = completed / operationsPerRound;
                long offset = (long)overlap->OffsetLow & 0xFFFFFFFFL | ((long)overlap->OffsetHigh << 32);
                nint buffer = bufferBase + completedSlot * blockBytes;
                if (*(uint*)buffer != SentinelWord(offset, round))
                    throw new StorageIocpValidationException("IOCP 讀回 sentinel 與寫入不符；結果不可信。");
            }

            latencies.Add(Stopwatch.GetElapsedTime(timestamps[completedSlot]).TotalMilliseconds * 1000d);
            completed++;
            _ = completionKey;
        }

        double elapsedSeconds = Stopwatch.GetElapsedTime(phaseTimestamp).TotalSeconds;
        if (elapsedSeconds <= 0)
            throw new StorageIocpValidationException("IOCP phase 計時時間異常。");
        iops.Add(totalOperations / elapsedSeconds);
        throughput.Add(totalOperations * (double)blockBytes / 1024 / 1024 / elapsedSeconds);
    }

    private static uint SentinelWord(long offset, int round) =>
        0xC7A5510Cu ^ (uint)offset ^ (uint)(round * 0x1F123BB5u);

    private static ulong Mix(uint round, uint operation)
    {
        ulong value = 0x9E3779B97F4A7C15UL * round + operation;
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        value ^= value >> 33;
        return value;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string path,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(
        IntPtr fileHandle,
        IntPtr existingCompletionPort,
        nuint completionKey,
        uint numberOfConcurrentThreads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(
        IntPtr completionPort,
        out uint numberOfBytesTransferred,
        out nuint completionKey,
        out NativeOverlapped* overlapped,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr file,
        void* buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr file,
        void* buffer,
        uint bytesToRead,
        out uint bytesRead,
        NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        IntPtr file,
        long distance,
        out long newPointer,
        uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEndOfFile(IntPtr file);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(IntPtr file);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
