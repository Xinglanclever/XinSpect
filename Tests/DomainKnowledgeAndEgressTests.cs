using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-041／P1-042／P1-043（域知識包）與
/// P1-049／P1-050（出網許可與觸發宣告）的守門。
/// </summary>
public class DomainKnowledgeAndEgressTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    // ── P1-041 微碼 CVE ────────────────────────────────────────────────

    [Fact]
    public void 微碼_查不到修訂版時要說查不到不等於沒問題()
    {
        var fact = DomainKnowledgeReport.ToMicrocodeCveFact(At, "0xf4",
            DomainKnowledgeReport.Coverage.NotCovered, []);

        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("不在知識包內", fact.Value);
        Assert.Contains("不等於", fact.UnavailableReason);
    }

    [Fact]
    public void 微碼_有對照且無CVE時要聲明那是當下版本的結論()
    {
        var fact = DomainKnowledgeReport.ToMicrocodeCveFact(At, "0xf4",
            DomainKnowledgeReport.Coverage.Covered, []);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("不是「這個修訂版永遠安全」", fact.UnavailableReason);
    }

    [Fact]
    public void 微碼_有CVE時要列出()
    {
        var fact = DomainKnowledgeReport.ToMicrocodeCveFact(At, "0xf4",
            DomainKnowledgeReport.Coverage.Covered, ["CVE-2023-0001", "CVE-2023-0002"]);

        Assert.Equal(2.0, fact.NumericValue);
        Assert.Contains("CVE-2023-0001", fact.Value);
    }

    [Fact]
    public void 微碼_讀不到修訂版時不冒充有問題也不冒充安全()
    {
        var fact = DomainKnowledgeReport.ToMicrocodeCveFact(At, null,
            DomainKnowledgeReport.Coverage.NoLocalValue, []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不是「沒有問題」", fact.UnavailableReason);
    }

    // ── P1-042 dbx 版本 ────────────────────────────────────────────────

    [Fact]
    public void dbx_本機不落後時標Present()
    {
        var fact = DomainKnowledgeReport.ToDbxFact(At, currentSvn: 300, knownLatestSvn: 300);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("≥", fact.Value);
    }

    [Fact]
    public void dbx_落後時標未確認且說出差距()
    {
        var fact = DomainKnowledgeReport.ToDbxFact(At, currentSvn: 200, knownLatestSvn: 300);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("差 100 級", fact.Value);
        Assert.Contains("落後本身不是故障", fact.UnavailableReason);
    }

    [Fact]
    public void dbx_沒有已知最新值時要說有dbx不等於dbx夠新()
    {
        var fact = DomainKnowledgeReport.ToDbxFact(At, currentSvn: 200, knownLatestSvn: null);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("不等於", fact.UnavailableReason);
    }

    [Fact]
    public void dbx_讀不到SVN時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            DomainKnowledgeReport.ToDbxFact(At, null, 300).Availability);
    }

    // ── P1-043 PSU 型號 ────────────────────────────────────────────────

    [Fact]
    public void 電源_查不到型號時不猜瓦數()
    {
        var fact = DomainKnowledgeReport.ToPsuFact(At, "ACME-850", knownWattage: null);
        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("猜出來的容量", fact.UnavailableReason);
    }

    [Fact]
    public void 電源_有對照時要聲明那是標稱值()
    {
        var fact = DomainKnowledgeReport.ToPsuFact(At, "ACME-850", knownWattage: 850);
        Assert.Equal(850.0, fact.NumericValue);
        Assert.Contains("不是這顆電源當下的輸出能力", fact.UnavailableReason);
    }

    [Fact]
    public void 電源_沒有型號時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            DomainKnowledgeReport.ToPsuFact(At, "  ", 850).Availability);
    }

    // ── P1-049 出網許可清單 ────────────────────────────────────────────

    [Fact]
    public void 許可清單_空清單是Present且說那是目標()
    {
        var fact = EgressPolicy.ToAllowlistFact(At, []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("沒有任何出網通路", fact.Value);
        Assert.Contains("空清單是常態也是目標", fact.UnavailableReason);
    }

    [Fact]
    public void 許可清單_使用者主動觸發才算許可()
    {
        var fact = EgressPolicy.ToAllowlistFact(At,
            [new EgressPolicy.AllowEntry("檢查更新", EgressPolicy.TriggerKind.UserInitiated, "使用者按下按鈕")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(1.0, fact.NumericValue);
    }

    [Fact]
    public void 許可清單_背景排程要標讀取失敗()
    {
        var fact = EgressPolicy.ToAllowlistFact(At,
            [new EgressPolicy.AllowEntry("遙測", EgressPolicy.TriggerKind.Scheduled, "每日回報")]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("不得存在", fact.UnavailableReason);
    }

    [Fact]
    public void 許可清單_沒有理由的登記不算許可()
    {
        var entry = new EgressPolicy.AllowEntry("檢查更新", EgressPolicy.TriggerKind.UserInitiated, "");
        Assert.False(entry.IsPermitted);
    }

    [Fact]
    public void 許可清單_四種觸發只有使用者主動是允許的()
    {
        foreach (var kind in Enum.GetValues<EgressPolicy.TriggerKind>())
        {
            var entry = new EgressPolicy.AllowEntry("測試", kind, "有理由");
            Assert.Equal(kind == EgressPolicy.TriggerKind.UserInitiated, entry.IsPermitted);
        }
    }

    // ── P1-050 出網觸發 ────────────────────────────────────────────────

    [Fact]
    public void 觸發_沒有通路與沒有觸發要分開講()
    {
        var noChannels = EgressPolicy.ToTriggerFact(At, registeredChannels: 0, observed: []);
        var noTrigger = EgressPolicy.ToTriggerFact(At, registeredChannels: 2, observed: []);

        Assert.Contains("沒有任何出網通路", noChannels.Value);
        Assert.Contains("都未被使用", noTrigger.Value);
        Assert.Contains("不等於沒有通路", noTrigger.UnavailableReason);
    }

    [Fact]
    public void 觸發_只有使用者主動時標Present()
    {
        var fact = EgressPolicy.ToTriggerFact(At, 1, [EgressPolicy.TriggerKind.UserInitiated]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("使用者主動", fact.Value);
    }

    [Fact]
    public void 觸發_非使用者主動的觸發要標讀取失敗()
    {
        var fact = EgressPolicy.ToTriggerFact(At, 2,
            [EgressPolicy.TriggerKind.UserInitiated, EgressPolicy.TriggerKind.OnStartup]);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("唯一允許的觸發方式", fact.UnavailableReason);
    }
}
