using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// BCD 開機順序（U10-BD-0001／BD-0003，第 315 輪）：解析器吃的是 bcdedit /enum 的原始文字。
/// 測資刻意包含真實輸出裡會出現的形狀——多行 displayorder、沒有 description 的識別碼、
/// 沒有 displayorder／timeout／default 欄位、以及被截斷的輸出。
/// </summary>
public class BootOrderFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 7, 0, 0, TimeSpan.Zero);

    private const string Guid1 = "{8a1b2c3d-0000-0000-0000-000000000001}";
    private const string Guid2 = "{8a1b2c3d-0000-0000-0000-000000000002}";

    private static string TwoEntries(string? defaultId = "{current}", string displayOrderExtra = "",
        string timeout = "30")
    {
        string timeoutLine = string.IsNullOrEmpty(timeout) ? "" : $"timeout                 {timeout}\n";
        string defaultLine = string.IsNullOrEmpty(defaultId) ? "" : $"default                 {defaultId}\n";
        return
            "Windows 開機管理程式\n" +
            "--------------------\n" +
            "identifier              {bootmgr}\n" +
            "device                  unknown\n" +
            "path                    \\EFI\\MICROSOFT\\BOOT\\BOOTMGFW.EFI\n" +
            "description             Windows Boot Manager\n" +
            defaultLine +
            "displayorder            {current}\n" +
            $"                        {Guid1}\n" +
            displayOrderExtra +
            timeoutLine +
            "\n" +
            "Windows 開機載入器\n" +
            "-------------------\n" +
            "identifier              {current}\n" +
            "device                  partition=C:\n" +
            "description             Windows Server\n" +
            "\n" +
            "Windows 開機載入器\n" +
            "-------------------\n" +
            $"identifier              {Guid1}\n" +
            "device                  partition=D:\n" +
            "description             Windows 11\n";
    }

    private static IReadOnlyList<HardwareFact> Parse(string? output, string? reason = null)
        => BootOrderFactsService.Collect(output, reason, At);

    private static HardwareFact One(IReadOnlyList<HardwareFact> facts, string key)
        => Assert.Single(facts, f => f.Key == key);

    [Fact]
    public void 清單上的每一項都要按順序產生一筆()
    {
        var facts = Parse(TwoEntries());

        var first = One(facts, BootOrderFactsService.ItemPrefix + "1");
        var second = One(facts, BootOrderFactsService.ItemPrefix + "2");

        Assert.Contains("{current}", first.Value);
        Assert.Contains("Windows Server", first.Value);
        Assert.Contains(Guid1, second.Value);
        Assert.Contains("Windows 11", second.Value);
        Assert.Equal(1, first.NumericValue);
        Assert.Equal(2, second.NumericValue);
        Assert.DoesNotContain(facts, f => f.Key == BootOrderFactsService.ItemPrefix + "3");
    }

    [Fact]
    public void 總覽要寫出項數與順序而且數字等於項數()
    {
        var summary = One(Parse(TwoEntries()), BootOrderFactsService.SummaryKey);
        Assert.Equal(2, summary.NumericValue);
        Assert.Contains("2 項依序", summary.Value);
        Assert.Contains("Windows Server", summary.Value);
        Assert.Contains("不判斷這個順序合不合理", summary.Value);
    }

    [Fact]
    public void 預設項目在清單裡時要寫出是第幾位()
    {
        var defaultFact = One(Parse(TwoEntries()), BootOrderFactsService.DefaultEntryKey);
        Assert.Contains("第 1 位", defaultFact.Value);
        Assert.Equal(1, defaultFact.NumericValue);
        Assert.Equal(FactAvailability.Present, defaultFact.Availability);
    }

    [Fact]
    public void 預設項目不在清單裡時要明說不一致()
    {
        var defaultFact = One(Parse(TwoEntries(defaultId: Guid2)), BootOrderFactsService.DefaultEntryKey);
        Assert.Contains("不在 displayorder 清單裡", defaultFact.Value);
        Assert.Null(defaultFact.NumericValue);
    }

    [Fact]
    public void 沒有default欄位時要說沒有而不是猜()
    {
        var defaultFact = One(Parse(TwoEntries(defaultId: null)), BootOrderFactsService.DefaultEntryKey);
        Assert.Contains("沒有 default 欄位", defaultFact.Value);
        Assert.Contains("不推測", defaultFact.Value);
        Assert.Null(defaultFact.NumericValue);
    }

    [Fact]
    public void 選單等待秒數要寫成數字並且可以沒有這個欄位()
    {
        Assert.Equal(30, One(Parse(TwoEntries()), BootOrderFactsService.TimeoutKey).NumericValue);

        var absent = One(Parse(TwoEntries(timeout: "")), BootOrderFactsService.TimeoutKey);
        Assert.Contains("沒有 timeout 欄位", absent.Value);
        Assert.Null(absent.NumericValue);
        Assert.Equal(FactAvailability.Present, absent.Availability);
    }

    [Fact]
    public void 多行displayorder要全部收進來()
    {
        string withThird =
            $"                        {Guid2}\n";
        // 兩行續行：第二、第三項分別在各自的縮排行上。
        var facts = Parse(TwoEntries(displayOrderExtra: withThird));
        Assert.Equal(3, One(facts, BootOrderFactsService.SummaryKey).NumericValue);
        Assert.Contains(Guid2, One(facts, BootOrderFactsService.ItemPrefix + "3").Value);
    }

    [Fact]
    public void 沒有displayorder欄位時總覽要誠實寫零項()
    {
        string text = TwoEntries().Replace("displayorder            {current}\n", "")
                                   .Replace($"                        {Guid1}\n", "");
        var summary = One(Parse(text), BootOrderFactsService.SummaryKey);
        Assert.Equal(0, summary.NumericValue);
        Assert.Contains("沒有列出開機選單的順序", summary.Value);
    }

    [Fact]
    public void 識別碼沒有描述時要說明描述不在輸出裡()
    {
        string text = TwoEntries().Replace(
            "Windows 開機載入器\n" +
            "-------------------\n" +
            $"identifier              {Guid1}\n" +
            "device                  partition=D:\n" +
            "description             Windows 11\n", "");

        var second = One(Parse(text), BootOrderFactsService.ItemPrefix + "2");
        Assert.Contains("描述不在輸出裡", second.Value);
        Assert.Equal(FactAvailability.Present, second.Availability);
    }

    [Fact]
    public void 讀不到輸出時三把鍵都要是讀取錯誤並帶同一句理由()
    {
        var facts = Parse(null, "bcdedit /enum 沒有任何輸出");
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("bcdedit /enum 沒有任何輸出", f.UnavailableReason);
        });
    }

    [Fact]
    public void 找不到bootmgr區塊時不得假裝清單是空的()
    {
        var facts = Parse("Windows 開機載入器\n-------------------\nidentifier              {current}\n");
        var summary = One(facts, BootOrderFactsService.SummaryKey);
        Assert.Equal(FactAvailability.ReadError, summary.Availability);
        Assert.Contains("{bootmgr}", summary.UnavailableReason);
        // 沒有逐項事實——「沒有清單」與「找不到區塊」不能寫成同一件事
        Assert.DoesNotContain(facts, f => f.Key.StartsWith(BootOrderFactsService.ItemPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void 六態契約_Present不帶理由_其餘必帶理由()
    {
        foreach (var output in new[] { TwoEntries(), TwoEntries(timeout: "", defaultId: null), null })
        {
            foreach (var fact in Parse(output))
            {
                if (fact.Availability == FactAvailability.Present) Assert.Null(fact.UnavailableReason);
                else Assert.False(string.IsNullOrWhiteSpace(fact.UnavailableReason));
            }
        }
    }

    [Fact]
    public void 逐項家族已登記在動態目錄()
    {
        Assert.True(FactKeyDynamicCatalog.Matches(BootOrderFactsService.ItemPrefix + "1"));
        Assert.True(FactKeyDynamicCatalog.Matches(BootOrderFactsService.ItemPrefix + "42"));
    }
}
