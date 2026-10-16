using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TPM2 與 WPBT 表事實的守門（ULTIMATE-1000 階段二 P2-017）。
/// </summary>
public class AcpiTpm2WpbtFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>造一張 ACPI 表：簽章 ＋ 長度 ＋ 指定的內容。</summary>
    private static byte[] Table(string signature, int totalLength, Action<byte[]>? fill = null)
    {
        var t = new byte[totalLength];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(t, 0);
        BitConverter.GetBytes((uint)totalLength).CopyTo(t, 4);
        fill?.Invoke(t);
        return t;
    }

    private static byte[] Tpm2(ushort platformClass, ulong address, uint length, uint method)
    {
        var t = Table("TPM2", 0x3C);
        BitConverter.GetBytes(platformClass).CopyTo(t, 0x24);
        BitConverter.GetBytes(address).CopyTo(t, 0x28);
        BitConverter.GetBytes(length).CopyTo(t, 0x30);
        BitConverter.GetBytes(method).CopyTo(t, 0x38);
        return t;
    }

    private static byte[] Wpbt(uint length, ulong address)
    {
        var t = Table("WPBT", 48);
        BitConverter.GetBytes(length).CopyTo(t, 0x24);
        BitConverter.GetBytes(address).CopyTo(t, 0x28);
        return t;
    }

    private static Dictionary<string, byte[]> Tables(params byte[][] tables)
        => tables.ToDictionary(
            t => System.Text.Encoding.ASCII.GetString(t, 0, 4),
            t => t, StringComparer.Ordinal);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有表來源時要標不支援()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
    }

    [Fact]
    public void 沒有TPM2表要說那與沒有TPM不同()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, Tables(Wpbt(100, 0x1000)));
        var t = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2Key);

        Assert.Equal(FactAvailability.NotApplicable, t.Availability);
        Assert.Contains("「韌體沒有描述 TPM 的位置」與「沒有 TPM」是不同的斷言", t.UnavailableReason);
        Assert.Contains("直接探測常見的 MMIO 位址", t.UnavailableReason);
    }

    [Fact]
    public void TPM2表長度不足要如實拒解()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, Tables(Table("TPM2", 40)));
        var t = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2Key);

        Assert.Equal(FactAvailability.ReadError, t.Availability);
        Assert.Contains("如實拒解，不猜", t.UnavailableReason);
    }

    // ── TPM2 內容 ───────────────────────────────────────────────────────

    [Fact]
    public void TPM2要報出控制區域位址與長度()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4)));
        var t = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2Key);

        Assert.Contains("客戶端（Client）", t.Value);
        Assert.Contains("0xFED40000", t.Value);
        Assert.Contains("20480 bytes", t.Value);
        Assert.Equal((double)0xFED40000, t.NumericValue);
    }

    [Fact]
    public void 伺服器平台類別要認得()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(1, 0xFED40000, 0x5000, 2)));
        var t = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2Key);

        Assert.Contains("伺服器（Server）", t.Value);
    }

    [Fact]
    public void TPM2要說出它是第三個獨立來源()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4)));
        var t = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2Key);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("只描述「TPM 在哪、怎麼跟它講話」", t.Value);
        Assert.Contains("tpm.*", t.Value);
        Assert.Contains("sb.tpmdev", t.Value);
        Assert.Contains("三個獨立來源並列的事實", t.Value);
    }

    [Fact]
    public void 起始方法要解出CRB()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4)));
        var m = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2MethodKey);

        Assert.StartsWith("CRB（命令回應緩衝區）", m.Value);
        Assert.Equal(4.0, m.NumericValue);
    }

    [Fact]
    public void 起始方法要說出協定宣告與實際可用是兩件事()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4)));
        var m = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2MethodKey);

        Assert.Contains("實際可用是兩件事", m.Value);
        Assert.Contains("TBS 的回應", m.Value);
    }

    [Fact]
    public void 起始方法名稱表要涵蓋常見值()
    {
        Assert.Equal("未指定", AcpiTpm2WpbtFactsService.StartMethodName(0));
        Assert.Equal("韌體 TPM 介面（FIFO）", AcpiTpm2WpbtFactsService.StartMethodName(3));
        Assert.Equal("CRB 搭配 5.0 版介面", AcpiTpm2WpbtFactsService.StartMethodName(15));
        Assert.Contains("方法 99", AcpiTpm2WpbtFactsService.StartMethodName(99));
    }

    [Fact]
    public void 沒有TPM2表時起始方法要標不適用()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, Tables(Wpbt(100, 0x1000)));
        var m = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.Tpm2MethodKey);

        Assert.Equal(FactAvailability.NotApplicable, m.Availability);
        Assert.Contains("沒有 TPM2 表", m.UnavailableReason);
    }

    // ── WPBT ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有WPBT時要說那是一件好事並說出理由()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4)));
        var w = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.WpbtKey);

        Assert.Equal(FactAvailability.NotApplicable, w.Availability);
        Assert.Contains("那是一件好事", w.UnavailableReason);
        Assert.Contains("要求 OS 在開機時執行韌體提供的一段程式", w.UnavailableReason);
    }

    [Fact]
    public void WPBT存在要報出映像位址與長度並標敏感()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, Tables(Wpbt(0x2000, 0x100000)));
        var w = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.WpbtKey);

        Assert.Equal(FactAvailability.Present, w.Availability);
        Assert.True(w.Sensitive);
        Assert.Contains("0x100000", w.Value);
        Assert.Contains("8192 bytes", w.Value);
    }

    [Fact]
    public void WPBT要說出它不是弱點但值得知道()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, Tables(Wpbt(0x2000, 0x100000)));
        var w = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.WpbtKey);

        Assert.Contains("本身不是弱點", w.Value);
        Assert.Contains("由韌體發起、在使用者登入前執行", w.Value);
        Assert.Contains("不解析那段映像的內容", w.Value);
    }

    [Fact]
    public void WPBT長度不足要如實拒解()
    {
        var facts = AcpiTpm2WpbtFactsService.Collect(At, Tables(Table("WPBT", 40)));
        var w = facts.Single(f => f.Key == AcpiTpm2WpbtFactsService.WpbtKey);

        Assert.Equal(FactAvailability.ReadError, w.Availability);
        Assert.Contains("如實拒解", w.UnavailableReason);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4), Wpbt(100, 0x1000)))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = AcpiTpm2WpbtFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = AcpiTpm2WpbtFactsService.Collect(At,
            Tables(Tpm2(0, 0xFED40000, 0x5000, 4), Wpbt(100, 0x1000)));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "TPM2／WPBT 事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
