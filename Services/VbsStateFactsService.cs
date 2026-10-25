using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// VBS／HVCI 的狀態與<b>未啟用原因面</b>（U10-SE-0003，鍵 <c>sec.vbs.state</c>＋<c>sec.vbs.required</c>）。
///
/// <para>
/// <b>來源：</b>WMI <c>root\Microsoft\Windows\DeviceGuard</c> 的 <c>Win32_DeviceGuard</c>
/// （零特權、唯讀；本機沒有該命名空間＝NotApplicable——那是環境事實）。
/// 值表出處是 Microsoft Learn〈Enable virtualization-based protection of code integrity〉的
/// Win32_DeviceGuard 欄位表（2026-10 對準），不是記憶。
/// </para>
///
/// <para>
/// <b>這一項與既有 VBS 事實的分工：</b><c>virt.vbs</c>（<see cref="VirtualizationFactsService"/>）
/// 已陳述 VirtualizationBasedSecurityStatus 三態；PlatformTrustService 的信任面板有
/// 設定／執行／可用三列。<b>缺的是「未啟用的原因面」</b>：狀態碼之外，把
/// <b>Required（政策要求的屬性）</b>、<b>Available（硬體韌體具備的屬性）</b>、
/// <b>Running（真的在跑的服務）</b>三張清單並列——
/// 「沒啟用」可能是政策沒開（可用而未啟用），也可能是硬體缺（不可用），
/// <b>把「沒啟用」寫成「不支援」是這一項明文禁止的混淆</b>。
/// </para>
///
/// <para>
/// <b>為什麼只出兩把固定鍵而不開逐屬性家族：</b>屬性代碼表（0–8）與服務代碼表（0–7）是
/// 規格的固定成員，清單內容隨機器變——逐碼開鍵會讓「沒要求的屬性」產生一堆空鍵；
/// 把清單成文在一把鍵裡，缺與不缺一眼可讀。家族若成員固定就只出單鍵，這裡就是那個形狀。
/// </para>
/// </summary>
public static class VbsStateFactsService
{
    private const string Category = "安全";
    private const string Source = "WMI Win32_DeviceGuard（root\\Microsoft\\Windows\\DeviceGuard，零特權唯讀）；值表＝Microsoft Learn 對準版";

    public const string StateKey = "sec.vbs.state";
    public const string RequiredKey = "sec.vbs.required";

    /// <summary>一次查詢拿到的全部欄位。<c>null</c>＝命名空間或類別不存在（NotApplicable）。</summary>
    public sealed record VbsSnapshot(
        uint? Status,
        IReadOnlyList<uint> Required,
        IReadOnlyList<uint> Available,
        IReadOnlyList<uint> Configured,
        IReadOnlyList<uint> Running);

    public delegate VbsSnapshot? SnapshotProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, SnapshotProbe? probe = null)
    {
        VbsSnapshot? s;
        bool threw = false;
        try { s = (probe ?? new SnapshotProbe(RealRead))(); }
        catch { s = null; threw = true; }

        if (s is null)
        {
            // 「命名空間／類別不存在（NotApplicable）」與「查詢拋例外（ReadError）」是不同的斷言，
            // 但兩者都指向「這一把現在讀不到」——如實分開說，不互代。
            var availability = threw ? FactAvailability.ReadError : FactAvailability.NotApplicable;
            string why = threw
                ? "Win32_DeviceGuard 查詢拋出例外（WMI 服務異常或存取被拒）——讀不到就是不猜"
                : "**本機沒有 root\\Microsoft\\Windows\\DeviceGuard 命名空間或 Win32_DeviceGuard 類別"
                  + "（或查詢成功但沒有實例）**——NotApplicable 是環境事實，不是「VBS 已關閉」；不推測";
            return
            [
                new HardwareFact(StateKey, Category, "VBS／HVCI 狀態", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, availability, why),
                new HardwareFact(RequiredKey, Category, "VBS 必要屬性", "", "項", Source,
                    FactTrustLevel.Unknown, false, at, null, availability,
                    threw ? "同一次查詢失敗——沒有 Required 清單可列" : "同一次查詢不可用——沒有 Required 清單可列"),
            ];
        }

        return [StateFact(at, s), RequiredFact(at, s)];
    }

    private static HardwareFact StateFact(DateTimeOffset at, VbsSnapshot s)
    {
        string status = s.Status is { } st
            ? $"{st}（{StatusName(st)}）"
            : "未回報（欄位讀不到，不代填）";

        string running = s.Running.Count == 0
            ? "無（0＝沒有服務在跑）"
            : string.Join("、", s.Running.Select(ServiceName));

        string reasonFace = s.Status == 0u
            ? "**未啟用≠不支援**：必要屬性 " + DescribeList(s.Required, PropertyName)
              + "、可用屬性 " + DescribeList(s.Available, PropertyName)
              + "——「可用而未啟用」（政策沒開）與「不可用」（硬體韌體缺）是**不同的斷言**，"
              + "兩張清單並列呈現，本服務不替機器歸因"
            : "執行中的服務與設定面（Configured）分開陳述——設定是政策，執行才是生效";

        return new HardwareFact(StateKey, Category, "VBS／HVCI 狀態",
            $"VirtualizationBasedSecurityStatus＝{status}；執行中的安全服務：{running}"
            + $"；已設定：{DescribeList(s.Configured, ServiceName)}。{reasonFace}。"
            + "狀態碼、服務碼、屬性碼的值表對準 Microsoft Learn 的 Win32_DeviceGuard 欄位表",
            "", Source, FactTrustLevel.Reported, false, at,
            s.Status, FactAvailability.Present, null);
    }

    private static HardwareFact RequiredFact(DateTimeOffset at, VbsSnapshot s)
    {
        return new HardwareFact(RequiredKey, Category, "VBS 必要屬性",
            s.Required.Count == 0
                ? "0（沒有任何屬性被要求——這是政策面的事實；「沒要求」≠「硬體不支援」）"
                : $"{s.Required.Count} 項：{string.Join("、", s.Required.Select(PropertyName))}"
                  + "。**Required 是政策要求的**，與 Available（硬體韌體具備的）、"
                  + "Running（真的在跑的）是三張不同的清單，不得互相代言",
            "項", Source, FactTrustLevel.Reported, false, at, s.Required.Count,
            FactAvailability.Present, null);
    }

    private static string DescribeList(IReadOnlyList<uint> list, Func<uint, string> name) =>
        list.Count == 0 ? "（空清單）" : string.Join("、", list.Select(name));

    /// <summary>VirtualizationBasedSecurityStatus：0 未啟用、1 已啟用但未執行、2 已啟用且執行中。</summary>
    internal static string StatusName(uint v) => v switch
    {
        0 => "未啟用", 1 => "已啟用但未執行", 2 => "已啟用且執行中",
        _ => $"0x{v:X}（值表未收錄）",
    };

    /// <summary>SecurityServicesConfigured／Running 的代碼表（0–7，Microsoft Learn 對準版）。</summary>
    internal static string ServiceName(uint v) => v switch
    {
        0 => "0（無服務）", 1 => "1（Credential Guard）", 2 => "2（記憶體完整性／HVCI）",
        3 => "3（System Guard Secure Launch）", 4 => "4（SMM 韌體量測）",
        5 => "5（核心模式硬體強制堆疊保護）", 6 => "6（同上，稽核模式）",
        7 => "7（Hypervisor-Enforced Paging Translation）",
        _ => $"0x{v:X}（值表未收錄）",
    };

    /// <summary>Available／RequiredSecurityProperties 的代碼表（0–8，Microsoft Learn 對準版）。</summary>
    internal static string PropertyName(uint v) => v switch
    {
        0 => "0（無）", 1 => "1（hypervisor 支援）", 2 => "2（Secure Boot）",
        3 => "3（DMA 保護）", 4 => "4（Secure Memory Overwrite）", 5 => "5（NX 保護）",
        6 => "6（SMM 緩解）", 7 => "7（MBEC/GMET）", 8 => "8（APIC 虛擬化）",
        _ => $"0x{v:X}（值表未收錄）",
    };

    // ── 真實讀取路徑（與 PlatformTrustService 同一個命名空間與類別；測試用合成 snapshot）──

    /// <summary>
    /// 查 Win32_DeviceGuard。<b>命名空間／類別不存在（ManagementException）或沒有實例＝回 null</b>
    /// （NotApplicable）；其他例外往上拋（→ReadError）。欄位逐個獨立轉換——一個欄位讀不到不拖累整份。
    /// </summary>
    internal static VbsSnapshot? RealRead()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard");
        foreach (ManagementObject m in searcher.Get())
        {
            using (m)
            {
                return new VbsSnapshot(
                    ToUInt(m["VirtualizationBasedSecurityStatus"]),
                    ToUIntList(m["RequiredSecurityProperties"]),
                    ToUIntList(m["AvailableSecurityProperties"]),
                    ToUIntList(m["SecurityServicesConfigured"]),
                    ToUIntList(m["SecurityServicesRunning"]));
            }
        }
        return null;   // 查詢成功但沒有實例——此 Windows 版本可能未提供該類別
    }

    private static uint? ToUInt(object? v)
    {
        try { return v is null ? null : Convert.ToUInt32(v); }
        catch { return null; }
    }

    private static IReadOnlyList<uint> ToUIntList(object? v)
    {
        try
        {
            if (v is not Array arr || arr.Length == 0) return [];
            var list = new List<uint>(arr.Length);
            foreach (var item in arr)
                if (ToUInt(item) is { } n) list.Add(n);
            return list;
        }
        catch { return []; }
    }
}
