using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>一個顯示轉接器的種類與證據。</summary>
/// <param name="Name">裝置名稱。</param>
/// <param name="PnpDeviceId">PnP 裝置識別碼（判斷物理匯流排的依據）。</param>
/// <param name="AdapterCompatibility">驅動回報的廠商字串。</param>
/// <param name="VideoProcessor">視訊處理器名稱（虛擬轉接器通常為空）。</param>
/// <param name="HasPciAddress">是否帶 PCI 位址（VEN_/DEV_）＝掛在真實匯流排上。</param>
/// <param name="IsRootEnumerated">是否由 ROOT 列舉（軟體建立的裝置節點）。</param>
/// <param name="HasDriverVendor">驅動是否有回報廠商字串。</param>
/// <param name="HasVideoProcessor">是否有回報視訊處理器名稱。</param>
/// <param name="IsMicrosoftBasicDisplay">是否為微軟的基本顯示驅動（沒有廠商驅動時的替代品）。</param>
public readonly record struct DisplayAdapterSample(
    string Name,
    string PnpDeviceId,
    string AdapterCompatibility,
    string VideoProcessor,
    bool HasPciAddress,
    bool IsRootEnumerated,
    bool HasDriverVendor,
    bool HasVideoProcessor,
    bool IsMicrosoftBasicDisplay);

/// <summary>
/// 顯示轉接器的真偽判讀：分辨「真實顯示卡」「軟體顯示轉接器」「沒有廠商驅動的替代品」。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要分：</b>Windows 會把三種東西一起列在「顯示卡」底下——真實的 GPU、模擬器與遠端桌面
/// 軟體自己裝的虛擬顯示轉接器、以及顯示卡沒有廠商驅動時頂上的 Microsoft Basic Display Adapter。
/// 對驗機的人來說這三者的意義完全不同：第一種是硬體、第二種<b>不是</b>（畫面是軟體畫出來的）、
/// 第三種代表驅動沒裝好。不分開的話，「你有 4 張顯示卡」這句話會同時涵蓋這三種情況。
/// </para>
/// <para>
/// <b>判準（依證據強度排序）：</b>
/// ① PnP 識別碼帶 <c>PCI\VEN_</c>＝掛在 PCI 匯流排上，是真實裝置；
/// ② <c>ROOT\</c> 開頭＝由作業系統的根列舉器建立，沒有實體匯流排；
/// ③ 驅動沒有回報視訊處理器名稱、也沒有廠商字串＝沒有真正的圖形驅動在後面。
/// 三者一致才下結論，任一項讀不到就說讀不到。
/// </para>
/// <para>
/// <b>本判讀不猜廠牌、不猜用途。</b>虛擬轉接器由哪個軟體安裝的，只在事實欄原樣帶出名稱與廠商字串，
/// 不推論它屬於哪個產品類別。
/// </para>
/// </remarks>
public static class DisplayAdapterJudge
{
    /// <summary>轉接器種類。</summary>
    public enum AdapterKind
    {
        /// <summary>有 PCI 位址、有廠商驅動＝真實顯示卡。</summary>
        Physical,
        /// <summary>由 ROOT 列舉、無 PCI 位址＝軟體建立的虛擬顯示轉接器。</summary>
        Virtual,
        /// <summary>微軟基本顯示驅動：真實硬體但沒有廠商驅動。</summary>
        BasicDisplay,
        /// <summary>證據不足，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀結果。</summary>
    /// <param name="Kind">種類。</param>
    /// <param name="Headline">一行結論。</param>
    /// <param name="Evidence">依據：實際讀到的欄位值。</param>
    /// <param name="CountsAsGpu">是否應計入「這台機器有幾張顯示卡」。虛擬轉接器不計入。</param>
    public readonly record struct Verdict(AdapterKind Kind, string Headline, string Evidence, bool CountsAsGpu);

    /// <summary>依證據判讀一個轉接器。</summary>
    [SpecRef("Windows 顯示轉接器的裝置節點來源：PCI 匯流排上的裝置 PnP 識別碼為 PCI\\VEN_xxxx&DEV_xxxx（見 PCI SIG 廠商識別碼與 PCI Express Base Specification 的設定空間標頭）；由作業系統根列舉器建立的軟體裝置為 ROOT\\…。Microsoft Basic Display Adapter 為無廠商驅動時的替代驅動（Microsoft 顯示驅動文件）。判讀僅比較唯讀欄位。")]
    public static Verdict Judge(DisplayAdapterSample s)
    {
        if (s.Name.Length == 0)
            return new Verdict(AdapterKind.Unknown, "—（沒有裝置名稱）", "名稱為空，無從判讀。", false);

        bool nameSaysBasic = s.IsMicrosoftBasicDisplay
            || s.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);

        if (s.HasPciAddress)
        {
            if (nameSaysBasic)
                return new Verdict(AdapterKind.BasicDisplay,
                    "真實硬體，但只有微軟基本顯示驅動",
                    $"{s.Name}：PnP 識別碼 {s.PnpDeviceId}（PCI 匯流排上的真實裝置），"
                    + "但驅動是微軟的基本顯示驅動——代表原廠顯示驅動沒有安裝或沒有正確載入。"
                    + "基本顯示驅動不支援 3D 加速、視訊解碼與多數顯示功能，"
                    + "這一台的圖形效能與硬體能力不符不是硬體的問題。",
                    true);

            if (!s.HasVideoProcessor && !s.HasDriverVendor)
                return new Verdict(AdapterKind.Physical,
                    "真實顯示卡（PCI 匯流排），但驅動沒有回報視訊處理器與廠商",
                    $"{s.Name}：PnP 識別碼 {s.PnpDeviceId}（PCI 匯流排上的真實裝置）；"
                    + "驅動未回報視訊處理器名稱與廠商字串——裝置本身是真的，"
                    + "但這台機器上的驅動資訊不完整，顯示相關的判讀請保留。",
                    true);

            return new Verdict(AdapterKind.Physical,
                "真實顯示卡（PCI 匯流排）",
                $"{s.Name}：PnP 識別碼 {s.PnpDeviceId}（PCI 匯流排上的真實裝置）"
                + (s.HasDriverVendor ? $"，廠商 {s.AdapterCompatibility}" : "")
                + (s.HasVideoProcessor ? $"，視訊處理器 {s.VideoProcessor}" : "") + "。",
                true);
        }

        if (s.IsRootEnumerated)
            return new Verdict(AdapterKind.Virtual,
                "軟體顯示轉接器——不是硬體",
                $"{s.Name}：PnP 識別碼 {s.PnpDeviceId}——<c>ROOT</c> 開頭代表由作業系統的根列舉器建立，"
                + "不在任何實體匯流排上"
                + (s.HasDriverVendor ? $"，驅動廠商字串為 {s.AdapterCompatibility}" : "，驅動未回報廠商字串")
                + (s.HasVideoProcessor ? "" : "，也沒有回報視訊處理器")
                + "。它會出現在顯示卡清單裡，但畫面不是由 GPU 產生的——"
                + "計算「這台機器有幾張顯示卡」時不應計入，顯示相關的效能判讀也不適用。",
                false);

        return new Verdict(AdapterKind.Unknown,
            "—（無法判定這個轉接器的來源）",
            $"{s.Name}：PnP 識別碼「{s.PnpDeviceId}」既不是 PCI 位址也不是 ROOT 列舉，"
            + "本判讀沒有可依據的匯流排證據——如實不判。",
            false);
    }

    /// <summary>
    /// 整組轉接器的摘要。實體／虛擬／基本顯示各幾個，並說明這對其他判讀的影響。
    /// </summary>
    [SpecRef("同上：以 PnP 識別碼的匯流排來源區分實體與軟體顯示裝置。計數僅陳述裝置數，不對圖形效能下結論。")]
    public static string Summarize(IReadOnlyList<DisplayAdapterSample> adapters)
    {
        if (adapters.Count == 0)
            return "沒有讀到任何顯示轉接器——這通常是查詢失敗，不代表機器沒有顯示裝置。";

        int physical = 0, virt = 0, basic = 0, unknown = 0;
        foreach (var a in adapters)
        {
            switch (Judge(a).Kind)
            {
                case AdapterKind.Physical: physical++; break;
                case AdapterKind.Virtual: virt++; break;
                case AdapterKind.BasicDisplay: basic++; break;
                default: unknown++; break;
            }
        }

        var parts = new List<string>();
        if (physical > 0) parts.Add($"{physical} 張真實顯示卡");
        if (basic > 0) parts.Add($"{basic} 張只有基本顯示驅動");
        if (virt > 0) parts.Add($"{virt} 個軟體顯示轉接器（不是硬體）");
        if (unknown > 0) parts.Add($"{unknown} 個無法判定");

        string tail = virt > 0
            ? " 軟體轉接器會被 Windows 列在顯示卡底下，但畫面不是由 GPU 產生的——"
              + "看圖形相關的數字時請先確認它在哪一個轉接器上量到的。"
            : "";
        return $"共 {adapters.Count} 個顯示轉接器：{string.Join("、", parts)}。{tail}";
    }
}
