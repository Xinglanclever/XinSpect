using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 420–429 項（匯出收尾與使用體驗）的事實服務測試。</summary>
public class ExportUxFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void 匯出驗證_唯讀不修改bundle()
    {
        var f = ExportUxFactsService.VerifyFact(At, () => (true, null));
        Assert.Contains("唯讀檢查", f.Value);
        Assert.Contains("不代表內容正確", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    [Fact]
    public void 匯出驗證_未通過標Unknown()
    {
        var f = ExportUxFactsService.VerifyFact(At, () => (false, "缺少 manifest"));
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.NotNull(f.UnavailableReason);
    }

    [Fact]
    public void 審計鏈_與EvidenceTimeline並存不重疊()
    {
        var f = ExportUxFactsService.AuditTimelineFact(At, () => (50, true));
        Assert.Contains("並存不重疊", f.Value);
        Assert.Contains("不互相取代", f.Value);
    }

    [Fact]
    public void schema遷移_不可逆要聲明()
    {
        var f = ExportUxFactsService.MigrateFact(At, () => (1, 2, false));
        Assert.Contains("升級不可逆要聲明", f.Value);
        Assert.Contains("不假裝升級無痛", f.Value);
    }

    [Fact]
    public void 命名查詢_寫入經閘門()
    {
        var f = ExportUxFactsService.NamedQueryFact(At, () => (5, true));
        Assert.Contains("使用者資產", f.Value);
        Assert.Contains("WriteGate", f.Value);
    }

    [Fact]
    public void 浮水印_有則列文字()
    {
        var f = ExportUxFactsService.WatermarkFact(At, () => (true, "機密"));
        Assert.Contains("機密", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    [Fact]
    public void 多語報告_覆蓋率如實不假裝全翻譯()
    {
        var f = ExportUxFactsService.I18nReportFact(At, () => (3, 82.5));
        Assert.Contains("回退原文", f.Value);
        Assert.Contains("不假裝全翻譯", f.Value);
        Assert.Equal(82.5, f.NumericValue);
    }

    [Fact]
    public void xinsig_spec公開且完整非正確()
    {
        var f = ExportUxFactsService.XinSigFact(At, () => (true, true));
        Assert.Contains("格式 spec 公開", f.Value);
        Assert.Contains("不證明內容正確", f.Value);
    }

    [Fact]
    public void 匯入_Unknown起步不覆蓋本機值()
    {
        var f = ExportUxFactsService.ImportFact(At, () => (2, 40));
        Assert.Contains("Unknown 起步", f.Value);
        Assert.Contains("不覆蓋本機值", f.Value);
        Assert.Contains("保留兩者", f.Value);
    }

    [Fact]
    public void 匯入_缺來源不適用()
    {
        var f = ExportUxFactsService.ImportFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不從缺來源推論", f.UnavailableReason);
    }

    [Fact]
    public void 資料年齡_如實不美化且0是值()
    {
        var f = ExportUxFactsService.AgeFact(At, () => (0, true));
        Assert.Contains("如實不美化", f.Value);
        Assert.Contains("0 分鐘是值", f.Value);
    }

    [Fact]
    public void 崩潰報告_如實不粉飾()
    {
        var f = ExportUxFactsService.CrashReportFact(At, () => (2, true));
        Assert.Contains("如實不粉飾", f.Value);
        Assert.Contains("不美化為「已修復」", f.Value);
    }

    [Fact]
    public void 崩潰報告_零次是值()
    {
        var f = ExportUxFactsService.CrashReportFact(At, () => (0, true));
        Assert.Contains("0 是值", f.Value);
    }

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = ExportUxFactsService.Collect(At);
        string[] keys =
        [
            ExportUxFactsService.VerifyKey, ExportUxFactsService.AuditTimelineKey, ExportUxFactsService.MigrateKey,
            ExportUxFactsService.NamedQueryKey, ExportUxFactsService.WatermarkKey, ExportUxFactsService.I18nReportKey,
            ExportUxFactsService.XinSigKey, ExportUxFactsService.ImportKey, ExportUxFactsService.AgeKey,
            ExportUxFactsService.CrashReportKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
