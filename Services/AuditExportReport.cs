using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 狀態變更審計、審計匯出、三方對帳（ULTIMATE-1000 階段一 P1-075、P1-076、P1-083）。
///
/// <para>
/// <b>P1-075 與 P1-069 的差別：</b>P1-069 管「審計鏈本身可不可信」，
/// P1-075 管「<b>什麼事情必須進鏈</b>」。這兩件事很容易混為一談，
/// 而混為一談的後果是：鏈很完美，但該記的事沒記。
/// </para>
///
/// <para>
/// <b>P1-076 為什麼要獨立一把鍵：</b>「我驗過了」與「你能自己驗」是兩件事。
/// 可交第三方驗證意味著驗證程序<b>不依賴本程式</b>——否則那份匯出只是本程式的自述。
/// </para>
/// </summary>
public static class AuditExportReport
{
    public const string StateChangeKey = "audit.state_change";
    public const string ExportKey = "audit.export";
    public const string ThreeWayKey = "coverage.threeway.diff";

    private const string Category = "寫入與審計";
    private const string Source = "寫入閘門／審計鏈";

    // ── P1-075 狀態變更審計 ────────────────────────────────────────────

    /// <summary>
    /// 必須進審計鏈的狀態變更種類。<b>沒登記在這裡的變更不會被要求入鏈</b>——
    /// 所以清單短而明確比長而模糊好：列太多的下場是每一條都沒人認真看。
    /// </summary>
    public static IReadOnlyList<string> AuditableChanges { get; } =
    [
        "知識包換檔", "快照匯出", "原始傾印寫入", "驅動憑證佈署", "設定變更",
    ];

    /// <summary>
    /// 產生 <c>audit.state_change</c>：哪些變更進了鏈、哪些沒有。
    /// <b>沒有進鏈的變更要指名</b>——「有幾筆沒記錄」不足以讓人去修。
    /// </summary>
    public static HardwareFact ToStateChangeFact(DateTimeOffset at,
        IReadOnlyList<(string Change, bool Recorded)> changes)
    {
        if (changes.Count == 0)
            return new HardwareFact(StateChangeKey, Category, "狀態變更審計",
                "本輪沒有狀態變更", "次", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有變更可以稽核");

        var missing = changes.Where(c => !c.Recorded).ToList();
        if (missing.Count == 0)
            return new HardwareFact(StateChangeKey, Category, "狀態變更審計",
                $"{changes.Count} 次狀態變更全部入鏈", "次", Source,
                FactTrustLevel.Derived, false, at, changes.Count, FactAvailability.Present, null);

        return new HardwareFact(StateChangeKey, Category, "狀態變更審計",
            $"{missing.Count}/{changes.Count} 次變更沒有入鏈：" +
            string.Join("、", missing.Select(m => m.Change)), "次", Source,
            FactTrustLevel.Derived, false, at, changes.Count - missing.Count,
            FactAvailability.ReadError,
            $"{missing.Count} 次狀態變更沒有留下紀錄——鏈再完美，該記的事沒記就等於沒發生");

    }

    /// <summary>某一次變更是否屬於「必須入鏈」的種類。</summary>
    public static bool IsAuditable(string change) =>
        AuditableChanges.Contains(change, StringComparer.Ordinal);

    // ── P1-076 審計匯出 ────────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>audit.export</c>：匯出檔能不能交第三方驗證。
    ///
    /// <para>
    /// <b>「可獨立驗證」的判準是「驗證程序不依賴本程式」</b>——
    /// 只含雜湊與欄位定義的純文字即可；要求對方跑本程式才能驗，等於沒有匯出。
    /// </para>
    /// </summary>
    public static HardwareFact ToExportFact(DateTimeOffset at,
        int exportedEntries, bool selfContained, bool includesAlgorithm)
    {
        if (exportedEntries == 0)
            return new HardwareFact(ExportKey, Category, "審計鏈匯出",
                "這一輪沒有匯出", "筆", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有匯出可以檢查");

        if (!selfContained)
            return new HardwareFact(ExportKey, Category, "審計鏈匯出",
                $"{exportedEntries} 筆，但匯出檔不完整", "筆", Source,
                FactTrustLevel.Derived, false, at, 0, FactAvailability.ReadError,
                "匯出檔必須自足——需要拿本程式才能驗的匯出，等於本程式的自述");

        if (!includesAlgorithm)
            return new HardwareFact(ExportKey, Category, "審計鏈匯出",
                $"{exportedEntries} 筆，但沒有寫出雜湊演算法", "筆", Source,
                FactTrustLevel.Derived, false, at, exportedEntries, FactAvailability.Unknown,
                "沒寫出演算法，第三方只能照猜——猜錯的驗證結果比不驗更糟");

        return new HardwareFact(ExportKey, Category, "審計鏈匯出",
            $"{exportedEntries} 筆，含欄位定義與雜湊演算法，可交第三方獨立驗證", "筆", Source,
            FactTrustLevel.Derived, false, at, exportedEntries, FactAvailability.Present, null);
    }

    // ── P1-083 三方對帳 ────────────────────────────────────────────────

    /// <summary>
    /// 產生 <c>coverage.threeway.diff</c>：目錄／掃描／矩陣<b>三方</b>的差集。
    ///
    /// <para>
    /// <b>為什麼要三方而不是兩方：</b>兩方對帳只能回答「這兩者一致嗎」。
    /// 三方可以回答<b>「哪一方是漏的那一方」</b>——
    /// 目錄有而掃描沒有＝可能有沒接線的鍵；
    /// 矩陣有而目錄沒有＝規格宣告了但沒實作；
    /// 矩陣與目錄都有而掃描沒有＝生產者的寫法掃不到。
    /// 這三種的<b>處理方式完全不同</b>。
    /// </para>
    /// </summary>
    public static HardwareFact ToThreeWayFact(DateTimeOffset at,
        IReadOnlyCollection<string> catalog, IReadOnlyCollection<string> scanned,
        IReadOnlyCollection<string> matrix)
    {
        if (catalog.Count == 0 || scanned.Count == 0)
            return new HardwareFact(ThreeWayKey, Category, "三方對帳",
                "缺少可比較的兩方", "鍵", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "目錄與掃描至少要有一方有內容");

        var catalogOnly = catalog.Except(scanned).ToList();
        var matrixOnly = matrix.Except(catalog).ToList();
        var matrixAndCatalogOnly = matrix.Intersect(catalog).Except(scanned).ToList();

        string detail = $"目錄 {catalog.Count}／掃描 {scanned.Count}／矩陣 {matrix.Count}；"
            + $"目錄獨有 {catalogOnly.Count}、矩陣獨有 {matrixOnly.Count}、"
            + $"矩陣與目錄共有而掃描缺 {matrixAndCatalogOnly.Count}";

        if (matrixOnly.Count > 0)
            return new HardwareFact(ThreeWayKey, Category, "三方對帳", detail, "鍵", Source,
                FactTrustLevel.Derived, false, at, catalogOnly.Count, FactAvailability.ReadError,
                $"矩陣宣告了 {matrixOnly.Count} 把目錄沒有的鍵——規格有而實作沒有："
                + string.Join("、", matrixOnly.Take(8)));

        if (matrixAndCatalogOnly.Count > 0)
            return new HardwareFact(ThreeWayKey, Category, "三方對帳", detail, "鍵", Source,
                FactTrustLevel.Derived, false, at, catalogOnly.Count, FactAvailability.Unknown,
                $"{matrixAndCatalogOnly.Count} 把鍵在矩陣與目錄都有、但原始碼掃不到——"
                + "可能是生產者的寫法掃不到（掃描器要擴充），也可能是真的沒接線："
                + string.Join("、", matrixAndCatalogOnly.Take(8)));

        if (catalogOnly.Count > 0)
            return new HardwareFact(ThreeWayKey, Category, "三方對帳", detail, "鍵", Source,
                FactTrustLevel.Derived, false, at, catalogOnly.Count, FactAvailability.Unknown,
                $"目錄有 {catalogOnly.Count} 把掃描沒有的鍵——既有目錄守門的範圍");

        return new HardwareFact(ThreeWayKey, Category, "三方對帳", detail, "鍵", Source,
            FactTrustLevel.Derived, false, at, 0, FactAvailability.Present, null);
    }
}
