using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 DS-006/007/010/011/012 項（資料安全深化二）的事實服務測試。</summary>
public class DataSecurityDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    // ── DS-006 憑證自動更新 ─────────────────────────────────────────

    [Fact]
    public void 憑證自動更新_讀不到不當成已停用()
    {
        var f = DataSecurityDeepFactsService.CertUpdateFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不把讀不到當成「已停用」", f.UnavailableReason);
    }

    [Fact]
    public void 憑證自動更新_設定狀態不是結果()
    {
        var f = DataSecurityDeepFactsService.CertUpdateFact(At,
            () => new DataSecurityDeepFactsService.CertUpdateState(8, 0, 1, true));
        Assert.Contains("設定狀態不是結果", f.Value);
        Assert.Contains("不保證", f.Value.Replace("不是「憑證一定都更新成功」", "不保證"));
        Assert.Contains("不會反映在這張表上", f.Value);
        Assert.Equal(8.0, f.NumericValue);
    }

    [Fact]
    public void 憑證自動更新_上次檢查讀不到要標示()
    {
        var f = DataSecurityDeepFactsService.CertUpdateFact(At,
            () => new DataSecurityDeepFactsService.CertUpdateState(8, 0, 1, false));
        Assert.Contains("上次檢查時間讀不到", f.Value);
        Assert.Contains("無法判斷更新機制最近有沒有動過", f.Value);
    }

    // ── DS-007 自簽憑證 ─────────────────────────────────────────────

    [Fact]
    public void 自簽憑證_零是值不是沒檢查()
    {
        var f = DataSecurityDeepFactsService.SelfSignedFact(At,
            () => new DataSecurityDeepFactsService.SelfSignedState(0, 0, 0, 0));
        Assert.Contains("0 是值，不是「沒檢查」", f.Value);
        Assert.Equal(0.0, f.NumericValue);
    }

    [Fact]
    public void 自簽憑證_不列主體名稱()
    {
        var f = DataSecurityDeepFactsService.SelfSignedFact(At,
            () => new DataSecurityDeepFactsService.SelfSignedState(6, 3, 5, 1));
        Assert.Contains("不列主體名稱", f.Value);
        Assert.Contains("常含機器名或使用者名", f.Value);
        Assert.Contains("自簽本身不是問題", f.Value);
        Assert.Equal(6.0, f.NumericValue);
    }

    [Fact]
    public void 自簽憑證_無到期日如實說()
    {
        var f = DataSecurityDeepFactsService.SelfSignedFact(At,
            () => new DataSecurityDeepFactsService.SelfSignedState(3, 2, 0, 0));
        Assert.Contains("沒有可讀的到期日", f.Value);
    }

    // ── DS-010 Smart App Control ────────────────────────────────────

    [Fact]
    public void SAC_無政策鍵為不適用並說明平台限制()
    {
        var f = DataSecurityDeepFactsService.SacFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("僅 Windows 11 全新安裝才有", f.UnavailableReason);
    }

    [Fact]
    public void SAC_評估中不是已啟用()
    {
        var f = DataSecurityDeepFactsService.SacFact(At,
            () => new DataSecurityDeepFactsService.SacState(2, "評估中"));
        Assert.Contains("這不是「已啟用」", f.Value);
        Assert.Contains("評估期結束前它不會阻擋任何東西", f.Value);
        Assert.Contains("把它讀成已保護是錯的", f.Value);
    }

    [Fact]
    public void SAC_強制執行與關閉如實區分()
    {
        var on = DataSecurityDeepFactsService.SacFact(At,
            () => new DataSecurityDeepFactsService.SacState(1, "強制執行"));
        Assert.Contains("未受信任的應用程式會被阻擋", on.Value);

        var off = DataSecurityDeepFactsService.SacFact(At,
            () => new DataSecurityDeepFactsService.SacState(0, "關閉"));
        Assert.Contains("不受 Smart App Control 限制", off.Value);
    }

    [Fact]
    public void SAC_未知狀態值不猜語意()
    {
        var f = DataSecurityDeepFactsService.SacFact(At,
            () => new DataSecurityDeepFactsService.SacState(7, "未知"));
        Assert.Contains("不猜語意", f.Value);
    }

    // ── DS-011 裝置安裝限制政策 ─────────────────────────────────────

    [Fact]
    public void 裝置政策_零設定如實說沒有額外限制()
    {
        var f = DataSecurityDeepFactsService.DevPolFact(At,
            () => new DataSecurityDeepFactsService.DevPolState(5, 0, 0, 0));
        Assert.Contains("對裝置安裝沒有額外限制", f.Value);
    }

    [Fact]
    public void 裝置政策_政策存在不等於安全()
    {
        var f = DataSecurityDeepFactsService.DevPolFact(At,
            () => new DataSecurityDeepFactsService.DevPolState(5, 3, 2, 1));
        Assert.Contains("政策存在不等於安全，也不等於不安全", f.Value);
        Assert.Contains("那取決於它的用途", f.Value);
        Assert.Contains("阻擋安裝 2、僅警告 1", f.Value);
        Assert.Equal(3.0, f.NumericValue);
    }

    [Fact]
    public void 裝置政策_無政策項為不適用()
    {
        var f = DataSecurityDeepFactsService.DevPolFact(At,
            () => new DataSecurityDeepFactsService.DevPolState(0, 0, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    // ── DS-012 摘要卡 ───────────────────────────────────────────────

    [Fact]
    public void 摘要_聚合訊號不是分數()
    {
        var facts = DataSecurityDeepFactsService.Collect(At,
            () => new DataSecurityDeepFactsService.CertUpdateState(8, 0, 1, true),
            () => new DataSecurityDeepFactsService.SelfSignedState(6, 3, 5, 1),
            () => new DataSecurityDeepFactsService.SacState(1, "強制執行"),
            () => new DataSecurityDeepFactsService.DevPolState(5, 3, 2, 1));
        var d = facts.Single(f => f.Key == DataSecurityDeepFactsService.DashboardKey);
        Assert.Contains("聚合的是訊號不是分數", d.Value);
        Assert.Contains("故意不產出", d.Value.Replace("本項刻意不產出", "故意不產出"));
        Assert.Contains("幾分算好", d.Value);
        Assert.Contains("只負責讓人知道往哪裡看", d.Value);
        Assert.Equal(4.0, d.NumericValue);
    }

    [Fact]
    public void 摘要_不可讀本身就是要看的訊號()
    {
        var facts = DataSecurityDeepFactsService.Collect(At);   // 全部探針缺席
        var d = facts.Single(f => f.Key == DataSecurityDeepFactsService.DashboardKey);
        Assert.Contains("可讀 0/4 項", d.Value);
        Assert.Contains("不可讀本身就是要看的訊號", d.Value);
        Assert.Contains("不是「沒問題」", d.Value);
    }

    // ── 整批 ────────────────────────────────────────────────────────

    [Fact]
    public void 整批_五把鍵齊且不重複()
    {
        var facts = DataSecurityDeepFactsService.Collect(At,
            () => new DataSecurityDeepFactsService.CertUpdateState(8, 0, 1, true),
            () => new DataSecurityDeepFactsService.SelfSignedState(6, 3, 5, 1),
            () => new DataSecurityDeepFactsService.SacState(1, "強制執行"),
            () => new DataSecurityDeepFactsService.DevPolState(5, 3, 2, 1));

        Assert.Equal(5, facts.Count);
        Assert.Equal(5, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            DataSecurityDeepFactsService.CertUpdateKey, DataSecurityDeepFactsService.SelfSignedKey,
            DataSecurityDeepFactsService.SacKey, DataSecurityDeepFactsService.DevPolKey,
            DataSecurityDeepFactsService.DashboardKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
