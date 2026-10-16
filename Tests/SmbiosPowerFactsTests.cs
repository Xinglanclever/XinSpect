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
/// Type 39 的功率欄位用 0x8000 當「未回報」旗標——把它當數值讀會得到 32768 W，
/// 一個荒謬但不會讓人懷疑的數字；把它當 0 則會讓合計少算一台。
/// </para>
/// </summary>
public class SmbiosPowerFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SmbiosStruct Struct(byte type, byte[] data, ushort handle = 0x0100,
        params string[] strings)
        => new(type, handle, data, strings);

    /// <summary>Type 39：群組@0x04、裝置名@0x06、位置@0x07、版本@0x0B、功率@0x0C、特性@0x0E。</summary>
    private static SmbiosStruct Type39(ushort power, ushort chars, bool reportPower = true,
        string device = "PSU", string location = "Bay 1")
    {
        var d = new byte[0x10];
        d[0x04] = 3;              // 群組字串
        d[0x06] = 1;              // 裝置名字串
        d[0x07] = 2;              // 位置字串
        BitConverter.GetBytes(reportPower ? power : (ushort)0x8000).CopyTo(d, 0x0C);
        BitConverter.GetBytes(chars).CopyTo(d, 0x0E);
        return Struct(39, d, 0x0100, device, location, "Group A");
    }

    /// <summary>Type 41：裝置型別@0x04、狀態@0x09、段群組@0x0B、參考指定@0x0D。</summary>
    private static SmbiosStruct Type41(byte deviceType, byte status, ushort segments,
        int refCount = 0)
    {
        var d = new byte[0x0D + refCount * 4 + 4];
        d[0x04] = deviceType;
        d[0x09] = status;
        BitConverter.GetBytes(segments).CopyTo(d, 0x0B);
        int p = 0x0D;
        for (int i = 0; i < refCount; i++)
        {
            d[p] = 1;                          // 參考型別 1
            BitConverter.GetBytes((ushort)(0x100 + i)).CopyTo(d, p + 1);
            p += 4;
        }
        d[p] = 0;                              // 參考型別 0 = 結束
        return Struct(41, d);
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
        Assert.Contains("1000 W", d.Value);
        Assert.Contains("在位", d.Value);
        Assert.Contains("已插電", d.Value);
    }

    [Fact]
    public void 特性位元要解出熱插拔與電源表()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(500, 0x0002 | 0x0001 | 0x0008)]);
        var d = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuDetailKey);

        Assert.Contains("有熱插拔", d.Value);
        Assert.Contains("有電源表", d.Value);
    }

    [Fact]
    public void 明細要說額定不等於現在輸出()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => [Type39(1000, 0x0002)]);
        var d = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuDetailKey);

        Assert.Contains("額定瓦數不等於現在輸出", d.UnavailableReason);
    }

    // ── 功率：未回報 vs 0 瓦 ────────────────────────────────────────────

    [Fact]
    public void 未回報的功率不得當成三萬兩千七百六十八瓦()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(0, 0x0002, reportPower: false)]);
        var p = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);

        Assert.Equal(FactAvailability.NotApplicable, p.Availability);
        Assert.Null(p.NumericValue);
        Assert.Contains("沒說」而不是「0 瓦", p.UnavailableReason);
    }

    [Fact]
    public void 部分回報時合計只涵蓋回報的那些()
    {
        // 一台 1000 W、一台未回報 → 合計 1000，不是 1000 + 32768。
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type39(0, 0x0002, reportPower: false)]);
        var p = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);

        Assert.Equal(1000.0, p.NumericValue);
        Assert.Contains("未回報的不以 0 計入", p.UnavailableReason);
    }

    [Fact]
    public void 全部回報時要說那是額定不是可用()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type39(1000, 0x0002), Type39(500, 0x0002)]);
        var p = facts.Single(f => f.Key == SmbiosPowerFactsService.PsuPowerKey);

        Assert.Equal(1500.0, p.NumericValue);
        Assert.Contains("1000 W＋500 W", p.Value);
        Assert.Contains("不是可用功率", p.UnavailableReason);
        Assert.Contains("轉換效率", p.UnavailableReason);
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
            () => [Type41(0x05, 0x81, 1, refCount: 2)]);
        var o = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);

        Assert.Contains("乙太網路", o.Value);
        Assert.Contains("已啟用", o.Value);
        Assert.Contains("1 個段群組", o.Value);
        Assert.Contains("2 筆參考指定", o.Value);
    }

    [Fact]
    public void 停用的內建裝置要說已停用()
    {
        var facts = SmbiosPowerFactsService.Collect(At, () => [Type41(0x03, 0x01, 0)]);
        var o = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);

        Assert.Contains("已停用", o.Value);
    }

    [Fact]
    public void 參考指定只計數不追進去()
    {
        var facts = SmbiosPowerFactsService.Collect(At,
            () => [Type41(0x0F, 0x81, 0, refCount: 3)]);
        var o = facts.Single(f => f.Key == SmbiosPowerFactsService.OnboardKey);

        Assert.Contains("只計數，不追進去看", o.UnavailableReason);
        Assert.Contains("3 筆參考指定", o.Value);
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
