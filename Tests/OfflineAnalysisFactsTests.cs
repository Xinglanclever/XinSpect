using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 371–378 項（離線分析）的事實服務測試。</summary>
public class OfflineAnalysisFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void hive_無掛載聲明唯讀不寫入()
    {
        var f = OfflineAnalysisFactsService.HiveFact(At, () => (0, 0, false));
        Assert.Contains("掛載＝讀取不寫入", f.Value);
        Assert.Equal(0.0, f.NumericValue);
    }

    [Fact]
    public void hive_離線明示不冒充本機現況()
    {
        var f = OfflineAnalysisFactsService.HiveFact(At, () => (2, 500, false));
        Assert.Contains("離線", f.Value);
        Assert.Contains("不冒充本機現況", f.Value);
    }

    [Fact]
    public void evtx_缺檔不推論()
    {
        var f = OfflineAnalysisFactsService.EvtxFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不從缺檔案推論", f.UnavailableReason);
    }

    [Fact]
    public void evtx_姊妹件離線讀回不列路徑()
    {
        var f = OfflineAnalysisFactsService.EvtxFact(At, () => (3, 5 * 1024 * 1024, true));
        Assert.Contains("姊妹件", f.Value);
        Assert.Contains("不列檔名或路徑", f.Value);
    }

    [Fact]
    public void dump_空集合不解析()
    {
        var f = OfflineAnalysisFactsService.DumpFact(At, () => (0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不解析空集合", f.UnavailableReason);
    }

    [Fact]
    public void dump_多傾印找模式不下判決()
    {
        var f = OfflineAnalysisFactsService.DumpFact(At, () => (4, true));
        Assert.Contains("共同徵兆", f.Value);
        Assert.Contains("不自動下根因判決", f.Value);
    }

    [Fact]
    public void bcd_只讀不寫來源()
    {
        var f = OfflineAnalysisFactsService.BcdFact(At, () => (3, true));
        Assert.Contains("只讀不寫", f.Value);
        Assert.Contains("不對來源 BCD 做任何修改", f.Value);
    }

    [Fact]
    public void raw_唯讀開檔不掛載不讀內容()
    {
        var f = OfflineAnalysisFactsService.RawFact(At, () => (3, 500L * 1024 * 1024 * 1024, true));
        Assert.Contains("唯讀開檔", f.Value);
        Assert.Contains("不讀取檔案內容", f.Value);
    }

    [Fact]
    public void raw_無來源不適用()
    {
        var f = OfflineAnalysisFactsService.RawFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void diff_零差異是值()
    {
        var f = OfflineAnalysisFactsService.DiffFact(At, () => (0, 0, 0));
        Assert.Contains("0 是值", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
    }

    [Fact]
    public void diff_差異是事實變了非判決()
    {
        var f = OfflineAnalysisFactsService.DiffFact(At, () => (2, 1, 3));
        Assert.Contains("純函式", f.Value);
        Assert.Contains("不是判決", f.Value);
        Assert.Equal(6.0, f.NumericValue);
    }

    [Fact]
    public void 寫入封鎖_無寫入API是機制化保證()
    {
        var f = OfflineAnalysisFactsService.WriteBlockFact(At, () => (true, 3));
        Assert.Contains("機制化", f.Value);
        Assert.Contains("不是口號", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    [Fact]
    public void 寫入封鎖_發現寫入API是警示不是通過()
    {
        var f = OfflineAnalysisFactsService.WriteBlockFact(At, () => (false, 2));
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("警示", f.Value);
        Assert.NotNull(f.UnavailableReason);
    }

    [Fact]
    public void 本批七把鍵齊()
    {
        var facts = OfflineAnalysisFactsService.Collect(At);
        string[] keys =
        [
            OfflineAnalysisFactsService.HiveKey, OfflineAnalysisFactsService.EvtxKey,
            OfflineAnalysisFactsService.DumpKey, OfflineAnalysisFactsService.BcdKey,
            OfflineAnalysisFactsService.RawKey, OfflineAnalysisFactsService.DiffKey,
            OfflineAnalysisFactsService.WriteBlockKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(7, facts.Count);
        Assert.Equal(7, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
