using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-045（敏感度逐欄標註）、P1-046（匯出遮蔽預設）、
/// P1-047（L2 選擇性解除）、P1-048（L3 禁項掃描守門）的守門。
///
/// <para><b>守門模式</b>：政策釘死。防範的真實風險是「某個 L3 欄位從顯示或匯出的
/// 某一條路徑漏出去」——這一類缺陷不會產生錯誤訊息，只會在下游被發現。
/// </para>
/// </summary>
public class SensitivityPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, bool sensitive = false) =>
        new(key, "測試", key, "值", "", "測試來源", FactTrustLevel.Reported, sensitive, At,
            null, FactAvailability.Present, null);

    // ── P1-045 分級 ────────────────────────────────────────────────────

    [Fact]
    public void 分級_一般硬體事實是L0()
    {
        Assert.Equal(SensitivityPolicy.Level.L0, SensitivityPolicy.Classify(Fact("cpu.cores")));
    }

    [Fact]
    public void 分級_本機元數據是L1()
    {
        Assert.Equal(SensitivityPolicy.Level.L1, SensitivityPolicy.Classify(Fact("system.name")));
        Assert.Equal(SensitivityPolicy.Level.L1, SensitivityPolicy.Classify(Fact("smb.share")));
    }

    [Fact]
    public void 分級_標記為敏感的是L2()
    {
        Assert.Equal(SensitivityPolicy.Level.L2, SensitivityPolicy.Classify(Fact("tpm.ek", sensitive: true)));
    }

    [Fact]
    public void 分級_禁項前綴一律是L3()
    {
        foreach (string key in new[] { "secret.token", "credential.user", "password.hash", "sam.hash" })
            Assert.Equal(SensitivityPolicy.Level.L3, SensitivityPolicy.Classify(Fact(key)));
    }

    [Fact]
    public void 分級_L3優先於其他判準()
    {
        // 一個同時「標了敏感」又落在禁項前綴的鍵，必須按 L3 處理——
        // 否則只要忘了標 Sensitive 就會降級成可輸出的 L2。
        Assert.Equal(SensitivityPolicy.Level.L3,
            SensitivityPolicy.Classify(Fact("secret.key", sensitive: true)));
    }

    [Fact]
    public void 分級_L3判斷不分大小寫()
    {
        Assert.Equal(SensitivityPolicy.Level.L3, SensitivityPolicy.Classify(Fact("SECRET.TOKEN")));
    }

    // ── P1-046 匯出遮蔽預設 ─────────────────────────────────────────────

    [Fact]
    public void 遮蔽_預設要連L2一起遮()
    {
        var facts = new[]
        {
            Fact("cpu.cores"),
            Fact("system.name"),
            Fact("tpm.ek", sensitive: true),
        };
        var fact = SensitivityPolicy.ToMaskFact(At, facts, SensitiveValuePolicy.Redact);

        Assert.Equal(2.0, fact.NumericValue);                 // L1 + L2
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("L1 1", fact.Value);
    }

    [Fact]
    public void 遮蔽_主動保留L2時不得標成Present()
    {
        var facts = new[] { Fact("system.name"), Fact("tpm.ek", sensitive: true) };
        var fact = SensitivityPolicy.ToMaskFact(At, facts, SensitiveValuePolicy.Preserve);

        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：1 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(fact.NumericValue);
        Assert.Contains("原本帶了數字 1", fact.UnavailableReason);
        // 只剩 L1
        Assert.NotEqual(FactAvailability.Present, fact.Availability);
        Assert.Contains("使用者主動選擇", fact.UnavailableReason);
    }

    [Fact]
    public void 遮蔽_沒有匯出時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            SensitivityPolicy.ToMaskFact(At, [], SensitiveValuePolicy.Redact).Availability);
    }

    // ── P1-047 L2 選擇性解除 ────────────────────────────────────────────

    [Fact]
    public void 解除_沒有可解除的類別時標不適用()
    {
        var fact = SensitivityPolicy.ToReleaseFact(At, [], []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    [Fact]
    public void 解除_未解除任何一類時標Present()
    {
        var fact = SensitivityPolicy.ToReleaseFact(At, ["網路", "儲存"], []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("未解除任何一類", fact.Value);
    }

    [Fact]
    public void 解除_逐類解除且要留紀錄()
    {
        var fact = SensitivityPolicy.ToReleaseFact(At, ["網路", "儲存", "帳號"], ["網路"]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("1/3", fact.Value);
        Assert.Contains("可稽核的紀錄", fact.UnavailableReason);
    }

    // ── P1-048 L3 禁項掃描守門 ──────────────────────────────────────────

    [Fact]
    public void 禁項掃描_乾淨的事實清單回空()
    {
        var leaks = SensitivityPolicy.ForbiddenLeaks(
            [Fact("cpu.cores"), Fact("system.name"), Fact("tpm.ek", sensitive: true)]);
        Assert.Empty(leaks);
    }

    [Fact]
    public void 禁項掃描_任何L3出現在輸出中就必須被抓到()
    {
        var leaks = SensitivityPolicy.ForbiddenLeaks(
            [Fact("cpu.cores"), Fact("secret.token")]);
        Assert.Single(leaks);
        Assert.Equal("secret.token", leaks[0].Key);
    }

    [Fact]
    public void 禁項掃描_正對照_已知會外洩的清單必須紅()
    {
        // 沒有這一條，這個守門可能因為前綴清單被清空而永遠是綠的——
        // 而永遠是綠的守門等於沒有守門。
        var mustBeCaught = new[]
        {
            Fact("secret.token"), Fact("credential.user"), Fact("key.private.pem"),
            Fact("dpapi.blob"), Fact("lsa.secret"), Fact("ntlm.hash"),
        };
        foreach (var f in mustBeCaught)
            Assert.Equal(SensitivityPolicy.Level.L3, SensitivityPolicy.Classify(f));
    }

    [Fact]
    public void 敏感度統計_四級都要出現包括零()
    {
        var fact = SensitivityPolicy.ToLevelFact(At, [Fact("cpu.cores")]);
        Assert.Contains("L0 1", fact.Value);
        Assert.Contains("L1 0", fact.Value);
        Assert.Contains("L2 0", fact.Value);
        Assert.Contains("L3 0", fact.Value);
    }

    [Fact]
    public void 敏感度統計_有L3時要說出不得輸出()
    {
        var fact = SensitivityPolicy.ToLevelFact(At, [Fact("secret.token")]);
        Assert.Contains("L3 1", fact.Value);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("不得出現在任何輸出中", fact.Value);
    }
}
