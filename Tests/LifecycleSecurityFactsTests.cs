using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 395–399 項（生命週期與安全收口）的事實服務測試。</summary>
public class LifecycleSecurityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 升級_未施測不代表可用()
    {
        var f = LifecycleSecurityFactsService.UpgradeFact(At, () => (false, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未施測不代表升級可用", f.Value);
    }

    [Fact]
    public void 升級_通過代表這條路徑非所有跳版()
    {
        var f = LifecycleSecurityFactsService.UpgradeFact(At, () => (true, 1, 2));
        Assert.Contains("不代表所有跳版組合", f.Value);
    }

    [Fact]
    public void 設定遷移_往返不通過代表相容問題()
    {
        var f = LifecycleSecurityFactsService.CfgMigrateFact(At, () => (false, 3));
        Assert.Contains("相容性問題", f.Value);
        Assert.Contains("不修改任何設定檔", f.Value);
    }

    [Fact]
    public void 多語安裝器_產品方向與PL001連動()
    {
        var f = LifecycleSecurityFactsService.I18nFact(At, () => (3, true));
        Assert.Contains("PL-001", f.Value);
        Assert.Equal(3.0, f.NumericValue);
    }

    [Fact]
    public void 憑證漫遊_永不匯出憑證內容()
    {
        var f = LifecycleSecurityFactsService.CredRoamFact(At, () => (true, true));
        Assert.Contains("永不匯出憑證內容", f.Value);
        Assert.Contains("不讀憑證、不讀私鑰", f.Value);
    }

    [Fact]
    public void 憑證漫遊_未設定不等於關閉()
    {
        var f = LifecycleSecurityFactsService.CredRoamFact(At, () => (false, false));
        Assert.Contains("未設定不等於關閉", f.Value);
    }

    [Fact]
    public void ESP基線_缺一不判()
    {
        var f = LifecycleSecurityFactsService.EspBaselineFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺一不判", f.UnavailableReason);
    }

    [Fact]
    public void ESP基線_攻擊面事實非中毒判決()
    {
        var f = LifecycleSecurityFactsService.EspBaselineFact(At, () => (10, 2));
        Assert.Contains("攻擊面事實非中毒判決", f.Value);
        Assert.Contains("不列檔名或雜湊", f.Value);
    }

    [Fact]
    public void 本批五把鍵齊()
    {
        var facts = LifecycleSecurityFactsService.Collect(At);
        string[] keys =
        [
            LifecycleSecurityFactsService.UpgradeKey, LifecycleSecurityFactsService.CfgMigrateKey,
            LifecycleSecurityFactsService.I18nInstallerKey, LifecycleSecurityFactsService.CredRoamKey,
            LifecycleSecurityFactsService.EspBaselineKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(5, facts.Count);
        Assert.Equal(5, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
