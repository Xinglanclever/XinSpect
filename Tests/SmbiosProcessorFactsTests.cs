using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 4／7 事實的守門（ULTIMATE-1000 階段二 P2-020）。
///
/// <para>
/// <b>這一組最重要的一條是快取大小的粒度旗標。</b>大小欄位的 bit15 是單位旗標：
/// 1 表示 64 KiB、0 表示 1 KiB。直接把它當 KiB 讀，<b>大快取會少算 64 倍</b>——
/// 而那個錯誤出來的數字仍然「像個快取大小」（例如 512 而不是 32768）。
/// </para>
/// </summary>
public class SmbiosProcessorFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>組一個 SMBIOS 結構：資料位元組 ＋ 字串表。</summary>
    private static SmbiosStruct Struct(byte type, byte[] data, params string[] strings)
        => new(type, 0x0100, data, strings);

    /// <summary>Type 4：插槽字串@0x04、廠商@0x07、家族@0x06、最高速度@0x14、核心@0x23、執行緒@0x25。</summary>
    private static SmbiosStruct Processor(string socket, string vendor, byte family,
        ushort maxSpeed, ushort cores, ushort threads)
    {
        var d = new byte[0x28];
        d[0x04] = 1;                       // Socket Designation → 字串 1
        d[0x06] = family;
        d[0x07] = 2;                       // Manufacturer → 字串 2
        BitConverter.GetBytes(maxSpeed).CopyTo(d, 0x14);
        BitConverter.GetBytes(cores).CopyTo(d, 0x23);
        BitConverter.GetBytes(threads).CopyTo(d, 0x25);
        return Struct(4, d, socket, vendor);
    }

    /// <summary>Type 7：插槽@0x04、配置@0x05、大小@0x09、關聯@0x0C、行大小@0x0D。</summary>
    private static SmbiosStruct Cache(string socket, byte level, ushort rawSize, byte assoc, ushort lineSize)
    {
        var d = new byte[0x13];
        d[0x04] = 1;
        BitConverter.GetBytes((ushort)(level - 1)).CopyTo(d, 0x05);   // 低 3 位是 level-1
        BitConverter.GetBytes(rawSize).CopyTo(d, 0x09);
        d[0x0C] = assoc;
        d[0x0D] = (byte)lineSize;
        return Struct(7, d, socket);
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有結構時要標不支援而不是回零()
    {
        var facts = SmbiosProcessorFactsService.Collect(At, () => []);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
    }

    [Fact]
    public void 讀取失敗時要標讀取失敗並附原因()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => throw new InvalidOperationException("測試注入的失敗"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.All(facts, f => Assert.Contains("測試注入的失敗", f.UnavailableReason));
    }

    [Fact]
    public void 沒有Type4時要說那不代表沒有處理器()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Struct(0, new byte[16])]);
        var c = facts.Single(f => f.Key == SmbiosProcessorFactsService.ProcCountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("不代表沒有處理器，只代表這個來源沒有陳述", c.UnavailableReason);
    }

    // ── Type 4 ──────────────────────────────────────────────────────────

    [Fact]
    public void 處理器數要如實計數()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Processor("CPU0", "Vendor", 0xB3, 4000, 8, 16),
                   Processor("CPU1", "Vendor", 0xB3, 4000, 8, 16)]);
        var c = facts.Single(f => f.Key == SmbiosProcessorFactsService.ProcCountKey);

        Assert.Equal(2.0, c.NumericValue);
        Assert.Equal(FactAvailability.Present, c.Availability);
    }

    [Fact]
    public void 處理器明細要含插槽廠商核心與執行緒()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Processor("CPU0", "GenuineIntel", 0xB3, 4000, 8, 16)]);
        var p = facts.Single(f => f.Key == SmbiosProcessorFactsService.ProcKey);

        Assert.Contains("插槽 CPU0", p.Value);
        Assert.Contains("GenuineIntel", p.Value);
        Assert.Contains("8 核", p.Value);
        Assert.Contains("16 執行緒", p.Value);
        Assert.Contains("4000 MHz", p.Value);
    }

    [Fact]
    public void 處理器明細要說出這是廠填欄位不是CPUID規格()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Processor("CPU0", "Vendor", 0xB3, 3000, 4, 8)]);
        var p = facts.Single(f => f.Key == SmbiosProcessorFactsService.ProcKey);

        Assert.Contains("不是 CPUID 讀到的規格", p.UnavailableReason);
        Assert.Contains("不是哪一個錯了", p.UnavailableReason);
    }

    [Fact]
    public void 核心數欄位不存在時要略過而不是填零()
    {
        // 長度只到 0x20——沒有核心數／執行緒數欄位（SMBIOS 2.5 以前）。
        var d = new byte[0x20];
        d[0x04] = 1;
        var s = Struct(4, d, "CPU0");

        var p = SmbiosProcessorFactsService.Collect(At, () => [s])
            .Single(f => f.Key == SmbiosProcessorFactsService.ProcKey);

        Assert.DoesNotContain("0 核", p.Value);
        Assert.DoesNotContain("0 執行緒", p.Value);
    }

    [Fact]
    public void 佔位字串不得出現在明細裡()
    {
        // "To Be Filled By O.E.M." 是 SMBIOS 常見的佔位值，不是真的廠商名。
        var s = Processor("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", 0xB3, 3000, 4, 8);
        var p = SmbiosProcessorFactsService.Collect(At, () => [s])
            .Single(f => f.Key == SmbiosProcessorFactsService.ProcKey);

        Assert.DoesNotContain("To Be Filled By O.E.M.", p.Value);
    }

    // ── Type 7：粒度旗標 ────────────────────────────────────────────────

    [Fact]
    public void 快取大小_粒度旗標為零時單位是1KiB()
    {
        // 0x0200 = 512，單位 1 KiB → 512 KiB
        Assert.Equal(512L * 1024, SmbiosProcessorFactsService.DecodeCacheSize(0x0200));
    }

    [Fact]
    public void 快取大小_粒度旗標為一時單位是64KiB()
    {
        // 0x8200 = bit15 設起 ＋ 512，單位 64 KiB → 32768 KiB
        Assert.Equal(32768L * 1024, SmbiosProcessorFactsService.DecodeCacheSize(0x8200));
    }

    [Fact]
    public void 快取大小_忽略粒度旗標會少算六十四倍()
    {
        // 這一條把失敗模式說清楚：同一個低位值 512，
        // 有旗標是 32768 KiB、沒有旗標是 512 KiB——差 64 倍。
        long withFlag = SmbiosProcessorFactsService.DecodeCacheSize(0x8200);
        long withoutFlag = SmbiosProcessorFactsService.DecodeCacheSize(0x0200);

        Assert.Equal(64, withFlag / withoutFlag);
    }

    [Fact]
    public void 快取大小_零代表未提供而不是零快取()
    {
        Assert.Equal(0L, SmbiosProcessorFactsService.DecodeCacheSize(0));
    }

    [Fact]
    public void 快取明細要含層級大小與關聯度()
    {
        // L3、0x8200（32768 KiB = 32 MiB）、16 路、行 64 位元組
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Cache("L3 Cache", level: 3, rawSize: 0x8200, assoc: 0x08, lineSize: 64)]);
        var c = facts.Single(f => f.Key == SmbiosProcessorFactsService.CacheKey);

        Assert.Contains("L3", c.Value);
        Assert.Contains("32768 KiB", c.Value);
        Assert.Contains("16 路關聯", c.Value);
        Assert.Contains("行 64 位元組", c.Value);
    }

    [Fact]
    public void 快取大小未提供時要說未提供而不是零()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Cache("L2 Cache", level: 2, rawSize: 0, assoc: 0x05, lineSize: 64)]);
        var c = facts.Single(f => f.Key == SmbiosProcessorFactsService.CacheKey);

        Assert.Contains("大小未提供", c.Value);
        Assert.DoesNotContain("0 KiB", c.Value);
    }

    [Fact]
    public void 沒有Type7時要說缺席不影響判讀()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Processor("CPU0", "Vendor", 0xB3, 3000, 4, 8)]);
        var c = facts.Single(f => f.Key == SmbiosProcessorFactsService.CacheCountKey);

        Assert.Equal(FactAvailability.NotApplicable, c.Availability);
        Assert.Contains("另有 CPUID 來源", c.UnavailableReason);
    }

    [Fact]
    public void 快取明細要說出這是本服務換算過的()
    {
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Cache("L1", 1, 0x0040, 0x07, 64)]);
        var c = facts.Single(f => f.Key == SmbiosProcessorFactsService.CacheKey);

        Assert.Contains("本服務自己算的，不是原始值", c.UnavailableReason);
    }

    // ── 目錄 ────────────────────────────────────────────────────────────

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = SmbiosProcessorFactsService.Collect(At,
            () => [Processor("CPU0", "V", 0xB3, 3000, 4, 8), Cache("L1", 1, 0x0040, 0x07, 64)]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "SMBIOS Type 4／7 產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
