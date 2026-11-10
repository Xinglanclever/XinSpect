using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 39／41 事實的守門（ULTIMATE-1000 階段二 P2-026／P2-027）。
///
/// <para>
/// <b>這一組的核心是功率的「未回報」與「0 瓦」之別。</b>
/// Type 39 的功率欄位用完整值 0x8000 表示「未回報」——把它當數值讀會得到 32768 W，
/// 一個荒謬但不會讓人懷疑的數字；把它當 0 則會讓合計少算一台。
/// </para>
/// </summary>
public class SmbiosPowerFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SmbiosStruct Struct(byte type, byte[] data, ushort handle = 0x0100,
        params string[] strings)
        => new(type, handle, data, strings);

    /// <summary>Type 39：群組@0x04、位置@0x05、裝置名@0x06、廠商@0x07、功率@0x0C、特性@0x0E。</summary>
    private static SmbiosStruct Type39(ushort power, ushort chars, bool reportPower = true,
        string device = "PSU", string location = "Bay 1")
    {
        var d = new byte[0x10];
        d[0x04] = 7;              // 數值群組，不是字串索引
        d[0x05] = 2;              // 位置字串
        d[0x06] = 1;              // 裝置名字串
        d[0x07] = 3;              // 廠商字串，不能冒充位置
        BitConverter.GetBytes(reportPower ? power : (ushort)0x8000).CopyTo(d, 0x0C);
        BitConverter.GetBytes(chars).CopyTo(d, 0x0E);
        return Struct(39, d, 0x0100, device, location, "Vendor C");
    }

    /// <summary>Type 41：名稱@0x04、型別與啟用位@0x05、實例@0x06、PCI 段@0x07、bus@0x09、devfn@0x0A。</summary>
    private static SmbiosStruct Type41(byte deviceType, byte status, ushort segments,
        byte bus = 2, byte devFunc = 0xFB)
    {
        var d = new byte[0x0B];
        d[0x04] = 1;
        d[0x05] = (byte)(deviceType | (status & 0x80));
        d[0x06] = 2;
        BitConverter.GetBytes(segments).CopyTo(d, 0x07);
        d[0x09] = bus;
        d[0x0A] = devFunc;
        return Struct(41, d, 0x0100, "Onboard device");
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有結構時要標不支援()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => []);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
    }

    [Fact]
    public void 讀取失敗時要標讀取失敗()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => throw new InvalidOperationException("注入"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 沒有Type39時要說缺席不代表沒有電源供應器()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => [Struct(0, new byte[0x12])]);
        var c = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuCountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("缺席不代表沒有電源供應器", c.UnavailableReason);
    }

    // ── Type 39 計數與明細 ──────────────────────────────────────────────

    [Fact]
    public void 電源供應器數要如實計數()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type39(1000, 0x0002)]);
        var c = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuCountKey);

        Assert.Equal(2.0, c.NumericValue);
        Assert.Equal(FactAvailability.Present, c.Availability);
    }

    [Fact]
    public void 明細要含裝置名位置與功率()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002, device: "PSU0", location: "Bay 1")]);
        var d = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuDetailKey);

        Assert.Contains("PSU0", d.Value);
        Assert.Contains("Bay 1", d.Value);
        Assert.Contains("群組 7", d.Value);
        Assert.Contains("製造商 Vendor C", d.Value);
        Assert.Contains("1000 W", d.Value);
        Assert.Contains("在位", d.Value);
        Assert.Contains("已插電", d.Value);
    }

    [Fact]
    public void 特性位元要解出熱插拔與電壓切換()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(500, 0x0002 | 0x0001 | (4 << 3))]);
        var d = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuDetailKey);

        Assert.Contains("有熱插拔", d.Value);
        Assert.Contains("輸入電壓切換：自動", d.Value);
        Assert.DoesNotContain("電源表", d.Value);
        Assert.DoesNotContain("解除警告", d.Value);
    }

    [Fact]
    public void 明細要說額定不等於現在輸出()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => [Type39(1000, 0x0002)]);
        var d = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuDetailKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("額定瓦數不等於現在輸出", d.Value);
    }

    // ── 功率：未回報 vs 0 瓦 ────────────────────────────────────────────

    [Fact]
    public void 未回報的功率不得當成三萬兩千七百六十八瓦()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(0, 0x0002, reportPower: false)]);
        var p = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);

        Assert.Equal(FactAvailability.Unknown, p.Availability);
        Assert.Null(p.NumericValue);
        Assert.Contains("沒說」而不是「0 瓦", p.UnavailableReason);
    }

    [Fact]
    public void 部分回報時不得把部分功率冒充完整合計()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type39(0, 0x0002, reportPower: false)]);
        var p = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);

        Assert.Null(p.NumericValue);
        Assert.Equal(FactAvailability.Unknown, p.Availability);
        Assert.Contains("未回報的不以 0 計入", p.Value);
    }

    [Fact]
    public void 全部回報時要說那是額定不是可用()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type39(500, 0x0002)]);
        var p = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);

        Assert.Equal(1500.0, p.NumericValue);
        Assert.Contains("1000 W＋500 W", p.Value);
        Assert.Contains("不是可用功率", p.Value);
        Assert.Contains("轉換效率", p.Value);

        var boundary = SmbiosPowerFactsService.Collect(At,
            () => [Type39(40000, 0x0002), Type39(500, 0x0000)]);
        var present = boundary.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);
        Assert.Equal(40000.0, present.NumericValue);
        Assert.Equal(FactAvailability.Present, present.Availability);
        var empty = SmbiosPowerFactsService.Collect(At, () => [Type39(500, 0)]);
        Assert.Null(empty.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey).NumericValue);
    }

    [Fact]
    public void 功率欄位不足時要略過而不是當零()
    {
        // 長度 0x0C 的 Type 39 沒有功率欄位（@0x0C 是 u16 需要 0x0E）。
        var s = Struct(39, new byte[0x0C]);
        var facts = SmbiosPowerFactsService.Collect(At, () => [s]);
        var c = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuCountKey);

        // 長度不足 0x10 的結構不列入——那不是「0 台」，是「這一筆讀不出來」。
        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
    }

    // ── Type 41 ─────────────────────────────────────────────────────────

    [Fact]
    public void 沒有Type41時要說多數消費級主機板不填()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => [Type39(1000, 0x0002)]);
        var c = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardCountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("多數消費級主機板不填這一項", c.UnavailableReason);
    }

    [Fact]
    public void 內建裝置明細要含型別與狀態()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type41(0x05, 0x81, 0x1234)]);
        var o = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);

        Assert.Contains("乙太網路", o.Value);
        Assert.Contains("已啟用", o.Value);
        Assert.Contains("Onboard device", o.Value);
        Assert.Contains("實例 2", o.Value);
        Assert.Contains("PCI 1234:02:1F.3", o.Value);
    }

    [Fact]
    public void 停用的內建裝置要說已停用()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => [Type41(0x03, 0x01, 0)]);
        var o = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);

        Assert.Contains("已停用", o.Value);
    }

    [Fact]
    public void 裝置類型與未知PCI位址要依固定欄位解析()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type41(0x0F, 0x81, 0xFFFF)]);
        var o = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);

        Assert.Contains("NVM Express", o.Value);
        Assert.Contains("PCI 位址未提供", o.Value);
        Assert.DoesNotContain("參考指定", o.Value);
        var wireless = SmbiosPowerFactsService.Collect(At,
            () => [Type41(0x0B, 0x80, 0, bus: 0xFF)]);
        var wlan = wireless.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);
        Assert.Contains("無線網路", wlan.Value);
        Assert.Contains("PCI 位址未提供", wlan.Value);
    }

    [Fact]
    public void 內建裝置數要如實計數()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type41(0x05, 0x81, 0), Type41(0x07, 0x81, 0)]);
        var c = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardCountKey);

        Assert.Equal(2.0, c.NumericValue);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type41(0x05, 0x81, 0)])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var failed = SmbiosPowerFactsService.Collect(At, () => [])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, failed);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type41(0x05, 0x81, 0)]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "Type 39／41 產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
