using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-096 – P1-105 的發佈、建置與對外文件守門。
///
/// <para><b>這一組處理的是「別人怎麼拿到與相信這份成果」。</b>
/// 核心是 P1-105：文件裡的數字如果只能人工維護，它就一定會漂移。
/// </para>
/// </summary>
public class ReleaseReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── P1-096 單檔發佈 ────────────────────────────────────────────────

    [Fact]
    public void 單檔_多檔發佈要標不支援並說出為什麼()
    {
        var fact = ReleaseReport.ToSingleFileFact(At, singleFile: false, 0, bundledFiles: 12);
        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("免安裝是產品方向", fact.UnavailableReason);
    }

    [Fact]
    public void 單檔_成單檔時要聲明仍需執行階段()
    {
        var fact = ReleaseReport.ToSingleFileFact(At, true, 80L * 1024 * 1024, 15);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("80.0 MB", fact.Value);
        Assert.Contains("仍需要對應的 .NET 執行階段", fact.UnavailableReason);
    }

    // ── P1-097 建置可重現 ──────────────────────────────────────────────

    [Fact]
    public void 可重現_兩次相同才算()
    {
        var fact = ReleaseReport.ToReproducibleFact(At, "sha256:aaa", "sha256:aaa");
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("可回答的問題", fact.UnavailableReason);
    }

    [Fact]
    public void 可重現_一個位元組不同就不可重現()
    {
        var fact = ReleaseReport.ToReproducibleFact(At, "sha256:aaa", "sha256:aab");
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("大部分相同", fact.UnavailableReason);
    }

    [Fact]
    public void 可重現_只有一次建置時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            ReleaseReport.ToReproducibleFact(At, "sha256:aaa", null).Availability);
    }

    // ── P1-099 發佈驗證 ────────────────────────────────────────────────

    [Fact]
    public void 發佈_沒有腳本時要說那等於要求使用者信任我們()
    {
        var fact = ReleaseReport.ToVerifyFact(At,
            [("XinSpect.exe", "sha256:aa", true)], scriptPresent: false);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("等於要求他信任我們的說明", fact.UnavailableReason);
    }

    [Fact]
    public void 發佈_有資產沒雜湊時標讀取失敗()
    {
        var fact = ReleaseReport.ToVerifyFact(At,
            [("a.exe", "sha256:aa", true), ("b.zip", "", false)], scriptPresent: true);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("b.zip", fact.Value);
    }

    [Fact]
    public void 發佈_齊備時標Present()
    {
        var fact = ReleaseReport.ToVerifyFact(At,
            [("a.exe", "sha256:aa", true), ("b.zip", "sha256:bb", true)], scriptPresent: true);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(2.0, fact.NumericValue);
    }

    // ── P1-100 測試基線 ────────────────────────────────────────────────

    [Fact]
    public void 基線_實際少於基線時要說比測試失敗更嚴重()
    {
        var fact = ReleaseReport.ToBaselineFact(At, baseline: 5700, actual: 5600, badgeValues: [5700]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("有測試消失了", fact.UnavailableReason);
        Assert.Contains("比測試失敗更嚴重", fact.UnavailableReason);
    }

    [Fact]
    public void 基線_徽章與基線不一致要說那就是說謊()
    {
        var fact = ReleaseReport.ToBaselineFact(At, 5700, 5700, badgeValues: [5512]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("徽章是給外人看的數字", fact.UnavailableReason);
    }

    [Fact]
    public void 基線_實際多於基線時要提醒不能忘記()
    {
        // 這正是本計畫當下的狀態：實際 5700+ 而基線 5512，等第 100 項才同步。
        var fact = ReleaseReport.ToBaselineFact(At, 5512, 5740, badgeValues: [5512]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("不能忘記", fact.UnavailableReason);
    }

    [Fact]
    public void 基線_完全一致時標Present()
    {
        var fact = ReleaseReport.ToBaselineFact(At, 5740, 5740, [5740, 5740, 5740]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-101 授權聲明 ────────────────────────────────────────────────

    [Fact]
    public void 授權_沒有授權檔時標讀取失敗()
    {
        var fact = ReleaseReport.ToNoticesFact(At, false, 0, 0);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("沒有說明使用條件", fact.UnavailableReason);
    }

    [Fact]
    public void 授權_沒聲明的第三方義務還是義務()
    {
        var fact = ReleaseReport.ToNoticesFact(At, true, thirdPartyCount: 5, declaredThirdParty: 3);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("沒聲明的授權義務還是義務", fact.UnavailableReason);
    }

    // ── P1-102 認證計畫 ────────────────────────────────────────────────

    [Fact]
    public void 認證_已送出不等於已通過()
    {
        var fact = ReleaseReport.ToCertPlanFact(At,
            [("EV 簽章", "已通過"), ("WHQL", "已送出")]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("WHQL=已送出", fact.Value);
        Assert.Contains("『已送出』不等於『已通過』", fact.UnavailableReason);
    }

    [Fact]
    public void 認證_全部通過時標Present()
    {
        var fact = ReleaseReport.ToCertPlanFact(At, [("EV", "已通過")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    // ── P1-103 公開限制 ────────────────────────────────────────────────

    [Fact]
    public void 限制_沒有文件時要說使用者會假設它沒有()
    {
        var fact = ReleaseReport.ToLimitsFact(At, [], documentPresent: false);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("假設它沒有", fact.UnavailableReason);
    }

    [Fact]
    public void 限制_空的清單不可信()
    {
        var fact = ReleaseReport.ToLimitsFact(At, [], documentPresent: true);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("任何工具都有它做不到的事", fact.UnavailableReason);
    }

    [Fact]
    public void 限制_有條目時要說可信度取決於具體程度()
    {
        var fact = ReleaseReport.ToLimitsFact(At,
            ["需要管理員權限才能讀 MSR", "不支援 ARM64 的深度探測"], true);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("『可能不準』不是一條限制", fact.UnavailableReason);
    }

    // ── P1-104 貢獻者指南 ──────────────────────────────────────────────

    [Fact]
    public void 指南_缺必要環節時要說那一環就是下一個人會漏掉的()
    {
        var fact = ReleaseReport.ToGuideFact(At, ["宣告來源", "實作服務"], guidePresent: true);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("鍵", fact.Value);
        Assert.Contains("下一個人會漏掉的東西", fact.UnavailableReason);
    }

    [Fact]
    public void 指南_涵蓋五環時標Present()
    {
        var fact = ReleaseReport.ToGuideFact(At,
            ["宣告來源", "實作服務", "登記鍵", "撰寫測試", "標注出處"], true);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    [Fact]
    public void 指南_沒有指南時標讀取失敗()
    {
        Assert.Equal(FactAvailability.ReadError,
            ReleaseReport.ToGuideFact(At, [], guidePresent: false).Availability);
    }

    // ── P1-105 文件對帳 ────────────────────────────────────────────────

    [Fact]
    public void 對帳_只能人工維護的數字一定會漂移()
    {
        var fact = ReleaseReport.ToDocReconcileFact(At,
        [
            ("測試數", "dotnet test", true),
            ("支援頁面數", "手數", false),
        ]);

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("支援頁面數", fact.Value);
        Assert.Contains("需要有人一直記得", fact.UnavailableReason);
    }

    [Fact]
    public void 對帳_全部可重跑時標Present()
    {
        var fact = ReleaseReport.ToDocReconcileFact(At,
            [("測試數", "dotnet test", true), ("鍵數", "FactKeyCatalog.Count", true)]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }
}
