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

    /// <summary>Type 43：廠商 ID@0x04、規格版本@0x08／@0x09、韌體@0x0A、特性@0x16、描述@0x1B。</summary>
    private static SmbiosStruct Tpm43(string vendorId, byte major, byte minor,
        ulong firmware, bool enabled, bool owned, bool tpm2, string? description = null)
    {
        var d = new byte[0x1F];
        uint vid = ((uint)vendorId[0] << 24) | ((uint)vendorId[1] << 16)
                 | ((uint)vendorId[2] << 8) | vendorId[3];
        BitConverter.GetBytes(vid).CopyTo(d, 0x04);
        d[0x08] = major;
        d[0x09] = minor;
        BitConverter.GetBytes((uint)(firmware & 0xFFFFFFFF)).CopyTo(d, 0x0A);
        BitConverter.GetBytes((uint)(firmware >> 32)).CopyTo(d, 0x0E);

        ulong chars = 0;
        if (enabled) chars |= 0x04;
        if (owned) chars |= 0x08;
        if (tpm2) chars |= 0x10;
        BitConverter.GetBytes((uint)(chars & 0xFFFFFFFF)).CopyTo(d, 0x16);
        d[0x1A] = (byte)(chars >> 32);

        if (description is not null) d[0x1B] = 1;
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
    }

    // ── 廠商 ID ─────────────────────────────────────────────────────────

    [Fact]
    public void 廠商ID要由高位到低位組成ASCII()
    {
        // 規格的 VendorID 是 u32，值是四個 ASCII 由高位排到低位。
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0x0007_0000, true, false, true)]);
        var v = facts.Single(f => f.Key == SmbiosTpmFactsService.VendorKey);

        Assert.Equal("INTC", v.Value);
    }

    [Fact]
    public void 廠商ID反過來讀會得到顛倒的字串()
    {
        // 這一條把失敗模式說清楚：byte 順序若按 little-endian 讀，
        // "INTC" 會變成 "CTNI"——一個看起來也像廠商代碼的字串。
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, false, true)]);
        var v = facts.Single(f => f.Key == SmbiosTpmFactsService.VendorKey);

        Assert.Equal("INTC", v.Value);
        Assert.NotEqual("CTNI", v.Value);
    }

    // ── 版本與狀態 ──────────────────────────────────────────────────────

    [Fact]
    public void 規格版本要標Present當主版本至少為2()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0x0007_0000, true, false, true)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SpecVersionKey);

        Assert.Equal("2.0", s.Value);
        Assert.Equal(FactAvailability.Present, s.Availability);
    }

    [Fact]
    public void 規格版本低於2時要說以實際回應為準但差異要留下()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 1, 2, 0x0000_0001, false, false, false)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.SpecVersionKey);

        Assert.Equal(FactAvailability.Unknown, s.Availability);
        Assert.Contains("應以實際 TPM 的回應為準", s.UnavailableReason);
        Assert.Contains("差異本身要留下來", s.UnavailableReason);
    }

    [Fact]
    public void 狀態三位元要各自陳述()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, enabled: true, owned: false, tpm2: true)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.StateKey);

        Assert.Contains("已啟用", s.Value);
        Assert.Contains("未佔有", s.Value);
        Assert.Contains("支援 TPM 2.0 協定", s.Value);
    }

    [Fact]
    public void 未佔有要說它不代表不能用()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, true, owned: false, tpm2: true)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.StateKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("不代表不能用", s.Value);
        Assert.Contains("還沒有擁有者", s.Value);
    }

    [Fact]
    public void 全部未啟用也要如實陳述()
    {
        var facts = SmbiosTpmFactsService.Collect(At,
            () => [Tpm43("INTC", 2, 0, 0, false, false, false)]);
        var s = facts.Single(f => f.Key == SmbiosTpmFactsService.StateKey);

        Assert.Contains("未啟用", s.Value);
        Assert.Contains("未宣告支援 TPM 2.0 協定", s.Value);
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
