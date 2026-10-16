using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 開機順序事實的守門（ULTIMATE-1000 階段二 P2-042）。
///
/// <para>
/// <b>核心是「變數裡寫的順序」與「實際的開機結果」之別。</b>
/// 韌體可以跳過順序裡的前幾項——失敗會往下找、按快速鍵會直接選、某些平台會重排。
/// 把變數當成「所以下次從這裡開」是最容易犯的錯，而它看起來完全合理。
/// </para>
/// </summary>
public class UefiBootOrderFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>把 u16 序列編成 BootOrder 變數的內容（little-endian）。</summary>
    private static byte[] Order(params ushort[] entries)
    {
        var b = new byte[entries.Length * 2];
        for (int i = 0; i < entries.Length; i++)
        {
            b[i * 2] = (byte)(entries[i] & 0xFF);
            b[i * 2 + 1] = (byte)(entries[i] >> 8);
        }
        return b;
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有特權時要標權限不足而不是沒有順序()
    {
        var facts = UefiBootOrderFactsService.Collect(At, privilegeOk: false);
        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
        Assert.All(facts, f => Assert.Contains("SeSystemEnvironmentPrivilege", f.UnavailableReason));
    }

    [Fact]
    public void 取不到BootOrder時要說那與順序為空不同()
    {
        var facts = UefiBootOrderFactsService.Collect(At, readOrder: () => null, privilegeOk: true);
        var o = facts.Single(f => f.Key == UefiBootOrderFactsService.OrderKey);

        Assert.Equal(FactAvailability.NotApplicable, o.Availability);
        Assert.Contains("Legacy 開機的機器沒有這個變數", o.UnavailableReason);
        Assert.Contains("與「順序是空的」是不同的斷言", o.UnavailableReason);
    }

    [Fact]
    public void 奇數長度要如實拒解()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => [0x01, 0x00, 0x02], privilegeOk: true);
        var o = facts.Single(f => f.Key == UefiBootOrderFactsService.OrderKey);

        Assert.Equal(FactAvailability.ReadError, o.Availability);
        Assert.Contains("不是偶數", o.UnavailableReason);
        Assert.Contains("如實拒解", o.UnavailableReason);
    }

    // ── 順序內容 ────────────────────────────────────────────────────────

    [Fact]
    public void 順序要依序列出每個Boot項目()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(0x0001, 0x0003, 0x0000), privilegeOk: true);
        var o = facts.Single(f => f.Key == UefiBootOrderFactsService.OrderKey);

        Assert.Equal("Boot0001 → Boot0003 → Boot0000", o.Value);
        Assert.Equal(3.0, o.NumericValue);
        Assert.Equal(FactAvailability.Present, o.Availability);
    }

    [Fact]
    public void 順序要說出那不是實際的開機結果()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(0x0001, 0x0002), privilegeOk: true);
        var o = facts.Single(f => f.Key == UefiBootOrderFactsService.OrderKey);

        Assert.Contains("不是實際的開機結果", o.UnavailableReason);
        Assert.Contains("跳過前幾項", o.UnavailableReason);
        Assert.Contains("開機事件記錄那個獨立來源", o.UnavailableReason);
    }

    [Fact]
    public void 空順序要標不適用並說那不是取不到()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => [], privilegeOk: true);
        var o = facts.Single(f => f.Key == UefiBootOrderFactsService.OrderKey);

        Assert.Equal(FactAvailability.NotApplicable, o.Availability);
        Assert.Contains("順序裡沒有東西", o.UnavailableReason);
        Assert.Contains("而不是「取不到順序」", o.UnavailableReason);
    }

    // ── 載入選項 ────────────────────────────────────────────────────────

    [Fact]
    public void 載入選項要量長度並列出取不到的()
    {
        var lengths = new Dictionary<string, int?> { ["Boot0001"] = 120, ["Boot0002"] = 200 };

        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(0x0001, 0x0002, 0x0009),
            readLoadOptionLength: n => lengths.TryGetValue(n, out int? v) ? v : null,
            privilegeOk: true);
        var l = facts.Single(f => f.Key == UefiBootOrderFactsService.LoadOptionsKey);

        Assert.Contains("Boot0001（120 bytes）", l.Value);
        Assert.Contains("Boot0002（200 bytes）", l.Value);
        Assert.Contains("取不到：Boot0009", l.Value);
        Assert.Equal(2.0, l.NumericValue);
    }

    [Fact]
    public void 取不到的選項要說刪除與權限不足在這裡分不出來()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(0x0001, 0x0009),
            readLoadOptionLength: n => n == "Boot0001" ? 100 : null,
            privilegeOk: true);
        var l = facts.Single(f => f.Key == UefiBootOrderFactsService.LoadOptionsKey);

        Assert.Contains("可能已被刪除", l.UnavailableReason);
        Assert.Contains("這兩者在這裡分不出來", l.UnavailableReason);
    }

    [Fact]
    public void 全部取不到時要標Unknown()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(0x0001, 0x0002),
            readLoadOptionLength: _ => null, privilegeOk: true);
        var l = facts.Single(f => f.Key == UefiBootOrderFactsService.LoadOptionsKey);

        Assert.Equal(FactAvailability.Unknown, l.Availability);
        Assert.Contains("都取不到內容", l.Value);
    }

    [Fact]
    public void 載入選項要說出它不解析內容()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(0x0001),
            readLoadOptionLength: _ => 100, privilegeOk: true);
        var l = facts.Single(f => f.Key == UefiBootOrderFactsService.LoadOptionsKey);

        Assert.Contains("不解析內容", l.UnavailableReason);
        Assert.Contains("裝置路徑", l.UnavailableReason);
    }

    [Fact]
    public void 沒有順序時不得產出載入選項的數字()
    {
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => null, privilegeOk: true);
        var l = facts.Single(f => f.Key == UefiBootOrderFactsService.LoadOptionsKey);

        Assert.Null(l.NumericValue);
        Assert.Contains("沒有 BootOrder 可以走訪", l.UnavailableReason);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(1), readLoadOptionLength: _ => 10, privilegeOk: true)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = UefiBootOrderFactsService.Collect(At, privilegeOk: false)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = UefiBootOrderFactsService.Collect(At,
            readOrder: () => Order(1), readLoadOptionLength: _ => 10, privilegeOk: true);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "開機順序事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
