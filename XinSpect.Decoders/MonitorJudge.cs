using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>一台顯示器（EDID 面）的實況。字串欄位為空＝該欄位讀不到。</summary>
/// <param name="InstanceName">WMI 實例名（含 PNP 路徑，可追回裝置）。</param>
/// <param name="ManufacturerCode">EDID 的三字母製造商代碼（來自 WmiMonitorID.ManufacturerName）。</param>
/// <param name="FriendlyName">EDID 的產品名稱（來自 UserFriendlyName）。</param>
/// <param name="Year">製造年份；0＝未回報。</param>
/// <param name="WeekOfManufacture">製造週次；0＝未回報。</param>
/// <param name="IsEdidBacked">是否由真實 EDID 支撐（WmiMonitorID 有這一筆）。</param>
/// <param name="VideoOutputTechnology">連接介面（WmiMonitorConnectionParams）；int.MinValue＝讀不到。</param>
/// <param name="HasPnpPath">InstanceName 是否帶 DISPLAY\ 的實體 PNP 路徑。</param>
public readonly record struct MonitorSample(
    string InstanceName,
    string ManufacturerCode,
    string FriendlyName,
    int Year,
    int WeekOfManufacture,
    bool IsEdidBacked,
    int VideoOutputTechnology,
    bool HasPnpPath);

/// <summary>
/// 顯示器真偽與輸出的判讀：分辨「有 EDID 的真實螢幕」與「軟體虛擬螢幕」。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要分：</b>Windows 會把三種東西列進顯示器清單——接了線的真實螢幕（有 EDID，
/// 帶製造商、型號、序號、解析度與時序）、<b>軟體虛擬螢幕</b>（模擬器、遠端桌面、虛擬顯示驅動
/// 自己插進來的，常常沒有真正的 EDID 或只有一個最小合成的 EDID），以及顯卡驅動自己報的
/// 預設監視器（沒有實體在後面）。對驗機的人來說，第一種才是硬體。
/// </para>
/// <para>
/// <b>判準：</b>EDID 支撐（WMI 有 <c>WmiMonitorID</c> 這一筆）＋ InstanceName 帶
/// <c>DISPLAY\</c> 的實體 PNP 路徑。兩者都有＝真實螢幕；只有 PNP 路徑而沒有 EDID 識別資料＝
/// 可疑（可能是虛擬驅動合成的最小 EDID）；<b>完全沒有 PNP 路徑</b>＝作業系統的預設監視器物件，
/// 後面沒有實體裝置。
/// </para>
/// <para><b>本判讀不從廠牌推論好壞，也不判斷畫質。</b>只陳述「這是不是有 EDID 支撐的實體螢幕」。</para>
/// </remarks>
public static class MonitorJudge
{
    /// <summary>顯示器種類。</summary>
    public enum MonitorKind
    {
        /// <summary>有 EDID 識別資料、有實體 PNP 路徑＝接了線的真實螢幕。</summary>
        Physical,
        /// <summary>有 PNP 路徑但沒有 EDID 識別資料——可能是虛擬顯示驅動合成的最小 EDID。</summary>
        Synthetic,
        /// <summary>沒有實體 PNP 路徑＝作業系統的預設監視器物件。</summary>
        Placeholder,
        /// <summary>讀不到足夠欄位，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀結果。</summary>
    /// <param name="Kind">種類。</param>
    /// <param name="Headline">一行結論。</param>
    /// <param name="Evidence">依據：實際讀到的欄位值。</param>
    /// <param name="CountsAsDisplay">是否應計入「接了幾台螢幕」。</param>
    public readonly record struct Verdict(MonitorKind Kind, string Headline, string Evidence, bool CountsAsDisplay);

    /// <summary>依證據判讀一台顯示器。</summary>
    [SpecRef("EDID 資料結構（VESA Enhanced EDID Standard）：製造商 ID 為 3 個 5 位元字母壓縮碼、產品名稱在描述子區、製造年月以週次＋年份（1990 起）表示。Windows 經 WMI root\\wmi 的 WmiMonitorID（EDID 解析結果）與 WmiMonitorConnectionParams（VideoOutputTechnology）呈現；沒有 EDID 支撐的實例不具備製造商與年份欄位。判讀僅比較唯讀欄位。")]
    public static Verdict Judge(MonitorSample s)
    {
        if (s.InstanceName.Length == 0)
            return new Verdict(MonitorKind.Unknown, "—（沒有實例名稱）", "實例名稱為空，無從判讀。", false);

        bool hasIdentity = s.ManufacturerCode.Length > 0 || s.FriendlyName.Length > 0 || s.Year > 0;

        if (!s.HasPnpPath)
            return new Verdict(MonitorKind.Placeholder,
                "作業系統的預設監視器物件——後面沒有實體螢幕",
                $"實例名「{s.InstanceName}」不帶 DISPLAY\\ 的實體 PNP 路徑，"
                + (hasIdentity ? "雖有 EDID 識別資料，" : "也沒有 EDID 識別資料，")
                + "這是驅動自己報的預設監視器，不是接了線的螢幕。計算「接了幾台螢幕」時不應計入。",
                false);

        if (!hasIdentity)
            return new Verdict(MonitorKind.Synthetic,
                "有實體路徑但沒有 EDID 識別資料——可能是虛擬顯示驅動",
                $"實例名「{s.InstanceName}」帶實體 PNP 路徑，但 EDID 的製造商代碼、產品名稱與製造年份"
                + "都沒有回報。真實螢幕的 EDID 一定含這三項；缺少它們通常代表這是軟體合成的最小 EDID"
                + "（虛擬顯示驅動、模擬器或遠端桌面的虛擬螢幕）。本判讀不指名是哪一套軟體，"
                + "請對照「顯示轉接器」那一區的判讀。",
                false);

        string identity = string.Join("／", new[]
        {
            s.ManufacturerCode.Length > 0 ? s.ManufacturerCode : "",
            s.FriendlyName.Length > 0 ? s.FriendlyName : "",
        }.Where(x => x.Length > 0));
        string when = s.Year > 0
            ? s.WeekOfManufacture > 0 ? $"，{s.Year} 年第 {s.WeekOfManufacture} 週製造" : $"，{s.Year} 年製造"
            : "";

        return new Verdict(MonitorKind.Physical,
            $"真實螢幕（{identity}）",
            $"實例名「{s.InstanceName}」帶實體 PNP 路徑，且有 EDID 識別資料：{identity}{when}。",
            true);
    }

    /// <summary>整組顯示器的摘要。</summary>
    [SpecRef("同上：以 EDID 支撐與 PNP 路徑區分實體螢幕與軟體合成的實例。計數僅陳述裝置數，不對顯示品質下結論。")]
    public static string Summarize(IReadOnlyList<MonitorSample> monitors)
    {
        if (monitors.Count == 0)
            return "沒有讀到任何顯示器實例——這通常是查詢失敗，不代表機器沒有接螢幕。";

        int physical = 0, synth = 0, placeholder = 0, unknown = 0;
        foreach (var m in monitors)
        {
            switch (Judge(m).Kind)
            {
                case MonitorKind.Physical: physical++; break;
                case MonitorKind.Synthetic: synth++; break;
                case MonitorKind.Placeholder: placeholder++; break;
                default: unknown++; break;
            }
        }

        var parts = new List<string>();
        if (physical > 0) parts.Add($"{physical} 台真實螢幕（有 EDID）");
        if (synth > 0) parts.Add($"{synth} 台有實體路徑但無 EDID 識別資料（可能是虛擬顯示驅動）");
        if (placeholder > 0) parts.Add($"{placeholder} 個作業系統預設監視器物件（後面沒有實體裝置）");
        if (unknown > 0) parts.Add($"{unknown} 個無法判定");

        return $"共 {monitors.Count} 個顯示器實例：{string.Join("、", parts)}。"
             + (physical < monitors.Count
                ? " 只有帶 EDID 的那幾台是接了線的真實螢幕，其餘是軟體或作業系統自己報的。"
                : "");
    }
}
