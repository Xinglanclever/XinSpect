using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 430–439 項（使用體驗二）的事實服務測試。</summary>
public class UserExperience2FactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 溯源_不等於正確性背書()
    {
        var f = UserExperience2FactsService.ProvenanceFact(At, () => (10, 1));
        Assert.Contains("溯源≠正確性背書", f.Value);
        Assert.Contains("不猜來源", f.Value);
    }

    [Fact]
    public void 命令面板_只導覽不改變狀態()
    {
        var f = UserExperience2FactsService.PaletteFact(At, () => (25, true));
        Assert.Contains("導覽", f.Value);
        Assert.Contains("不執行會改變狀態的操作", f.Value);
    }

    [Fact]
    public void Help檢索_本機離線不出網()
    {
        var f = UserExperience2FactsService.HelpSearchFact(At, () => (80, true));
        Assert.Contains("本機離線", f.Value);
        Assert.Contains("不出網", f.Value);
    }

    [Fact]
    public void UIA_測試結果非功能()
    {
        var f = UserExperience2FactsService.UiaFact(At, () => (200, 3));
        Assert.Contains("螢幕朗讀者", f.Value);
        Assert.Contains("不是產品功能", f.Value);
    }

    [Fact]
    public void DPI_不符是訊號需人確認()
    {
        var f = UserExperience2FactsService.DpiFact(At, () => (2, false));
        Assert.Contains("版面漂移", f.Value);
        Assert.Contains("不符是訊號", f.Value);
    }

    [Fact]
    public void 主題_跟隨系統只回應不覆寫()
    {
        var f = UserExperience2FactsService.ThemeFact(At, () => (true, false));
        Assert.Contains("跟隨系統", f.Value);
        Assert.Contains("只回應不覆寫", f.Value);
    }

    [Fact]
    public void 節能取樣_受效能預算約束()
    {
        var f = UserExperience2FactsService.EcoFact(At, () => (30, true));
        Assert.Contains("受效能預算約束", f.Value);
        Assert.Contains("不能無限", f.Value);
    }

    [Fact]
    public void 錯誤聚合_重試是唯讀重掃()
    {
        var f = UserExperience2FactsService.ErrorsFact(At, () => (5, true));
        Assert.Contains("重試＝唯讀重掃", f.Value);
        Assert.Contains("不修復", f.Value);
    }

    [Fact]
    public void 鍵瀏覽器_顯示semver()
    {
        var f = UserExperience2FactsService.KeyBrowserFact(At, () => (727, true));
        Assert.Contains("公開契約", f.Value);
        Assert.Equal(727.0, f.NumericValue);
    }

    [Fact]
    public void 右鍵複製_含鍵名與時間戳讓值可追溯()
    {
        var f = UserExperience2FactsService.CopyFact(At, () => (true, true));
        Assert.Contains("可追溯", f.Value);
        Assert.Contains("單獨一個數字貼出去沒有意義", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = UserExperience2FactsService.Collect(At);
        string[] keys =
        [
            UserExperience2FactsService.ProvenanceKey, UserExperience2FactsService.PaletteKey,
            UserExperience2FactsService.HelpSearchKey, UserExperience2FactsService.UiaKey,
            UserExperience2FactsService.DpiKey, UserExperience2FactsService.ThemeKey,
            UserExperience2FactsService.EcoKey, UserExperience2FactsService.ErrorsKey,
            UserExperience2FactsService.KeyBrowserKey, UserExperience2FactsService.CopyKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(10, facts.Count);
        Assert.Equal(10, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
