using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 韌體設定可讀項盤點的守門（ULTIMATE-1000 階段二 P2-046）。
///
/// <para>
/// <b>核心是「讀不到」與「沒有這個值」在使用者眼裡長得一樣——都是空的。</b>
/// 這一項的工作就是把它們分開。
/// </para>
/// </summary>
public class FirmwareSettingsReachFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static FirmwareSettingsReachFactsService.Probe P(string name, bool ok, string? detail = null)
        => new(name, ok, detail);

    [Fact]
    public void 可讀項要列出名稱與數量()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At,
            [P("UEFI 變數", true), P("ACPI 表", true)]);
        var r = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.ReadableKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.StartsWith("UEFI 變數、ACPI 表", r.Value);
        Assert.Equal(2.0, r.NumericValue);
        Assert.Equal(FactAvailability.Present, r.Availability);
    }

    [Fact]
    public void 可讀項要說出讀得到不代表值對()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At, [P("A", true)]);
        var r = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.ReadableKey);

        Assert.Contains("讀得到不代表值對", r.Value);
        Assert.Contains("這條路現在通不通", r.Value);
    }

    [Fact]
    public void 不可讀項要附上原因()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At,
            [P("UEFI 變數", false, "權限不足"), P("SPI 快閃", false, "登錄機碼不存在")]);
        var b = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.BlockedKey);

        Assert.Contains("UEFI 變數（權限不足）", b.Value);
        Assert.Contains("SPI 快閃（登錄機碼不存在）", b.Value);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：2 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(b.NumericValue);
        Assert.Contains("原本帶了數字 2", b.UnavailableReason);
    }

    [Fact]
    public void 不可讀項要說出它與沒有值長得一樣()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At, [P("A", false, "權限不足")]);
        var b = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.BlockedKey);

        Assert.Contains("長得一樣——都是空的", b.UnavailableReason);
        Assert.Contains("這條路現在不通", b.UnavailableReason);
    }

    [Fact]
    public void 全部讀不到時要說那通常是權限而不是沒有設定()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At,
            [P("A", false, "權限不足"), P("B", false, "權限不足")]);
        var r = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.ReadableKey);

        Assert.Equal(FactAvailability.ReadError, r.Availability);
        Assert.Equal("沒有任何來源可讀", r.Value);
        Assert.Contains("而不是「這台機器沒有韌體設定」", r.UnavailableReason);
    }

    [Fact]
    public void 全部讀得到時要說那排除了權限這個解釋()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At, [P("A", true), P("B", true)]);
        var b = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.BlockedKey);

        Assert.StartsWith("所有來源都讀得到", b.Value);
        Assert.Equal(0.0, b.NumericValue);
        Assert.Contains("排除了「看不到是因為權限」這個常見解釋", b.Value);
    }

    [Fact]
    public void 沒有附原因的不可讀項只列名稱()
    {
        var facts = FirmwareSettingsReachFactsService.Collect(At, [P("A", false)]);
        var b = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.BlockedKey);

        Assert.Equal("A", b.Value);
    }

    [Fact]
    public void 兩把鍵的數量必須互補為總數()
    {
        var probes = new[] { P("A", true), P("B", false, "x"), P("C", true) };
        var facts = FirmwareSettingsReachFactsService.Collect(At, probes);
        var readableFact = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.ReadableKey);
        var blockedFact = facts.Single(f => f.Key == FirmwareSettingsReachFactsService.BlockedKey);

        // 可讀那一把是 Present，數字還在；**不可讀那一把是 Unknown，
        // 不變式把它的數字收斂成 null 並在理由欄留下痕跡**——
        // 所以筆數要從痕跡裡讀（不是靜默修正，見 HardwareFactInvariantTests）。
        double readable = readableFact.NumericValue!.Value;
        double blocked = 痕跡裡的數字(blockedFact);

        Assert.Equal(probes.Length, readable + blocked);

        static double 痕跡裡的數字(HardwareFact f) => double.Parse(
            System.Text.RegularExpressions.Regex
                .Match(f.UnavailableReason ?? "", @"原本帶了數字 (\d+(?:\.\d+)?)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void 真實盤點不得丟例外且每一項都要有名字()
    {
        // 這一條走真實的登錄探測——在 CI 或不同權限下結果會不同，
        // 但不論結果如何，都必須是「一組有名有姓的探測結果」。
        var probes = FirmwareSettingsReachFactsService.ReadAll();

        Assert.NotEmpty(probes);
        Assert.All(probes, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.All(probes, p => Assert.True(p.Reachable || p.Detail is not null,
            $"{p.Name} 標為不可讀但沒有給原因——那會讓使用者無從查起"));
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = FirmwareSettingsReachFactsService.Collect(At, [P("A", true)]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "可讀性盤點產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
