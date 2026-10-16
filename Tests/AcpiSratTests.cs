using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ACPI SRAT 解碼器的守門（ULTIMATE-1000 階段二 P2-007）。
///
/// <para>
/// <b>這一組最重要的一條是鄰近域的拼法。</b>型別 0 的鄰近域被拆成兩個位元組
/// （高位在 @2、低位在 @9），看起來像兩個獨立的欄位，其實拼起來才是
/// 一個 32 位元的值。把任一半當成完整值，會得到一個「看起來像節點編號」的數字
/// ——而那正是最難發現的錯。
/// </para>
/// </summary>
public class AcpiSratTests
{
    private static byte[] Build(params byte[][] structures)
    {
        var body = new List<byte>();
        body.AddRange(new byte[12]);   // Reserved @36
        foreach (var s in structures) body.AddRange(s);

        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "SRAT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        table[8] = 3;
        "TESTID"u8.CopyTo(table.AsSpan(10));
        body.CopyTo(table, AcpiTable.HeaderLength);
        return table;
    }

    /// <summary>處理器親和（型別 0，長 16）：鄰近域拆成高／低兩個位元組。</summary>
    private static byte[] ProcAffinity(uint domain, byte apicId, bool enabled)
    {
        var b = new byte[16];
        b[0] = 0; b[1] = 16;
        b[2] = (byte)(domain >> 8);         // Proximity Domain High
        b[3] = apicId;
        BitConverter.GetBytes(enabled ? 1u : 0u).CopyTo(b, 4);
        b[9] = (byte)(domain & 0xFF);       // Proximity Domain Low
        return b;
    }

    /// <summary>處理器 x2APIC 親和（型別 2，長 24）。</summary>
    private static byte[] X2ApicAffinity(uint domain, uint x2apicId, bool enabled)
    {
        var b = new byte[24];
        b[0] = 2; b[1] = 24;
        BitConverter.GetBytes(domain).CopyTo(b, 4);
        BitConverter.GetBytes(x2apicId).CopyTo(b, 8);
        BitConverter.GetBytes(enabled ? 1u : 0u).CopyTo(b, 12);
        return b;
    }

    /// <summary>記憶體親和（型別 1，長 40）。</summary>
    private static byte[] MemAffinity(uint domain, ulong baseAddr, ulong length, bool enabled)
    {
        var b = new byte[40];
        b[0] = 1; b[1] = 40;
        BitConverter.GetBytes(domain).CopyTo(b, 2);
        BitConverter.GetBytes(baseAddr).CopyTo(b, 8);
        BitConverter.GetBytes(length).CopyTo(b, 16);
        BitConverter.GetBytes(enabled ? 1u : 0u).CopyTo(b, 28);
        return b;
    }

    /// <summary>一般親和（型別 5，長 16）。</summary>
    private static byte[] GenericAffinity(uint domain, byte handleType, uint handle)
    {
        var b = new byte[16];
        b[0] = 5; b[1] = 16;
        b[3] = handleType;
        BitConverter.GetBytes(domain).CopyTo(b, 4);
        BitConverter.GetBytes(handle).CopyTo(b, 8);
        return b;
    }

    // ── 走訪 ────────────────────────────────────────────────────────────

    [Fact]
    public void 走訪_從表頭36加12之後起算()
    {
        var s = AcpiSrat.Structures(Build(ProcAffinity(0, 0, true)));
        Assert.Single(s);
        Assert.Equal(AcpiTable.HeaderLength + 12, s[0].Offset);
    }

    [Fact]
    public void 走訪_長度為零要停而不是無窮迴圈()
    {
        Assert.Empty(AcpiSrat.Structures(Build(new byte[] { 0, 0 })));
    }

    [Fact]
    public void 走訪_長度超出表尾要停()
    {
        Assert.Empty(AcpiSrat.Structures(Build(new byte[] { 0, 200 })));
    }

    // ── 處理器親和的鄰近域拼法 ──────────────────────────────────────────

    [Fact]
    public void 處理器親和_鄰近域要拼高低兩個位元組()
    {
        // 0x0102 = 高 0x01、低 0x02。只讀任一半會得到 1 或 2，都不是 258。
        var t = Build(ProcAffinity(domain: 0x0102, apicId: 7, enabled: true));
        var p = Assert.Single(AcpiSrat.ProcessorAffinities(t));

        Assert.Equal(0x0102u, p.ProximityDomain);
        Assert.Equal((byte)7, p.ApicId);
        Assert.True(p.Enabled);
        Assert.False(p.IsX2Apic);
    }

    [Fact]
    public void 處理器親和_只讀低位會得到錯的節點()
    {
        // 這一條把「拼法」的失敗模式說清楚：域 0x0100 的低位是 0，
        // 只讀低位會得到「節點 0」——一個完全合理但錯的答案。
        var t = Build(ProcAffinity(domain: 0x0100, apicId: 1, enabled: true));
        var p = Assert.Single(AcpiSrat.ProcessorAffinities(t));

        Assert.Equal(0x0100u, p.ProximityDomain);
        Assert.NotEqual(0u, p.ProximityDomain);
    }

    [Fact]
    public void 處理器親和_停用旗標要如實回報()
    {
        var t = Build(ProcAffinity(0, 5, enabled: false));
        Assert.False(Assert.Single(AcpiSrat.ProcessorAffinities(t)).Enabled);
    }

    [Fact]
    public void 處理器親和_長度不足16要略過()
    {
        var b = new byte[12];
        b[0] = 0; b[1] = 12;
        Assert.Empty(AcpiSrat.ProcessorAffinities(Build(b)));
    }

    // ── x2APIC 親和 ─────────────────────────────────────────────────────

    [Fact]
    public void x2APIC親和_兩個欄位都是32位元()
    {
        // 用超過 255 的值驗——寫成 byte 會立刻看得出來。
        var t = Build(X2ApicAffinity(domain: 0x0000_0123, x2apicId: 0x0000_0456, enabled: true));
        var p = Assert.Single(AcpiSrat.X2ApicAffinities(t));

        Assert.Equal(0x123u, p.ProximityDomain);
        Assert.Equal(0x456u, p.ApicId);
        Assert.True(p.IsX2Apic);
    }

    [Fact]
    public void x2APIC親和_長度不足24要略過()
    {
        var b = new byte[16];
        b[0] = 2; b[1] = 16;
        Assert.Empty(AcpiSrat.X2ApicAffinities(Build(b)));
    }

    // ── 記憶體親和 ──────────────────────────────────────────────────────

    [Fact]
    public void 記憶體親和_位址與長度都是64位元()
    {
        // 用超過 32 位元的位址驗——截成 32 位元會讓高位消失，
        // 而剩下來的位址看起來仍然「像個位址」。
        var t = Build(MemAffinity(domain: 1, baseAddr: 0x1_0000_0000, length: 0x4000_0000, enabled: true));
        var m = Assert.Single(AcpiSrat.MemoryAffinities(t));

        Assert.Equal(1u, m.ProximityDomain);
        Assert.Equal(0x1_0000_0000ul, m.BaseAddress);
        Assert.Equal(0x4000_0000ul, m.Length);
        Assert.True(m.Enabled);
    }

    [Fact]
    public void 記憶體親和_長度不足40要略過()
    {
        var b = new byte[32];
        b[0] = 1; b[1] = 32;
        Assert.Empty(AcpiSrat.MemoryAffinities(Build(b)));
    }

    // ── 一般親和 ────────────────────────────────────────────────────────

    [Fact]
    public void 一般親和_欄位偏移正確()
    {
        var t = Build(GenericAffinity(domain: 2, handleType: 0, handle: 0x0001_0000));
        var g = Assert.Single(AcpiSrat.GenericAffinities(t));

        Assert.Equal(2u, g.ProximityDomain);
        Assert.Equal((byte)0, g.DeviceHandleType);
        Assert.Equal(0x0001_0000u, g.Handle);
    }

    // ── 型別統計 ────────────────────────────────────────────────────────

    [Fact]
    public void 型別統計_未收錄型別也要留下來()
    {
        var unknown = new byte[] { 99, 4, 0, 0 };
        var t = Build(ProcAffinity(0, 1, true), MemAffinity(0, 0, 0x1000, true), unknown);

        var c = AcpiSrat.CountByType(t);
        Assert.Equal(1, c[0]);
        Assert.Equal(1, c[1]);
        Assert.Equal(1, c[99]);
        Assert.Null(AcpiSrat.DescribeType(99));
    }

    [Fact]
    public void 型別名稱_六個已知型別都有中文名且互異()
    {
        byte[] known = [0, 1, 2, 3, 4, 5];
        var names = known.Select(AcpiSrat.DescribeType).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(6, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ── 界線 ────────────────────────────────────────────────────────────

    [Fact]
    public void 空表或過短表不得丟例外()
    {
        Assert.Empty(AcpiSrat.Structures([]));
        Assert.Empty(AcpiSrat.Structures(new byte[20]));
        Assert.Empty(AcpiSrat.ProcessorAffinities(new byte[20]));
        Assert.Empty(AcpiSrat.X2ApicAffinities(new byte[20]));
        Assert.Empty(AcpiSrat.MemoryAffinities(new byte[20]));
        Assert.Empty(AcpiSrat.GenericAffinities(new byte[20]));
    }

    [Fact]
    public void 非SRAT簽章回空()
    {
        var t = Build(ProcAffinity(0, 1, true));
        "XXXX"u8.CopyTo(t.AsSpan());
        Assert.Empty(AcpiSrat.Structures(t));
    }
}
