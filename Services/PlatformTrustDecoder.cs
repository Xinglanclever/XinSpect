using System.Text;

namespace XinSpect;

/// <summary>平台可信度的解碼純函式（單元測試涵蓋，不接觸硬體）。</summary>
public static class PlatformTrustDecoder
{
    /// <summary>把 CPUID 0x40000000 的 EBX/ECX/EDX 併回 12 位元組 ASCII 簽章。</summary>
    public static string HypervisorSignature(uint ebx, uint ecx, uint edx)
    {
        var bytes = new byte[12];
        BitConverter.GetBytes(ebx).CopyTo(bytes, 0);
        BitConverter.GetBytes(ecx).CopyTo(bytes, 4);
        BitConverter.GetBytes(edx).CopyTo(bytes, 8);
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
    }

    public static string HypervisorVendor(string signature) => signature switch
    {
        "Microsoft Hv" => "Microsoft Hyper-V",
        "KVMKVMKVM" => "KVM",
        "VMwareVMware" => "VMware",
        "XenVMMXenVMM" => "Xen",
        "VBoxVBoxVBox" => "VirtualBox",
        "prl hyperv" => "Parallels",
        "TCGTCGTCGTCG" => "QEMU（TCG 模擬）",
        "" => "—（簽章為空）",
        _ => signature,
    };

    /// <summary>Win32_DeviceGuard.VirtualizationBasedSecurityStatus：0 未啟用、1 已啟用但未執行、2 已啟用並執行。</summary>
    public static string DescribeVbsStatus(uint? status) => status switch
    {
        null => "—（讀不到）",
        0 => "未啟用",
        1 => "已啟用但未執行",
        2 => "已啟用並正在執行",
        _ => $"未知代碼 {status}",
    };

    /// <summary>Win32_DeviceGuard.SecurityServicesConfigured／Running 的服務代碼。</summary>
    public static string ServiceName(uint code) => code switch
    {
        0 => "無",
        1 => "Credential Guard",
        2 => "記憶體完整性（HVCI）",
        3 => "System Guard 安全啟動",
        4 => "SMM 韌體量測",
        5 => "核心模式硬體強制堆疊保護",
        6 => "Hypervisor 強制分頁轉譯",
        7 => "核心模式硬體強制堆疊保護（稽核）",
        _ => $"未知代碼 {code}",
    };

    /// <summary>Win32_DeviceGuard.AvailableSecurityProperties 的屬性代碼。</summary>
    public static string PropertyName(uint code) => code switch
    {
        1 => "Hypervisor 支援",
        2 => "Secure Boot",
        3 => "DMA 保護",
        4 => "安全記憶體覆寫",
        5 => "UEFI 程式碼唯讀",
        6 => "SMM 安全緩解 1.0",
        7 => "模式化執行控制（MBEC）",
        8 => "APIC 虛擬化",
        _ => $"未知代碼 {code}",
    };

    public static string DescribeServices(uint[]? codes)
    {
        var real = (codes ?? []).Where(c => c != 0).ToArray();
        return real.Length == 0 ? "無（空清單或僅含 0）" : string.Join("、", real.Select(ServiceName));
    }

    public static string DescribeProperties(uint[]? codes)
        => codes is null || codes.Length == 0 ? "—（讀不到或為空）" : string.Join("、", codes.Select(PropertyName));

    /// <summary>SYSTEM_CODEINTEGRITY_INFORMATION.CodeIntegrityOptions 的旗標。</summary>
    public static readonly (uint Flag, string Name)[] CodeIntegrityFlags =
    {
        (0x0001, "已啟用"),
        (0x0002, "測試簽章模式（testsigning）"),
        (0x0004, "使用者模式程式碼完整性（UMCI）"),
        (0x0008, "UMCI 稽核模式"),
        (0x0010, "UMCI 排除路徑"),
        (0x0020, "測試版建置"),
        (0x0040, "預生產建置"),
        (0x0080, "核心除錯模式"),
        (0x0100, "Flight 建置"),
        (0x0200, "Flighting 已啟用"),
        (0x0400, "HVCI 核心模式已啟用"),
        (0x0800, "HVCI 核心模式稽核"),
        (0x1000, "HVCI 嚴格模式"),
        (0x2000, "HVCI IUM"),
        (0x4000, "WHQL 強制"),
        (0x8000, "WHQL 稽核模式"),
    };

    public static string DescribeCodeIntegrity(uint options)
    {
        var on = CodeIntegrityFlags.Where(f => (options & f.Flag) != 0).Select(f => f.Name).ToArray();
        return on.Length == 0 ? "未啟用（Options 為 0）" : string.Join("、", on);
    }

    /// <summary>
    /// 一句話結論：本機的 MSR／TSC／PMU 讀值算不算原生。
    /// 只依據讀到的事實，讀不到就說讀不到——不猜。
    /// </summary>
    public static string Verdict(bool hypervisorPresent, uint? vbsStatus, bool invariantTsc)
    {
        string tsc = invariantTsc
            ? "Invariant TSC 存在，時間基準成立。"
            : "⚠ 沒有 Invariant TSC：以 TSC 換算時間的卡片（頻率真相、延遲量測）都不可信。";

        if (!hypervisorPresent)
            return "本機為裸機執行（CPUID 的 hypervisor 位為 0，VBS 未執行）："
                 + "MSR、PMU 計數器與 TSC 皆由硬體直接回應，本程式的 MSR 類卡片為原生讀值。" + tsc;

        string vbs = vbsStatus switch
        {
            2 => "且 VBS 正在執行——Windows 本身是 Hyper-V 上的一個分割區",
            1 => "但 VBS 只是「已設定未執行」，hypervisor 來自別處",
            0 => "而 VBS 未啟用，hypervisor 來自別處（例如本機是虛擬機客體）",
            _ => "，VBS 狀態讀不到",
        };
        return $"⚠ 偵測到 hypervisor{vbs}："
             + "MSR 可能被攔截、遮罩或回傳虛擬值，PMU 計數可能不被轉送，TSC 可能被偏移或縮放。"
             + "本程式的 MSR 類卡片（Top-down、頻率真相、RDT、MCA、安全位元）在此情況下只能當參考。" + tsc;
    }
}
