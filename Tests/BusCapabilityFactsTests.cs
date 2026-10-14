using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>匯流排能力殘項（UV-005 CDAT／UV-009 USB TRIM）單元測試。</summary>
public class BusCapabilityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── UV-005 CDAT ──────────────────────────────────────────────────────

    [Fact]
    public void CDAT存在時只報版本與條目數()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            cdat: () => new BusCapabilityFactsService.CdatState(true, 1, 12, 0x01)), "uv.cdat");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("版本 1", f.Value);
        Assert.Contains("條目 12 個", f.Value);
        Assert.Contains("主要結構型別 0x01", f.Value);
        Assert.Equal(12d, f.NumericValue);
    }

    [Fact]
    public void CDAT不逐條解讀的理由要寫在值裡()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            cdat: () => new BusCapabilityFactsService.CdatState(true, 1, 3, null)), "uv.cdat");
        Assert.Contains("不逐條解讀", f.Value);
        Assert.Contains("沒對準出處就不出值", f.Value);
    }

    [Fact]
    public void CDAT宣告不改變硬體能力()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            cdat: () => new BusCapabilityFactsService.CdatState(true, 1, 3, null)), "uv.cdat");
        Assert.Contains("宣告本身不改變硬體能力", f.Value);
    }

    [Fact]
    public void 沒有CDAT是不適用且說明是常態()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            cdat: () => new BusCapabilityFactsService.CdatState(false, 0, 0, null)), "uv.cdat");
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有它是常態", f.UnavailableReason);
    }

    [Fact]
    public void CDAT讀取失敗是ReadError()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At, cdat: () => null), "uv.cdat");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 主要型別讀不到時不猜()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            cdat: () => new BusCapabilityFactsService.CdatState(true, 1, 2, null)), "uv.cdat");
        Assert.Contains("主要結構型別讀不到", f.Value);
    }

    // ── UV-009 USB TRIM ──────────────────────────────────────────────────

    [Fact]
    public void 有裝置時陳述宣告數與UASP()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            trim: () => new BusCapabilityFactsService.UsbTrimState(3, 1, 2)), "uv.usb.trim");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("USB 儲存裝置 3 個", f.Value);
        Assert.Contains("1 個裝置宣告支援", f.Value);
        Assert.Contains("走 UASP 協定 2 個", f.Value);
        Assert.Equal(1d, f.NumericValue);
    }

    [Fact]
    public void 宣告不等於執行必須寫在值裡()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            trim: () => new BusCapabilityFactsService.UsbTrimState(1, 1, 1)), "uv.usb.trim");
        // 這是本項最重要的一句：很多隨身碟只是把 UNMAP 指令吃掉。
        Assert.Contains("「宣告」不等於「執行」", f.Value);
        Assert.Contains("只是把指令吃掉而不真的把區塊交還", f.Value);
    }

    [Fact]
    public void UASP是轉發UNMAP的前提()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            trim: () => new BusCapabilityFactsService.UsbTrimState(2, 0, 0)), "uv.usb.trim");
        Assert.Contains("能轉發 SCSI 指令（含 UNMAP）的前提", f.Value);
        Assert.Contains("沒有裝置宣告 UNMAP／TRIM 能力", f.Value);
    }

    [Fact]
    public void 沒有隨身碟是不適用不是零()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            trim: () => new BusCapabilityFactsService.UsbTrimState(0, 0, 0)), "uv.usb.trim");
        // 「沒插」與「插了不支援」是兩件事。
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒插隨身碟", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 不預測隨身碟壽命或速度()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At,
            trim: () => new BusCapabilityFactsService.UsbTrimState(1, 1, 1)), "uv.usb.trim");
        Assert.Contains("不預測壽命或速度", f.Value);
    }

    [Fact]
    public void 讀取失敗是ReadError()
    {
        var f = Fact(BusCapabilityFactsService.Collect(At, trim: () => null), "uv.usb.trim");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 契約 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 兩把鍵齊且互不影響()
    {
        var facts = BusCapabilityFactsService.Collect(At,
            cdat: () => throw new InvalidOperationException("c"),
            trim: () => new BusCapabilityFactsService.UsbTrimState(1, 1, 1));
        Assert.Equal(new[] { "uv.cdat", "uv.usb.trim" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "uv.cdat").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "uv.usb.trim").Availability);
    }
}
