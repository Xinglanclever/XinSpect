using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol1 第 SA-011/012/015/017/018/020 項（安全審計深化）的事實服務測試。</summary>
public class SecurityAuditDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

    // ── SA-011 ADS ──────────────────────────────────────────────────

    [Fact]
    public void ADS_未檢查不等於乾淨()
    {
        var f = SecurityAuditDeepFactsService.AdsFact(At,
            () => new SecurityAuditDeepFactsService.AdsState(2, 500, 3, 3, 12));
        Assert.Contains("未檢查不等於乾淨", f.Value);
        Assert.Contains("12 個檔案因權限未檢查", f.Value);
    }

    [Fact]
    public void ADS_標明只掃系統目錄不是全機()
    {
        var f = SecurityAuditDeepFactsService.AdsFact(At,
            () => new SecurityAuditDeepFactsService.AdsState(2, 300, 0, 0, 0));
        Assert.Contains("範圍聲明", f.Value);
        Assert.Contains("不是全機掃描", f.Value);
        Assert.Contains("全機掃只會把正常現象列成一堆噪音", f.Value);
        Assert.Equal(0.0, f.NumericValue);
    }

    [Fact]
    public void ADS_讀不到不當成乾淨()
    {
        var f = SecurityAuditDeepFactsService.AdsFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不把讀不到當成乾淨", f.UnavailableReason);
    }

    // ── SA-012 已安裝程式簽章 ───────────────────────────────────────

    [Fact]
    public void 簽章_零未通過是值不是沒檢查()
    {
        var f = SecurityAuditDeepFactsService.UninstFact(At,
            () => new SecurityAuditDeepFactsService.UninstState(120, 120, 0, 0, 0));
        Assert.Contains("0 是值——不是「沒檢查」", f.Value);
    }

    [Fact]
    public void 簽章_未通過不是惡意判決()
    {
        var f = SecurityAuditDeepFactsService.UninstFact(At,
            () => new SecurityAuditDeepFactsService.UninstState(120, 110, 8, 2, 0));
        Assert.Contains("未通過不是惡意判決", f.Value);
        Assert.Contains("不是「有多少可疑程式」", f.Value);
        Assert.Equal(8.0, f.NumericValue);
    }

    [Fact]
    public void 簽章_空清單為不適用不是全部通過()
    {
        var f = SecurityAuditDeepFactsService.UninstFact(At,
            () => new SecurityAuditDeepFactsService.UninstState(0, 0, 0, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是「全部通過」", f.UnavailableReason);
    }

    [Fact]
    public void 簽章_未檢查項要另計()
    {
        var f = SecurityAuditDeepFactsService.UninstFact(At,
            () => new SecurityAuditDeepFactsService.UninstState(50, 40, 5, 2, 3));
        Assert.Contains("3 項未檢查（權限或檔案已移除）", f.Value);
    }

    // ── SA-015 登入歷史 ────────────────────────────────────────────

    [Fact]
    public void 登入_無事件不當成沒有登入()
    {
        var f = SecurityAuditDeepFactsService.LogonFact(At,
            () => new SecurityAuditDeepFactsService.LogonState(0, 0, 0, 7, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不當成「沒有登入」", f.UnavailableReason);
    }

    [Fact]
    public void 登入_只到聚合層級不列識別資訊()
    {
        var f = SecurityAuditDeepFactsService.LogonFact(At,
            () => new SecurityAuditDeepFactsService.LogonState(200, 15, 1, 7, 4));
        Assert.Contains("只到聚合層級", f.Value);
        Assert.Contains("不列使用者名稱、不列來源位址、不列時間點", f.Value);
        Assert.Contains("來自 4 個不同的來源數", f.Value);
        Assert.Equal(15.0, f.NumericValue);
    }

    [Fact]
    public void 登入_零失敗如實說期間沒有失敗登入()
    {
        var f = SecurityAuditDeepFactsService.LogonFact(At,
            () => new SecurityAuditDeepFactsService.LogonState(150, 0, 0, 7, 0));
        Assert.Contains("期間沒有失敗登入", f.Value);
    }

    // ── SA-017 服務二進位 ACL ──────────────────────────────────────

    [Fact]
    public void 服務二進位_可寫是面不是已劫持()
    {
        var f = SecurityAuditDeepFactsService.SvcBinFact(At,
            () => new SecurityAuditDeepFactsService.SvcBinState(300, 4, 2, 0));
        Assert.Contains("可寫＝劫持面，不是已劫持", f.Value);
        Assert.Contains("存在一條路不等於有人走了", f.Value);
        Assert.Contains("不列服務名稱或路徑", f.Value);
        Assert.Equal(4.0, f.NumericValue);
    }

    [Fact]
    public void 服務二進位_ACL未檢查要聲明()
    {
        var f = SecurityAuditDeepFactsService.SvcBinFact(At,
            () => new SecurityAuditDeepFactsService.SvcBinState(300, 0, 0, 7));
        Assert.Contains("未檢查不等於安全", f.Value);
    }

    // ── SA-018 PATH 可寫性 ─────────────────────────────────────────

    [Fact]
    public void PATH_零可寫是防禦視角的好結果()
    {
        var f = SecurityAuditDeepFactsService.PathFact(At,
            () => new SecurityAuditDeepFactsService.PathState(20, 0, 0, 0));
        Assert.Contains("0 是值——這是防禦視角下的好結果", f.Value);
    }

    [Fact]
    public void PATH_可寫目錄說明劫持原理()
    {
        var f = SecurityAuditDeepFactsService.PathFact(At,
            () => new SecurityAuditDeepFactsService.PathState(20, 2, 2, 0));
        Assert.Contains("在這些目錄放一個同名的 exe", f.Value);
        Assert.Contains("防禦視角唯讀", f.Value);
        Assert.Contains("也可能是刻意設計", f.Value);
    }

    [Fact]
    public void PATH_空PATH為不適用()
    {
        var f = SecurityAuditDeepFactsService.PathFact(At,
            () => new SecurityAuditDeepFactsService.PathState(0, 0, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    // ── SA-020 服務 SDDL ───────────────────────────────────────────

    [Fact]
    public void SDDL_權限事實不是漏洞判決()
    {
        var f = SecurityAuditDeepFactsService.SddlFact(At,
            () => new SecurityAuditDeepFactsService.SddlState(300, 6, 20, 0));
        Assert.Contains("權限事實不是漏洞判決", f.Value);
        Assert.Contains("有些服務刻意讓一般使用者啟停", f.Value);
        Assert.Contains("判斷留給使用者", f.Value);
        Assert.Contains("不列服務名稱或 SDDL 字串", f.Value);
        Assert.Equal(6.0, f.NumericValue);
    }

    [Fact]
    public void SDDL_無服務為不適用()
    {
        var f = SecurityAuditDeepFactsService.SddlFact(At,
            () => new SecurityAuditDeepFactsService.SddlState(0, 0, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    // ── 整批 ──────────────────────────────────────────────────────

    [Fact]
    public void 整批_六把鍵齊且不重複()
    {
        var facts = SecurityAuditDeepFactsService.Collect(At,
            () => new SecurityAuditDeepFactsService.AdsState(2, 500, 3, 3, 0),
            () => new SecurityAuditDeepFactsService.UninstState(120, 110, 8, 2, 0),
            () => new SecurityAuditDeepFactsService.LogonState(200, 15, 1, 7, 4),
            () => new SecurityAuditDeepFactsService.SvcBinState(300, 4, 2, 0),
            () => new SecurityAuditDeepFactsService.PathState(20, 2, 2, 0),
            () => new SecurityAuditDeepFactsService.SddlState(300, 6, 20, 0));

        Assert.Equal(6, facts.Count);
        Assert.Equal(6, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            SecurityAuditDeepFactsService.AdsKey, SecurityAuditDeepFactsService.UninstKey,
            SecurityAuditDeepFactsService.LogonKey, SecurityAuditDeepFactsService.SvcBinKey,
            SecurityAuditDeepFactsService.PathKey, SecurityAuditDeepFactsService.SddlKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
