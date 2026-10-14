using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>iSCSI 工作階段深化（ST-020）單元測試。</summary>
public class IscsiSessionFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── 工作階段 ─────────────────────────────────────────────────────────

    [Fact]
    public void 工作階段_陳述登入數與iSER()
    {
        var rows = new List<IscsiSessionFactsService.SessionRow>
        {
            new(true, false), new(true, true), new(false, false),
        };
        var f = Fact(IscsiSessionFactsService.Collect(At, sessions: () => rows), "st.iscsi.sessions");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("3 條工作階段", f.Value);
        Assert.Contains("已登入 2", f.Value);
        Assert.Contains("未登入 1", f.Value);
        Assert.Contains("iSER（RDMA）工作階段 1 條", f.Value);
    }

    [Fact]
    public void 工作階段_未連線如實是零不是不適用()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At, sessions: () => []), "st.iscsi.sessions");
        // 服務裝了但還沒接目標，是正常的中間狀態。
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("目前沒有任何 iSCSI 工作階段", f.Value);
        Assert.Contains("正常的中間狀態", f.Value);
    }

    [Fact]
    public void 工作階段_不列目標位址與IQN()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At, sessions: () => [new(true, false)]),
            "st.iscsi.sessions");
        Assert.Contains("不列目標位址與 IQN", f.Value);
        Assert.Contains("內網儲存架構", f.Value);
    }

    [Fact]
    public void 工作階段_登入不等於IO正常()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At, sessions: () => [new(true, false)]),
            "st.iscsi.sessions");
        Assert.Contains("已登入不等於 I/O 正常", f.Value);
    }

    [Fact]
    public void 工作階段_讀取失敗是ReadError不畫成零()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At, sessions: () => null), "st.iscsi.sessions");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不畫成 0 條", f.UnavailableReason);
    }

    // ── 多路徑 ───────────────────────────────────────────────────────────

    [Fact]
    public void 多路徑_計數與路徑總數()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At,
            paths: () => new IscsiSessionFactsService.PathState(3, 1, 5)), "st.iscsi.paths");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("iSCSI 磁碟 3 顆", f.Value);
        Assert.Contains("其中 1 顆走多路徑", f.Value);
        Assert.Contains("路徑總數 5", f.Value);
        Assert.Equal(5d, f.NumericValue);
    }

    [Fact]
    public void 多路徑_單路徑不是缺點()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At,
            paths: () => new IscsiSessionFactsService.PathState(2, 0, 2)), "st.iscsi.paths");
        Assert.Contains("單路徑不是缺點", f.Value);
    }

    [Fact]
    public void 多路徑_不判斷路徑好壞()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At,
            paths: () => new IscsiSessionFactsService.PathState(1, 1, 2)), "st.iscsi.paths");
        Assert.Contains("不判斷哪條路徑比較好", f.Value);
        Assert.Contains("有兩條路不等於兩條都健康", f.Value);
    }

    [Fact]
    public void 多路徑_沒有iSCSI碟是零是值()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At,
            paths: () => new IscsiSessionFactsService.PathState(0, 0, 0)), "st.iscsi.paths");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0d, f.NumericValue);
        Assert.Contains("沒有以 iSCSI 掛載的磁碟", f.Value);
    }

    [Fact]
    public void 多路徑_讀取失敗是ReadError()
    {
        var f = Fact(IscsiSessionFactsService.Collect(At, paths: () => null), "st.iscsi.paths");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 契約 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 兩把鍵齊()
    {
        var facts = IscsiSessionFactsService.Collect(At);
        Assert.Equal(new[] { "st.iscsi.paths", "st.iscsi.sessions" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 兩項互不影響()
    {
        var facts = IscsiSessionFactsService.Collect(At,
            sessions: () => throw new InvalidOperationException("s"),
            paths: () => new IscsiSessionFactsService.PathState(1, 0, 1));
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "st.iscsi.sessions").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "st.iscsi.paths").Availability);
    }
}
