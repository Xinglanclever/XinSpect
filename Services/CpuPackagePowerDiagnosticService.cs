using System.Diagnostics;

namespace XinSpect;

internal interface ICpuPackagePowerDiagnosticReadLease : IDisposable
{
    CpuPackagePowerDiagnosticSource Source { get; }
    bool IsCurrent { get; }
    ulong? Read(uint index);
}

internal interface ICpuPackagePowerDiagnosticPin : IDisposable { bool Ok { get; } }

internal interface ICpuPackagePowerDiagnosticRuntime
{
    long Timestamp { get; }
    long Frequency { get; }
    DateTimeOffset UtcNow { get; }
    IReadOnlyList<IReadOnlyList<ProcessorRef>> ReadPackages();
    ICpuPackagePowerDiagnosticPin Pin(ProcessorRef processor);
    CpuPackagePowerDiagnosticPlatform IdentifyPinned(ProcessorRef processor, int packageCount);
    bool IsOnProcessor(ProcessorRef processor);
    void Wait(int milliseconds, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit, bounded, passive RAPL capture. Construction does no work. Capture uses
/// one ordinary background thread so its group affinity survives every read/wait.
/// No load test, backend initialization, retry, driver change, or trust promotion.
/// </summary>
public sealed class CpuPackagePowerDiagnosticService
{
    public const string Specification = "Intel PCM 8574a1fc1055bc39d3afc2d47ee9b322265df2d7: src/cpucounters.h:2129,2739-2763 (06_55H package-energy support); "
        + "src/types.h:510,512 (611H/606H); src/cpucounters.cpp:1782-1788,1817-1821 (ESU bits12:8, 2^-ESU J/count, 32-bit per-socket counter). "
        + "Intel SDM Vol.3B §16.10.3/Fig.16-37 (253669-087, repository historical reference; PDF not freshly retrieved). "
        + "Intel SDM Vol.2 CPUID leaf0BH extended topology; Microsoft PROCESSOR_RELATIONSHIP / GROUP_AFFINITY.";
    internal const int SampleCount = 8;
    internal const int SampleIntervalMilliseconds = 500;
    internal const double MaxReadSeconds = 0.250;
    internal const double MaxIntervalSeconds = 2;
    private readonly Func<bool> _allowed;
    private readonly Func<ICpuPackagePowerDiagnosticReadLease?> _acquire;
    private readonly ICpuPackagePowerDiagnosticRuntime _runtime;
    private int _running;

    public CpuPackagePowerDiagnosticService() : this(() => HardwareOperationGate.CanRunAdvancedDiagnostics,
        () => KernelBackendHost.TryAcquireExistingPowerReadLease(out _), new CpuPackagePowerDiagnosticRuntime()) { }

    internal CpuPackagePowerDiagnosticService(Func<bool> allowed,
        Func<ICpuPackagePowerDiagnosticReadLease?> acquire, ICpuPackagePowerDiagnosticRuntime runtime)
        => (_allowed, _acquire, _runtime) = (allowed, acquire, runtime);

    public Task<CpuPackagePowerDiagnosticResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("此診斷已有採樣正在執行；必須等待同步讀取確實返回。");
        try
        {
            // A dedicated read/wait thread, never a benchmark/load worker. Do not
            // pass cancellation to StartNew: the finally must always reset _running.
            return Task.Factory.StartNew(() =>
            {
                try { return Capture(cancellationToken); }
                finally { Volatile.Write(ref _running, 0); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { Volatile.Write(ref _running, 0); throw; }
    }

    private CpuPackagePowerDiagnosticResult Capture(CancellationToken token)
    {
        DateTimeOffset started = _runtime.UtcNow;
        var samples = new List<CpuPackagePowerDiagnosticSample>();
        CpuPackagePowerDiagnosticPlatform? platform = null;
        CpuPackagePowerDiagnosticSource? source = null;
        CpuPackagePowerDiagnosticResult Result(FactAvailability availability, string reason,
            bool cancelled = false, bool policy = false) => new(started, _runtime.UtcNow, availability,
                reason, cancelled, policy, _runtime.Frequency, platform, source, samples.ToArray());
        try
        {
            Check(token);
            if (_runtime.Frequency <= 0) return Result(FactAvailability.ReadError, "QPC 頻率無效；未讀 MSR。");
            var packages = _runtime.ReadPackages();
            Check(token);
            if (packages.Count != 1 || packages[0].Count == 0)
                return Result(FactAvailability.NotSupported, "無法確認 Windows 單一實體封裝拓撲；本版不對多封裝或未知拓撲取樣。");
            ProcessorRef processor = packages[0][0];
            using var pin = _runtime.Pin(processor);
            if (!pin.Ok) return Result(FactAvailability.ReadError, "無法釘選指定 group／LP；未讀 MSR。");
            Check(token);
            platform = _runtime.IdentifyPinned(processor, packages.Count);
            if (!Supported(platform))
                return Result(FactAvailability.NotSupported, "本診斷僅收錄裸機 Intel family 6/model 0x55，且須取得 CPUID package 身份；未知平台未讀 MSR。");
            using var lease = _acquire();
            if (lease is null) return Result(FactAvailability.InsufficientPrivilege,
                "沒有已開啟且獲准的共用 MSR 通道；本診斷未初始化後端、未載入／重裝驅動。既有 LHM 通道不代表共用路由已開啟。");
            source = lease.Source;
            CpuPackagePowerDiagnosticSample? previous = null;
            for (int index = 0; index < SampleCount; index++)
            {
                Check(token, lease, processor);
                var units = Read(lease, 0x606, token, processor);
                var sample = new CpuPackagePowerDiagnosticSample(index, _runtime.UtcNow, units, null, null, "基準點；尚無兩個可比較端點。");
                samples.Add(sample); // Preserve completed unit evidence if the next call aborts.
                CpuPackagePowerDiagnosticRead? energy = null;
                if (units.Availability == FactAvailability.Present)
                    energy = Read(lease, 0x611, token, processor);
                sample = sample with { Energy = energy, CapturedAtUtc = _runtime.UtcNow };
                samples[index] = sample;
                if (units.Availability != FactAvailability.Present || energy?.Availability != FactAvailability.Present)
                {
                    samples[index] = sample with { State = "讀取失敗；舊值與差分基準均不沿用。" };
                    Check(token, lease, processor);
                    return Result(FactAvailability.ReadError, units.Availability != FactAvailability.Present ? units.Reason : energy?.Reason ?? "能量未讀取。");
                }
                if (!TryEnergyUnit(units.Raw!.Value, out double unitJ) || (energy.Raw!.Value >> 32) != 0)
                {
                    samples[index] = sample with { State = "單位或能量保留位無法確認；不換算瓦數。" };
                    return Result(FactAvailability.Unknown, "MSR 單位全零／保留位異常，或能量高 32 位非零；保留 raw，不猜測換算。");
                }
                if (previous is not null)
                    sample = sample with { Interval = Derive(previous, sample, unitJ, _runtime.Frequency), State = "被動計數差分；未驗證估值。" };
                samples[index] = sample;
                previous = sample;
                if (index + 1 < SampleCount)
                {
                    // Recheck at most every 50 ms while waiting; native synchronous
                    // I/O cannot be forcibly cancelled and keeps its lease until return.
                    for (int remaining = SampleIntervalMilliseconds; remaining > 0; remaining -= 50)
                    {
                        Check(token, lease, processor);
                        _runtime.Wait(Math.Min(50, remaining), token);
                        Check(token, lease, processor);
                    }
                }
            }
            Check(token, lease, processor);
            return Result(FactAvailability.Unknown, "採樣完成：保留 8 組原始值與 QPC 差分，所有瓦數仍為未驗證估值；未啟動負載或變更信任。");
        }
        catch (OperationCanceledException)
        {
            bool policy = !_allowed();
            return Result(policy ? FactAvailability.InsufficientPrivilege : FactAvailability.Unknown,
                policy ? "第一級進階診斷已關閉；停止後續讀取。" : token.IsCancellationRequested
                    ? "使用者已取消；目前同步讀取確實返回後停止，未沿用舊值。" : "既有來源／通道政策已變更；停止後續讀取。",
                token.IsCancellationRequested, policy);
        }
        catch (Exception ex)
        {
            return Result(FactAvailability.ReadError, "被動診斷失敗；未重試或切換通道：" + ex.Message);
        }
    }

    private void Check(CancellationToken token, ICpuPackagePowerDiagnosticReadLease? lease = null, ProcessorRef? processor = null)
    {
        token.ThrowIfCancellationRequested();
        if (!_allowed()) throw new OperationCanceledException("第一級進階診斷已關閉。");
        if (lease is not null && !lease.IsCurrent) throw new OperationCanceledException("既有通道已變更。");
        if (processor is ProcessorRef target && !_runtime.IsOnProcessor(target))
            throw new InvalidOperationException("目前 group／LP 與釘選目標不符；停止取樣。");
    }

    private CpuPackagePowerDiagnosticRead Read(ICpuPackagePowerDiagnosticReadLease lease, uint msr,
        CancellationToken token, ProcessorRef processor)
    {
        Check(token, lease, processor);
        long before = _runtime.Timestamp;
        ulong? raw = null;
        string? failure = null;
        try { raw = lease.Read(msr); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { failure = ex.Message; }
        long after = _runtime.Timestamp;
        // Retain the just-returned raw value as failed/unknown evidence even if
        // policy was revoked during I/O; never use it as a new power endpoint.
        try { Check(token, lease, processor); }
        catch (Exception ex) { failure = ex.Message; }
        bool validTime = after >= before && (after - before) / (double)_runtime.Frequency <= MaxReadSeconds;
        return new(msr, raw, before, after, raw is not null && failure is null && validTime
            ? FactAvailability.Present : FactAvailability.ReadError,
            !validTime ? "同步讀取的 QPC 區間無效或超過 250 ms 預算；raw 僅供診斷，不換算。"
                : failure ?? (raw is null ? "既有通道讀取失敗；無 raw，未以零代替。" : "同步讀取成功；不是計數器精度認證。"));
    }

    [SpecRef("Intel PCM 8574a1fc1055bc39d3afc2d47ee9b322265df2d7 src/cpucounters.cpp:1782-1788: 606H bits12:8, 2^-ESU joules/count; existing CeilingDecoder unit-field layout.")]
    internal static bool TryEnergyUnit(ulong raw, out double joules)
    {
        joules = 0;
        if (raw == 0 || (raw & ~0x000F1F0FUL) != 0) return false;
        joules = Math.ScaleB(1.0, -(int)((raw >> 8) & 0x1F));
        return true;
    }

    [SpecRef("Intel PCM 8574a1fc1055bc39d3afc2d47ee9b322265df2d7 src/cpucounters.h:2129,2739-2763: SKX=family6/model85 has package energy metrics; only bare-metal single-package supported here.")]
    internal static bool Supported(CpuPackagePowerDiagnosticPlatform p) => p.Vendor == "GenuineIntel"
        && p.Family == 6 && p.Model == 0x55 && !p.Hypervisor && p.WindowsPackageCount == 1
        && p.CpuidPackageId is not null && p.Processor.Index is >= 0 and < 64;

    [SpecRef("Intel SDM Vol.3B §16.10.3/Fig.16-37: PKG_ENERGY_STATUS bits31:0 modulo2^32. QPC endpoints use read midpoints; precision is not accuracy.")]
    internal static CpuPackagePowerDiagnosticInterval Derive(CpuPackagePowerDiagnosticSample from,
        CpuPackagePowerDiagnosticSample to, double unitJ, long frequency)
    {
        var a = from.Energy!; var b = to.Energy!;
        uint counts = CeilingDecoder.Delta32(a.Raw!.Value, b.Raw!.Value);
        bool wrap = (uint)b.Raw.Value < (uint)a.Raw.Value;
        // Subtract before converting to double, avoiding precision loss in a long QPC uptime.
        double ticks = (b.BeforeQpc - a.BeforeQpc) + ((b.AfterQpc - b.BeforeQpc) - (a.AfterQpc - a.BeforeQpc)) / 2.0;
        double seconds = frequency > 0 ? ticks / frequency : 0;
        bool valid = double.IsFinite(seconds) && seconds >= 0.01 && seconds <= MaxIntervalSeconds
            && from.Units.Raw == to.Units.Raw && double.IsFinite(unitJ) && unitJ > 0;
        double? watts = valid && !wrap && counts > 0 ? counts * unitJ / seconds : null;
        return new(from.Index, counts, seconds, unitJ, watts,
            valid ? unitJ / seconds : null, wrap,
            !valid ? "時間窗／單位不連續；不換算瓦數，下筆重新建立基準。"
                : wrap ? "已保留模 2^32 增量；下降可能是回繞或重設，無法區分，因此不發布瓦數。"
                : counts == 0 ? "計數未前進；可能受更新週期／解析度限制，不宣稱功耗為零。"
                : "能量計未經校準；一個 count 的瓦數解析度不是誤差界線。無法只靠端點排除多次回繞或中途重設。");
    }
}
