using System.Text;

namespace XinSpect;

/// <summary>
/// 把離線 Redfish 目錄摘要（<see cref="RedfishInventory"/>）轉成 <c>rf.*</c> 事實。
/// </summary>
/// <remarks>
/// <para>
/// <b>信任等級一律 Reported。</b>這些值是 BMC 的自我宣告（感測器讀數、序號、韌體版本），
/// 本工具沒有辦法驗證——把它標成 Measured 就等於替 BMC 背書。
/// </para>
/// <para>
/// <b>不連網。</b>輸入只能來自 <see cref="RedfishBundleService.Read"/>，後者沒有任何 HTTP 用戶端。
/// </para>
/// <para>
/// <b>鍵的形狀：</b>目錄鍵（<see cref="FactKeyCatalog"/>）只登記彙總與邊界；
/// 逐項的 <c>rf.item.&lt;類別&gt;.&lt;序&gt;</c> 是動態家族
/// （<see cref="FactKeyDynamicCatalog"/> 登記 <c>rf.item.</c>）——成員由離線包裡有幾台設備決定，
/// 編譯期數不出來，序號按類別內 Id 排序（穩定，同一份包重讀得同一組鍵）。
/// </para>
/// </remarks>
public static class RedfishFactsService
{
    public const string Category = "資料中心帶外";
    public const string SummaryKey = "rf.summary";
    public const string BoundaryKey = "rf.boundary";
    private const string ItemPrefix = "rf.item.";
    private const string Source = "離線 Redfish 目錄（BMC 自我宣告，唯讀；不連網）";

    public static IReadOnlyList<HardwareFact> Collect(RedfishInventory inventory, DateTimeOffset at)
    {
        var facts = new List<HardwareFact> { Boundary(at) };

        if (inventory.Managers.Count == 0 && inventory.Chassis.Count == 0 && inventory.Systems.Count == 0)
        {
            facts.Add(new HardwareFact(SummaryKey, Category, "帶外管理摘要", "", "個", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "離線目錄裡沒有可讀的管理器／機箱／系統——「目錄不存在」與「機器沒有 BMC」"
                + "是兩件事，後者要從目錄內容本身判斷，不是讀不到就算沒有"));
            return facts;
        }

        string summary = $"{inventory.Managers.Count} 個管理器、{inventory.Chassis.Count} 個機箱、{inventory.Systems.Count} 個系統";
        if (inventory.PowerSupplies.Count > 0) summary += $"、{inventory.PowerSupplies.Count} 個電源供應器";
        if (inventory.Fans.Count > 0) summary += $"、{inventory.Fans.Count} 個風扇";
        if (inventory.Temperatures.Count > 0) summary += $"、{inventory.Temperatures.Count} 個溫度感測器";
        summary += "；值為 BMC 回報（Reported），本工具未量測";
        facts.Add(new HardwareFact(SummaryKey, Category, "帶外管理摘要", summary, "個", Source,
            FactTrustLevel.Reported, false, at));

        foreach (var m in inventory.Managers.OrderBy(x => x.Id, StringComparer.Ordinal))
            facts.Add(Item("manager", m.Id, Describe(m.Id, m.Model, m.FirmwareVersion)));
        foreach (var c in inventory.Chassis.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            facts.Add(Item("chassis", c.Id, Describe(c.Id, c.Type, c.SerialNumber)));
            if (c.Intrusion is { } intrusion)
                facts.Add(Item("chassis.intrusion", c.Id, $"{c.Id}，入侵感測：{intrusion}"));
        }
        foreach (var s in inventory.Systems.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string model = string.IsNullOrWhiteSpace(s.Manufacturer) ? s.Model ?? "" : s.Manufacturer + " " + (s.Model ?? "");
            facts.Add(Item("system", s.Id, Describe(s.Id, model, s.BiosVersion)));
        }
        foreach (var p in inventory.PowerSupplies.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string v = p.Id
                + (p.OutputWatts is { } w ? $"，輸出 {w:0} W" : "")
                + (p.InputVoltage is { } vlt ? $"，輸入 {vlt:0} V" : "")
                + (p.Status is { } st ? $"，狀態 {st}" : "");
            facts.Add(Item("psu", p.Id, v));
        }
        foreach (var f in inventory.Fans.OrderBy(x => x.Id, StringComparer.Ordinal))
            facts.Add(Item("fan", f.Id,
                f.ReadingRpm is { } rpm ? $"{f.Id}，{rpm:0} RPM" + (f.Status is { } st ? $"，狀態 {st}" : "")
                                        : $"{f.Id}（無轉速回報）"));
        foreach (var t in inventory.Temperatures.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string v = t.ReadingCelsius is { } c
                ? $"{t.Id}，{c:0.#} °C" + (t.UpperCritical is { } uc ? $"（臨界 {uc:0.#} °C）" : "")
                : $"{t.Id}（無讀數回報）";
            facts.Add(Item("temp", t.Id, v));
        }
        if (inventory.PowerConsumedWatts is { } power)
            facts.Add(new HardwareFact("rf.power.consumed", Category, "機箱功耗（BMC 回報）",
                $"{power:0.#} W", "W", Source, FactTrustLevel.Reported, false, at, power));

        if (inventory.Warnings.Count > 0)
            facts.Add(new HardwareFact("rf.bundle.warnings", Category, "離線目錄讀取警告",
                $"{inventory.Warnings.Count} 條：" + string.Join("；", inventory.Warnings.Take(5)), "條", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Present,
                "被拒絕的 @odata.id、找不到的檔案與超限都記在這裡；沒有靜默略過"));

        return facts;
    }

    /// <summary>逐項事實：<c>rf.item.&lt;kind&gt;.&lt;序&gt;</c>，序按類別內出現順序（輸入已排序）。</summary>
    private static HardwareFact Item(string kind, string id, string value)
        => new(ItemPrefix + kind + "." + Safe(id), Category, "帶外：" + kind, value, "", Source,
            FactTrustLevel.Reported, false, DateTimeOffset.UtcNow);

    /// <summary>BMC 宣告的邊界：值是回報不是量測、離線包不追外部連結。</summary>
    private static HardwareFact Boundary(DateTimeOffset at)
        => new(BoundaryKey, Category, "帶外資料邊界",
            "rf.* 的全部值來自 BMC 的自我宣告，信任等級一律 Reported：感測器讀數、序號與韌體版本"
            + "都沒有經過本工具驗證；離線目錄不追外部 URL、不連 BMC、不連網。",
            "", Source, FactTrustLevel.Reported, false, at);

    private static string Describe(string id, string? model, string? extra)
    {
        var sb = new StringBuilder(id);
        if (!string.IsNullOrWhiteSpace(model)) sb.Append("，").Append(model);
        if (!string.IsNullOrWhiteSpace(extra)) sb.Append("，版本／韌體 ").Append(extra);
        return sb.ToString();
    }

    /// <summary>把 BMC 給的 Id 壓成鍵名可用的一段：只留文數字，其餘換連字號並截長 40。</summary>
    private static string Safe(string rawId)
    {
        var sb = new StringBuilder();
        foreach (char ch in rawId)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        string s = sb.ToString().Trim('-');
        return s.Length > 40 ? s[..40] : s.Length == 0 ? "0" : s;
    }
}
