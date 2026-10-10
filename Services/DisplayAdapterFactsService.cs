using System;
using System.Collections.Generic;
using System.Management;

namespace XinSpect;

/// <summary>
/// 顯示轉接器真偽的<b>讀取</b>層：從 <c>Win32_VideoController</c> 取出判讀所需欄位。
/// </summary>
/// <remarks>
/// 全部唯讀、usermode 零特權。判準見 <see cref="DisplayAdapterJudge"/>：
/// 以 PnP 識別碼的匯流排來源（<c>PCI\VEN_</c> vs <c>ROOT\</c>）為主，
/// 驅動有沒有回報視訊處理器與廠商字串為輔。
/// </remarks>
public static class DisplayAdapterFactsService
{
    private const string Category = "顯示";

    /// <summary>收集顯示轉接器事實。測試以注入樣本取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<DisplayAdapterSample>>? probe = null)
    {
        IReadOnlyList<DisplayAdapterSample> adapters;
        try { adapters = (probe ?? ReadAll)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("display.summary", Category, "顯示轉接器組成", "", "",
                    "WMI Win32_VideoController", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "讀取失敗：" + ex.Message),
            ];
        }

        if (adapters.Count == 0)
            return
            [
                new HardwareFact("display.summary", Category, "顯示轉接器組成", "", "",
                    "WMI Win32_VideoController", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError,
                    "沒有回報任何顯示轉接器——讀不到就是不猜（可能是 WMI 未回應）"),
            ];

        var list = new List<HardwareFact>
        {
            new("display.summary", Category, "顯示轉接器組成",
                DisplayAdapterJudge.Summarize(adapters), "", "WMI Win32_VideoController（PnP 識別碼匯流排來源）",
                FactTrustLevel.Derived, false, at, null, FactAvailability.Present),
        };

        // 硬體 GPU 在場狀態（v2.57）：「沒有硬體顯示卡」是觀察到的 Present 事實，
        // 不是三態——我們真的讀到了組成，缺席的是卡、不是讀取。GPU 實測類功能
        // 依這條判斷「本機能不能量」，而不是靠例外路徑回頭猜。
        var hw = adapters.Where(a => a.HasPciAddress && !a.IsMicrosoftBasicDisplay).ToList();
        list.Add(new HardwareFact("display.hw.gpu", Category, "硬體 GPU 在場狀態",
            hw.Count > 0
                ? string.Join("、", hw.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal))
                : "沒有可辨識的硬體顯示卡（僅虛擬／基本顯示轉接器）",
            "", "WMI Win32_VideoController（PCI 列舉且非基本顯示）",
            FactTrustLevel.Derived, false, at, (double)hw.Count, FactAvailability.Present));

        foreach (var a in adapters)
        {
            var v = DisplayAdapterJudge.Judge(a);
            list.Add(new HardwareFact($"display.adapter.{a.Name}", Category, a.Name,
                v.Headline, "", $"WMI Win32_VideoController（{a.PnpDeviceId}）",
                FactTrustLevel.Measured, false, at, null,
                v.Kind == DisplayAdapterJudge.AdapterKind.Unknown
                    ? FactAvailability.ReadError : FactAvailability.Present,
                v.Kind == DisplayAdapterJudge.AdapterKind.Unknown ? v.Evidence : null));
        }

        return list;
    }

    /// <summary>WMI 通路：讀全部顯示轉接器。個別欄位缺漏以 false／空字串表達，不讓整批失敗。</summary>
    internal static IReadOnlyList<DisplayAdapterSample> ReadAll()
    {
        var result = new List<DisplayAdapterSample>();
        using var s = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT Name, PNPDeviceID, AdapterCompatibility, VideoProcessor FROM Win32_VideoController");
        foreach (ManagementObject o in s.Get())
            using (o)
            {
                string name = o["Name"] as string ?? "";
                if (name.Length == 0) continue;
                string id = o["PNPDeviceID"] as string ?? "";
                string compat = o["AdapterCompatibility"] as string ?? "";
                string proc = o["VideoProcessor"] as string ?? "";
                result.Add(new DisplayAdapterSample(
                    name, id, compat, proc,
                    HasPciAddress: id.Contains("PCI\\VEN_", StringComparison.OrdinalIgnoreCase),
                    IsRootEnumerated: id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase),
                    HasDriverVendor: compat.Trim().Length > 0,
                    HasVideoProcessor: proc.Trim().Length > 0,
                    IsMicrosoftBasicDisplay: compat.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)));
            }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }
}
