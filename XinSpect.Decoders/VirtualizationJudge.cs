using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>虛擬化平台的可觀測狀態（每一項都是「量到的」，不是推論的）。</summary>
/// <param name="HypervisorLoaded">CPUID 回報虛擬層存在＝hypervisor 真的載入了。</param>
/// <param name="HypervisorVendor">虛擬層簽章（僅在載入時有意義），未載入傳空字串。</param>
/// <param name="PlatformInstalled">Hyper-V 平台元件（Microsoft-Hyper-V）已安裝。</param>
/// <param name="VmmsRunning">Hyper-V 虛擬機器管理服務（vmms）執行中。</param>
/// <param name="VmCount">已定義的虛擬機器數；讀不到為 -1。</param>
/// <param name="VirtualSwitchCount">虛擬交換器數；讀不到為 -1。</param>
/// <param name="HnsRunning">主機網路服務（hns）執行中。</param>
/// <param name="ContainersFeature">容器選用功能已啟用。</param>
/// <param name="WslFeature">WSL 選用功能已啟用。</param>
/// <param name="VbsEnabled">VBS／記憶體完整性已啟用（0＝關、1＝啟用但未執行、2＝執行中）。</param>
/// <param name="TaskCount">排程工作中的總數；讀不到為 -1。</param>
/// <param name="TaskRunsUnderSystem">以 SYSTEM／最高權限執行且動作含程式路徑的工作數；讀不到為 -1。</param>
public readonly record struct VirtualizationState(
    bool HypervisorLoaded,
    string HypervisorVendor,
    bool PlatformInstalled,
    bool VmmsRunning,
    int VmCount,
    int VirtualSwitchCount,
    bool HnsRunning,
    bool ContainersFeature,
    bool WslFeature,
    int VbsEnabled,
    int TaskCount,
    int TaskRunsUnderSystem);

/// <summary>
/// 虛擬化平台狀態的判讀：把「元件裝了」「服務在跑」「hypervisor 真的載入了」三件事分開。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要分開：</b>Windows 上這三件事各自獨立，而一般工具只回報其中一件，
/// 於是「Hyper-V 已啟用」這句話同時涵蓋了四種完全不同的處境：
/// </para>
/// <list type="number">
/// <item>全關——乾淨的裸機，MSR／TSC／PMU 讀值原生。</item>
/// <item>元件裝了、服務在跑，但 <c>hypervisorlaunchtype</c> 是 Off——開機沒有載入虛擬層。
/// 此時<b>不會有任何 VM 能執行</b>、也不佔用記憶體，但服務與其攻擊面是開著的。
/// 這是「以為自己開了虛擬化、其實沒有」的狀態，最容易誤判。</item>
/// <item>平台停用但 VBS／記憶體完整性開啟——hypervisor 仍然會被載入（VBS 需要它），
/// 於是 MSR／TSC 讀值變成虛擬化的，即使使用者從未開過 Hyper-V。</item>
/// <item>全部開啟——真正在跑虛擬機器。</item>
/// </list>
/// <para>
/// <b>本類別只判讀，不改變任何設定。</b>讀的是 CPUID、服務狀態、選用功能與 WMI 的唯讀欄位。
/// </para>
/// </remarks>
public static class VirtualizationJudge
{
    /// <summary>判讀結果的成因分類。</summary>
    public enum VirtKind
    {
        /// <summary>沒有虛擬層，也沒有虛擬化元件在跑。</summary>
        BareMetal,
        /// <summary>元件裝了、服務在跑，但 hypervisor 未載入——不會有 VM 能執行。</summary>
        InstalledNotLoaded,
        /// <summary>平台未用，但 VBS／記憶體完整性把 hypervisor 拉起來了。</summary>
        VbsOnly,
        /// <summary>hypervisor 載入且確實在跑虛擬機器。</summary>
        Running,
        /// <summary>hypervisor 載入但沒有已定義的 VM——可能是 VBS、WSL2 或容器在用。</summary>
        LoadedWithoutVms,
        /// <summary>狀態讀不到足夠欄位，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀結果。</summary>
    /// <param name="Kind">成因分類。</param>
    /// <param name="Headline">一行結論（繁中原文，交由語言層翻譯）。</param>
    /// <param name="Evidence">依據：實際讀到的值與推論步驟，供使用者自行核對。</param>
    /// <param name="Attention">真需要處理的處境（＝值得看一眼，不是錯誤）。</param>
    /// <param name="MsrReadable">MSR／TSC 讀值是否為原生（hypervisor 未載入時為 true）。</param>
    public readonly record struct Verdict(
        VirtKind Kind, string Headline, string Evidence, bool Attention, bool MsrReadable);

    /// <summary>
    /// 依可觀測狀態判讀。判讀順序由「影響最大」排到「影響最小」：
    /// 虛擬層載入與否 → 載入但沒 VM → 裝了沒載入 → VBS 拉起 → 全關。
    /// </summary>
    [SpecRef("Intel SDM Vol. 2A CPUID leaf 1 ECX bit 31（Hypervisor Present）與 leaf 0x40000000（Hypervisor Vendor Leaf）的語意；Microsoft Hyper-V hypervisorlaunchtype（bcdedit）與 Win32_ComputerSystem.HypervisorPresent 的對應關係；Hv#1 存在位僅表示「有虛擬層在跑」，不表示 Hyper-V 的角色已啟用——兩者必須分開陳述。判讀僅比較唯讀欄位。")]
    public static Verdict Judge(VirtualizationState s)
    {
        // 幾乎什麼都讀不到：不猜
        if (!s.PlatformInstalled && !s.VmmsRunning && s.VmCount < 0 && s.TaskCount < 0)
            return new Verdict(VirtKind.Unknown, "—（讀不到虛擬化狀態）",
                "選用功能、服務與 WMI 都查不到，無從判斷。", false, !s.HypervisorLoaded);

        if (s.HypervisorLoaded)
        {
            string vendor = string.IsNullOrWhiteSpace(s.HypervisorVendor)
                ? "（簽章未回報）" : VendorName(s.HypervisorVendor);

            if (s.VmCount > 0)
                return new Verdict(VirtKind.Running,
                    $"虛擬層載入中（{vendor}），已定義 {s.VmCount} 台虛擬機器",
                    $"CPUID 回報虛擬層存在、簽章為 {vendor}；Hyper-V 管理服務"
                    + (s.VmmsRunning ? "執行中" : "未執行")
                    + $"、已定義 {s.VmCount} 台 VM、{Count(s.VirtualSwitchCount, "個虛擬交換器")}"
                    + "。MSR／TSC／PMU 的讀值可能被虛擬化攔截或改寫，本程式所有 MSR 類卡片請對照可信度說明。",
                    false, false);

            return new Verdict(VirtKind.LoadedWithoutVms,
                $"虛擬層載入中（{vendor}），但沒有任何已定義的虛擬機器",
                $"CPUID 回報虛擬層存在、簽章為 {vendor}；但 Hyper-V 管理服務"
                + (s.VmmsRunning ? "執行中" : "未執行")
                + $"、VM 數為 0、{Count(s.VirtualSwitchCount, "個虛擬交換器")}"
                + (s.VbsEnabled >= 1 ? "、VBS／記憶體完整性已啟用" : "")
                + (s.HnsRunning ? "、主機網路服務（hns）執行中——容器或 WSL2 會用到它" : "")
                + "。虛擬層不是只有 Hyper-V 會拉起來：VBS／記憶體完整性、WSL2、容器都會。"
                + "MSR／TSC／PMU 的讀值在這種情況下仍可能被虛擬化。",
                false, false);
        }

        // 未載入虛擬層，但元件與服務是開的
        if (s.PlatformInstalled || s.VmmsRunning)
        {
            string running = s.VmmsRunning
                ? "Hyper-V 虛擬機器管理服務（vmms）正在執行"
                : "Hyper-V 管理服務未執行";
            string defs = s.VmCount > 0
                ? $"，且已定義 {s.VmCount} 台虛擬機器"
                : "，也沒有已定義的虛擬機器";
            return new Verdict(VirtKind.InstalledNotLoaded,
                "Hyper-V 元件已安裝且服務在執行，但開機未載入虛擬層——無法執行任何虛擬機器",
                $"已安裝 Hyper-V 平台、{running}{defs}"
                + $"，但 CPUID 回報沒有虛擬層（bcdedit 的 hypervisorlaunchtype 為 Off 時就是這個狀態）"
                + $"。{Count(s.VirtualSwitchCount, "個虛擬交換器")}。"
                + "這種狀態下不會有 VM 能跑、也不佔用記憶體與 CPU，但服務、驅動與其攻擊面是開著的；"
                + "要真的能用 Hyper-V 需把 hypervisorlaunchtype 設為 Auto 並重開機，"
                + "要移除攻擊面則需停用 Hyper-V 選用功能。本程式只讀不寫，請自行決定。",
                true, true);
        }

        if (s.VbsEnabled >= 1)
            return new Verdict(VirtKind.VbsOnly,
                "Hyper-V 平台未啟用，但 VBS／記憶體完整性已開啟——虛擬層仍可能被載入",
                $"VBS 狀態碼為 {s.VbsEnabled}（1＝啟用但未執行、2＝執行中）"
                + "。VBS 需要虛擬層，因此即使從未開過 Hyper-V，MSR／TSC／PMU 的讀值也可能被虛擬化。",
                false, false);

        // 連 VBS 也沒有，且服務與平台都關著 → 裸機
        // VBS 狀態讀不到（-1）時不能寫成「VBS 關閉」——那是兩件事，講成關閉就是把未知當已知
        string vbsText = s.VbsEnabled switch
        {
            0 => "VBS 關閉",
            >= 1 => $"VBS 狀態碼為 {s.VbsEnabled}",
            _ => "VBS 狀態讀不到（不影響本判讀，但請注意這項未確認）",
        };
        string extras = s.TaskCount >= 0
            ? $"，排程工作 {s.TaskCount} 項（其中 {s.TaskRunsUnderSystem} 項以最高權限執行）"
            : "";
        return new Verdict(VirtKind.BareMetal,
            "裸機執行：沒有虛擬層，MSR／TSC／PMU 讀值為原生",
            $"未偵測到虛擬層（CPUID leaf 1 ECX bit 31 為 0）、Hyper-V 平台未啟用、"
            + vbsText + extras + "。",
            false, true);
    }

    private static string Count(int n, string what)
        => n < 0 ? $"{what}數讀不到" : $"{what} {n} 個";

    /// <summary>CPUID leaf 0x40000000 的 12 位元組 ASCII 簽章 → 廠商名（與 FirmwareService 同口徑）。</summary>
    [SpecRef("CPUID leaf 0x40000000（Hypervisor Vendor Leaf）：EBX／ECX／EDX 各 4 位元組 ASCII。已知簽章對照表見 Linux 核心與各 hypervisor 實作，未收錄者原樣帶出，不猜。")]
    public static string VendorName(string signature) => signature switch
    {
        "Microsoft Hv" => "Microsoft Hyper-V",
        "KVMKVMKVM" or "KVMKVMKVM   " => "KVM",
        "VMwareVMware" => "VMware",
        "XenVMMXenVMM" => "Xen",
        "VBoxVBoxVBox" => "VirtualBox",
        "TCGTCGTCGTCG" => "TCG（QEMU 軟體模擬）",
        "bhyve bhyve " => "bhyve",
        "ACRNACRNACRN" => "ACRN",
        "" => "（簽章為空）",
        _ => signature,
    };
}
