using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 鍵目錄的分母宣告與命名規範（ULTIMATE-1000 階段一 P1-077 – P1-084）。
///
/// <para>
/// <b>這一組處理的是「目錄自己可不可信」：</b>既有的目錄守門會在編譯期掃原始碼比對鍵，
/// 但<b>發佈版沒有原始碼可以掃</b>——那裡的目錄只能靠自己的一致性站住。
/// 而「自己的一致性」需要三件事說得清楚：分母是什麼、動態家族由誰決定、豁免為什麼豁免。
/// </para>
///
/// <para>
/// <b>為什麼分母要成文：</b>「覆蓋率 87%」這句話在不知道分母是什麼的情況下毫無意義——
/// 分母是「全部的事實鍵」還是「扣掉動態家族後的鍵」會給出完全不同的數字。
/// 百分比之所以經常騙人，通常不是分子錯了，是分母沒有被說出來。
/// </para>
/// </summary>
public static class KeyCatalogPolicy
{
    public const string CountKey = "key.count";
    public const string FamilyKey = "dyn.family";
    public const string DenominatorKey = "coverage.denominator";
    public const string ExemptKey = "coverage.exempt";

    private const string Category = "鍵目錄";
    private const string Source = "鍵目錄政策";

    /// <summary>
    /// 全鍵的命名規範。
    /// <b>這條規則與動態家族的存在是同一件事的兩面：</b>
    /// 因為目錄要能編譯期比對，成員不確定的鍵就必須走家族——
    /// 所以「不符合格式」不是疏漏，是<b>訊號</b>：它在說「這個鍵的成員我列不完」。
    /// </summary>
    public static readonly Regex KeyPattern =
        new(@"^[a-z][a-z0-9]*(\.[a-z0-9_]+)+$", RegexOptions.Compiled);

    /// <summary>合法前綴的來源：靜態目錄裡所有鍵的第一段。</summary>
    public static IReadOnlySet<string> LegalFirstSegments() =>
        FactKeyCatalog.Keys
            .Select(k => k.Split('.')[0])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 檢查一個鍵是否符合命名規範。<c>null</c>＝合規。
    /// 回傳的字串會說明<b>為什麼</b>不合規而不只是「不合規」。
    /// </summary>
    public static string? CheckKeyName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "空鍵";
        if (FactKeyDynamicCatalog.Matches(key)) return null;   // 家族成員自成一套規則
        if (!KeyPattern.IsMatch(key))
            return $"「{key}」不符全鍵格式（須為小寫字母數字、以點分段）——"
                 + "若成員不確定，應登記動態家族而不是放寬格式";
        return null;
    }

    /// <summary>產生 <c>key.count</c>：目錄的鍵數與家族數。</summary>
    public static HardwareFact ToCountFact(DateTimeOffset at, int catalogKeys, int familyCount)
    {
        if (catalogKeys == 0)
            return new HardwareFact(CountKey, Category, "鍵目錄全集的鍵數",
                "目錄為空", "鍵", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.ReadError, "空的目錄不能證明任何事");

        return new HardwareFact(CountKey, Category, "鍵目錄全集的鍵數",
            $"{catalogKeys} 個靜態鍵 ＋ {familyCount} 個動態家族", "鍵", Source,
            FactTrustLevel.Derived, false, at, catalogKeys, FactAvailability.Present,
            "發佈版沒有原始碼可以掃，目錄的全集只能靠自己的一致性站住");
    }

    /// <summary>
    /// 產生 <c>dyn.family</c>：逐家族列出「成員由什麼決定」。
    /// <b>沒寫出決定的家族要指出來</b>——沒有理由的家族與漏登記的鍵無法區分。
    /// </summary>
    public static HardwareFact ToFamilyFact(DateTimeOffset at)
    {
        var families = FactKeyDynamicCatalog.Families;
        if (families.Count == 0)
            return new HardwareFact(FamilyKey, Category, "動態家族",
                "沒有登記任何動態家族", "個", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有家族可以申報");

        var unexplained = families
            .Where(kv => !ExplainsMembership(kv.Value))
            .Select(kv => kv.Key).ToList();

        if (unexplained.Count > 0)
            return new HardwareFact(FamilyKey, Category, "動態家族",
                $"{families.Count} 個家族，其中 {unexplained.Count} 個沒寫出成員由什麼決定："
                + string.Join("、", unexplained), "個", Source, FactTrustLevel.Derived, false, at,
                families.Count - unexplained.Count, FactAvailability.ReadError,
                "沒寫出『成員由什麼決定』的家族，與漏登記的鍵無法區分——家族需要理由才成立");

        return new HardwareFact(FamilyKey, Category, "動態家族",
            $"{families.Count} 個家族全部載明成員由什麼決定", "個", Source,
            FactTrustLevel.Derived, false, at, families.Count, FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>coverage.denominator</c>：分母的定義、數值，以及<b>為什麼這樣定義</b>。
    /// </summary>
    public static HardwareFact ToDenominatorFact(DateTimeOffset at, int staticKeys, int dynamicFamilies,
        string definition, string rationale)
    {
        if (string.IsNullOrWhiteSpace(definition) || string.IsNullOrWhiteSpace(rationale))
            return new HardwareFact(DenominatorKey, Category, "覆蓋分母",
                $"分母 {staticKeys} 個靜態鍵", "鍵", Source, FactTrustLevel.Derived, false, at,
                staticKeys, FactAvailability.ReadError,
                "分母沒有成文——百分比之所以騙人通常不是分子錯了，是分母沒有被說出來");

        return new HardwareFact(DenominatorKey, Category, "覆蓋分母",
            $"{definition}：{staticKeys} 個靜態鍵（另有 {dynamicFamilies} 個動態家族不進分母）",
            "鍵", Source, FactTrustLevel.Derived, false, at, staticKeys,
            FactAvailability.Present,
            $"分母的定義與理由都成文：{rationale}");
    }

    /// <summary>
    /// 產生 <c>exempt</c>：豁免的門檻檢查。
    ///
    /// <para>
    /// <b>豁免只准用一個理由：「無法交叉」。</b>
    /// 其他形式的理由——「還沒寫」「不重要」「先放著」——都不是豁免，
    /// 是<b>待辦</b>。把待辦登記成豁免，就是讓申報比實際乾淨。
    /// </para>
    /// </summary>
    /// <summary>
    /// 豁免的正當性類別（P1-082）。
    ///
    /// <para>
    /// <b>這是有限的列舉，不是關鍵詞清單——這一點是刻意的。</b>
    /// 第一版用「理由文字含什麼詞」判斷豁免是否正當，結果 584 條真實豁免裡
    /// 有 507 條被判不合格；而當時的處置是<b>連續擴大詞表</b>，
    /// 那正是本計畫 P1-063 警告的事：用擴大詞表讓紅燈變綠，只是形式上看起來像修正。
    /// </para>
    ///
    /// <para>
    /// 關鍵詞比對必然走向兩條路之一：<b>詞表窄則大量誤報</b>
    /// （而誤報會導致守門被放寬，放寬過的守門比沒有更糟），<b>詞表寬則形同虛設</b>。
    /// 所以改成要求申報者<b>指名一個類別</b>——類別有限，判斷就不會漂移。
    /// </para>
    ///
    /// <para>
    /// <b>為什麼這樣能擋掉待辦：</b>「還沒寫對帳規則」選不出任何一個合法類別。
    /// 待辦不是「另一種豁免」，是<b>還沒做的事</b>。
    /// </para>
    /// </summary>
    public enum ExemptionKind
    {
        /// <summary>那項事實只有一個來源——沒有第二份資料可以交叉。</summary>
        SingleSource,
        /// <summary>那不是量測值（是政策宣告、契約、呈現、導覽、模型），沒有可對的對象。</summary>
        NotMeasurement,
        /// <summary>刻意不取得第二份來源（不出網、不讀內容、不列名稱、不開啟裝置）。</summary>
        SourceWithheld,
        /// <summary>來源本身就是這份申報——自己對自己交叉沒有意義（刻意無遞迴）。</summary>
        SelfReferential,
        /// <summary>本機沒有那個硬體或環境，無從量測。</summary>
        NotApplicable,
    }

    /// <summary>類別的中文名——申報時要印出來，讓讀者不必查列舉。</summary>
    public static string Describe(ExemptionKind kind) => kind switch
    {
        ExemptionKind.SingleSource => "單一來源",
        ExemptionKind.NotMeasurement => "非量測值",
        ExemptionKind.SourceWithheld => "刻意不取得",
        ExemptionKind.SelfReferential => "自我指涉",
        _ => "本機不適用",
    };

    /// <summary>
    /// 一條豁免的申報。<b>類別是機器判的，理由是給人看的</b>——兩者都要有。
    /// </summary>
    /// <param name="Kind">正當性類別（列舉，有限）。</param>
    /// <param name="Reason">為什麼是這個類別（給人讀，不得空白）。</param>
    public sealed record ExemptionEntry(ExemptionKind Kind, string Reason)
    {
        public bool IsComplete => !string.IsNullOrWhiteSpace(Reason) && Reason.Trim().Length >= 10;
    }

    public static HardwareFact ToExemptFact(DateTimeOffset at,
        IReadOnlyDictionary<string, ExemptionEntry> exemptions, int cap, int pendingCount = 0)
    {
        if (pendingCount > 0)
            return new HardwareFact(ExemptKey, Category, "豁免理由",
                $"{pendingCount} 條登記為豁免但其實是待辦", "條", Source,
                FactTrustLevel.Derived, false, at, exemptions.Count,
                FactAvailability.ReadError,
                "把待辦登記成豁免，就是讓申報比實際乾淨——待辦選不出任何一個合法的豁免類別");

        if (exemptions.Count == 0)
            return new HardwareFact(ExemptKey, Category, "豁免理由",
                "沒有任何豁免", "條", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有豁免可以檢查");

        var incomplete = exemptions.Where(kv => !kv.Value.IsComplete).Select(kv => kv.Key).ToList();
        if (incomplete.Count > 0)
            return new HardwareFact(ExemptKey, Category, "豁免理由",
                $"{incomplete.Count} 條豁免沒有寫出理由：" + string.Join("、", incomplete.Take(5)),
                "條", Source, FactTrustLevel.Derived, false, at,
                exemptions.Count - incomplete.Count, FactAvailability.ReadError,
                "類別要機器判，理由要給人看——缺理由的豁免與沒有豁免無異");

        var byKind = exemptions.GroupBy(kv => kv.Value.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        string detail = $"{exemptions.Count} 條豁免（"
            + string.Join("、", byKind.OrderBy(kv => kv.Key)
                .Select(kv => $"{Describe(kv.Key)} {kv.Value}")) + "）";

        if (exemptions.Count > cap)
            return new HardwareFact(ExemptKey, Category, "豁免理由", detail, "條", Source,
                FactTrustLevel.Derived, false, at, exemptions.Count, FactAvailability.Unknown,
                $"已達上限 {cap}——先檢視這些豁免是不是該處理了，而不是提高上限");

        return new HardwareFact(ExemptKey, Category, "豁免理由", detail, "條", Source,
            FactTrustLevel.Derived, false, at, exemptions.Count, FactAvailability.Present, null);
    }

    /// <summary>找出登記了卻沒有任何鍵符合的前綴——<b>殭屍前綴</b>。</summary>
    public static IReadOnlyList<string> ZombiePrefixes(
        IEnumerable<string> prefixes, IEnumerable<string> knownKeys, IEnumerable<string> scannedKeys)
    {
        var known = knownKeys.Concat(scannedKeys).ToList();
        return prefixes
            .Where(p => !known.Any(k => k.StartsWith(p, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// 家族的理由有沒有說出「成員由什麼決定」。
    /// <b>不要求特定用詞</b>——只要說明了成員是機器的事實而不是編譯期常數即可。
    /// 要求含「由」這個字會把「埠號是平台事實，不是編譯期常量」判成不合格，
    /// 而那一句正是最標準的家族理由。
    /// </summary>
    private static bool ExplainsMembership(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        string[] markers =
        [
            "由", "成員", "決定", "枚舉", "探測", "掃描", "列舉", "事實", "常數", "常量",
            "數量", "任意", "個數", "逐", "來自", "取決", "不一定", "可能有", "不確定",
            "機器", "環境", "安裝", "韌體", "規範", "編譯期",
        ];
        return markers.Any(m => reason.Contains(m, StringComparison.Ordinal));
    }
}
