using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 343–345 項（治理與知識收口）的事實服務測試。</summary>
public class GovernanceKnowledgeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public void 候選_讀不到不猜()
    {
        var f = GovernanceKnowledgeFactsService.CandidateFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不猜", f.UnavailableReason);
    }

    [Fact]
    public void 候選_零記錄是值不代表規則完整()
    {
        var f = GovernanceKnowledgeFactsService.CandidateFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 是值", f.Value);
    }

    [Fact]
    public void 候選_絕不自動入庫需人核可()
    {
        var f = GovernanceKnowledgeFactsService.CandidateFact(At, () => (5, 3));
        Assert.Contains("人工核可", f.Value);
        Assert.Contains("不自動入庫", f.Value);
        Assert.Equal(5.0, f.NumericValue);
    }

    [Fact]
    public void 保留_讀不到不猜()
    {
        var f = GovernanceKnowledgeFactsService.RetentionFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 保留_裁剪不可逆且審計鏈例外()
    {
        var f = GovernanceKnowledgeFactsService.RetentionFact(At, () => (1000, 90, true));
        Assert.Contains("裁剪不可逆", f.Value);
        Assert.Contains("永不裁剪", f.Value);
        Assert.Equal(1000.0, f.NumericValue);
    }

    [Fact]
    public void 對帳_無文件宣稱不判()
    {
        var f = GovernanceKnowledgeFactsService.DocMapFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺文件不判差異", f.UnavailableReason);
    }

    [Fact]
    public void 對帳_防文件說謊但不自動改文件()
    {
        var f = GovernanceKnowledgeFactsService.DocMapFact(At, () => (50, 45));
        Assert.Contains("防文件說謊", f.Value);
        Assert.Contains("不自動改文件", f.Value);
        Assert.Contains("落差 5 項", f.Value);
    }

    [Fact]
    public void 本批三把鍵齊()
    {
        var facts = GovernanceKnowledgeFactsService.Collect(At);
        Assert.Contains(facts, f => f.Key == GovernanceKnowledgeFactsService.CandidateKey);
        Assert.Contains(facts, f => f.Key == GovernanceKnowledgeFactsService.RetentionKey);
        Assert.Contains(facts, f => f.Key == GovernanceKnowledgeFactsService.DocMapKey);
        Assert.Equal(3, facts.Count);
    }
}
