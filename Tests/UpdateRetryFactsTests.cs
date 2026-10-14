using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>更新失敗重試鏈（UF-002）單元測試。</summary>
public class UpdateRetryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static WuHistoryEntry E(string title, uint code) => new(title, code, At, "更新");

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── KB 抽取 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-10 適用於 Windows 的安全性更新 (KB5034123)", "KB5034123")]
    [InlineData("kb5034123 lowercase", "KB5034123")]
    [InlineData("Security Update KB2267602 (Definition 1.0)", "KB2267602")]
    public void 抽得到KB編號(string title, string expected)
        => Assert.Equal(expected, UpdateRetryFactsService.ExtractKb(title));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("適用於 Windows 的安全性更新")]
    [InlineData("KB123")]              // 位數不足，不是完整 KB 編號
    [InlineData("KB123456789")]        // 位數過多
    public void 抽不到就不硬湊(string title)
        => Assert.Equal("", UpdateRetryFactsService.ExtractKb(title));

    // ── 鏈的建構 ─────────────────────────────────────────────────────────

    [Fact]
    public void 同一KB多次失敗收成一條鏈()
    {
        var chains = UpdateRetryFactsService.BuildChains(
        [
            ("KB5034123", 4u), ("KB5034123", 4u), ("KB5034123", 2u),
        ]);
        var c = Assert.Single(chains);
        Assert.Equal("KB5034123", c.Id);
        Assert.Equal(2, c.Failures);
        Assert.Equal(3, c.Attempts);   // 一次成功算進嘗試，不算失敗
    }

    [Fact]
    public void 不同KB各失敗一次是兩條不成鏈()
    {
        var chains = UpdateRetryFactsService.BuildChains([("KB1", 4u), ("KB2", 4u)]);
        Assert.Equal(2, chains.Count);
        // 這是本項存在的理由：wu.history.failed 數到 2，但那裡看不出這兩筆毫無關聯。
        Assert.Empty(UpdateRetryFactsService.Stuck(chains));
    }

    [Fact]
    public void 失敗達門檻才算卡住()
    {
        var chains = UpdateRetryFactsService.BuildChains(
        [
            ("KB1", 4u), ("KB1", 4u), ("KB1", 4u),   // 三次＝卡住
            ("KB2", 4u), ("KB2", 4u),                 // 兩次＝還沒
        ]);
        var stuck = Assert.Single(UpdateRetryFactsService.Stuck(chains));
        Assert.Equal("KB1", stuck.Id);
    }

    [Fact]
    public void 中止碼5也算失敗()
    {
        var chains = UpdateRetryFactsService.BuildChains(
            [("KB1", 5u), ("KB1", 5u), ("KB1", 5u)]);
        Assert.Single(UpdateRetryFactsService.Stuck(chains));
    }

    [Fact]
    public void 無編號的記錄歸同一桶()
    {
        var rows = new List<(string, uint)>
        {
            ("", 4u), ("", 4u), ("", 4u),
        };
        var chains = UpdateRetryFactsService.BuildChains(rows);
        var c = Assert.Single(chains);
        Assert.Equal("", c.Id);
        Assert.Equal(3, c.Failures);
    }

    [Fact]
    public void KB大小寫視為同一個()
    {
        var chains = UpdateRetryFactsService.BuildChains([("KB5034123", 4u), ("kb5034123", 4u)]);
        Assert.Single(chains);
    }

    // ── 事實值 ───────────────────────────────────────────────────────────

    [Fact]
    public void 沒有失敗時兩把鍵都是零是值()
    {
        var facts = UpdateRetryFactsService.Collect(At, [E("KB1", 2u), E("KB2", 2u)]);
        Assert.Equal(0d, Fact(facts, "uf.retry.summary").NumericValue);
        Assert.Contains("沒有任何失敗", Fact(facts, "uf.retry.summary").Value);
        Assert.Contains("0 個", Fact(facts, "uf.retry.count").Value);
    }

    [Fact]
    public void 卡住的更新會列出編號與次數()
    {
        var facts = UpdateRetryFactsService.Collect(At,
        [
            E("更新 (KB5034123)", 4u), E("更新 (KB5034123)", 4u), E("更新 (KB5034123)", 4u),
        ]);
        Assert.Contains("卡住 1 個", Fact(facts, "uf.retry.summary").Value);
        Assert.Contains("KB5034123（失敗 3／嘗試 3 次）", Fact(facts, "uf.retry.summary").Value);
        Assert.Equal(1d, Fact(facts, "uf.retry.count").NumericValue);
    }

    [Fact]
    public void 低於門檻的失敗不算卡住()
    {
        var facts = UpdateRetryFactsService.Collect(At, [E("KB1", 4u), E("KB1", 4u)]);
        Assert.Equal(0d, Fact(facts, "uf.retry.count").NumericValue);
        Assert.Contains("沒有達到卡住門檻", Fact(facts, "uf.retry.summary").Value);
    }

    [Fact]
    public void 值裡要說明重試不等於失敗()
    {
        var facts = UpdateRetryFactsService.Collect(At, [E("KB1", 4u)]);
        Assert.Contains("重試不等於失敗", Fact(facts, "uf.retry.summary").Value);
        Assert.Contains("看的是<b>失敗次數</b>不是嘗試次數", Fact(facts, "uf.retry.summary").Value);
    }

    [Fact]
    public void 值裡要說明用KB不用標題且不斷言原因()
    {
        var facts = UpdateRetryFactsService.Collect(At, [E("KB1", 4u)]);
        Assert.Contains("匯總用 KB 編號不用標題", Fact(facts, "uf.retry.summary").Value);
        Assert.Contains("不斷言原因", Fact(facts, "uf.retry.summary").Value);
    }

    [Fact]
    public void 無編號的鏈在值裡標成無KB編號()
    {
        var facts = UpdateRetryFactsService.Collect(At,
        [
            E("適用於 Windows 的更新", 4u), E("適用於 Windows 的更新", 4u), E("適用於 Windows 的更新", 4u),
        ]);
        Assert.Contains("無 KB 編號", Fact(facts, "uf.retry.summary").Value);
    }

    [Fact]
    public void 歷史讀不到是ReadError不當成沒有失敗()
    {
        var facts = UpdateRetryFactsService.Collect(At, null);
        foreach (var f in facts)
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("讀不到就是不猜", f.UnavailableReason);
            Assert.Equal("", f.Value);
        }
    }

    [Fact]
    public void 失敗分布最多列五條()
    {
        var rows = Enumerable.Range(1, 8)
            .Select(i => E($"更新 (KB503412{i})", 4u)).ToList();
        var facts = UpdateRetryFactsService.Collect(At, rows);
        // 八個不同 KB 各失敗一次：全部低於門檻，但分布要看得到（取前五）。
        string value = Fact(facts, "uf.retry.summary").Value;
        Assert.Contains("失敗分布：", value);
        // 只數「失敗分布：」之後的明細——門檻說明裡的「同一個識別失敗 3 次」也符合
        // 「失敗 N 次」的形式，整段掃會多算一條。這個坑我自己踩過一次。
        string distribution = value[value.IndexOf("失敗分布：", StringComparison.Ordinal)..];
        int detailCount = System.Text.RegularExpressions.Regex.Matches(
            distribution, @"失敗 \d+ 次").Count;
        Assert.Equal(5, detailCount);
    }
}
