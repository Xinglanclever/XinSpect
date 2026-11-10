using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 43（TPM 裝置）事實的守門（ULTIMATE-1000 階段二 P2-029）。
///
/// <para>
/// 這一組的核心是「<b>韌體宣告</b>與「<b>TPM 回應</b>」是兩個獨立來源。
/// 所以斷言的重點不是「值對不對」，而是<b>措辭有沒有把來源講清楚</b>——
/// 一個把「韌體說有 TPM」寫成「有 TPM」的事實，會讓對帳失去意義。
/// </para>
/// </summary>
public class SmbiosTpmFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SmbiosStruct Struct(byte type, byte[] data, params string[] strings)
        => new(type, 0x0100, data, strings);

    /// <summary>Type 43：原始 ASCII@0x04、規格@0x08／09、韌體@0x0A／0E、描述@0x12、QWORD 特性@0x13。</summary>
    private static SmbiosStruct Tpm43(string vendorId, byte major, byte minor,
        ulong firmware, bool firmwareConfig, bool softwareConfig, bool oemConfig, string? description = null)
    {
        var d = new byte[0x1F];
        System.Text.Encoding.ASCII.GetBytes(vendorId).CopyTo(d, 0x04);
        d[0x08] = major;
        d[0x09] = minor;
        BitConverter.GetBytes((uint)(firmware & 0xFFFFFFFF)).CopyTo(d, 0x0A);
        BitConverter.GetBytes((uint)(firmware >> 32)).CopyTo(d, 0x0E);

        ulong chars = 0;
        if (firmwareConfig) chars |= 0x08;
        if (softwareConfig) chars |= 0x10;
        if (oemConfig) chars |= 0x20;
        BitConverter.GetBytes(chars).CopyTo(d, 0x13);

        if (description is not null) d[0x12] = 1;
        d[0x1B] = 0xFF; // OEM-defined data is not a string index.
        return Struct(43, d, description ?? "");
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有結構時要標不支援()
    {
        var facts = SmbiosTpmFactsService.Collect(At, () => []);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
    }

    [Fact]
    public void 讀取失敗時要標讀取失敗()
    {
        var facts = SmbiosTpmFactsService.Collect(At, () => throw new InvalidOperationException("注入"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 沒有Type43時要說那只代表這個來源沒有陳述()
    {
        var facts = SmbiosTpmFactsService.Collect(At, () => [Struct(0, new byte[0x14])]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey);

        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Contains("不代表沒有 TPM", s.UnavailableReason);
        Assert.Contains("這一個來源沒有陳述", s.UnavailableReason);
        Assert.Contains("虛擬機", s.UnavailableReason);
    }

    [Fact]
    public void 長度不足時要如實拒解()
    {
        var facts = SmbiosTpmFactsService.Collect(At, () => [Struct(43, new byte[0x10])]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey);

        Assert.Equal(FactAvailability.ReadError, s.Availability);
        Assert.Contains("如實拒解，不猜", s.UnavailableReason);
        var minimum = new byte[0x1B];
        System.Text.Encoding.ASCII.GetBytes("AMD ").CopyTo(minimum, 4);
        minimum[8] = 2;
        var valid = SmbiosTpmFactsService.Collect(At, () => [Struct(43, minimum)]);
        Assert.Equal(FactAvailability.Present, valid.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey).Availability);
        Assert.Equal("AMD", valid.Single(f => f.Key == SmbiosTpmFactsService.VendorKey).Value);
    }

    // ── 廠商 ID ─────────────────────────────────────────────────────────

    [Fact]
    public void 廠商ID要依原始位元組順序讀取ASCII()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0x0007_0000, true, false, true)]);
        var v = facts.Single(f => f.Key == SmbiosTpmFactsService.VendorKey);

        Assert.Equal("INTC", v.Value);
    }

    [Fact]
    public void 廠商ID反過來讀會得到顛倒的字串()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, false, true)]);
        var v = facts.Single(f => f.Key == SmbiosTpmFactsService.VendorKey);

        Assert.Equal("INTC", v.Value);
        Assert.NotEqual("CTNI", v.Value);
    }

    // ── 版本與狀態 ──────────────────────────────────────────────────────

    [Fact]
    public void 已知規格版本要標Present()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0x0007_0000, true, false, true)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SpecVersionKey);

        Assert.Equal("2.0", s.Value);
        Assert.Equal(FactAvailability.Present, s.Availability);
    }

    [Fact]
    public void 規格12有效而未知版本不得從旗標猜測()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 1, 2, 0x0000_0001, false, false, false)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SpecVersionKey);

        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Equal("1.2", s.Value);
        var unknown = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 3, 1, 0, true, true, true)]);
        var version = unknown.Single(f => f.Key == SmbiosTpmFactsService.SpecVersionKey);
        Assert.Equal(FactAvailability.Unknown, version.Availability);
        Assert.Null(version.NumericValue);
    }

    [Fact]
    public void 家族配置特性三位元要各自陳述()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, firmwareConfig: true, softwareConfig: true, oemConfig: true)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.StateKey);

        Assert.Contains("韌體更新配置", s.Value);
        Assert.Contains("平台軟體配置", s.Value);
        Assert.Contains("OEM 專有機制配置", s.Value);
        Assert.DoesNotContain("已啟用", s.Value);
        Assert.DoesNotContain("已佔有", s.Value);
        Assert.Null(facts.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey).NumericValue);
    }

    [Fact]
    public void 特性未提供位元優先且完整保留QWORD()
    {
        var data = new byte[0x1F];
        System.Text.Encoding.ASCII.GetBytes("INTC").CopyTo(data, 4);
        data[8] = 2;
        BitConverter.GetBytes(0x8000_0000_0000_003CUL).CopyTo(data, 0x13);
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Struct(43, data)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.StateKey);

        Assert.Contains("TPM 裝置特性未提供", s.Value);
        Assert.Contains("800000000000003C", s.Value);
        Assert.DoesNotContain("韌體更新配置", s.Value);
    }

    [Fact]
    public void 零特性不能推定啟用與擁有者狀態()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, false, false, false)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.StateKey);

        Assert.Contains("未宣告家族配置特性", s.Value);
        Assert.DoesNotContain("未啟用", s.Value);
        Assert.DoesNotContain("未佔有", s.Value);
        Assert.DoesNotContain("支援 TPM 2.0 協定", s.Value);
    }

    // ── 來源措辭 ────────────────────────────────────────────────────────

    [Fact]
    public void 彙總必須說出這是韌體宣告而不是TPM的回應()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0x0007_0000, true, false, true)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey);

        Assert.Contains("是韌體宣告，不是 TPM 的回應", s.Value);
        Assert.Contains("不是哪一個錯了", s.Value);
    }

    [Fact]
    public void 描述字串要能取到且佔位值要濾掉()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, false, true, "TPM 2.0 Device")]);
        Assert.Contains("TPM 2.0 Device",
            facts.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey).Value);

        var filtered = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, false, true, "To Be Filled By O.E.M.")]);
        Assert.DoesNotContain("To Be Filled By O.E.M.",
            filtered.Single(f => f.Key == SmbiosTpmFactsService.SummaryKey).Value);
    }

    // ── 目錄與鍵集合 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, false, true)])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var failed = SmbiosTpmFactsService.Collect(At, () => [])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, failed);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, false, true)]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "Type 43 產出了目錄裡沒有的鍵：\n" + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
