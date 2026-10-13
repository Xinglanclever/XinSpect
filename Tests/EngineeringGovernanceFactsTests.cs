using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 356–365 項（工程治理）的事實服務測試。</summary>
public class EngineeringGovernanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    public void p95_無樣本不給0冒充()
    {
        var f = EngineeringGovernanceFactsService.P95Fact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不給 0 冒充", f.UnavailableReason);
    }

    [Fact]
    public void p95_遙測預設關閉只記數字()
    {
        var f = EngineeringGovernanceFactsService.P95Fact(At, () => (120, 480, 50));
        Assert.Contains("p95 480", f.Value);
        Assert.Contains("預設關閉", f.Value);
    }

    [Fact]
    public void 版號語意_是宣告不是量測()
    {
        var f = EngineeringGovernanceFactsService.SemverFact(At, () => ("2", "80", "minor＝功能批次"));
        Assert.Contains("宣告", f.Value);
        Assert.Contains("不破壞下游契約", f.Value);
    }

    [Fact]
    public void 已知不可為_明文列出不假裝有()
    {
        var f = EngineeringGovernanceFactsService.NotImplFact(At);
        Assert.Contains("不假裝有", f.Value);
        Assert.True(f.NumericValue >= 3);
    }

    [Fact]
    public void 鍵semver_鍵是公開契約()
    {
        var f = EngineeringGovernanceFactsService.KeyApiFact(At, () => (654, 0));
        Assert.Contains("公開契約", f.Value);
        Assert.Contains("棄用流程", f.Value);
        Assert.Equal(654.0, f.NumericValue);
    }

    [Fact]
    public void 混合架構_同質架構不適用()
    {
        var f = EngineeringGovernanceFactsService.MixArchFact(At, () => (0, 0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("混合架構路徑不適用", f.UnavailableReason);
    }

    [Fact]
    public void 混合架構_未施測一律標明()
    {
        var f = EngineeringGovernanceFactsService.MixArchFact(At, () => (8, 4, false));
        Assert.Contains("未施測", f.Value);
        Assert.Contains("不宣稱可用", f.Value);
    }

    [Fact]
    public void 未施測聲明_未施測不代表可用()
    {
        var f = EngineeringGovernanceFactsService.UntestedFact(At);
        Assert.Contains("未施測不代表可用", f.Value);
    }

    [Fact]
    public void 知識包_離線無更新通道()
    {
        var f = EngineeringGovernanceFactsService.KnowledgeFact(At, () => ("v1", true));
        Assert.Contains("無更新通道", f.Value);
        Assert.Contains("快照日期必印", f.Value);
    }

    [Fact]
    public void 倉庫衛生_未追蹤會進exe只陳述不清理()
    {
        var f = EngineeringGovernanceFactsService.HygieneFact(At, () => (5, true));
        Assert.Contains("會被 publish 一起編進 exe", f.Value);
        Assert.Contains("不自動清理", f.Value);
    }

    [Fact]
    public void 文件單一來源_不自動合併()
    {
        var f = EngineeringGovernanceFactsService.DocSingleFact(At, () => (40, 2));
        Assert.Contains("只陳述計數", f.Value);
        Assert.Contains("不自動合併", f.Value);
    }

    [Fact]
    public void corpus_無樣本不建構()
    {
        var f = EngineeringGovernanceFactsService.CorpusFact(At, () => (0, true));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不建構空包", f.UnavailableReason);
    }

    [Fact]
    public void corpus_只出遮蔽版上傳不實作()
    {
        var f = EngineeringGovernanceFactsService.CorpusFact(At, () => (100, true));
        Assert.Contains("只出遮蔽版", f.Value);
        Assert.Contains("上傳通路刻意不實作", f.Value);
    }

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = EngineeringGovernanceFactsService.Collect(At);
        string[] keys =
        [
            EngineeringGovernanceFactsService.P95Key, EngineeringGovernanceFactsService.SemverKey,
            EngineeringGovernanceFactsService.NotImplKey, EngineeringGovernanceFactsService.KeyApiKey,
            EngineeringGovernanceFactsService.MixArchKey, EngineeringGovernanceFactsService.UntestedKey,
            EngineeringGovernanceFactsService.KnowledgeKey, EngineeringGovernanceFactsService.HygieneKey,
            EngineeringGovernanceFactsService.DocSingleKey, EngineeringGovernanceFactsService.CorpusKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
