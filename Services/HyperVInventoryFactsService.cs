using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// Hyper-V 虛擬機配置盤點（Vol2 第 DV-006 項）：<c>dv.vm.count</c>、<c>dv.vm.inventory</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b><c>dv.nested</c>（DV-002）問的是<b>這台機器能不能當來賓跑巢狀虛擬化</b>，
/// <c>sv.hyperv.switches</c>（SV-009）問的是<b>虛擬交換器</b>——兩者都不回答
/// <b>「現在有幾台 VM、它們在跑嗎」</b>。本項補的就是那一層。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>零 VM 是誠實空清單</b>——Hyper-V 裝好但還沒建 VM 是 0 台，
/// 與「沒裝 Hyper-V」（<see cref="FactAvailability.NotApplicable"/>）是兩件不同的事；
/// ② <b>不列 VM 名稱</b>：VM 名稱常等於專案名、客戶名或測試環境代號——
/// 「有幾台在跑」是容量事實，「哪幾台」是人名與專案名的清單；
/// ③ 只陳述<b>狀態分布與資源配置合計</b>，不判斷配置合理不合理（超賣與超額配置是刻意的管理決策）。
/// </para>
/// </remarks>
public static class HyperVInventoryFactsService
{
    private const string Cat = "虛擬化";
    private const string Source = @"root\virtualization\v2（Msvm_ComputerSystem／Msvm_SummaryInformation，唯讀）";

    /// <summary>DV-006 虛擬機數量。</summary>
    public const string CountKey = "dv.vm.count";
    /// <summary>DV-006 虛擬機資源配置。</summary>
    public const string InventoryKey = "dv.vm.inventory";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>一台 VM：是否在跑、配置的記憶體位元組、配置的虛擬處理器數。<b>不含名稱。</b></summary>
    public sealed record VmRow(bool Running, long MemoryBytes, int VirtualProcessors);

    public delegate IReadOnlyList<VmRow>? VmsProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, VmsProbe? probe = null)
    {
        IReadOnlyList<VmRow>? rows;
        try { rows = (probe ?? ReadVms)(); }
        catch { rows = null; }

        if (rows is null)
            return
            [
                Unavailable(at, CountKey, "虛擬機數量", FactAvailability.NotApplicable,
                    "Hyper-V 角色未安裝（root\\virtualization\\v2 命名空間不存在）——不適用，不是缺陷"),
                Unavailable(at, InventoryKey, "虛擬機資源配置", FactAvailability.NotApplicable,
                    "Hyper-V 角色未安裝——不適用（沒裝 Hyper-V 與裝了但零 VM 是兩件事）"),
            ];

        var v = rows;
        int running = v.Count(r => r.Running);
        long mem = v.Sum(r => r.MemoryBytes);
        int vcpu = v.Sum(r => r.VirtualProcessors);

        // 零 VM 是誠實空清單：Hyper-V 裝好但還沒建 VM。
        string countValue = v.Count == 0
            ? "Hyper-V 已安裝，但目前沒有任何虛擬機（尚未建立）——零台是值不是缺席。"
            : $"虛擬機 {v.Count} 台，其中執行中 {running} 台、已停止 {v.Count - running} 台。";

        return
        [
            new HardwareFact(CountKey, Cat, "虛擬機數量", countValue +
                "<b>不列虛擬機名稱</b>：名稱常等於專案名、客戶名或測試環境代號——" +
                "「有幾台在跑」是容量事實，「哪幾台」是人名與專案名的清單。" +
                "「已停止」不代表有問題：範本機、待命機、季節性工作負載本來就大部分時間是停的。",
                "台", Source, FactTrustLevel.Reported, false, at, v.Count),

            new HardwareFact(InventoryKey, Cat, "虛擬機資源配置",
                v.Count == 0
                    ? "沒有虛擬機，因此沒有配置可彙總（配置記憶體 0、虛擬處理器 0）——0 是值。"
                    : $"配置記憶體合計 {mem / 1073741824.0:N2} GiB・虛擬處理器合計 {vcpu} 個" +
                      $"（平均每台 {(mem / 1073741824.0 / v.Count):N2} GiB／{(double)vcpu / v.Count:N1} 個 vCPU）。" +
                      "<b>這是配置值不是使用量</b>：配置 32 GiB 的 VM 可能只用到 2 GiB，" +
                      "反過來說配置總和超過實體記憶體也是常態（超額配置是刻意的管理決策）。" +
                      "本項不判斷配置合理不合理，也不讀任何 VM 內部資料。",
                "GiB", Source, FactTrustLevel.Reported, false, at, mem / 1073741824.0),
        ];
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// WMI 通路：虛擬機清單。<b>只取執行狀態與配置量</b>——查詢刻意不含 <c>ElementName</c>
    /// （紅線②在程式碼上的落點）；命名空間不存在＝回 <c>null</c>（不適用，不是 0 台）。
    /// </summary>
    internal static IReadOnlyList<VmRow>? ReadVms()
    {
        ManagementScope scope;
        try
        {
            scope = new ManagementScope(@"\\.\root\virtualization\v2");
            scope.Connect();
        }
        catch { return null; }   // 沒有這個命名空間 → Hyper-V 不在

        var list = new List<VmRow>();
        // Msvm_ComputerSystem 的 Caption／Description 可用來排除宿主本身（宿主不是 VM）。
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT EnabledState, Caption FROM Msvm_ComputerSystem"));
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                // 宿主本身以 Caption＝"Virtual Machine" 區分；其餘條目才是 VM。
                string caption = o["Caption"] as string ?? "";
                if (!caption.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase)) continue;

                bool running = o["EnabledState"] is { } es && Convert.ToInt32(es) == 2;   // 2＝Enabled
                long mem = 0;
                int procs = 0;
                try
                {
                    using var inParams = o.GetMethodParameters("GetSummaryInformation");
                    inParams["RequestedInformation"] = new uint[] { 100, 101 };   // MemoryUsage／ProcessorCount
                    using var outParams = o.InvokeMethod("GetSummaryInformation", inParams, null);
                    if (outParams?["SummaryInformation"] is ManagementBaseObject[] infos && infos.Length > 0)
                        using (var info = infos[0])
                        {
                            if (info["MemoryUsage"] is { } m) mem = Convert.ToInt64(m);
                            if (info["ProcessorCount"] is { } p) procs = Convert.ToInt32(p);
                        }
                }
                catch
                {
                    // 單台 VM 的摘要讀不到不影響其他台；該台以 0 配置計入並由合計看得出來。
                }
                list.Add(new VmRow(running, mem, procs));
            }
        return list;
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
