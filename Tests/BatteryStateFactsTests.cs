using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 電池充放狀態的守門（ULTIMATE-1000 階段二 P2-095）。
///
/// <para>
/// <b>核心是「讀不到」與「沒有電池」之別</b>，
/// 以及它與 <c>pw.battery.summary</c>（電池健康）的區隔。
/// </para>
/// </summary>
public class BatteryStateFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static BatteryInfo Info(bool present = true, int percent = 80,
        string status = "放電中", string name = "TestBat", string chemistry = "鋰離子（Li-ion）")
        => new()
        {
            Present = present, ChargePercent = percent, StatusText = status,
            Name = name, Chemistry = chemistry,
        };

    [Fact]
    public void 讀取失敗時要標讀取錯誤並說那與沒有電池不同()
    {
        var facts = BatteryStateFactsService.Collect(At, null);
        var s = facts.Single();

        Assert.Equal(FactAvailability.ReadError, s.Availability);
        Assert.Null(s.NumericValue);
        Assert.Contains("「讀不到」與「這台機器沒有電池」是不同的斷言", s.UnavailableReason);
        Assert.Contains("會讓一次查詢失敗看起來像一個硬體事實", s.UnavailableReason);
    }

    [Fact]
    public void 沒有電池時要說那是確定的答案()
    {
        var facts = BatteryStateFactsService.Collect(At, Info(present: false));
        var s = facts.Single();

        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Equal("沒有電池", s.Value);
        Assert.Null(s.NumericValue);
        Assert.Contains("這是確定的答案，不是讀不到", s.UnavailableReason);
        Assert.Contains("桌機與伺服器沒有電池是正常的", s.UnavailableReason);
    }

    [Fact]
    public void 有電池時要報出狀態與電量()
    {
        var facts = BatteryStateFactsService.Collect(At, Info(percent: 42, status: "充電中"));
        var s = facts.Single();

        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Equal(42.0, s.NumericValue);
        Assert.Contains("充電中、電量 42%", s.Value);
    }

    [Fact]
    public void 要報出電池名稱與化學類型()
    {
        var facts = BatteryStateFactsService.Collect(At, Info());
        var s = facts.Single();

        Assert.Contains("（TestBat、鋰離子（Li-ion））", s.Value);
    }

    [Fact]
    public void 沒有名稱或化學類型時不得留下空括號()
    {
        var facts = BatteryStateFactsService.Collect(At,
            Info(name: "", chemistry: ""));
        var s = facts.Single();

        Assert.DoesNotContain("（）", s.Value);
        Assert.Contains("放電中、電量 80%", s.Value);
    }

    /// <summary>
    /// <b>這一條守著本項與既有鍵的區隔。</b>
    /// 充放狀態與電池健康是兩個問題，混成一把鍵會讓兩種情況看起來一樣。
    /// </summary>
    [Fact]
    public void 要說出它與電池健康是不同的問題()
    {
        var facts = BatteryStateFactsService.Collect(At, Info());
        var s = facts.Single();

        // **這一條測的是「區隔有沒有被寫下來」，不是「它放在哪一個欄位」。**
        // 而它現在的位置是服務檔的類別層 XML 文件註解——
        // Present 的事實不得帶理由欄（那條不變式），而這一段話不是「為什麼讀不到」。
        Assert.Contains("現在是什麼狀況", ServiceSource());
        Assert.Contains("這顆電池老了沒有", ServiceSource());
        Assert.Contains("pw.battery.summary", ServiceSource());
        Assert.Contains("健康但沒接電」與「接電但快掛了」看起來一樣", ServiceSource());
    }

    [Fact]
    public void 要說出它不預測續航也不判斷健康()
    {
        var facts = BatteryStateFactsService.Collect(At, Info());
        var s = facts.Single();

        Assert.Contains("不預測續航、不判斷健康", ServiceSource());
    }

    /// <summary>
    /// 服務檔的原始碼，<b>去掉強調與參照標記</b>後回傳。
    ///
    /// <para>
    /// <b>為什麼要讀原始碼：</b>「它與電池健康是不同的問題」以及「不預測續航、
    /// 不判斷健康」都是<b>這一項的界線宣告</b>——而 <c>HardwareFact</c> 的第二條
    /// 不變式（<c>Present</c> ⇒ 說明併進 <c>Value</c>）與
    /// <c>HardwareSnapshotService</c> 的「可用的事實不得帶 unavailableReason」
    /// 合起來要求這種話不該放在事實的資料欄位裡。
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
            dir!.FullName, "Services", "BatteryStateFactsService.cs"));
        text = text.Replace("<b>", "").Replace("</b>", "").Replace("**", "");
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"</?c>|<see cref=""([^""]+)""\s*/>",
            m => m.Groups[1].Success ? m.Groups[1].Value : "");
    }

    // ── 映射的單一來源 ──────────────────────────────────────────────────

    /// <summary>
    /// <b>狀態文字的映射必須來自既有的 <c>BatteryService.StatusText</c>，
    /// 而不是本服務自己複製一份。</b>
    /// 兩個狀態表就是兩個漂移點。
    /// </summary>
    [Fact]
    public void 狀態文字要與既有的映射一致()
    {
        // 既有的映射是 internal，測試專案透過 InternalsVisibleTo 看得到。
        foreach (int code in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })
        {
            string expected = BatteryService.StatusText(code);
            var facts = BatteryStateFactsService.Collect(At, Info(status: expected));
            Assert.Contains(expected, facts.Single().Value);
        }
    }

    [Fact]
    public void 未知的狀態碼要回破折號而不是崩潰()
    {
        Assert.Equal("—", BatteryService.StatusText(99));
        Assert.Equal("—", BatteryService.StatusText(0));
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = BatteryStateFactsService.Collect(At, Info());
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "電池狀態事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
