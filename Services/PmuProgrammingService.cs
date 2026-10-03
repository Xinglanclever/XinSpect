using System.Diagnostics;

namespace XinSpect;

/// <summary>PMU MSR 讀寫通路（編程驗證用）。生產實作走 WinRing0 的 WriteMsrPair。</summary>
public interface IPmuMsrAccess
{
    bool Available { get; }
    string? UnavailableReason { get; }
    ulong? ReadMsr(uint index);
    bool WriteMsr(uint index, ulong value);
}

/// <summary>一輪編程驗證的結果。三個旗標全 true 才算該輪通過。</summary>
public sealed record PmuRoundResult(bool EnableReadbackOk, bool CounterActive, bool CleanupOk, ulong CounterStart, ulong CounterEnd);

/// <summary>
/// WP27 PMU 編程驗證（**多輪測試・不保證可用**）：沙箱方案 S2＋S3＋S4 的前哨實作。
/// <list type="bullet">
/// <item>**最小寫入**：只動 IA32_FIXED_CTR_CTRL（0x38D）的固定計數器 0 使能位（bit0 OS＋bit1 USR），
///       以 **OR** 併入原值（不覆寫別人已啟用的位元）；<b>絕不碰 PMI 位元</b>——不使能中斷就沒有 PMI 風暴。</item>
/// <item>每輪：啟用→讀回一致→跑工作量→讀固定計數器 0（0x309）確認有動→還原原值→讀回確認。</item>
/// <item>**還原保證**：原值先讀存，還原在每輪尾端執行；即使中途失敗也以最後已知原值還原。</item>
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
        "【多輪測試・不保證可用】PMU 編程驗證正在分輪進行（沙箱方案 S2 啟用讀回／S3 計數活動／S4 還原確認）， " +
        "寫入範圍僅限固定計數器 0 使能位、絕不碰 PMI 位元，每輪結束還原原值——" +
        "本驗證尚未對照 wpr 等參考工具（S5 未跑），結果不保證可用、不作為正式功能宣稱。";

    public static IReadOnlyList<PmuRoundResult> RunConsentedVerification(bool userConsent, IPmuMsrAccess msr,
        int rounds = 3, Action? workload = null, Func<(uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuidProbe = null)
    {
        if (!userConsent)
            throw new InvalidOperationException("PMU 編程驗證需要明確同意才會執行。" + FormatNotice);
        workload ??= SpinWorkload;

        var results = new List<PmuRoundResult>();
        for (int i = 0; i < rounds; i++)
            results.Add(RunRound(msr, workload, cpuidProbe));
        return results;
    }

    private static PmuRoundResult RunRound(IPmuMsrAccess msr, Action workload, Func<(uint, uint, uint, uint)>? cpuidProbe)
    {
        if (!msr.Available || !PmuPresent(cpuidProbe))
            return new PmuRoundResult(false, false, false, 0, 0);

        ulong? original = msr.ReadMsr(MsrFixedCtrCtrl);
        if (original is not { } orig)
            return new PmuRoundResult(false, false, false, 0, 0);

        try
        {
            // S2：最小啟用（OR 併入原值）＋讀回一致
            if (!msr.WriteMsr(MsrFixedCtrCtrl, orig | EnableBits0)) return Fail(orig);
            ulong? readback = msr.ReadMsr(MsrFixedCtrCtrl);
            if (readback != (orig | EnableBits0)) return Fail(orig);

            // S3：計數器活動確認（不宣稱精確度——delta>0 且合理範圍內即「有在動」）
            ulong start = msr.ReadMsr(MsrFixedCounter0) ?? 0;
            workload();
            ulong end = msr.ReadMsr(MsrFixedCounter0) ?? 0;
            ulong delta = end - start;
            bool active = delta is > 0 and < 0x4000_0000;

            return new PmuRoundResult(true, active, Cleanup(msr, orig), start, end);
        }
        finally
        {
            msr.WriteMsr(MsrFixedCtrCtrl, orig); // 還原保證：任何路徑都把 0x38D 還成原值
        }
    }

    private static bool Cleanup(IPmuMsrAccess msr, ulong orig)
    {
        if (!msr.WriteMsr(MsrFixedCtrCtrl, orig)) return false;
        return msr.ReadMsr(MsrFixedCtrCtrl) == orig;
    }

    private static PmuRoundResult Fail(ulong _) => new(false, false, false, 0, 0);

    private static bool PmuPresent(Func<(uint, uint, uint, uint)>? cpuidProbe)
    {
        var raw = (cpuidProbe ?? ReadCpId0xA)();
        return PmuDecoder.DecodeCapability(raw.Item1, raw.Item4).Version > 0;
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

/// <summary>生產通路：WinRing0 的 MSR 讀寫（bridge 自帶 WriteMsrPair）。</summary>
public sealed class WinRing0PmuMsrAccess : IPmuMsrAccess
{
    private readonly WinRing0Bridge _bridge = WinRing0Bridge.Create();
    public bool Available => _bridge.Available;
    public string? UnavailableReason => Available ? null : (_bridge.Error is { Length: > 0 } e ? e : "缺 ring0：WinRing0 未就緒");

    public ulong? ReadMsr(uint index)
    {
        try { return _bridge.ReadMsrPair64(index); }
        catch { return null; }
    }

    public bool WriteMsr(uint index, ulong value)
    {
        try { return _bridge.WriteMsrPair(index, unchecked((uint)(value & 0xFFFFFFFF)), unchecked((uint)(value >> 32))); }
        catch { return false; }
    }
}
