using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 410–419 項（證據與匯出）的事實服務測試。</summary>
public class EvidenceExportFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PDF_字型嵌入是主要成本只產生不落檔()
    {
        var f = EvidenceExportFactsService.PdfFact(At, () => (true, 12));
        Assert.Contains("中文字型嵌入", f.Value);
        Assert.Contains("只產生內容不落檔", f.Value);
        Assert.Equal(12.0, f.NumericValue);
    }

    [Fact]
    public void 證據包_空包不建構()
    {
        var f = EvidenceExportFactsService.PackFact(At, () => (0, ""));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不建構空包", f.UnavailableReason);
    }

    [Fact]
    public void 證據包_manifest可獨立驗證不上傳()
    {
        var f = EvidenceExportFactsService.PackFact(At, () => (4, "abcdef1234567890"));
        Assert.Contains("manifest 可獨立驗證", f.Value);
        Assert.Contains("不上傳", f.Value);
    }

    [Fact]
    public void 差分_零差異是值()
    {
        var f = EvidenceExportFactsService.DiffFact(At, () => (0, 0, 0));
        Assert.Contains("0 是值", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
    }

    [Fact]
    public void 查詢聚合_加法擴充不破壞舊解析()
    {
        var f = EvidenceExportFactsService.QueryAggFact(At, () => (4, ["count", "avg", "top"]));
        Assert.Contains("不破壞既有解析", f.Value);
        Assert.Contains("導出數", f.Value);
    }

    [Fact]
    public void watch_未啟用不跑()
    {
        var f = EvidenceExportFactsService.WatchFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("預設不跑", f.UnavailableReason);
    }

    [Fact]
    public void watch_是使用者啟動非常駐()
    {
        var f = EvidenceExportFactsService.WatchFact(At, () => (30, 10));
        Assert.Contains("使用者啟動", f.Value);
        Assert.Contains("不是常駐", f.Value);
    }

    [Fact]
    public void 排程_建立經寫入閘門()
    {
        var f = EvidenceExportFactsService.ScheduleFact(At, () => (true, "daily"));
        Assert.Contains("建立經 WriteGate", f.Value);
        Assert.Contains("未經閘門的寫入一律拒絕", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    [Fact]
    public void 機隊_單機不假裝()
    {
        var f = EvidenceExportFactsService.FleetFact(At, () => (1, 10));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("單機不假裝有機隊", f.UnavailableReason);
    }

    [Fact]
    public void 機隊_呈現非排名()
    {
        var f = EvidenceExportFactsService.FleetFact(At, () => (3, 20));
        Assert.Contains("呈現不是排名", f.Value);
    }

    [Fact]
    public void 規則包_版本是公開契約()
    {
        var f = EvidenceExportFactsService.RuleVerFact(At, () => ("1.4", 26));
        Assert.Contains("公開契約", f.Value);
        Assert.Equal(26.0, f.NumericValue);
    }

    [Fact]
    public void CSV_敏感值預設遮蔽()
    {
        var f = EvidenceExportFactsService.CsvFact(At, () => (100, 7));
        Assert.Contains("已遮蔽", f.Value);
        Assert.Contains("只產生內容不落檔", f.Value);
    }

    [Fact]
    public void 儀表板_空配置不建構()
    {
        var f = EvidenceExportFactsService.DashFact(At, () => (0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("空儀表板不建構", f.UnavailableReason);
    }

    [Fact]
    public void 儀表板_不存在的鍵顯示空白非0()
    {
        var f = EvidenceExportFactsService.DashFact(At, () => (8, true));
        Assert.Contains("不存在的鍵會顯示空白而非 0", f.Value);
    }

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = EvidenceExportFactsService.Collect(At);
        string[] keys =
        [
            EvidenceExportFactsService.PdfKey, EvidenceExportFactsService.PackKey, EvidenceExportFactsService.DiffKey,
            EvidenceExportFactsService.QueryAggKey, EvidenceExportFactsService.WatchKey, EvidenceExportFactsService.ScheduleKey,
            EvidenceExportFactsService.FleetKey, EvidenceExportFactsService.RuleVerKey, EvidenceExportFactsService.CsvKey,
            EvidenceExportFactsService.DashKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
