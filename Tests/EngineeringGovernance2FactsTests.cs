using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 470–489 項（工程治理二）的事實服務測試。</summary>
public class EngineeringGovernance2FactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    // ── EG-011 CI ─────────────────────────────────────────────────────

    [Fact]
    public void CI_無工作流如實聲明()
    {
        var f = EngineeringGovernance2FactsService.CiFact(At, () => (false, true));
        Assert.Contains("尚未設定 CI", f.Value);
        Assert.Equal(0.0, f.NumericValue);
    }

    [Fact]
    public void CI_有工作流標runner差異()
    {
        var f = EngineeringGovernance2FactsService.CiFact(At, () => (true, true));
        Assert.Contains("runner 差異聲明", f.Value);
        Assert.Contains("CI 綠不代表本機硬體路徑也綠", f.Value);
    }

    [Fact]
    public void CI_探針爆掉標讀取錯誤()
    {
        var f = EngineeringGovernance2FactsService.CiFact(At, () => throw new InvalidOperationException());
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── EG-012 PR/issue 模板 ──────────────────────────────────────────

    [Fact]
    public void PR模板_只陳述存在性()
    {
        var f = EngineeringGovernance2FactsService.PrTemplateFact(At, () => (true, true));
        Assert.Contains("PR 模板=有", f.Value);
        Assert.Contains("只陳述存在性", f.Value);
    }

    [Fact]
    public void PR模板_皆無時為零()
    {
        var f = EngineeringGovernance2FactsService.PrTemplateFact(At, () => (false, false));
        Assert.Equal(0.0, f.NumericValue);
    }

    // ── EG-014 效能回歸守門 ───────────────────────────────────────────

    [Fact]
    public void 效能回歸_標環境抖動容差()
    {
        var f = EngineeringGovernance2FactsService.PerfRegressFact(At, () => (145.2, true));
        Assert.Contains("環境抖動容差聲明", f.Value);
        Assert.Equal(145.2, f.NumericValue);
    }

    // ── EG-017 docs 連結 ──────────────────────────────────────────────

    [Fact]
    public void docs連結_零斷裂是值()
    {
        var f = EngineeringGovernance2FactsService.DocsLinksFact(At, () => (312, 0));
        Assert.Contains("0 斷裂是值", f.Value);
        Assert.Equal(0.0, f.NumericValue);
    }

    [Fact]
    public void docs連結_有斷裂如實()
    {
        var f = EngineeringGovernance2FactsService.DocsLinksFact(At, () => (312, 4));
        Assert.Equal(4.0, f.NumericValue);
        Assert.Contains("連結驗證", f.Value);
    }

    // ── EG-019 SECURITY.md ────────────────────────────────────────────

    [Fact]
    public void SECURITY_標私下通報管道()
    {
        var f = EngineeringGovernance2FactsService.SecurityFact(At, () => (true, true));
        Assert.Contains("私下", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    // ── EG-020 逐頁導覽 ───────────────────────────────────────────────

    [Fact]
    public void 逐頁導覽_標截圖需同步()
    {
        var f = EngineeringGovernance2FactsService.PageNavFact(At, () => (42, 38));
        Assert.Contains("截圖需與當前版同步", f.Value);
        Assert.Equal(42.0, f.NumericValue);
    }

    // ── EG-021 ARM64 ──────────────────────────────────────────────────

    [Fact]
    public void ARM64_有路徑未施測如實標()
    {
        var f = EngineeringGovernance2FactsService.Arm64Fact(At, () => (true, false));
        Assert.Contains("未施測", f.Value);
        Assert.Contains("有代碼不等於在 ARM64 上驗證過", f.Value);
    }

    [Fact]
    public void ARM64_無路徑為範圍非缺陷()
    {
        var f = EngineeringGovernance2FactsService.Arm64Fact(At, () => (false, false));
        Assert.Contains("不是缺陷，是範圍", f.Value);
        Assert.Equal(0.0, f.NumericValue);
    }

    // ── EG-022 Win10/11 分支 ──────────────────────────────────────────

    [Fact]
    public void Win分支_標降級測試()
    {
        var f = EngineeringGovernance2FactsService.WinBranchFact(At, () => (7, false));
        Assert.Contains("降級測試", f.Value);
        Assert.Equal(7.0, f.NumericValue);
    }

    // ── EG-023 fuzz ───────────────────────────────────────────────────

    [Fact]
    public void fuzz_無harness為不適用()
    {
        var f = EngineeringGovernance2FactsService.FuzzFact(At, () => (0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("尚未建立 fuzz harness", f.UnavailableReason);
    }

    [Fact]
    public void fuzz_有harness標結構化()
    {
        var f = EngineeringGovernance2FactsService.FuzzFact(At, () => (5, 3));
        Assert.Contains("結構化 fuzz", f.Value);
        Assert.Equal(5.0, f.NumericValue);
    }

    // ── EG-024 真實樣本語料 ───────────────────────────────────────────

    [Fact]
    public void 真語料_標敏感資料遮蔽()
    {
        var f = EngineeringGovernance2FactsService.RealCorpusFact(At, () => (12, 9, true));
        Assert.Contains("敏感資料必遮蔽", f.Value);
        Assert.Contains("已遮蔽", f.Value);
    }

    // ── EG-025 覆蓋率 ─────────────────────────────────────────────────

    [Fact]
    public void 覆蓋率_如實不下品質判決()
    {
        var f = EngineeringGovernance2FactsService.CoverageFact(At, () => (62.4, true));
        Assert.Contains("覆蓋率低不代表品質差要如實", f.Value);
        Assert.Equal(62.4, f.NumericValue);
    }

    // ── EG-027 鍵文件 ─────────────────────────────────────────────────

    [Fact]
    public void 鍵文件_與目錄同源()
    {
        var f = EngineeringGovernance2FactsService.KeyDocsFact(At, () => (771, true));
        Assert.Contains("由事實鍵目錄產生", f.Value);
        Assert.Equal(771.0, f.NumericValue);
    }

    // ── EG-028 快照矩陣 ───────────────────────────────────────────────

    [Fact]
    public void 快照矩陣_單版不假裝有矩陣()
    {
        var f = EngineeringGovernance2FactsService.SnapshotMatrixFact(At, () => (1, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("單版不假裝有矩陣", f.UnavailableReason);
    }

    [Fact]
    public void 快照矩陣_多版列組合()
    {
        var f = EngineeringGovernance2FactsService.SnapshotMatrixFact(At, () => (4, 12));
        Assert.Contains("相容性矩陣", f.Value);
        Assert.Equal(12.0, f.NumericValue);
    }

    // ── EG-030 SelfTelemetry ──────────────────────────────────────────

    [Fact]
    public void 自我遙測_預設關閉不上傳()
    {
        var f = EngineeringGovernance2FactsService.SelfTelFact(At, () => (false, true));
        Assert.Contains("未啟用（預設關閉）", f.Value);
        Assert.Contains("不上傳", f.Value);
    }

    // ── 整批 ──────────────────────────────────────────────────────────

    [Fact]
    public void 整批_蒐集十四把鍵且鍵名不重複()
    {
        var facts = EngineeringGovernance2FactsService.Collect(At,
            () => (true, true), () => (true, true), () => (140, true), () => (300, 0),
            () => (true, true), () => (40, 36), () => (true, true), () => (7, true),
            () => (5, 3), () => (12, 9, true), () => (62.4, true), () => (771, true),
            () => (4, 12), () => (false, true));

        Assert.Equal(14, facts.Count);
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
        Assert.Equal(14, System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(facts, f => f.Key)).Count());
    }
}
