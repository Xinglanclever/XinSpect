using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ACPI DMAR 解碼器的守門（ULTIMATE-1000 階段二 P2-010）。
///
/// <para>
/// <b>這一組的核心是 <c>0xFFFF</c> 那個結束標記。</b>
/// 裝置範圍段以 0xFFFF 收尾，若把它當成一段真實範圍，
/// 就會憑空多出一台「裝置編號 65535」——而那種錯誤不會拋例外，
/// 只會讓使用者以為機器上有一台不存在的裝置。
/// </para>
///
/// <para>
/// 第二個核心是 <b>RMRR 的語意</b>：它宣告的是「這些記憶體不受 IOMMU 保護」，
/// 也就是保護的<b>例外清單</b>。解碼器必須正確取到基底與上限，
/// 因為那兩個值決定了「洞有多大」。
/// </para>
/// </summary>
public class AcpiDmarTests
{
    private static byte[] Build(params byte[][] structures)
    {
        var body = new List<byte>();
        body.Add(0x2A);              // Host Address Width @36
        body.Add(0x03);              // Flags @37
        body.AddRange(new byte[10]); // Reserved @38
        foreach (var s in structures) body.AddRange(s);

        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "DMAR"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        table[8] = 1;
        "TESTID"u8.CopyTo(table.AsSpan(10));
        body.CopyTo(table, AcpiTable.HeaderLength);
        return table;
    }

    private static void WriteHeader(byte[] b, ushort type, ushort length)
    {
        BitConverter.GetBytes(type).CopyTo(b, 0);
        BitConverter.GetBytes(length).CopyTo(b, 2);
    }

    /// <summary>DRHD（型別 0）：旗標、段、暫存器基底，後接裝置範圍段（自動補 0xFFFF）。</summary>
    private static byte[] Drhd(byte flags, ushort segment, ulong registerBase,
        params (ushort Start, ushort End)[] scopes)
        => DrhdRaw(flags, segment, registerBase, true, scopes);

    private static byte[] DrhdRaw(byte flags, ushort segment, ulong registerBase,
        bool appendTerminator, params (ushort Start, ushort End)[] scopes)
    {
        int scopeBytes = scopes.Length * 4 + (appendTerminator ? 4 : 0);
        var b = new byte[16 + scopeBytes];
        WriteHeader(b, 0, (ushort)b.Length);
        b[4] = flags;
        BitConverter.GetBytes(segment).CopyTo(b, 6);
        BitConverter.GetBytes(registerBase).CopyTo(b, 8);
        int p = 16;
        foreach (var (start, end) in scopes)
        {
            BitConverter.GetBytes(start).CopyTo(b, p);
            BitConverter.GetBytes(end).CopyTo(b, p + 2);
            p += 4;
        }
        if (appendTerminator) BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, p);
        return b;
    }

    /// <summary>RMRR（型別 1）：段、基底、上限，後接裝置範圍段。</summary>
    private static byte[] Rmrr(ushort segment, ulong baseAddress, ulong limit,
        params (ushort Start, ushort End)[] scopes)
    {
        var b = new byte[24 + scopes.Length * 4 + 4];
        WriteHeader(b, 1, (ushort)b.Length);
        BitConverter.GetBytes(segment).CopyTo(b, 6);
        BitConverter.GetBytes(baseAddress).CopyTo(b, 8);
        BitConverter.GetBytes(limit).CopyTo(b, 16);
        int p = 24;
        foreach (var (start, end) in scopes)
        {
            BitConverter.GetBytes(start).CopyTo(b, p);
            BitConverter.GetBytes(end).CopyTo(b, p + 2);
            p += 4;
        }
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, p);
        return b;
    }

    /// <summary>ATSR（型別 2）：旗標、段。</summary>
    private static byte[] Atsr(byte flags, ushort segment, params (ushort Start, ushort End)[] scopes)
    {
        var b = new byte[8 + scopes.Length * 4 + 4];
        WriteHeader(b, 2, (ushort)b.Length);
        b[4] = flags;
        BitConverter.GetBytes(segment).CopyTo(b, 6);
        int p = 8;
        foreach (var (start, end) in scopes)
        {
            BitConverter.GetBytes(start).CopyTo(b, p);
            BitConverter.GetBytes(end).CopyTo(b, p + 2);
            p += 4;
        }
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, p);
        return b;
    }

    // ── 表頭與走訪 ──────────────────────────────────────────────────────

    [Fact]
    public void 表頭_讀得出位址寬度與旗標()
    {
        var h = AcpiDmar.Header(Build(Drhd(1, 0, 0xFED90000)));
        Assert.NotNull(h);
        Assert.Equal(0x2A, h!.Value.HostAddressWidth);
        Assert.Equal(0x03, h.Value.Flags);
    }

    [Fact]
    public void 表頭_非DMAR或過短回null()
    {
        Assert.Null(AcpiDmar.Header("XXXX"u8.ToArray()));
        Assert.Null(AcpiDmar.Header(new byte[40]));
    }

    [Fact]
    public void 走訪_從表頭36加12之後起算()
    {
        var s = AcpiDmar.Structures(Build(Drhd(1, 0, 0xFED90000)));
        Assert.Single(s);
        Assert.Equal(AcpiTable.HeaderLength + 12, s[0].Offset);
    }

    [Fact]
    public void 走訪_長度為零要停而不是無窮迴圈()
    {
        var bad = new byte[] { 0, 0, 0, 0 };
        Assert.Empty(AcpiDmar.Structures(Build(bad)));
    }

    [Fact]
    public void 走訪_長度超出表尾要停()
    {
        var bad = new byte[] { 0, 0, 0xFF, 0xFF };
        Assert.Empty(AcpiDmar.Structures(Build(bad)));
    }

    // ── DRHD ────────────────────────────────────────────────────────────

    [Fact]
    public void DRHD_欄位偏移正確()
    {
        var t = Build(Drhd(flags: 1, segment: 0, registerBase: 0xFED90000));
        var d = Assert.Single(AcpiDmar.Drhds(t));

        Assert.Equal(1, d.Flags);
        Assert.Equal(0, d.Segment);
        Assert.Equal(0xFED90000ul, d.RegisterBase);
        Assert.True(d.CoversWholeMachine);
    }

    [Fact]
    public void DRHD_段號是16位元_用超過255的值驗()
    {
        // 段號在 PCI 上是 16 位元；寫成 byte 會讓 segment 256 變成 0。
        var t = Build(Drhd(0, segment: 0x0102, registerBase: 0x1000));
        var d = Assert.Single(AcpiDmar.Drhds(t));
        Assert.Equal(0x0102, d.Segment);
    }

    [Fact]
    public void DRHD_沒有涵蓋全機的旗標時要如實回報()
    {
        var t = Build(Drhd(flags: 0, segment: 0, registerBase: 0x1000));
        var d = Assert.Single(AcpiDmar.Drhds(t));
        Assert.False(d.CoversWholeMachine);
    }

    [Fact]
    public void DRHD_裝置範圍段要排除結束標記()
    {
        // 三段真實範圍 + 一個 0xFFFF 標記。若把標記當一段，Count 會是 4。
        var t = Build(Drhd(1, 0, 0x1000, (0x0002, 0x0002), (0x0014, 0x0014), (0x001D, 0x001D)));
        var d = Assert.Single(AcpiDmar.Drhds(t));

        Assert.Equal(3, d.DeviceScopes.Count);
        Assert.Equal((0x0002, 0x0002), d.DeviceScopes[0]);
        Assert.Equal((0x001D, 0x001D), d.DeviceScopes[2]);
        Assert.DoesNotContain(d.DeviceScopes, s => s.Start == 0xFFFF);
    }

    [Fact]
    public void DRHD_沒有結束標記時也要正確解讀()
    {
        var t = Build(DrhdRaw(1, 0, 0x1000, appendTerminator: false, (0x0002, 0x0002)));
        var d = Assert.Single(AcpiDmar.Drhds(t));
        Assert.Single(d.DeviceScopes);
    }

    [Fact]
    public void DRHD_長度不足16要略過()
    {
        var b = new byte[10];
        WriteHeader(b, 0, 10);
        Assert.Empty(AcpiDmar.Drhds(Build(b)));
    }

    // ── RMRR ────────────────────────────────────────────────────────────

    [Fact]
    public void RMRR_基底與上限要正確_那兩個值決定洞有多大()
    {
        var t = Build(Rmrr(segment: 0, baseAddress: 0x0000_0000_8F00_0000,
            limit: 0x0000_0000_8FFF_FFFF, (0x0014, 0x0014)));
        var r = Assert.Single(AcpiDmar.Rmrrs(t));

        Assert.Equal(0ul, r.Segment);
        Assert.Equal(0x8F00_0000ul, r.BaseAddress);
        Assert.Equal(0x8FFF_FFFFul, r.Limit);
        Assert.Single(r.DeviceScopes);
    }

    [Fact]
    public void RMRR_沒有裝置範圍段時要回空清單而不是丟例外()
    {
        var b = new byte[24 + 4];
        WriteHeader(b, 1, (ushort)b.Length);
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, 24);

        var r = Assert.Single(AcpiDmar.Rmrrs(Build(b)));
        Assert.Empty(r.DeviceScopes);
    }

    [Fact]
    public void RMRR_長度不足24要略過()
    {
        var b = new byte[16];
        WriteHeader(b, 1, 16);
        Assert.Empty(AcpiDmar.Rmrrs(Build(b)));
    }

    // ── ATSR ────────────────────────────────────────────────────────────

    [Fact]
    public void ATSR_欄位偏移正確()
    {
        var t = Build(Atsr(flags: 1, segment: 0, (0x0002, 0x0002)));
        var a = Assert.Single(AcpiDmar.Atsrs(t));

        Assert.Equal(1, a.Flags);
        Assert.Equal(0, a.Segment);
        Assert.Single(a.DeviceScopes);
    }

    [Fact]
    public void ATSR_長度不足8要略過()
    {
        var b = new byte[6];
        WriteHeader(b, 2, 6);
        Assert.Empty(AcpiDmar.Atsrs(Build(b)));
    }

    // ── 型別統計與未收錄 ────────────────────────────────────────────────

    [Fact]
    public void 型別統計_未收錄的型別也要留下來()
    {
        var unknown = new byte[] { 99, 0, 4, 0 };
        var t = Build(Drhd(1, 0, 0x1000), Rmrr(0, 0x1000, 0x1FFF), unknown);

        var counts = AcpiDmar.CountByType(t);
        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[1]);
        Assert.Equal(1, counts[99]);
        Assert.Null(AcpiDmar.DescribeType(99));
    }

    [Fact]
    public void 型別名稱_六個已知型別都有中文名且互異()
    {
        var names = new ushort[] { 0, 1, 2, 3, 4, 5 }.Select(AcpiDmar.DescribeType).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(6, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 空表或過短表不得丟例外()
    {
        Assert.Empty(AcpiDmar.Structures([]));
        Assert.Empty(AcpiDmar.Structures(new byte[20]));
        Assert.Empty(AcpiDmar.Drhds(new byte[20]));
        Assert.Empty(AcpiDmar.Rmrrs(new byte[20]));
        Assert.Empty(AcpiDmar.Atsrs(new byte[20]));
        Assert.Empty(AcpiDmar.CountByType(new byte[20]));
    }

    [Fact]
    public void 非DMAR簽章回空()
    {
        var t = Build(Drhd(1, 0, 0x1000));
        "XXXX"u8.CopyTo(t.AsSpan());
        Assert.Empty(AcpiDmar.Structures(t));
    }
}
