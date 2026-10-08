using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>一項識別資料的實況。字串為空＝該欄位讀不到。</summary>
/// <param name="Label">欄位名稱（系統序號、主機板序號、機箱序號、資產標籤…）。</param>
/// <param name="Value">讀到的原始字串（未經修飾）。</param>
/// <param name="Source">來源（SMBIOS Type 1／2／3 的哪一欄）。</param>
/// <param name="IsPlaceholder">是否為韌體未填時的預設字串。</param>
public readonly record struct AssetField(
    string Label, string Value, string Source, bool IsPlaceholder);

/// <summary>一台機器的識別資料集合。</summary>
/// <param name="Fields">各欄位。</param>
/// <param name="ChassisTypeCode">機箱類型代碼（Type 3 位移 0x05 的低 7 位）；0＝讀不到。</param>
/// <param name="ChassisTypeName">機箱類型名稱；空＝讀不到。</param>
/// <param name="Uuid">系統 UUID（已依規格解好）；空＝讀不到。</param>
/// <param name="IsServerChassis">機箱類型是否屬於伺服器類（機架式／刀鋒／主機式等）。</param>
public readonly record struct AssetRecord(
    IReadOnlyList<AssetField> Fields,
    byte ChassisTypeCode,
    string ChassisTypeName,
    string Uuid,
    bool IsServerChassis);

/// <summary>
/// 機器識別與資產的判讀：把 SMBIOS 的識別欄位收成一組，並如實標出「韌體沒填」的那些。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要特別處理「韌體沒填」：</b>組裝機與許多主機板的 SMBIOS 識別欄位填的是預設字串
/// （「Default string」「To be filled by O.E.M.」「System Serial Number」），
/// 看起來像有值，其實沒有。資產管理最常見的錯誤就是把這些字串當成序號登錄，
/// 於是十台機器有十個「Default string」——序號欄位形同虛設。
/// </para>
/// <para>
/// <b>本判讀不補值、不推測。</b>讀到預設字串就照實顯示原字串並標注；讀不到就說讀不到。
/// 機器本身的識別可以改用主機板序號、UUID 或硬碟序號，但那要由使用者決定，不由本程式代選。
/// </para>
/// <para>
/// <b>機箱類型是判斷「這是不是伺服器」最直接的證據</b>——韌體自己宣告的，
/// 不必從型號或外觀猜。代碼 0x11（工作站）、0x12（伺服器）、0x1F（主機式）、
/// 0x25／0x26（刀鋒）、0x27（機架式）屬於伺服器／機房類。
/// </para>
/// </remarks>
public static class AssetJudge
{
    /// <summary>判讀結果。</summary>
    /// <param name="Headline">一行結論。</param>
    /// <param name="UsableFields">可用的識別欄位數（非預設字串、非空）。</param>
    /// <param name="PlaceholderFields">韌體未填的欄位數。</param>
    /// <param name="CanIdentify">是否足以唯一識別這台機器（至少一個可用欄位）。</param>
    /// <param name="Evidence">依據。</param>
    public readonly record struct Verdict(
        string Headline, int UsableFields, int PlaceholderFields, bool CanIdentify, string Evidence);

    /// <summary>屬於伺服器／機房類的機箱類型代碼。</summary>
    [SpecRef("SMBIOS Specification, System Enclosure or Chassis (Type 3) offset 0x05 的 Chassis Type 代碼表（規格 7.4.1）：0x11 工作站、0x12 伺服器、0x1F 主機式機箱、0x25 刀鋒機箱、0x26 刀鋒伺服器機箱、0x27 機架式機箱、0x32 嵌入式邊緣伺服器。分類為本程式的判讀約定。")]
    public static bool IsServerChassisType(byte code) => code is 0x11 or 0x12 or 0x1F or 0x25 or 0x26 or 0x27 or 0x32;

    /// <summary>判讀一組識別資料。</summary>
    [SpecRef("SMBIOS Specification, System Information (Type 1) 的 Serial Number／SKU Number／Family／UUID、Baseboard Information (Type 2) 的 Serial Number、System Enclosure (Type 3) 的 Serial Number／Asset Tag。規格允許這些欄位為「未填」狀態，實作常填預設字串；本判讀如實標示，不代為補值。")]
    public static Verdict Judge(AssetRecord r)
    {
        if (r.Fields.Count == 0)
            return new Verdict("—（讀不到任何識別欄位）", 0, 0, false,
                "SMBIOS 的識別欄位一個都沒讀到——無從判斷這台機器的識別資料是否可用。");

        int usable = 0, placeholder = 0;
        var usableNames = new List<string>();
        var missingNames = new List<string>();
        foreach (var f in r.Fields)
        {
            if (f.Value.Length == 0 || f.IsPlaceholder) { placeholder++; missingNames.Add(f.Label); }
            else { usable++; usableNames.Add(f.Label); }
        }

        string chassis = r.ChassisTypeName.Length > 0
            ? $"機箱類型為「{r.ChassisTypeName}」" + (r.IsServerChassis ? "（伺服器／機房類）" : "（一般用途類）")
            : "機箱類型讀不到";

        if (usable == 0)
            return new Verdict(
                "⚠ 所有識別欄位都是韌體未填——這台機器在資產清單裡無法唯一識別",
                usable, placeholder, false,
                $"讀到的 {r.Fields.Count} 個識別欄位全部是空值或韌體預設字串"
                + $"（{string.Join("、", missingNames)}）；{chassis}。"
                + "這是組裝機與部分主機板的常態，不是故障——但把預設字串當序號登錄，"
                + "會讓資產清單上出現一堆一模一樣的「Default string」，序號欄位形同虛設。"
                + "要唯一識別這台機器，可改用磁碟序號、網卡 MAC 或由使用者自行指定資產編號；"
                + "本程式不代為選擇，也不補一個看起來合理的值。");

        string placeholders = placeholder > 0
            ? $"，另有 {placeholder} 個欄位是韌體未填（{string.Join("、", missingNames)}）"
            : "，沒有韌體未填的欄位";

        return new Verdict(
            $"可用的識別欄位 {usable} 個（{string.Join("、", usableNames)}）{placeholders}",
            usable, placeholder, true,
            $"讀到 {r.Fields.Count} 個識別欄位，其中 {usable} 個有實際值、{placeholder} 個是空值或韌體預設字串；"
            + $"{chassis}。" + (r.Uuid.Length > 0 ? $"系統 UUID 為 {r.Uuid}。" : "系統 UUID 讀不到。")
            + "有實際值的欄位才適合用來識別這台機器；被標注為預設字串的那些請不要當序號使用。");
    }
}
