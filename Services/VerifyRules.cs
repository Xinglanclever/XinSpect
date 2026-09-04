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
    private const string T02 = "記憶體模組序號異常";
    private const string T03 = "記憶體未跑在標稱速度";
    private const string T04 = "記憶體陣列宣稱與實際安裝對不上";

    public static readonly VerifyRule[] All =
    [
        new("R-MEM-01", PartMemory, T01,
            [FactId.DimmCount, FactId.DimmManufacturers, FactId.DimmPartNumbers], MixedModules),
        new("R-MEM-02", PartMemory, T02,
            [FactId.DimmCount, FactId.DimmSerials], BadSerials),
        new("R-MEM-03", PartMemory, T03,
            [FactId.DimmSpeedMts, FactId.DimmConfiguredMts], UnderclockedMemory),
        new("R-MEM-04", PartMemory, T04,
            [FactId.DimmSizeTotalMiB, FactId.ArrayMaxCapacityMiB, FactId.ArraySlotCount, FactId.DimmCount],
            ArrayMismatch),
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

    /// <summary>全 0 或全 F 的序號：韌體沒燒序號，或序號被抹掉。</summary>
    private static bool IsBogusSerial(string s) =>
        s.Length > 0 && (s.All(c => c == '0') || s.All(c => c is 'F' or 'f'));

    private static VerifyFinding BadSerials(VerifyFacts f)
    {
        var ev = new[] { f.Get(FactId.DimmSerials)! };
        var serials = Split(f.Text(FactId.DimmSerials));

        // 重複序號在物理上不該出現（序號是模組廠逐條燒的），故比全 0／全 F 更嚴重。
        bool dup = serials.Length > 1 &&
                   serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length;
        if (dup)
            return new("R-MEM-02", PartMemory, T02, VerifyVerdict.Conflict, Severity.Serious,
                "兩條以上模組回報同一組序號。序號是模組廠逐條燒進 SPD 的，正常不會重複。",
                "同一顆晶片廠的模組被不同品牌貼牌時偶有序號規則衝突；但完全相同的序號更常見於仿冒模組。",
                ev);

        if (serials.Any(IsBogusSerial))
            return new("R-MEM-02", PartMemory, T02, VerifyVerdict.Conflict, Severity.Warning,
                "有模組的序號是全 0 或全 F——SPD 裡沒有燒序號，或序號被抹掉了。",
                "少數白牌與工業用模組出廠就不燒序號；但翻新與貼牌模組也是這個樣子。", ev);

        return new("R-MEM-02", PartMemory, T02, VerifyVerdict.Match, Severity.Good,
            "各條模組序號互異，且不是全 0／全 F。", null, ev);
    }

    private static VerifyFinding UnderclockedMemory(VerifyFacts f)
    {
        double rated = f.Num(FactId.DimmSpeedMts)!.Value;
        double cur = f.Num(FactId.DimmConfiguredMts)!.Value;
        var ev = new[] { f.Get(FactId.DimmSpeedMts)!, f.Get(FactId.DimmConfiguredMts)! };

        return cur < rated
            ? new("R-MEM-03", PartMemory, T03, VerifyVerdict.Conflict, Severity.Warning,
                $"模組標稱 {rated:0} MT/s，實際只跑 {cur:0} MT/s。",
                "多數情況是主機板沒有啟用 XMP／EXPO，或處理器的記憶體控制器上限較低——"
                + "這不是模組本身的問題，進 BIOS 開啟設定檔即可。", ev)
            : new("R-MEM-03", PartMemory, T03, VerifyVerdict.Match, Severity.Good,
                "實際運行速度已達標稱值。", null, ev);
    }

    private static VerifyFinding ArrayMismatch(VerifyFacts f)
    {
        double total = f.Num(FactId.DimmSizeTotalMiB)!.Value;
        double max = f.Num(FactId.ArrayMaxCapacityMiB)!.Value;
        double slots = f.Num(FactId.ArraySlotCount)!.Value;
        double dimms = f.Num(FactId.DimmCount)!.Value;
        var ev = new[]
        {
            f.Get(FactId.DimmSizeTotalMiB)!, f.Get(FactId.ArrayMaxCapacityMiB)!,
            f.Get(FactId.ArraySlotCount)!, f.Get(FactId.DimmCount)!,
        };

        if (total > max)
            return new("R-MEM-04", PartMemory, T04, VerifyVerdict.Conflict, Severity.Warning,
                $"已安裝 {total:0} MiB，但記憶體陣列宣稱上限只有 {max:0} MiB。",
                "部分主機板的 SMBIOS 把陣列上限寫錯，這種情況機器照樣正常運作。", ev);

        if (dimms > slots)
            return new("R-MEM-04", PartMemory, T04, VerifyVerdict.Conflict, Severity.Warning,
                $"回報 {dimms:0} 條模組，但陣列只宣告 {slots:0} 個插槽。",
                "SMBIOS 表寫錯也會這樣；但也可能是其中一條沒有被正確列舉。", ev);

        return new("R-MEM-04", PartMemory, T04, VerifyVerdict.Match, Severity.Good,
            "安裝總量與模組數都在陣列宣告的範圍內。", null, ev);
    }
}
