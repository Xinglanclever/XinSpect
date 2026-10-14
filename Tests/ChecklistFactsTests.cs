using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 自訂檢查清單（NG-002）單元測試。
/// 重點在「不做靜默部分成功」與「行號對得上檔案」，以及「認不得的鍵名不算錯」。
/// </summary>
public class ChecklistFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "in.self", "st.wear.summary", "cpu.thermal.events",
    };

    // ── 鍵名抽取 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("`in.self` 檢查自我完整性", "in.self")]
    [InlineData("[st.wear.summary] 固態碟磨損", "st.wear.summary")]
    [InlineData("cpu.thermal.events：看有沒有節流", "cpu.thermal.events")]
    [InlineData("cpu.thermal.events: 半形冒號也可以", "cpu.thermal.events")]
    public void 三種寫法都抽得到鍵名(string line, string expected)
        => Assert.Equal(expected, ChecklistFactsService.ExtractKey(line));

    [Theory]
    [InlineData("這是一段純文字說明")]
    [InlineData("請檢查一下散熱：看看風扇有沒有卡住")]   // 冒號前不是鍵名形狀
    [InlineData("")]
    [InlineData("`不是鍵名` 因為有空格")]
    public void 抽不到鍵名時回null(string line)
        => Assert.Null(ChecklistFactsService.ExtractKey(line));

    [Theory]
    [InlineData("aa.bb", true)]
    [InlineData("aa.bb.cc", true)]
    [InlineData("a.b", true)]           // 長度 3 且在 3–60 之間 → 合法；
                                        // 這裡刻意不改實作去迎合測試：單字母前綴在鍵表裡
                                        // 不存在，但「形狀」判定不該替鍵表代言。
    [InlineData("ab", false)]           // 太短（沒有點）
    [InlineData("AA.bb", false)]        // 大寫
    [InlineData(".aa.bb", false)]       // 開頭是點
    [InlineData("aa.bb.", false)]       // 結尾是點
    [InlineData("aa..bb", false)]       // 連續點
    [InlineData("aa-bb.cc", false)]     // 連字號
    public void 鍵名形狀判定(string s, bool expected)
        => Assert.Equal(expected, ChecklistFactsService.IsKeyShape(s));

    // ── 逐行判讀 ─────────────────────────────────────────────────────────

    [Fact]
    public void 行號從1起算且對應檔案實際行()
    {
        var lines = new[] { "# 註解", "", "`in.self` 檢查", "純文字步驟" };
        var r = ChecklistFactsService.Parse(lines, Known);
        Assert.Equal(2, r.Valid.Count);
        Assert.Equal(3, r.Valid[0].Line);      // 第三行
        Assert.Equal(4, r.Valid[1].Line);      // 第四行
        Assert.Equal(2, r.Comments);
    }

    [Fact]
    public void 註解與空白行不計入有效項()
    {
        var r = ChecklistFactsService.Parse(["# 只有註解", "", "   ", "# 再一個"], Known);
        Assert.Empty(r.Valid);
        Assert.Empty(r.Invalid);
        Assert.Equal(4, r.Comments);
    }

    [Fact]
    public void 認不得的鍵名列為未知不是無效()
    {
        var r = ChecklistFactsService.Parse(["`some.future.key` 別的版本的鍵"], Known);
        Assert.Empty(r.Valid);
        Assert.Empty(r.Invalid);                // 關鍵：不算錯
        var u = Assert.Single(r.UnknownKeys);
        Assert.Equal("some.future.key", u.Key);
    }

    [Fact]
    public void 不做鍵名檢查時全部視為有效()
    {
        var r = ChecklistFactsService.Parse(["`some.future.key` 任何鍵"], null);
        Assert.Single(r.Valid);
        Assert.Empty(r.UnknownKeys);
    }

    [Fact]
    public void 單行過長算無效並帶行號()
    {
        var longLine = new string('x', ChecklistFactsService.MaxLineLength + 1);
        var r = ChecklistFactsService.Parse(["正常的一行", longLine], Known);
        var bad = Assert.Single(r.Invalid);
        Assert.Equal(2, bad.Line);
        Assert.Contains("超過", bad.Error);
    }

    [Fact]
    public void 超過項數上限算無效()
    {
        var lines = Enumerable.Range(0, ChecklistFactsService.MaxItems + 5)
            .Select(i => $"步驟 {i}").ToList();
        var r = ChecklistFactsService.Parse(lines, Known);
        Assert.Contains(r.Invalid, x => x.Error!.Contains("上限"));
    }

    [Fact]
    public void 無效項的錯誤訊息不含整行原文避免洗版()
    {
        var longLine = new string('x', ChecklistFactsService.MaxLineLength + 1);
        var r = ChecklistFactsService.Parse([longLine], Known);
        // 值給人的是行號與原因，不是把原文再貼一次（原文在檔案裡）。
        Assert.True(r.Invalid[0].Text.Length <= 61);
    }

    // ── 事實值（走暫存目錄）─────────────────────────────────────────────

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "XinSpect-checklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 沒有清單檔是不適用不是錯誤()
    {
        string dir = TempDir();
        try
        {
            var f = ChecklistFactsService.Collect(At, Known, dir);
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Contains("不適用，不是錯誤", f.UnavailableReason);
            Assert.Contains("可選的使用者資產", f.UnavailableReason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 檔案存在時逐項統計並帶行號()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, ChecklistFactsService.FileName),
            [
                "# 我的檢查清單",
                "`in.self` 看自我完整性",
                "看一下風扇轉速是否正常",
                new string('y', ChecklistFactsService.MaxLineLength + 1),
            ]);
            var f = ChecklistFactsService.Collect(At, Known, dir);
            Assert.Equal(FactAvailability.Present, f.Availability);
            Assert.Contains("有效 2 項", f.Value);
            Assert.Contains("1 項無效", f.Value);
            Assert.Contains("第 4 行", f.Value);       // 行號對得上
            Assert.Equal(2d, f.NumericValue);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 只有註解時是有效狀態不是錯誤()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, ChecklistFactsService.FileName), ["# 還沒填", "# 真的"]);
            var f = ChecklistFactsService.Collect(At, Known, dir);
            Assert.Equal(FactAvailability.Present, f.Availability);
            Assert.Contains("只有註解與空白行", f.Value);
            Assert.Contains("有效狀態不是錯誤", f.Value);
            Assert.Equal(0d, f.NumericValue);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 認不得的鍵名在值裡明說不算錯()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, ChecklistFactsService.FileName),
                ["`some.future.key` 別的版本", "`in.self` 認得的"]);
            var f = ChecklistFactsService.Collect(At, Known, dir);
            Assert.Contains("認不得的鍵名", f.Value);
            Assert.Contains("認不得不等於寫錯", f.Value);
            Assert.Equal(1d, f.NumericValue);   // 只有認得的那項算有效
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 值裡聲明只驗證不執行且唯讀()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, ChecklistFactsService.FileName), ["`in.self` 看"]);
            var f = ChecklistFactsService.Collect(At, Known, dir);
            Assert.Contains("不執行清單裡任何東西", f.Value);
            Assert.Contains("唯讀不寫", f.Value);
            Assert.Contains("WriteGate", f.Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 不做靜默部分成功()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, ChecklistFactsService.FileName),
            [
                "`in.self` 一", "`st.wear.summary` 二",
                new string('z', ChecklistFactsService.MaxLineLength + 1),
            ]);
            var f = ChecklistFactsService.Collect(At, Known, dir);
            // 十條有兩條錯就明說少了哪兩條，不能只回「載入 N 條」。
            Assert.Contains("無效", f.Value);
            Assert.Contains("第 3 行", f.Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 路徑與設定檔同一個目錄()
    {
        // 清單與 settings.json 放在一起是刻意的：使用者只有一個地方要備份。
        Assert.Equal(ChecklistFactsService.Directory, Path.GetDirectoryName(ChecklistFactsService.FilePath));
        Assert.EndsWith("checklist.txt", ChecklistFactsService.FilePath);
    }
}
