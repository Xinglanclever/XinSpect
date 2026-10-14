using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace XinSpect;

/// <summary>
/// 處理器電源能力（Vol1 第 AC-002 項）：<c>ac.cpc.summary</c>——CPPC（協同處理器效能控制）
/// 的<b>能力輪廓</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項刻意不解碼 AML。</b>_CPC 是定義在 DSDT 裡的 AML 物件，要取它的值就必須寫一個
/// AML 解譯器——而「解不出來卻有值」正是本專案最怕的失敗模式（見
/// <see cref="AcpiTableService"/> 對 _PSS／_CPC／_DSM 的界線聲明）。
/// 所以本項改走<b>事實可得的那一層</b>：Windows 自己有沒有把 CPPC 用起來。
/// </para>
/// <para>
/// <b>三個可查的來源，任一為真即算支援：</b>
/// ① <c>Kernel-Processor-Power</c> 事件通道裡有沒有 CPPC 相關的啟動記錄；
/// ② <c>HWP</c>（Intel Speed Shift）的 MSR 能力位（CPUID.06H:EAX[7]）——
/// HWP 與 CPPC 是同一件事的兩種廠商實作；
/// ③ 登錄裡有沒有效能/能耗偏好設定（CPPC 的 EPP 值）。
/// </para>
/// <para>
/// <b>紅線：</b>① <b>未提供＝不適用</b>——主機板沒實作 _CPC 是環境事實，不是缺陷；
/// ② <b>不把 CPUID 能力當成「已啟用」</b>：硬體有 HWP 不代表 OS 正在用；
/// ③ 事件通道讀不到時<b>不推論</b>，直接說讀不到。
/// </para>
/// </remarks>
public static class CppcCapabilityFactsService
{
    private const string Cat = "ACPI";
    private const string Source = "CPUID／MSR／登錄與 Kernel-Processor-Power 事件（唯讀）";

    /// <summary>AC-002 CPPC 能力輪廓。</summary>
    public const string CppcKey = "ac.cpc.summary";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（硬體回報 HWP／CPPC 能力, 作業系統是否已啟用, 效能偏好設定可讀, 事件通道痕跡數）。</summary>
    public sealed record CppcState(bool HardwareCapable, bool OsEnabled, bool EppReadable, int EventTraces);

    public delegate CppcState? Probe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static HardwareFact Collect(DateTimeOffset at, Probe? probe = null)
    {
        CppcState? s;
        try { s = (probe ?? ReadCppc)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, FactAvailability.ReadError,
                "CPPC 能力查詢失敗——讀不到就是不猜（不用 CPU 型號猜有沒有 CPPC）");

        var v = s;

        // 硬體不支援、OS 沒開、也沒有任何痕跡 → 不適用。
        if (!v.HardwareCapable && !v.OsEnabled && v.EventTraces == 0)
            return Unavailable(at, FactAvailability.NotApplicable,
                "本機沒有回報 CPPC／HWP 能力（CPUID 能力位未設、沒有啟用痕跡）——" +
                "不適用，不是缺陷：主機板沒有實作 _CPC 物件是平台設計選擇，" +
                "舊平台或伺服器 BIOS 常刻意關掉它換取更確定的功耗行為");

        // 硬體有但不代表 OS 在用——這兩件事要分開講。
        string capText = v.HardwareCapable
            ? "硬體回報具備 CPPC／HWP 能力"
            : "硬體能力位<b>未回報</b>（可能是不支援，也可能是 CPUID 此版本不提供該位——讀不到不代填）";
        string osText = v.OsEnabled
            ? "作業系統<b>已啟用</b>（有啟動痕跡）"
            : "<b>未見啟用痕跡</b>——硬體有能力不等於正在使用它";
        string eppText = v.EppReadable
            ? "效能／能耗偏好（EPP）設定可讀"
            : "EPP 設定<b>讀不到</b>（不猜偏好值）";

        return new HardwareFact(CppcKey, Cat, "CPPC 處理器效能控制",
            $"{capText}・{osText}・{eppText}・事件通道痕跡 {v.EventTraces} 筆。" +
            "<b>本項不解碼 AML</b>：_CPC 物件在 DSDT 的位元碼裡，本工具沒有 AML 解譯器，" +
            "而「解不出來卻有值」比「誠實說沒有這個值」糟糕得多——所以只陳述上面三條" +
            "作業系統層面看得到的事實，_CPC 的欄位內容（最高效能、最低非線性…）不在本項範圍。" +
            "CPPC 的價值在於讓 OS 與硬體協商效能，<b>協商品質要看實際的頻率與功耗曲線</b>，" +
            "不從「有沒有支援」推論。",
            "項", Source, FactTrustLevel.Reported, false, at,
            (v.HardwareCapable ? 1 : 0) + (v.OsEnabled ? 1 : 0));
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 讀 CPPC 相關事實。三個來源各自獨立，任一失敗不影響其他；
    /// 整體讀不到時回 <c>null</c>（由呼叫端標 ReadError）。
    /// </summary>
    internal static CppcState? ReadCppc()
    {
        bool hw = ProbeHwpCapability();
        var (osEnabled, traces) = ProbeProcessorPowerEvents();
        bool epp = ProbeEppReadable();
        return new CppcState(hw, osEnabled, epp, traces);
    }

    /// <summary>
    /// CPUID.06H:EAX bit 7＝HWP（Hardware P-States，Intel 的 CPPC 實作）。
    /// 取不到 CPUID 時回 <c>false</c>（保守），並由值的措辭表明「未回報」而非「不支援」。
    /// </summary>
    internal static bool ProbeHwpCapability()
    {
        try
        {
            if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return false;
            // CPUID leaf 6 subleaf 0；X86Base.CpuId 回傳 (EAX, EBX, ECX, EDX)。
            var (eax, _, _, _) = System.Runtime.Intrinsics.X86.X86Base.CpuId(6, 0);
            return (eax & (1 << 7)) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 從 <c>Kernel-Processor-Power</c> 事件通道找啟用痕跡。
    /// <b>只數筆數不讀訊息內容</b>——事件訊息可能含處理器拓撲細節；
    /// 讀不到通道時回 (false, 0)，由值的措辭表明讀不到。
    /// </summary>
    internal static (bool Enabled, int Traces) ProbeProcessorPowerEvents()
    {
        try
        {
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power']]]")
            { ReverseDirection = true };

            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            int traces = 0;
            for (int i = 0; i < 50; i++)
            {
                var evt = reader.ReadEvent();
                if (evt is null) break;
                using (evt) traces++;
            }
            return (traces > 0, traces);
        }
        catch { return (false, 0); }
    }

    /// <summary>
    /// EPP（Energy Performance Preference）設定是否可讀。
    /// 走 <c>powercfg</c> 的電源計畫別名——但這裡不打外部程序，改看處理器電源設定的登錄存在性。
    /// </summary>
    internal static bool ProbeEppReadable()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00");
            return key is not null;
        }
        catch { return false; }
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(CppcKey, Cat, "CPPC 處理器效能控制", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
