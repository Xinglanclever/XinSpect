using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 收集步驟的讀取代價（U10 第三圈 MT-0004，第 311 輪）。
///
/// <para>
/// 這一組最重要的那一條是「逐鍵代價未量要寫出來」：步驟代價看起來很精確（毫秒），
/// 很容易被當成「每一把鍵都這個速度」。不把界線寫在資料裡，遲早有人拿它去算別的東西。
/// </para>
/// </summary>
public sealed class MeasurementCostFactsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-29T12:00:00+00:00");

    private static EvidenceLabService.CollectionStep Step(string name, double ms, bool ok = true)
        => new(name, ms, ok);

    private static HardwareFact Summary(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == MeasurementCostFactsService.SummaryKey);

    [Fact]
    public void 沒有步驟時要說沒有記錄而不是很快()
    {
        var facts = MeasurementCostFactsService.Collect([], At);
        var summary = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, summary.Availability);
        Assert.Contains("沒有記錄到任何收集步驟", summary.UnavailableReason);
    }

    [Fact]
    public void 總計是各步驟相加而且步數要對()
    {
        var facts = MeasurementCostFactsService.Collect([Step("A", 10), Step("B", 25.5)], At);
        var summary = Summary(facts);
        Assert.Contains("2 個收集步驟", summary.Value);
        Assert.Contains("總計 35.5 毫秒", summary.Value);
        Assert.Equal(35.5, summary.NumericValue);
    }

    [Fact]
    public void 明細依耗時由大到小且鍵從0001開始()
    {
        var facts = MeasurementCostFactsService.Collect([Step("快", 1), Step("慢", 100), Step("中", 20)], At);
        var steps = facts.Where(f => f.Key.StartsWith(MeasurementCostFactsService.StepPrefix, StringComparison.Ordinal)).ToList();

        Assert.Equal(3, steps.Count);
        Assert.EndsWith("：慢", steps[0].Name);
        Assert.Equal(MeasurementCostFactsService.StepPrefix + "0001", steps[0].Key);
        Assert.EndsWith("：中", steps[1].Name);
        Assert.EndsWith("：快", steps[2].Name);
    }

    [Fact]
    public void 失敗的步驟也要記時間並標出來()
    {
        var facts = MeasurementCostFactsService.Collect([Step("壞掉的", 800, ok: false)], At);

        var step = Assert.Single(facts, f => f.Key.StartsWith(MeasurementCostFactsService.StepPrefix, StringComparison.Ordinal));
        Assert.Contains("800.0 毫秒", step.Value);
        Assert.Contains("失敗：時間照記", step.Value);
        // 六態契約：讀到時間就是 Present，不得再帶「讀不到的原因」。
        Assert.Equal(FactAvailability.Present, step.Availability);
        Assert.True(string.IsNullOrEmpty(step.UnavailableReason));

        // 失敗也要算進總時間：讀了 800 毫秒才失敗，時間並沒有消失。
        Assert.Contains("總計 800.0 毫秒", Summary(facts).Value);
        Assert.Contains("1 個步驟失敗", Summary(facts).Value);
    }

    [Fact]
    public void 逐鍵代價未量要寫在資料裡()
    {
        var facts = MeasurementCostFactsService.Collect([Step("A", 10)], At);
        Assert.Contains("逐鍵代價未量", Summary(facts).Value);
    }

    [Fact]
    public void 超過上限要說出還有幾個步驟沒列()
    {
        var steps = Enumerable.Range(1, 5).Select(i => Step("S" + i, i * 10)).ToList();
        var facts = MeasurementCostFactsService.Collect(steps, At, maxStepFacts: 2);

        Assert.Equal(2, facts.Count(f => f.Key.StartsWith(MeasurementCostFactsService.StepPrefix, StringComparison.Ordinal)));
        Assert.Contains("另有 3 個未列出", Summary(facts).Value);
    }

    [Fact]
    public void 同名步驟重複出現時兩次都要留()
    {
        // LoadMultimedia 在收集鏈上出現兩次；折疊會讓總時間與步數對不上。
        var facts = MeasurementCostFactsService.Collect([Step("重複", 5), Step("重複", 7)], At);
        Assert.Equal(2, facts.Count(f => f.Key.StartsWith(MeasurementCostFactsService.StepPrefix, StringComparison.Ordinal)));
        Assert.Contains("2 個收集步驟", Summary(facts).Value);
    }

    [Fact]
    public void 家族與靜態鍵都登記了()
    {
        Assert.True(FactKeyDynamicCatalog.Matches(MeasurementCostFactsService.StepPrefix + "0001"));
        Assert.Contains(MeasurementCostFactsService.SummaryKey, FactKeyCatalog.Keys);
    }
}
