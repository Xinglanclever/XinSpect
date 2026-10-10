using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 伺服器合規摘要（Vol 2 批次 D／SV-014）。
/// <para>
/// <b>與 SV-001 的關係：</b>角色盤點（SV-001）在本專案<b>早已存在</b>——
/// <see cref="RoleSurfaceFactsService"/> 自 v2.x 起就生產 <c>role.surface</c>、<c>role.surface.evidence</c>
/// 與 <c>role.installed.*</c>（動態家族，見 <see cref="FactKeyDynamicCatalog"/>）。
/// 所以這一輪<b>不重做盤點</b>，只補上原本缺的那一層：把角色事實聚合成一句可讀的摘要
/// （幾個角色、幾個角色的服務沒全跑、哪些值得先看）。
/// </para>
/// <para>
/// <b>界線：</b>①「裝了」不等於「配置好」——角色安裝狀態不回答設定是否正確；
/// ②服務沒在跑也可能是刻意停用（安全性收縮、省資源），所以只陳述事實與計數，不評級、不給紅黃判決；
/// ③摘要的輸入是角色事實本身，來源缺席就如實說「尚未讀取」，不給 0 個角色的假摘要。
/// </para>
/// </summary>
public static class ServerComplianceFactsService
{
    public const string Category = "伺服器角色";
    public const string SummaryKey = "sv.summary";

    private static readonly Regex ServiceCounts =
        new(@"服務執行中\s*(\d+)、已安裝未執行\s*(\d+)", RegexOptions.Compiled);

    /// <summary>可被解析出「已安裝未執行 N」的角色列。</summary>
    public sealed record RoleStanding(string Role, int Running, int Stopped);

    /// <summary>從角色事實裡解析每個角色的服務狀態（解析不出來的角色不列入，也不補 0）。</summary>
    public static IReadOnlyList<RoleStanding> Standings(IReadOnlyList<HardwareFact> roleFacts)
    {
        var list = new List<RoleStanding>();
        foreach (var f in roleFacts)
        {
            if (!f.Key.StartsWith("role.installed.", StringComparison.Ordinal)) continue;
            var m = ServiceCounts.Match(f.Value);
            if (!m.Success) continue;   // 「服務狀態未查」如實略過，不當成 0
            list.Add(new RoleStanding(f.Name, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        return list;
    }

    /// <summary>由角色事實聚合出一條摘要事實（<c>sv.summary</c>）。</summary>
    public static HardwareFact Collect(DateTimeOffset at, IReadOnlyList<HardwareFact> roleFacts, string? osName = null)
    {
        var surface = roleFacts.FirstOrDefault(f => f.Key == RoleSurfaceFactsService.SurfaceKey);
        if (surface is null || surface.Availability != FactAvailability.Present)
            return new HardwareFact(SummaryKey, Category, "伺服器合規摘要", "", "", "角色事實聚合",
                FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported,
                surface is null
                    ? "角色盤點尚未讀取（role.surface 不在這一批事實裡）——摘要不憑空生"
                    : $"角色盤點不可得（{surface.UnavailableReason ?? "原因未提供"}）——摘要跟著不可得");

        var standings = Standings(roleFacts);
        var withStopped = standings.Where(s => s.Stopped > 0).OrderByDescending(s => s.Stopped).ToList();
        int installedRoles = surface.NumericValue is double v ? (int)v : 0;

        string detail = withStopped.Count == 0
            ? standings.Count == 0
                ? "沒有任何角色回報服務狀態（可能全是角色服務以外的項目）"
                : $"解析到服務狀態的 {standings.Count} 個角色都全數在跑"
            : $"{withStopped.Count} 個角色的服務沒全跑：" +
              string.Join("、", withStopped.Take(3).Select(s => $"{s.Role}（停 {s.Stopped}）")) +
              (withStopped.Count > 3 ? " 等" : "");
        string sku = string.IsNullOrWhiteSpace(osName) ? "" : $"作業系統：{osName}・";

        return new HardwareFact(SummaryKey, Category, "伺服器合規摘要",
            $"{sku}已安裝角色／功能 {installedRoles} 個・{detail}。" +
            "「裝了」不等於「配置好」：服務沒在跑也可能是刻意停用（安全性收縮、省資源），" +
            "本摘要只陳述計數與明細，不做紅黃判決。",
            "個", "RoleSurfaceFactsService 事實聚合", FactTrustLevel.Derived, false, at, installedRoles);
    }
}
