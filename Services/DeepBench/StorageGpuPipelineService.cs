using System.Diagnostics;
using System.IO;

namespace XinSpect;

public sealed record GpuPipelineWorkload(long DataBytes);

public sealed record GpuPipelineGpuResult(
    double UploadSeconds,
    double PassSeconds,
    double ReadbackSeconds,
    uint[] HashedElements);

public sealed record GpuPipelineContext(
    byte[] Data,
    IProgress<DeepBenchProgress> Progress,
    CancellationToken CancellationToken);

public interface IGpuPipelineEngine
{
    Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context);
}

public sealed record GpuPipelineMeasurement(
    double DiskReadSeconds,
    GpuPipelineGpuResult GpuResult);

public sealed class GpuPipelineValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// I/O → GPU 管線深測：把確定性 PRNG 內容寫進唯一暫存檔並 FlushToDisk，再走
/// 磁碟讀取 → GPU 上傳 → compute shader 逐元素 FNV-1a → staging 回讀，最後與 CPU 參考逐元素比對。
/// 量的是跨域管線各階段的吞吐與資料生命週期完整性；不模擬斷電、不是儲存認證。
/// </summary>
public sealed class StorageGpuPipelineService(
    string root,
    long tempBudgetBytes,
    IDiskIoFileSystem? fileSystem = null,
    IGpuPipelineEngine? engine = null) : IDeepBenchTest
{
    public const string TestId = "storage.io-gpu-pipeline";
    private const long MinimumWorkingSetBytes = 64L * 1024 * 1024;

    public static string[] Limitations { get; } =
    [
        "GPU 階段的 compute shader 只做逐元素 FNV-1a 雜湊，是真實上傳／回讀路徑但不是代表性運算負載；吞吐不外推成實際應用的管線效能。",
        "CPU 端先寫入暫存檔再讀回，量到的磁碟讀取與其他儲存測項一樣受檔案系統快取影響；FlushToDisk 不模擬斷電。",
        "生命週期驗證只涵蓋本行程的暫存檔到 GPU buffer 的往返；不宣稱裝置認證、不是 MemTest86。",
        "各階段吞吐並列輸出原始值；不合成單一總分。",
        "WARP 與軟體渲染一律拒收；裝置移除即整場失敗。",
    ];

    private readonly string _root = root;
    private readonly long _budgetBytes = tempBudgetBytes;
    private readonly IDiskIoFileSystem _fileSystem = fileSystem ?? new WindowsDiskIoFileSystemAdapter();
    private readonly IGpuPipelineEngine _engine = engine ?? new D3D11PipelineEngine();

    internal Func<DeepBenchRunProfile, GpuPipelineWorkload> WorkloadFactory { get; init; } = GetWorkload;

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.Report(new DeepBenchProgress(Id, 0, 1, 0.02, "規劃暫存檔"));
            cancellationToken.ThrowIfCancellationRequested();
            GpuPipelineWorkload workload = WorkloadFactory(context.Profile);
            cancellationToken.ThrowIfCancellationRequested();
            long free = _fileSystem.GetAvailableFreeSpace(_root);
            cancellationToken.ThrowIfCancellationRequested();
            if (workload.DataBytes > _budgetBytes || workload.DataBytes * 2 > free)
            {
                return Failed(context, started, DeepBenchFailureKind.NotRun,
                    $"可用空間或暫存預算不足以支撐 {workload.DataBytes / 1024.0 / 1024.0:0} MiB 工作集（預算 {_budgetBytes / 1024.0 / 1024.0:0} MiB、可用 {free / 1024.0 / 1024.0:0} MiB）。");
            }

            string root = Path.GetFullPath(_root);
            if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
            cancellationToken.ThrowIfCancellationRequested();
            using OwnedTemporaryFile temp = OwnedTemporaryFile.Create(root, "XinSpect.io-gpu");
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = CreateSyntheticData(workload.DataBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.Report(new DeepBenchProgress(Id, 0, 2, 0.05, "寫入暫存檔並 FlushToDisk"));
            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.Write(temp.Handle, data, 0);
            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.FlushToDisk(temp.Handle);
            cancellationToken.ThrowIfCancellationRequested();

            context.Progress.Report(new DeepBenchProgress(Id, 1, 2, 0.15, "磁碟讀取"));
            cancellationToken.ThrowIfCancellationRequested();
            long readTimestamp = Stopwatch.GetTimestamp();
            byte[] readBack = new byte[data.Length];
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(temp.Handle, readBack, cancellationToken);
            double diskReadSeconds = Stopwatch.GetElapsedTime(readTimestamp).TotalSeconds;
            if (readBack.Length != data.Length)
                throw new GpuPipelineValidationException("磁碟讀回長度與寫入不一致；整場拒收。");

            context.Progress.Report(new DeepBenchProgress(Id, 2, 3, 0.3, "上傳到 GPU 並執行"));
            cancellationToken.ThrowIfCancellationRequested();
            // Await actual engine completion: cancellation does not release file/data resources early.
            GpuPipelineGpuResult gpuResult = await _engine
                .ProcessAsync(new GpuPipelineContext(readBack, context.Progress, cancellationToken))
                .ConfigureAwait(false);
            Validate(workload, gpuResult);
            cancellationToken.ThrowIfCancellationRequested();

            uint mismatches = CountMismatches(readBack, gpuResult.HashedElements, cancellationToken);
            if (mismatches == 0) cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(context, started, workload, diskReadSeconds, gpuResult, mismatches);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested
            && exception.CancellationToken == cancellationToken)
        {
            return Cancelled(context, started);
        }
        catch (GpuUnsupportedException exception)
        {
            return Unsupported(context, started, exception.Message);
        }
        catch (GpuPipelineValidationException exception)
        {
            return Failed(context, started, DeepBenchFailureKind.Unstable, exception.Message);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static GpuPipelineWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new(128L * 1024 * 1024),
        DeepBenchRunProfile.Full => new(256L * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>確定性 PRNG 內容（xorshift32），長度為 4 的倍數以對齊 uint 元素。</summary>
    internal static byte[] CreateSyntheticData(long dataBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long aligned = dataBytes / 4 * 4;
        var data = new byte[aligned];
        uint prng = 0x5DEECE66u;
        for (int index = 0; index < aligned; index += 4)
        {
            if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
            data[index] = (byte)prng;
            data[index + 1] = (byte)(prng >> 8);
            data[index + 2] = (byte)(prng >> 16);
            data[index + 3] = (byte)(prng >> 24);
        }
        return data;
    }

    private static void ReadExactly(Microsoft.Win32.SafeHandles.SafeFileHandle handle, byte[] data,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, data.AsSpan(offset), offset);
            if (read == 0) throw new GpuPipelineValidationException("磁碟讀回提早結束；整場拒收。");
            offset += read;
        }
    }

    internal static uint HashElement(byte[] data, int elementIndex)
    {
        uint value = data[elementIndex * 4]
            | (uint)data[elementIndex * 4 + 1] << 8
            | (uint)data[elementIndex * 4 + 2] << 16
            | (uint)data[elementIndex * 4 + 3] << 24;
        uint hash = 2166136261u;
        hash ^= value;
        hash *= 16777619u;
        return hash;
    }

    private static void Validate(GpuPipelineWorkload workload, GpuPipelineGpuResult gpuResult)
    {
        long elementCount = workload.DataBytes / 4;
        if (gpuResult.HashedElements.Length != elementCount)
            throw new GpuPipelineValidationException(
                $"GPU 回傳元素數 {gpuResult.HashedElements.Length} 與規劃 {elementCount} 不一致；整場拒收。");
        if (!double.IsFinite(gpuResult.UploadSeconds) || gpuResult.UploadSeconds <= 0
            || !double.IsFinite(gpuResult.PassSeconds) || gpuResult.PassSeconds <= 0
            || !double.IsFinite(gpuResult.ReadbackSeconds) || gpuResult.ReadbackSeconds <= 0)
            throw new GpuPipelineValidationException("GPU 階段計時出現非有限或非正數值；整場拒收。");
    }

    private static uint CountMismatches(byte[] data, uint[] hashed, CancellationToken cancellationToken)
    {
        uint mismatches = 0;
        for (int index = 0; index < hashed.Length; index++)
        {
            if (mismatches == 0 && (index & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (HashElement(data, index) != hashed[index])
                mismatches++;
        }
        return mismatches;
    }

    private static DeepBenchTestResult CreateResult(
        DeepBenchRunContext context,
        DateTime started,
        GpuPipelineWorkload workload,
        double diskReadSeconds,
        GpuPipelineGpuResult gpuResult,
        uint mismatches)
    {
        long elementCount = workload.DataBytes / 4;
        double mibs(double seconds) => workload.DataBytes / seconds / 1024.0 / 1024.0;
        double totalSeconds = diskReadSeconds + gpuResult.UploadSeconds + gpuResult.PassSeconds + gpuResult.ReadbackSeconds;
        var metrics = new List<DeepBenchMetric>
        {
            new("storage.io-gpu.disk-read.mibs", "磁碟讀取", "MiB/s", true, "暫存檔整檔讀入", [mibs(diskReadSeconds)], []),
            new("storage.io-gpu.upload.mibs", "上傳到 GPU", "MiB/s", true, "系統記憶體 → D3D11 structured buffer", [mibs(gpuResult.UploadSeconds)], []),
            new("storage.io-gpu.gpu-pass.mibs", "GPU compute pass", "MiB/s", true, "逐元素 FNV-1a（cs_5_0）", [mibs(gpuResult.PassSeconds)], []),
            new("storage.io-gpu.readback.mibs", "GPU 回讀", "MiB/s", true, "staging buffer Map 讀回", [mibs(gpuResult.ReadbackSeconds)], []),
            new("storage.io-gpu.end-to-end.mibs", "端到端管線", "MiB/s", true, "總位元組／各階段合計牆鐘", [mibs(totalSeconds)], []),
        };

        return new DeepBenchTestResult(
            TestId,
            context.SessionId,
            context.Profile,
            started,
            DateTime.UtcNow,
            $"{workload.DataBytes / 1024.0 / 1024.0:0} MiB；{elementCount} 個 uint 元素",
            metrics,
            [
                mismatches == 0
                    ? "生命週期驗證通過：磁碟 → RAM → GPU → 回讀逐元素與 CPU 參考一致。"
                    : $"生命週期驗證失敗：{mismatches} 個元素與 CPU 參考不一致。",
                "本場唯一暫存檔以同一 handle 完成寫讀，結束後關閉並要求 OS DeleteOnClose；未另行驗證刪除結果。",
            ],
            Limitations,
            mismatches == 0 ? DeepBenchFailureKind.None : DeepBenchFailureKind.Unstable,
            mismatches == 0 ? null : $"{mismatches} 個元素與 CPU 參考不一致；管線資料完整性失敗。");
    }

    private static DeepBenchTestResult Unsupported(DeepBenchRunContext context, DateTime started, string reason) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "不支援", [], [],
            Limitations, DeepBenchFailureKind.Unsupported, reason);

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [],
            ["取消後不輸出部分管線補值。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(
        DeepBenchRunContext context,
        DateTime started,
        DeepBenchFailureKind kind,
        string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [],
            ["量測不完整時不推算缺失階段。"], kind, error);
}

/// <summary>D3D11 compute 引擎：上傳 → FNV-1a 逐元素雜湊 → staging 回讀；槽位沿用 FP32 服務已驗證集合。</summary>
public sealed class D3D11PipelineEngine : IGpuPipelineEngine
{
    public Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context)
        => D3D11Native.MeasureIoGpuPipelineAsync(context, CancellationToken.None);
}
