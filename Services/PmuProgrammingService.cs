using System.Diagnostics;

namespace XinSpect;

/// <summary>PMU 注入交易的 MSR 讀寫通路；原生 adapter 在平台驗證完成前不可用。</summary>
public interface IPmuMsrAccess
{
    bool Available { get; }
    string? UnavailableReason { get; }
    ulong? ReadMsr(uint index);
    bool WriteMsr(uint index, ulong value);
}

/// <summary>一輪編程驗證的結果。三個旗標全 true 才算該輪通過；CounterActive=false 時數值欄可能是未取得的 legacy 佔位，不代表量到零。</summary>
public sealed record PmuRoundResult(bool EnableReadbackOk, bool CounterActive, bool CleanupOk, ulong CounterStart, ulong CounterEnd);

/// <summary>
/// WP27 PMU 編程驗證（**多輪測試・不保證可用**）：沙箱方案 S2＋S3＋S4 的前哨實作。
/// <list type="bullet">
/// <item>**最小寫入**：只動 IA32_FIXED_CTR_CTRL（0x38D）的固定計數器 0 使能位（bit0 OS＋bit1 USR），
///       以 **OR** 併入原值（不覆寫別人已啟用的位元）；<b>絕不碰 PMI 位元</b>——不使能中斷就沒有 PMI 風暴。</item>
/// <item>每輪：啟用→讀回一致→跑工作量→讀固定計數器 0（0x309）確認有動→還原原值→讀回確認。</item>
/// <item>注入交易會先保存原值，在每輪尾端嘗試還原並核對；恢復未確認時停止後續輪次。</item>
/// <item>**同意閘門**：userConsent 非 true 丟例外。</item>
/// <item>**誠實標註**：<see cref="FormatNotice"/>——多輪測試進行中、不保證可用、非正式出貨。</item>
/// </list>
/// </summary>
public static class PmuProgrammingService
{
    public const uint MsrFixedCtrCtrl = 0x38D;
    public const uint MsrFixedCounter0 = 0x309;
    private const ulong EnableBits0 = 0x3; // EN0_OS | EN0_USR——不含任何 PMI 位元

    public const string FormatNotice =
        "【多輪測試・不保證可用】PMU 注入交易涵蓋 S2 啟用讀回／S3 計數活動／S4 還原確認；" +
        "原生編程未支援：CPU／SMT 計數器所有權、核心釘選與寫回能力尚未驗證。" +
        "注入交易只驗流程，每輪嘗試還原並核對，不能保證實機恢復；" +
        "本驗證尚未對照 wpr 等參考工具（S5 未跑），結果不保證可用、不作為正式功能宣稱。";

    public static IReadOnlyList<PmuRoundResult> RunConsentedVerification(bool userConsent, IPmuMsrAccess msr,
        int rounds = 3, Action? workload = null, Func<(uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuidProbe = null)
    {
        if (!userConsent)
            throw new InvalidOperationException("PMU 編程驗證需要明確同意才會執行。" + FormatNotice);
        if (!HardwareOperationGate.IsHighRiskModeEnabled)
            throw new InvalidOperationException("PMU 編程驗證需要啟用第二級高風險硬體操作。" + FormatNotice);
        using var writePermit = HardwareOperationGate.TryBeginHighRiskOperation();
        if (writePermit is null)
            throw new InvalidOperationException("PMU 編程驗證需要啟用第二級高風險硬體操作。" + FormatNotice);
        workload ??= SpinWorkload;

        var results = new List<PmuRoundResult>();
        for (int i = 0; i < rounds; i++)
        {
            if (!HardwareOperationGate.IsHighRiskModeEnabled) break;
            var result = RunRound(msr, workload, cpuidProbe);
            results.Add(result);
            if (!result.CleanupOk) break;
        }
        return results;
    }

    private static PmuRoundResult RunRound(IPmuMsrAccess msr, Action workload, Func<(uint, uint, uint, uint)>? cpuidProbe)
    {
        if (!HardwareOperationGate.IsHighRiskModeEnabled || !msr.Available || !PmuPresent(cpuidProbe))
            return new PmuRoundResult(false, false, false, 0, 0);

        ulong? original = msr.ReadMsr(MsrFixedCtrCtrl);
        if (original is not { } orig)
            return new PmuRoundResult(false, false, false, 0, 0);

        bool setupAttempted = false;
        bool cleanupOk = false;
        PmuRoundResult result = Fail();
        Exception? executionFailure = null, cleanupFailure = null;
        try { result = Execute(); }
        catch (Exception ex) { executionFailure = ex; }
        finally
        {
            // The operation permit keeps restoration possible after revocation;
            // it never authorizes a new round, setup, or workload.
            if (setupAttempted) cleanupOk = Cleanup(msr, orig, out cleanupFailure);
        }
        if (executionFailure is not null)
        {
            if (setupAttempted && !cleanupOk)
                throw new AggregateException("PMU execution failed and restoration could not be verified.",
                    executionFailure, cleanupFailure!);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executionFailure).Throw();
        }
        return result with { CleanupOk = cleanupOk };

        PmuRoundResult Execute()
        {
            if (!HardwareOperationGate.IsHighRiskModeEnabled) return Fail();
            // S2：最小啟用（OR 併入原值）＋讀回一致
            setupAttempted = true;
            if (!msr.WriteMsr(MsrFixedCtrCtrl, orig | EnableBits0)) return Fail();
            ulong? readback = msr.ReadMsr(MsrFixedCtrCtrl);
            if (readback != (orig | EnableBits0)) return Fail();

            // S3：計數器活動確認（不宣稱精確度——delta>0 且合理範圍內即「有在動」）
            ulong? startRead = msr.ReadMsr(MsrFixedCounter0);
            if (startRead is not { } start) return new(true, false, false, 0, 0);
            if (!HardwareOperationGate.IsHighRiskModeEnabled)
                return new(true, false, false, start, start);
            workload();
            ulong? endRead = msr.ReadMsr(MsrFixedCounter0);
            if (endRead is not { } end) return new(true, false, false, start, 0);
            ulong delta = end - start;
            bool active = delta is > 0 and < 0x4000_0000;

            return new PmuRoundResult(true, active, false, start, end);
        }
    }

    private static bool Cleanup(IPmuMsrAccess msr, ulong orig, out Exception? failure)
    {
        bool written = false, restored = false;
        var errors = new List<Exception>();
        // Observe both steps once and retain provider failures alongside the original failure.
        try { written = msr.WriteMsr(MsrFixedCtrCtrl, orig); }
        catch (Exception ex) { errors.Add(ex); }
        try { restored = msr.ReadMsr(MsrFixedCtrCtrl) == orig; }
        catch (Exception ex) { errors.Add(ex); }
        failure = written && restored ? null : new AggregateException(
            $"PMU restoration could not be verified (write accepted: {written}; readback matched: {restored}).", errors);
        return written && restored;
    }

    private static PmuRoundResult Fail() => new(false, false, false, 0, 0);

    private static bool PmuPresent(Func<(uint, uint, uint, uint)>? cpuidProbe)
    {
        var raw = (cpuidProbe ?? ReadCpId0xA)();
        var capability = PmuDecoder.DecodeCapability(raw.Item1, raw.Item4);
        return capability.Version >= 2 && capability.FixedCounters >= 1
            && capability.FixedWidthBits is > 0 and <= 64;
    }

    private static (uint, uint, uint, uint) ReadCpId0xA()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return (0, 0, 0, 0);
        var r = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0xA), 0);
        return (unchecked((uint)r.Eax), unchecked((uint)r.Ebx), unchecked((uint)r.Ecx), unchecked((uint)r.Edx));
    }

    private static void SpinWorkload()
    {
        // 已知大小的迴圈——固定計數器 0（INST_RETIRED）應看到顯著增量
        long acc = 0;
        for (int i = 0; i < 1_000_000; i++) acc += i;
        if (acc == -1) Debugger.Break(); // 永不成立——防止整段被 JIT 消除
    }
}

/// <summary>原生編程暫停；在平台所有權、釘選及寫回驗證完成前不建立橋接。</summary>
public sealed class WinRing0PmuMsrAccess : IPmuMsrAccess
{
    public bool Available => false;
    public string? UnavailableReason => "PMU 原生編程未支援：CPU／SMT 計數器所有權、核心釘選與寫回能力尚未驗證；未建立硬體橋接，未執行編程。";
    public ulong? ReadMsr(uint index) => null;
    public bool WriteMsr(uint index, ulong value) => false;
}
