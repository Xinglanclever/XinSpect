using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SRUM 讀回（FS-004）的單元測試。
/// 這一項的紅線是<b>把最敏感的那份資料庫只交付骨架</b>：測試要能證明
/// 「逐筆欄位從來沒有出現過」，而不只是「我們記得不要寫」。
/// </summary>
public class SrumReadbackFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static SrumDatabase Db(
        int tables = 12, long rows = 480_000,
        DateOnly? oldest = null, DateOnly? newest = null)
        => new(true, tables, rows,
               oldest ?? new DateOnly(2026, 9, 14), newest ?? new DateOnly(2026, 10, 13));

    // ── 未讀取 ───────────────────────────────────────────────────────────

    [Fact]
    public void 未注入探針是不適用不是零張表()
    {
        var facts = SrumReadbackService.Collect(At);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
        Assert.All(facts, f => Assert.Contains("未讀不等於沒有這個資料庫", f.UnavailableReason));
    }

    [Fact]
    public void 未讀取的理由說出ESE未對準出處不出值()
    {
        var r = Fact(SrumReadbackService.Collect(At), "fs.srum.count").UnavailableReason;
        Assert.Contains("ESE", r);
        Assert.Contains("未對準出處前不出值", r);
    }

    // ── 資料庫不存在 ─────────────────────────────────────────────────────

    [Fact]
    public void 沒有資料庫是沒有這份資料不是零筆()
    {
        var facts = SrumReadbackService.Collect(At, new SrumDatabase(false, 0, 0, null, null));

        var c = Fact(facts, "fs.srum.count");
        Assert.Equal(FactAvailability.Present, c.Availability);
        Assert.Equal(0, c.NumericValue);
        Assert.Contains("沒有這份資料＝沒有這份資料", c.Value);
        Assert.Contains("不從缺檔案推論使用情況", c.Value);
    }

    [Fact]
    public void 沒有資料庫時涵蓋範圍不以零天冒充()
    {
        var f = Fact(SrumReadbackService.Collect(At, new SrumDatabase(false, 0, 0, null, null)),
            "fs.srum.range");

        Assert.Null(f.NumericValue);
        Assert.Contains("不以「0 天」冒充", f.Value);
    }

    // ── 有資料庫 ─────────────────────────────────────────────────────────

    [Fact]
    public void 有資料庫時報結構與範圍()
    {
        var facts = SrumReadbackService.Collect(At, Db());

        Assert.Contains("12 張表", Fact(facts, "fs.srum.count").Value);
        Assert.Contains("480,000 列", Fact(facts, "fs.srum.count").Value);
        Assert.Contains("2026-09-14 到 2026-10-13", Fact(facts, "fs.srum.range").Value);
        Assert.Equal(30, Fact(facts, "fs.srum.range").NumericValue);
    }

    [Fact]
    public void 列數明說是粗估()
    {
        var f = Fact(SrumReadbackService.Collect(At, Db()), "fs.srum.count");
        Assert.Contains("列數是粗估", f.Value);
        Assert.Contains("不是精確計數", f.Value);
    }

    [Fact]
    public void 涵蓋範圍讀不到時留白()
    {
        var f = Fact(SrumReadbackService.Collect(At,
            new SrumDatabase(true, 5, 100, null, null)), "fs.srum.range");

        Assert.Null(f.NumericValue);
        Assert.Contains("不以 0 天冒充", f.Value);
    }

    [Fact]
    public void 只有一端有日期時不硬算範圍()
    {
        var f = Fact(SrumReadbackService.Collect(At,
            new SrumDatabase(true, 5, 100, new DateOnly(2026, 9, 1), null)), "fs.srum.range");
        Assert.Null(f.NumericValue);
        Assert.Contains("涵蓋範圍讀不到", f.Value);
    }

    // ── 只讀骨架不讀內容 ─────────────────────────────────────────────────

    [Fact]
    public void 值裡明說只讀骨架不讀內容()
    {
        var f = Fact(SrumReadbackService.Collect(At, Db()), "fs.srum.count");
        Assert.Contains("只讀骨架不讀內容", f.Value);
        Assert.Contains("本項的輸出裡沒有任何一個逐筆欄位", f.Value);
    }

    [Fact]
    public void 三個敏感維度逐一點名不讀()
    {
        var f = Fact(SrumReadbackService.Collect(At, Db()), "fs.srum.count");
        Assert.Contains("不列行程", f.Value);
        Assert.Contains("不列帳號", f.Value);
        Assert.Contains("不列資料量", f.Value);
        Assert.Contains("從一開始就沒有讀", f.Value);
    }

    [Fact]
    public void 日期只到日不到時()
    {
        var f = Fact(SrumReadbackService.Collect(At, Db()), "fs.srum.range");
        Assert.Contains("日期只到「日」不到「時」", f.Value);
    }

    [Fact]
    public void 涵蓋範圍短不代表機器沒在用()
    {
        var f = Fact(SrumReadbackService.Collect(At, Db()), "fs.srum.range");
        Assert.Contains("不代表機器沒在用", f.Value);
        Assert.Contains("保留期", f.Value);
    }

    [Fact]
    public void 不列完整路徑以外的識別資訊()
    {
        // 來源字串只提到固定的系統路徑，不含使用者目錄或機器名
        var f = Fact(SrumReadbackService.Collect(At, Db()), "fs.srum.count");
        Assert.Contains("SRUDB.dat", f.Source);
        Assert.DoesNotContain(Environment.UserName, f.Value);
        Assert.DoesNotContain(Environment.UserName, f.Source);
    }

    [Fact]
    public void 鍵名就是目錄登記的那兩把()
    {
        var keys = SrumReadbackService.Collect(At).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "fs.srum.count", "fs.srum.range" },
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
