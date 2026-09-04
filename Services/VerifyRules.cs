namespace XinSpect;

/// <summary>
/// 一條驗機規則。<paramref name="RequiredFacts"/> 是它需要的事實；引擎會先檢查齊不齊，
/// 缺就自己判「無法判定」，所以 <paramref name="Evaluate"/> 裡不必再寫任何缺值分支。
/// </summary>
public sealed record VerifyRule(
    string Id,
    string Part,
    string Title,
    FactId[] RequiredFacts,
    Func<VerifyFacts, VerifyFinding> Evaluate);

/// <summary>
/// FactId 的顯示名與權限需求。
/// </summary>
/// <remarks>
/// 缺事實時得說得出「缺的是什麼」以及「怎樣才讀得到」——只顯示一片「—」等於把使用者丟在原地。
/// 名稱不放在 <see cref="VerifyFact.Label"/> 是因為缺少的事實根本沒有實例可以問。
/// </remarks>
public static class FactCatalog
{
    private static readonly Dictionary<FactId, (string Name, bool Admin)> Map = new()
    {
        [FactId.DimmCount] = ("記憶體模組數", false),
        [FactId.DimmManufacturers] = ("各條模組製造商", false),
        [FactId.DimmPartNumbers] = ("各條模組料號", false),
        [FactId.DimmSerials] = ("各條模組序號", false),
        [FactId.DimmSpeedMts] = ("模組標稱速度", false),
        [FactId.DimmConfiguredMts] = ("模組實際運行速度", false),
        [FactId.DimmSizeTotalMiB] = ("已安裝記憶體總量", false),
        [FactId.ArrayMaxCapacityMiB] = ("記憶體陣列宣稱上限", false),
        [FactId.ArraySlotCount] = ("記憶體插槽數", false),
        [FactId.NvmePowerOnHours] = ("NVMe 通電小時", true),
        [FactId.NvmeDataUnitsWritten] = ("NVMe 累計寫入量", true),
        [FactId.NvmePercentageUsed] = ("NVMe 已用壽命", true),
        [FactId.NvmePowerCycles] = ("NVMe 通電次數", true),
        [FactId.NvmeUnsafeShutdowns] = ("NVMe 不安全關機次數", true),
        [FactId.NvmeCriticalWarning] = ("NVMe 關鍵警告", true),
        [FactId.SmartPowerOnHours] = ("SMART 通電小時", true),
        [FactId.SmartHostWritesGiB] = ("SMART 主機寫入量", true),
        [FactId.SmartPowerCycles] = ("SMART 通電次數", true),
        [FactId.AtaRotationRate] = ("標稱旋轉速率", true),
        [FactId.AtaTotalLba] = ("可定址 LBA 總數", true),
        [FactId.AtaAcsVersion] = ("ACS 版本", true),
        [FactId.DiskClaimedCapacityGB] = ("宣稱容量", false),
        [FactId.DiskModel] = ("磁碟型號", false),
        [FactId.BatteryDesignCapacityMWh] = ("電池設計容量", false),
        [FactId.BatteryFullCapacityMWh] = ("電池滿充容量", false),
    };

    public static string Name(FactId id) => Map.TryGetValue(id, out var v) ? v.Name : id.ToString();

    public static bool NeedsAdmin(FactId id) => Map.TryGetValue(id, out var v) && v.Admin;

    /// <summary>目錄是否已涵蓋所有 FactId（由測試守住，新增事實時不得漏登記）。</summary>
    public static bool Covers(FactId id) => Map.ContainsKey(id);
}

/// <summary>
/// 規則引擎。純函式、零硬體相依：把事實袋餵進去，拿到每條規則的判定。
/// </summary>
public static class VerifyEngine
{
    public static IReadOnlyList<VerifyFinding> Run(VerifyFacts facts)
        => VerifyRules.All.Select(r => Evaluate(r, facts)).ToList();

    /// <summary>
    /// 缺依賴就直接判「無法判定」，並列出缺的是哪幾個事實、需不需要管理員權限。
    /// 規則本體因此只剩純比對邏輯。
    /// </summary>
    internal static VerifyFinding Evaluate(VerifyRule rule, VerifyFacts facts)
    {
        var missing = rule.RequiredFacts.Where(id => !facts.Has(id)).ToArray();
        if (missing.Length == 0) return rule.Evaluate(facts);

        bool admin = missing.Any(FactCatalog.NeedsAdmin);
        string why = "無法判定：缺 " + string.Join("、", missing.Select(FactCatalog.Name))
                   + (admin ? "（需要管理員權限才讀得到）" : "");
        return new(rule.Id, rule.Part, rule.Title, VerifyVerdict.Unread, Severity.Neutral, why, null, []);
    }
}

/// <summary>
/// 規則表。一條規則一列，由上而下全部都跑（規則之間互不遮蔽）。
/// </summary>
/// <remarks>
/// 判定只有三種：相符／矛盾／無法判定。**不給分數、不給「正品」結論**——
/// 綠勾會讓使用者停止思考，而賣家可能有正當理由。工具把矛盾指出來，判斷留給人。
/// </remarks>
public static class VerifyRules
{
    public const string PartMemory = "記憶體";

    private const string T01 = "各條記憶體模組並非同批";

    public static readonly VerifyRule[] All =
    [
        new("R-MEM-01", PartMemory, T01,
            [FactId.DimmCount, FactId.DimmManufacturers, FactId.DimmPartNumbers], MixedModules),
    ];

    /// <summary>逐條模組的字串以 <c>|</c> 相連（collector 產出的形式）。</summary>
    internal static string[] Split(string? s) =>
        (s ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static VerifyFinding MixedModules(VerifyFacts f)
    {
        var evidence = new[] { f.Get(FactId.DimmManufacturers)!, f.Get(FactId.DimmPartNumbers)! };

        if ((f.Num(FactId.DimmCount) ?? 0) < 2)
            return new("R-MEM-01", PartMemory, T01, VerifyVerdict.Match, Severity.Good,
                "只有一條模組，無從混批。", null, evidence);

        bool mixed = Split(f.Text(FactId.DimmManufacturers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                  || Split(f.Text(FactId.DimmPartNumbers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        return mixed
            ? new("R-MEM-01", PartMemory, T01, VerifyVerdict.Conflict, Severity.Warning,
                "製造商或料號不一致，這幾條不是同一批出廠的模組。",
                "使用者自己後來加裝的模組也會這樣；混批不影響保固，但可能造成時序回退到較慢的那一條。",
                evidence)
            : new("R-MEM-01", PartMemory, T01, VerifyVerdict.Match, Severity.Good,
                "所有模組的製造商與料號一致。", null, evidence);
    }
}
