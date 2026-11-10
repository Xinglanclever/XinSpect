using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// EC 界線與存在性的守門（ULTIMATE-1000 階段二 P2-092／P2-093）。
///
/// <para>
/// <b>核心是「沒有 ECDT」與「沒有 EC」之別</b>，
/// 以及「界線要被說出來」。
/// </para>
/// </summary>
public class EmbeddedControllerFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 表清單讀不到時要說那與沒有ECDT不同()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, null);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.NotSupported, p.Availability);
        Assert.Null(p.NumericValue);
        Assert.Contains("「表清單讀不到」與「表清單裡沒有 ECDT」是不同的斷言", p.UnavailableReason);
    }

    [Fact]
    public void 有ECDT時要報出張數()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["DSDT", "FACP", "ECDT", "SSDT"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.Present, p.Availability);
        Assert.Equal(1.0, p.NumericValue);
        Assert.Contains("有 ECDT（1 張）", p.Value);
    }

    /// <summary>這一條守著本項最重要的一句話。</summary>
    [Fact]
    public void 沒有ECDT時要說那不等於沒有EC()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["DSDT", "FACP"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.NotApplicable, p.Availability);
        // **沒有 ECDT 時不帶數字**——「沒有」不是一個量測結果，而是一個否定。
        // 第一版我寫 Assert.Equal(0.0, ...)，而型別不變式把它收斂成 null，
        // 紅燈逼我面對「這把鍵該不該在沒有 ECDT 時帶 0」——答案是不該。
        Assert.Null(p.NumericValue);
        Assert.Contains("「沒有 ECDT」與「沒有 EC」是不同的斷言", p.UnavailableReason);
        Assert.Contains("ECDT 是可選的表", p.UnavailableReason);
        Assert.Contains("PNP0C09", p.UnavailableReason);
        Assert.Contains("只說「沒有從 ACPI 表看到它」", p.UnavailableReason);
    }

    [Fact]
    public void EC存在性使用ACPI與PnP線索且不直接讀取EC()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Contains("只使用作業系統可列舉的線索", ServiceSource());
        Assert.Contains("ACPI\\PNP0C09", ServiceSource());
        Assert.Contains("不讀寫 0x62／0x66 埠", ServiceSource());
    }

    [Fact]
    public void 簽章比對要忽略大小寫()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ecdt"]);
        var p = facts.Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.Present, p.Availability);
    }

    [Fact]
    public void 沒有ECDT時使用PnP的PNP0C09線索()
    {
        var pnp = new EmbeddedControllerFactsService.PnpEcState(true,
        [
            new EmbeddedControllerFactsService.PnpEcDevice("ACPI EC", "OK", 0),
        ]);
        var p = EmbeddedControllerFactsService.Collect(At, [], pnp)
            .Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.Present, p.Availability);
        Assert.Contains("PnP 已列舉 ACPI EC", p.Value);
        Assert.Contains("不直接讀寫 EC 埠或 EC RAM", p.Value);
    }

    [Fact]
    public void ACPI與PnP都成功但都無線索時不宣稱沒有EC()
    {
        var p = EmbeddedControllerFactsService.Collect(At, [],
                new EmbeddedControllerFactsService.PnpEcState(true, []))
            .Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.NotApplicable, p.Availability);
        Assert.Contains("不能據此斷言平台沒有 EC", p.UnavailableReason);
    }

    [Fact]
    public void PnP失敗且ACPI無ECDT時不猜EC不存在()
    {
        var p = EmbeddedControllerFactsService.Collect(At, [],
                new EmbeddedControllerFactsService.PnpEcState(false, [], "WMI unavailable"))
            .Single(f => f.Key == EmbeddedControllerFactsService.PresentKey);

        Assert.Equal(FactAvailability.NotSupported, p.Availability);
        Assert.Contains("WMI unavailable", p.UnavailableReason);
    }

    /// <summary>
    /// 服務檔的原始碼，<b>去掉強調與參照標記</b>後回傳。
    ///
    /// <para>
    /// <b>為什麼要讀原始碼：</b>「不直接讀寫 EC 埠或 EC RAM」是<b>這一項的界線宣告</b>——
    /// 而 <c>HardwareFact</c> 的第二條不變式（<c>Present</c> ⇒ 說明併進
    /// <c>Value</c>）與 <c>HardwareSnapshotService</c> 的「可用的事實不得帶
    /// unavailableReason」合起來要求這種話不該放在事實的資料欄位裡。
    /// <b>所以它的位置在類別層的 XML 文件註解</b>，而斷言要測的是
    /// 「那句話有沒有被寫下來」，不是「它放在哪一個欄位」。
    /// </para>
    ///
    /// <para>
    /// <b>而去標記是因為兩邊的基準要一致</b>：服務檔的註解用 XML 的
    /// <c>&lt;b&gt;</c> 與 <c>&lt;c&gt;</c>，而斷言的字串寫的是純文字。
    /// </para>
    /// </summary>
    private static string ServiceSource()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(
                   System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, "Services", "EmbeddedControllerFactsService.cs"));
        text = text.Replace("<b>", "").Replace("</b>", "").Replace("**", "");
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"</?c>|<see cref=""([^""]+)""\s*/>",
            m => m.Groups[1].Success ? m.Groups[1].Value : "");
    }

    // ── 界線鍵 ──────────────────────────────────────────────────────────

    [Fact]
    public void 界線鍵要說出本專案不讀寫EC埠()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Equal("本專案不讀寫 EC 埠", b.Value);
    }

    [Fact]
    public void 界線鍵要說出為什麼不碰()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Contains("0x62／0x66", b.UnavailableReason);
        Assert.Contains("有狀態的握手", b.UnavailableReason);
        Assert.Contains("通常由 ACPI 的 EC 驅動持有", b.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著那把鍵存在的理由。</b>
    /// 直接讀 EC 的後果不是「讀到錯的值」，而是「讓別人的值變錯」。
    /// </summary>
    [Fact]
    public void 界線鍵要說出後果是讓別人的值變錯()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Contains("不是「讀到錯的值」", b.UnavailableReason);
        Assert.Contains("讓別人的值變錯", b.UnavailableReason);
    }

    [Fact]
    public void 界線鍵要說出沒說出口的界線會被誤讀()
    {
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var b = facts.Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Contains("這把鍵存在的理由就是說出那件事", b.UnavailableReason);
        Assert.Contains("會被讀成「還沒做」而不是「刻意不做」", b.UnavailableReason);
    }

    [Fact]
    public void 界線鍵在失敗路徑也要出現且內容相同()
    {
        var ok = EmbeddedControllerFactsService.Collect(At, ["ECDT"])
            .Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);
        var none = EmbeddedControllerFactsService.Collect(At, null)
            .Single(f => f.Key == EmbeddedControllerFactsService.BoundaryKey);

        Assert.Equal(ok.Value, none.Value);
        Assert.Equal(ok.UnavailableReason, none.UnavailableReason);
    }

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = EmbeddedControllerFactsService.Collect(At, ["ECDT"])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = EmbeddedControllerFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = EmbeddedControllerFactsService.Collect(At, ["ECDT"]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "EC 事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
