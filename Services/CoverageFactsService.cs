namespace XinSpect;

/// <summary>
/// 覆蓋統計（Vol2 NG-006）：把「事實鍵對帳覆蓋」的申報數字收成事實，讓 CLI／快照／報告
/// 也能回答「現在有幾個鍵、幾個被規則碰到、幾個未覆蓋」——現在只有設定頁看得到。
/// </summary>
/// <remarks>
/// <b>輸入不含自己</b>：與 <c>cap.*</c>／<c>qs.range</c> 同款，彙總吃的是「前面各組事實的聯集」，
/// 不含彙總自身產出的鍵——否則就會出現「覆蓋率取決於覆蓋率」的自我指涉。
/// <b>未覆蓋不是錯誤</b>：強制全覆蓋會逼出湊數的規則，這裡只把數字攤開。
/// </remarks>
public static class CoverageFactsService
{
    private const string Category = "診斷導航";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyCollection<string> factKeys,
        IReadOnlyList<(string Id, IReadOnlyList<string> Inputs)> rules)
    {
        var summary = FactCoverageReport.Report(factKeys, rules);

        return
        [
            new HardwareFact("ng.coverage.keys", Category, "事實鍵總數",
                summary.Total.ToString(System.Globalization.CultureInfo.InvariantCulture), "個",
                "事實鍵目錄∪執行期鍵", FactTrustLevel.Derived, false, at, summary.Total),
            new HardwareFact("ng.coverage.covered", Category, "有規則考慮的鍵數",
                summary.Covered.ToString(System.Globalization.CultureInfo.InvariantCulture), "個",
                $"{summary.RuleCount} 條對帳規則", FactTrustLevel.Derived, false, at, summary.Covered),
            new HardwareFact("ng.coverage.exempt", Category, "明文豁免的鍵數",
                summary.Exempt.ToString(System.Globalization.CultureInfo.InvariantCulture), "個",
                "單一來源類事實（每條豁免都附理由）", FactTrustLevel.Derived, false, at, summary.Exempt),
            new HardwareFact("ng.coverage.uncovered", Category, "未覆蓋的鍵數",
                summary.Uncovered.ToString(System.Globalization.CultureInfo.InvariantCulture), "個",
                "未被任何規則碰到也未豁免——不是錯誤，但應該被看見", FactTrustLevel.Derived, false, at, summary.Uncovered),
            new HardwareFact("ng.coverage.headline", Category, "覆蓋申報",
                summary.UncoveredKeys.Count > 0
                    ? summary.Headline + " 未覆蓋前幾個：" + string.Join("、", summary.UncoveredKeys.Take(10))
                    : summary.Headline,
                "", "FactCoverageReport（掃描期目錄∪執行期鍵）", FactTrustLevel.Derived, false, at),
        ];
    }
}
