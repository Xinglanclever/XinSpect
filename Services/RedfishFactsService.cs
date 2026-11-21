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
        DateTimeOffset? measuredAt = inventory.CollectedAtUtc;
        var facts = new List<HardwareFact> { Boundary(at) };

        if (inventory.Managers.Count == 0 && inventory.Chassis.Count == 0 && inventory.Systems.Count == 0)
        {
            facts.Add(new HardwareFact(SummaryKey, Category, "帶外管理摘要", "", "個", Source,
                FactTrustLevel.Unknown, false, at, null, inventory.ReadFailureAvailability ?? FactAvailability.Unknown,
                inventory.ReadFailureAvailability is not null
                    ? "離線目錄讀取失敗或被預算拒絕；請查看讀取警告，不代表沒有 BMC"
                    : "離線目錄裡沒有可讀的管理器／機箱／系統——「目錄不存在」與「機器沒有 BMC」"
                      + "是兩件事，後者要從目錄內容本身判斷，不是讀不到就算沒有"));
            AddWarnings(facts, inventory, at);
            return StampImported(facts, inventory, at);
        }

        string summary = $"{inventory.Managers.Count} 個管理器、{inventory.Chassis.Count} 個機箱、{inventory.Systems.Count} 個系統";
        if (inventory.PowerSupplies.Count > 0) summary += $"、{inventory.PowerSupplies.Count} 個電源供應器";
        if (inventory.Fans.Count > 0) summary += $"、{inventory.Fans.Count} 個風扇";
        if (inventory.Temperatures.Count > 0) summary += $"、{inventory.Temperatures.Count} 個溫度感測器";
        summary += "；值為 BMC 回報（Reported），本工具未量測";
        if (inventory.ReadIncomplete) summary += "；讀取不完整，以上為已讀部分";
        facts.Add(new HardwareFact(SummaryKey, Category, "帶外管理摘要", summary, "個", Source,
            FactTrustLevel.Reported, false, at, null,
            inventory.ReadIncomplete || measuredAt is null ? FactAvailability.Unknown : FactAvailability.Present,
            inventory.ReadIncomplete ? "離線目錄讀取不完整；請查看讀取警告"
                : measuredAt is null ? "離線包沒有採集時間；不把呼叫時間冒充來源時間" : null));

        foreach (var m in inventory.Managers.OrderBy(x => x.Id, StringComparer.Ordinal))
            facts.Add(Item("manager", m.Id, Describe(m.Id, m.Model, m.FirmwareVersion), at));
        foreach (var c in inventory.Chassis.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string value = Describe(c.Id, c.Type, null)
                + (string.IsNullOrWhiteSpace(c.SerialNumber) ? "" : "，序號 " + c.SerialNumber);
            facts.Add(Item("chassis", c.Id, value, at, !string.IsNullOrWhiteSpace(c.SerialNumber)));
            if (c.Intrusion is { } intrusion)
                facts.Add(Item("chassis.intrusion", c.Id, $"{c.Id}，入侵感測：{intrusion}", at));
        }
        foreach (var s in inventory.Systems.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string model = string.IsNullOrWhiteSpace(s.Manufacturer) ? s.Model ?? "" : s.Manufacturer + " " + (s.Model ?? "");
            facts.Add(Item("system", s.Id, Describe(s.Id, model, s.BiosVersion), at));
        }
        foreach (var p in inventory.PowerSupplies.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string v = p.Id
                + (p.OutputWatts is { } w ? $"，輸出 {w:0} W" : "")
                + (p.InputVoltage is { } vlt ? $"，輸入 {vlt:0} V" : "")
                + (p.Status is { } st ? $"，狀態 {st}" : "");
            facts.Add(Item("psu", p.Id, v, at));
        }
        foreach (var f in inventory.Fans.OrderBy(x => x.Id, StringComparer.Ordinal))
            facts.Add(Item("fan", f.Id,
                f.ReadingRpm is { } rpm ? $"{f.Id}，{rpm:0} RPM" + (f.Status is { } st ? $"，狀態 {st}" : "")
                                        : f.RawReading is { } raw
                                            ? $"{f.Id}，原始 Reading {raw:0.###}，單位 {f.ReadingUnits ?? "未知"}（不可換算為 RPM）"
                                            : $"{f.Id}（無轉速回報）", at));
        foreach (var t in inventory.Temperatures.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            string v = t.ReadingCelsius is { } c
                ? $"{t.Id}，{c:0.#} °C" + (t.UpperCritical is { } uc ? $"（臨界 {uc:0.#} °C）" : "")
                : $"{t.Id}（無讀數回報）";
            facts.Add(Item("temp", t.Id, v, at));
        }
        foreach (var control in inventory.PowerControls.Take(RedfishBundleService.MaxPowerControls)
            .OrderBy(x => x.ScopeIdentity, StringComparer.Ordinal))
        {
            bool usable = control.ScopeResolved && RedfishBundleService.ValidObservationLocation(control.ChassisResourceUri)
                && RedfishBundleService.ValidObservationLocation(control.PowerResourceUri) && control.ArrayIndex >= 0
                && control.UnavailableReason is null && control.PowerConsumedWatts is { } watts
                && double.IsFinite(watts) && watts >= 0;
            string detail = PowerScopeText(control) + (usable
                ? "，PowerConsumedWatts " + control.PowerConsumedWatts!.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + " W"
                : "，PowerConsumedWatts 未取得或範圍未確認；未補 0");
            facts.Add(new HardwareFact(PowerItemKey(control), Category, "帶外：電力控制觀測（非整包總功耗）",
                detail, "W", Source + "；DMTF Power.v1_7_3 PowerControl.PowerConsumedWatts", FactTrustLevel.Reported,
                true, at, usable ? control.PowerConsumedWatts : null,
                usable ? FactAvailability.Present : FactAvailability.Unknown,
                usable ? null : control.UnavailableReason ?? "來源範圍或 PowerConsumedWatts 未確認"));
        }
        var selection = RedfishBundleService.SelectPowerConsumed(inventory);
        facts.Add(new HardwareFact("rf.power.consumed", Category, "單一電力控制讀數（BMC 回報；非整包總功耗）",
            selection.Watts is { } power && selection.Observation is { } observation
                ? PowerScopeText(observation) + "，" + power.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + " W"
                : "無唯一可用的具範圍電力控制讀數；不選首筆、不加總",
            "W", Source + "；DMTF Power.v1_7_3 PowerControl.PowerConsumedWatts", FactTrustLevel.Reported,
            selection.Observation is not null, at, selection.Watts,
            selection.Watts is null ? FactAvailability.Unknown : FactAvailability.Present,
            selection.Watts is null ? selection.Reason : null));

        AddWarnings(facts, inventory, at);

        // Repeated raw IDs can occur in malformed exports; retain every row without duplicate fact keys.
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < facts.Count; i++)
        {
            string key = facts[i].Key;
            int duplicate = 1;
            while (!used.Add(facts[i].Key)) facts[i] = facts[i] with { Key = key + "-duplicate-" + duplicate++ };
        }
        return StampImported(facts, inventory, at);
    }

    private static void AddWarnings(List<HardwareFact> facts, RedfishInventory inventory, DateTimeOffset at)
    {
        if (inventory.Warnings.Count > 0)
            facts.Add(new HardwareFact("rf.bundle.warnings", Category, "離線目錄讀取警告",
                $"{inventory.Warnings.Count} 條：" + string.Join("；", inventory.Warnings.Take(5)), "條", Source,
                FactTrustLevel.Reported, true, at, null, FactAvailability.Present,
                "被拒絕的 @odata.id、找不到的檔案與超限都記在這裡；沒有靜默略過"));
    }

    private static IReadOnlyList<HardwareFact> StampImported(List<HardwareFact> facts, RedfishInventory inventory, DateTimeOffset at)
    {
        string collection = inventory.CollectedAtUtc is { } collected
            ? "來源採集時間 " + collected.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            : "來源採集時間未知";
        return facts.Select(f =>
        {
            if (f.Key == BoundaryKey) return f;
            bool failedImport = f.Key == SummaryKey && inventory.ReadFailureAvailability is not null;
            const string unknownTime = "離線包沒有採集時間；來源採集時間未知，MeasuredAtUtc 為本程式匯入時間";
            return f with
            {
                MeasuredAtUtc = at,
                Source = f.Source + "；本程式匯入時間 " + at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "；" + collection,
                Availability = inventory.CollectedAtUtc is null && !failedImport ? FactAvailability.Unknown : f.Availability,
                NumericValue = inventory.CollectedAtUtc is null ? null : f.NumericValue,
                UnavailableReason = inventory.CollectedAtUtc is null
                    ? f.Key == SummaryKey && inventory.ReadIncomplete ? f.UnavailableReason + "；" + unknownTime : unknownTime
                    : f.UnavailableReason,
            };
        }).ToArray();
    }

    // Raw resource/member identifiers can contain serials. Keep them in sensitive Value, never in Source/key.
    internal static string PowerItemKey(RedfishPowerControl observation)
        => ItemPrefix + "power." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(observation.ScopeIdentity))).ToLowerInvariant()[..32];

    private static string PowerScopeText(RedfishPowerControl observation)
        => "機箱來源 " + observation.ChassisResourceUri + "，電力來源 " + observation.ObservationLocation
            + "，MemberId " + (observation.MemberId ?? "未宣告（本機陣列索引定位）")
            + (observation.MemberUri is { } uri ? "，成員宣告 URI " + uri : "")
            + (observation.Name is { } name ? "，Name " + name : "")
            + (observation.PhysicalContext is { } context ? "，PhysicalContext " + context : "");

    /// <summary>逐項事實：<c>rf.item.&lt;kind&gt;.&lt;序&gt;</c>，序按類別內出現順序（輸入已排序）。</summary>
    private static HardwareFact Item(string kind, string id, string value, DateTimeOffset importedAt, bool sensitive = false)
        => new(ItemPrefix + kind + "." + Safe(id), Category, "帶外：" + kind, value, "", Source,
            FactTrustLevel.Reported, sensitive, importedAt);

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

    /// <summary>輸出符合快照鍵契約的小寫 ASCII；轉換或截長時保留原始 Id 雜湊，避免大小寫與標點碰撞。</summary>
    private static string Safe(string rawId)
    {
        var sb = new StringBuilder();
        foreach (char ch in rawId)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        string s = sb.ToString().Trim('-');
        if (s == rawId && s.Length is > 0 and <= 40) return s;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawId))).ToLowerInvariant()[..8];
        return (s.Length > 31 ? s[..31] : s.Length == 0 ? "0" : s) + "-" + hash;
    }
}
