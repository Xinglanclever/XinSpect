using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// 選定邏輯處理器的 LLC 事件與每事件 64 位元組的等效流量；不是 iMC 實測 DRAM 流量。
/// </summary>
/// <remarks>
/// 使用選定邏輯處理器的架構效能事件
/// LONGEST_LAT_CACHE.MISS／REFERENCE（Intel SDM 列為架構事件，跨世代編碼相同），
/// 不是需要逐微架構查表的 OFFCORE_RESPONSE 遮罩——後者填錯會得到一個看起來合理的假數字。
/// <para>
/// 以讀取 512 MiB 工作集做量級檢查；符合範圍不代表精度校準、逐次 DRAM 傳輸或程序獨占計數。
/// </para>
/// <para>
/// 本功能未取用 iMC 逐通道計數器，不能列出 DRAM 通道分佈。
/// </para>
/// </remarks>
public sealed class DramTrafficService : ObservableObject
{
    private const uint MsrPerfEvtSel0 = 0x186;
    private const uint MsrPmc0 = 0xC1;
    private const uint MsrGlobalCtrl = 0x38F;

    /// <summary>LONGEST_LAT_CACHE：未命中（umask 0x41）與參照（umask 0x4F），皆為架構事件。</summary>
    private const uint EvLlc = 0x2E, UmMiss = 0x41, UmRef = 0x4F;

    /// <summary>工作集大小；不能保證所有平台都超出 LLC 或每次存取都落到 DRAM。</summary>
    private const long BufferBytes = 512L * 1024 * 1024;

    /// <summary>每條快取行只碰一個位元組就夠——要的是「搬了幾條行」，不是算術。</summary>
    private const int Stride = 64;

    private bool _busy;
    private CancellationTokenSource? _measurementCancellation;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanMeasure)); }
    }

    public bool CanMeasure => !_busy && HardwareOperationGate.CanRunAdvancedDiagnostics
        && HardwareOperationGate.CanWriteHardware;

    public void RefreshPolicy()
    {
        if (!HardwareOperationGate.IsHighRiskModeEnabled) _measurementCancellation?.Cancel();
        OnPropertyChanged(nameof(CanMeasure));
    }

    private const string NativeUnavailableReason = "DRAM PMU 原生量測未支援：CPU／SMT 計數器獨占與跨程序 PMU 所有權尚未驗證；未建立硬體橋接，未執行編程。";
    private string _status = NativeUnavailableReason;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _validation = "尚未驗證。";
    public string ValidationText { get => _validation; private set => SetProperty(ref _validation, value); }

    private Severity _severity = Severity.Neutral;
    public Severity Severity { get => _severity; private set => SetProperty(ref _severity, value); }

    private string _miss = "—";
    public string MissText { get => _miss; private set => SetProperty(ref _miss, value); }

    private string _reference = "—";
    public string ReferenceText { get => _reference; private set => SetProperty(ref _reference, value); }

    private string _hit = "—";
    public string HitText { get => _hit; private set => SetProperty(ref _hit, value); }

    private string _traffic = "—";
    public string TrafficText { get => _traffic; private set => SetProperty(ref _traffic, value); }

    /// <summary>逐通道為什麼沒有——固定文字，讓「量不到」跟「沒做」長得不一樣。</summary>
    public string ChannelNote =>
        "本功能未讀取 iMC 逐通道計數器；LLC 事件不等於 DRAM 實際傳輸。"
        + "計數涵蓋選定邏輯處理器上的使用者與核心執行，不能排除其他工作負載的影響。";

    public void Measure()
    {
        if (_busy) return;
        if (!HardwareOperationGate.CanRunAdvancedDiagnostics || !HardwareOperationGate.CanWriteHardware)
        {
            Status = !HardwareOperationGate.CanRunAdvancedDiagnostics
                ? "DRAM 流量量測已阻止：請先啟用第一級進階診斷。"
                : "DRAM PMU 能力檢查已阻止：請先啟用第二級高風險硬體操作；原生量測仍未支援。";
            return;
        }
        BeginMeasurement();
        var cancellation = new CancellationTokenSource();
        _measurementCancellation = cancellation;

        _ = Task.Run(() => Run(cancellation.Token)).ContinueWith(t =>
        {
            _measurementCancellation = null;
            cancellation.Dispose();
            CompleteMeasurement(t);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    internal void BeginMeasurement()
    {
        ApplyResult(Empty("正在檢查 DRAM PMU 量測能力；尚未執行硬體編程或負載。"));
        IsBusy = true;
    }

    internal void CompleteMeasurement(Task<Result> task)
    {
        try
        {
            if (task.IsCanceled)
                ApplyResult(Empty("DRAM PMU 能力檢查已取消；沒有取得計數。"));
            else if (task.IsFaulted)
            {
                var error = task.Exception!.GetBaseException();
                Diag.Swallow("DramTraffic.Measure", task.Exception!, "背景能力檢查失敗，清空讀數並解除忙碌");
                ApplyResult(Empty($"DRAM PMU 能力檢查失敗：{error.GetType().Name}：{error.Message}"));
            }
            else ApplyResult(task.GetAwaiter().GetResult());
        }
        finally { IsBusy = false; }
    }

    private void ApplyResult(Result result)
    {
        Status = result.Status;
        ValidationText = result.Validation;
        Severity = result.Severity;
        MissText = result.Miss;
        ReferenceText = result.Reference;
        HitText = result.Hit;
        TrafficText = result.Traffic;
    }

    internal readonly record struct Result(string Status, string Validation, Severity Severity,
                                          string Miss, string Reference, string Hit, string Traffic);

    private static Result Empty(string status) =>
        new(status, "沒有量到計數，因此沒有可驗證的東西。", Severity.Neutral, "—", "—", "—", "—");

    private static Result Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Empty("DRAM PMU 能力檢查已取消；沒有取得計數。");
        if (!HardwareOperationGate.IsHighRiskModeEnabled)
            return Empty("DRAM PMU 能力檢查未授權，未執行硬體編程或負載。");
        return Empty(NativeUnavailableReason);
    }

    /// <summary>寫一遍讓分頁真的落地（否則讀到的是零頁，不會去記憶體）。<b>不在計數視窗內</b>。</summary>
    private static void Fill(byte[] buf, CancellationToken cancellationToken)
    {
        for (long i = 0; i < buf.LongLength; i += Stride)
        {
            if ((i & 0xFFFF) == 0) CheckPolicy(cancellationToken);
            buf[i] = 1;
        }
    }

    /// <summary>把緩衝區讀滿一次；回傳實際碰過的位元組數。這一段才是被計數的負載。</summary>
    private static long ReadAll(byte[] buf, CancellationToken cancellationToken)
    {
        long sum = 0;
        for (long i = 0; i < buf.LongLength; i += Stride)
        {
            if ((i & 0xFFFF) == 0) CheckPolicy(cancellationToken);
            sum += buf[i];
        }
        return sum == 0 ? 0 : buf.LongLength;
    }

    private static void CheckPolicy(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HardwareOperationGate.IsHighRiskModeEnabled)
            throw new OperationCanceledException("High-risk hardware mode was disabled.");
    }

    internal readonly record struct CounterResult(ulong Misses, ulong References, double Seconds);

    internal static CounterResult MeasureCounters(IPmuMsrAccess msr, Func<long> workload,
        CancellationToken cancellationToken = default,
        Func<uint, (uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuidProbe = null)
    {
        CheckPolicy(cancellationToken);
        CheckPmuSupport(cpuidProbe ?? ReadCpuId);
        using var permit = HardwareOperationGate.TryBeginHighRiskOperation();
        if (permit is null) throw new OperationCanceledException("Hardware writes are not authorized.");
        uint[] registers = [MsrGlobalCtrl, MsrPerfEvtSel0, MsrPerfEvtSel0 + 1, MsrPmc0, MsrPmc0 + 1];
        ulong[] saved = registers.Select(index => msr.ReadMsr(index)
            ?? throw new InvalidOperationException($"Cannot preserve MSR 0x{index:X}.")).ToArray();
        if ((saved[0] & 3) != 0 || ((saved[1] | saved[2]) & (1UL << 22)) != 0
            || saved[3] != 0 || saved[4] != 0)
            throw new InvalidOperationException("PMC0/PMC1 are active or contain unowned state.");

        void Configure(uint index, ulong value)
        {
            CheckPolicy(cancellationToken);
            if (!msr.WriteMsr(index, value))
                throw new InvalidOperationException($"Cannot program MSR 0x{index:X}.");
        }

        CounterResult result = default;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            Configure(MsrGlobalCtrl, saved[0] & ~3UL);
            Configure(MsrPerfEvtSel0, Sel(EvLlc, UmMiss));
            Configure(MsrPerfEvtSel0 + 1, Sel(EvLlc, UmRef));
            Configure(MsrPmc0, 0);
            Configure(MsrPmc0 + 1, 0);
            Configure(MsrGlobalCtrl, saved[0] | 3);
            CheckPolicy(cancellationToken);
            long started = Stopwatch.GetTimestamp();
            long touched = workload();
            long ended = Stopwatch.GetTimestamp();
            CheckPolicy(cancellationToken);
            if (touched <= 0) throw new InvalidOperationException("The measurement workload did not run.");
            ulong misses = msr.ReadMsr(MsrPmc0) ?? throw new InvalidOperationException("PMC0 read failed.");
            ulong references = msr.ReadMsr(MsrPmc0 + 1) ?? throw new InvalidOperationException("PMC1 read failed.");
            result = new CounterResult(misses, references, Stopwatch.GetElapsedTime(started, ended).TotalSeconds);
        }
        catch (Exception ex) { primaryFailure = ex; }
        finally
        {
            // The permit survives revocation only so all original registers can be restored.
            // Retain every cleanup failure without replacing the setup/workload/cancellation error.
            void Restore(uint index, ulong value)
            {
                try
                {
                    if (!msr.WriteMsr(index, value))
                        cleanupFailures.Add(new InvalidOperationException($"PMU restoration write failed at 0x{index:X}."));
                }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            Restore(MsrGlobalCtrl, saved[0] & ~3UL);
            for (int i = 1; i < registers.Length; i++) Restore(registers[i], saved[i]);
            Restore(MsrGlobalCtrl, saved[0]);
            for (int i = 0; i < registers.Length; i++)
            {
                try
                {
                    if (msr.ReadMsr(registers[i]) != saved[i])
                        cleanupFailures.Add(new InvalidOperationException($"PMU restoration readback failed at 0x{registers[i]:X}."));
                }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
        }
        if (cleanupFailures.Count != 0)
        {
            if (primaryFailure is not null)
                throw new AggregateException("PMU measurement failed and register restoration could not be verified.",
                    new[] { primaryFailure }.Concat(cleanupFailures));
            throw new InvalidOperationException("PMU register restoration could not be verified.",
                new AggregateException(cleanupFailures));
        }
        if (primaryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        return result;
    }

    [SpecRef("Intel SDM Vol.2, CPUID leaf 0AH: EAX PMU version/count/width/event-vector length; EBX bits 3/4 indicate architectural LLC reference/miss events are unavailable. Vol.3, Architectural Performance Monitoring: IA32_PERF_GLOBAL_CTRL requires PMU version 2 or later.")]
    internal static void CheckPmuSupport(Func<uint, (uint Eax, uint Ebx, uint Ecx, uint Edx)> cpuid)
    {
        var root = cpuid(0);
        if (root.Ebx != 0x756E6547 || root.Edx != 0x49656E69 || root.Ecx != 0x6C65746E)
            throw new NotSupportedException("本量測使用 Intel 架構 PMU 事件；非 Intel 平台不適用。");
        if (root.Eax < 0xA)
            throw new NotSupportedException("處理器未提供 CPUID 0xA，無法確認 PMU 能力。");
        var leaf = cpuid(0xA);
        var capability = PmuDecoder.DecodeCapability(leaf.Eax, leaf.Edx);
        if (capability.Version < 2 || capability.GeneralCounters < 2
            || capability.GeneralWidthBits is < 32 or > 64)
            throw new NotSupportedException("PMU 版本、通用計數器數量或位寬不支援本量測。");
        if ((leaf.Eax >> 24) < 5 || (leaf.Ebx & ((1u << 3) | (1u << 4))) != 0)
            throw new NotSupportedException("處理器未確認支援架構 LLC 參照與未命中事件。");
    }

    private static (uint Eax, uint Ebx, uint Ecx, uint Edx) ReadCpuId(uint leaf)
    {
        if (!X86Base.IsSupported) return default;
        var value = X86Base.CpuId(unchecked((int)leaf), 0);
        return ((uint)value.Eax, (uint)value.Ebx, (uint)value.Ecx, (uint)value.Edx);
    }

    /// <summary>PERFEVTSEL：事件、umask、USR＋OS、啟用。</summary>
    private static ulong Sel(uint ev, uint um)
        => ev | (um << 8) | (1UL << 16) | (1UL << 17) | (1UL << 22);
}
