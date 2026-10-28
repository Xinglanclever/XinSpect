using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 逐域量測預算（U10-MT-0002，第 313 輪）：表格本身是「自述」，最容易腐爛——
/// 改了取樣窗卻沒改文案、由驅動決定的精度卻被寫成一個漂亮的 ±值、或某列少寫了換算式。
/// 這裡逐條釘住那些會讓自述變成假話的形狀。
/// </summary>
public class MeasurementBudgetFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 5, 0, 0, TimeSpan.Zero);

    private static HardwareFact FamilyFact(IEnumerable<HardwareFact> facts, string id)
        => Assert.Single(facts, f => f.Key == MeasurementBudgetFactsService.FamilyPrefix + id);

    [Fact]
    public void 每一列的視窗換算精度都要寫滿()
    {
        foreach (var row in MeasurementBudgetFactsService.Rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Name), $"{row.Id}：沒有名稱");
            Assert.False(string.IsNullOrWhiteSpace(row.Window), $"{row.Id}：沒有寫視窗");
            Assert.False(string.IsNullOrWhiteSpace(row.Formula), $"{row.Id}：沒有寫換算式");
            Assert.False(string.IsNullOrWhiteSpace(row.Precision), $"{row.Id}：沒有寫精度");
            // 「短到看不出東西」與空白一樣沒用：三句話都要有實質長度。
            Assert.True(row.Window.Length >= 8, $"{row.Id}：視窗只寫了「{row.Window}」");
            Assert.True(row.Formula.Length >= 8, $"{row.Id}：換算只寫了「{row.Formula}」");
            Assert.True(row.Precision.Length >= 12, $"{row.Id}：精度只寫了「{row.Precision}」");
        }
    }

    [Fact]
    public void 精度歸屬只能是本機或驅動()
    {
        foreach (var row in MeasurementBudgetFactsService.Rows)
        {
            Assert.True(
                row.PrecisionOwner is MeasurementBudgetFactsService.OwnerSelf or MeasurementBudgetFactsService.OwnerDriver,
                $"{row.Id}：精度歸屬寫了「{row.PrecisionOwner}」——只接受「本機」或「驅動」");
        }
    }

    [Fact]
    public void 鍵是前綴加域代號且只含合法字元()
    {
        var facts = MeasurementBudgetFactsService.Collect(At);
        var idPattern = new Regex(@"^[a-z][a-z0-9._-]*$");

        foreach (var row in MeasurementBudgetFactsService.Rows)
        {
            Assert.Matches(idPattern, row.Id);
            var fact = FamilyFact(facts, row.Id);
            Assert.Equal(MeasurementBudgetFactsService.FamilyPrefix + row.Id, fact.Key);
        }
    }

    [Fact]
    public void 域代號不得重複()
    {
        var ids = MeasurementBudgetFactsService.Rows.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 家族已登記在動態目錄且每一列都落在家族裡()
    {
        Assert.True(FactKeyDynamicCatalog.Matches(MeasurementBudgetFactsService.FamilyPrefix + "cpu.util"));
        foreach (var row in MeasurementBudgetFactsService.Rows)
            Assert.True(FactKeyDynamicCatalog.Matches(MeasurementBudgetFactsService.FamilyPrefix + row.Id),
                $"{row.Id}：家族前綴沒有被動態目錄收下");
    }

    [Fact]
    public void 每一列都是Present而且沒有讀不到的理由()
    {
        var facts = MeasurementBudgetFactsService.Collect(At);
        foreach (var fact in facts)
        {
            Assert.Equal(FactAvailability.Present, fact.Availability);
            Assert.Null(fact.UnavailableReason);
            Assert.False(string.IsNullOrWhiteSpace(fact.Value));
        }
    }

    [Fact]
    public void CPU視窗要寫出程式裡的實際常數而不是背下來的數字()
    {
        // 這條是防「常數改了、文案沒改」：DifferentialWindowMs 從 120 改成別的，這裡會紅。
        var fact = FamilyFact(MeasurementBudgetFactsService.Collect(At), "cpu.util");
        Assert.Contains($"{SystemUtilSampler.DifferentialWindowMs} ms", fact.Value);
    }

    [Fact]
    public void 由驅動決定的域不得寫出誤差上限()
    {
        var facts = MeasurementBudgetFactsService.Collect(At);
        foreach (var row in MeasurementBudgetFactsService.Rows)
        {
            if (row.PrecisionOwner != MeasurementBudgetFactsService.OwnerDriver) continue;
            var value = FamilyFact(facts, row.Id).Value;
            Assert.DoesNotContain("±", value);
            Assert.Contains("由驅動決定", value);
        }
    }

    [Fact]
    public void 摘要要說出本機與驅動各有幾個域且與表相符()
    {
        var facts = MeasurementBudgetFactsService.Collect(At);
        var summary = Assert.Single(facts, f => f.Key == MeasurementBudgetFactsService.SummaryKey);

        int self = MeasurementBudgetFactsService.Rows.Count(r => r.PrecisionOwner == MeasurementBudgetFactsService.OwnerSelf);
        int driver = MeasurementBudgetFactsService.Rows.Count(r => r.PrecisionOwner == MeasurementBudgetFactsService.OwnerDriver);

        Assert.Contains($"{MeasurementBudgetFactsService.Rows.Count} 個即時量測域", summary.Value);
        Assert.Contains($"其中 {self} 個域", summary.Value);
        Assert.Contains($"{driver} 個域的精度由驅動決定", summary.Value);
        Assert.Equal((double)MeasurementBudgetFactsService.Rows.Count, summary.NumericValue);
    }

    [Fact]
    public void 事實總數等於列表數加一筆摘要()
    {
        var facts = MeasurementBudgetFactsService.Collect(At);
        Assert.Equal(MeasurementBudgetFactsService.Rows.Count + 1, facts.Count);
        Assert.Single(facts, f => f.Key == MeasurementBudgetFactsService.SummaryKey);
    }
}
