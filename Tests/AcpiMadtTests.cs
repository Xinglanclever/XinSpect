using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ACPI MADT 解碼器的守門（ULTIMATE-1000 階段二 P2-006）。
///
/// <para>
/// <b>這一組的測試資料是合成表位元組，欄位偏移逐個驗</b>——
/// 不是「跑得過去就算過」。原因很直接：解碼器最危險的失效模式是
/// <b>偏移寫錯但程式不報錯</b>，它會安靜地產出看起來合理、實際上錯位的值。
/// 只有逐欄位釘住的測試能抓到那種錯。
/// </para>
/// </summary>
public class AcpiMadtTests
{
    /// <summary>組一張合成 MADT：36 bytes 表頭 ＋ Local APIC 位址／旗標 ＋ 各子結構。</summary>
    private static byte[] Build(uint lapicAddress, uint flags, params byte[][] structures)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(lapicAddress));   // @36
        body.AddRange(BitConverter.GetBytes(flags));          // @40
        foreach (var s in structures) body.AddRange(s);

        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "MADT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        table[8] = 5;                                          // Revision
        "TESTID"u8.CopyTo(table.AsSpan(10));
        "XINSPECT"u8.CopyTo(table.AsSpan(16));
        body.CopyTo(table, AcpiTable.HeaderLength);
        return table;
    }

    /// <summary>Local APIC 子結構（型別 0，長 8）：處理器編號／APIC ID／旗標。</summary>
    private static byte[] Lapic(byte procId, byte apicId, uint flags)
    {
        var b = new byte[8];
        b[0] = 0; b[1] = 8; b[2] = procId; b[3] = apicId;
        BitConverter.GetBytes(flags).CopyTo(b, 4);
        return b;
    }

    /// <summary>I/O APIC 子結構（型別 1，長 12）。</summary>
    private static byte[] IoApic(byte id, uint address, uint gsiBase)
    {
        var b = new byte[12];
        b[0] = 1; b[1] = 12; b[2] = id;
        BitConverter.GetBytes(address).CopyTo(b, 4);
        BitConverter.GetBytes(gsiBase).CopyTo(b, 8);
        return b;
    }

    /// <summary>中斷來源覆寫（型別 2，長 10）。</summary>
    private static byte[] Override(byte bus, byte source, uint gsi, ushort flags)
    {
        var b = new byte[10];
        b[0] = 2; b[1] = 10; b[2] = bus; b[3] = source;
        BitConverter.GetBytes(gsi).CopyTo(b, 4);
        BitConverter.GetBytes(flags).CopyTo(b, 8);
        return b;
    }

    /// <summary>Local x2APIC（型別 9，長 16）：x2APIC ID 與 ACPI UID 都是 32 位元。</summary>
    private static byte[] X2Apic(uint x2apicId, uint uid, uint flags)
    {
        var b = new byte[16];
        b[0] = 9; b[1] = 16;
        BitConverter.GetBytes(x2apicId).CopyTo(b, 4);
        BitConverter.GetBytes(flags).CopyTo(b, 8);
        BitConverter.GetBytes(uid).CopyTo(b, 12);
        return b;
    }

    /// <summary>Local x2APIC NMI（型別 10，長 12）。</summary>
    private static byte[] X2ApicNmi(ushort flags, uint uid, byte lint)
    {
        var b = new byte[12];
        b[0] = 10; b[1] = 12;
        BitConverter.GetBytes(flags).CopyTo(b, 2);
        BitConverter.GetBytes(uid).CopyTo(b, 4);
        b[8] = lint;
        return b;
    }

    // ── 表頭 ────────────────────────────────────────────────────────────

    [Fact]
    public void 表頭_讀得出LocalAPIC位址與旗標()
    {
        var t = Build(0xFEE00000, 1, Lapic(0, 0, 1));
        var h = AcpiMadt.Header(t);

        Assert.NotNull(h);
        Assert.Equal(0xFEE00000u, h!.Value.Address);
        Assert.Equal(1u, h.Value.Flags);
    }

    [Fact]
    public void 表頭_非MADT或過短回null()
    {
        Assert.Null(AcpiMadt.Header("XXXX"u8.ToArray()));
        Assert.Null(AcpiMadt.Header(new byte[40]));
    }

    // ── 子結構走訪 ──────────────────────────────────────────────────────

    [Fact]
    public void 走訪_從表頭36加8之後起算()
    {
        var t = Build(0xFEE00000, 0, Lapic(0, 0, 1), IoApic(2, 0xFEC00000, 0));
        var s = AcpiMadt.Structures(t);

        Assert.Equal(2, s.Count);
        Assert.Equal(AcpiTable.HeaderLength + 8, s[0].Offset);   // 第一個子結構的位移
    }

    [Fact]
    public void 走訪_長度為零的子結構會讓位移不前進_必須停而不是無窮迴圈()
    {
        // 一個 type=0 length=0 的結構若被接受，off 永遠不變——這是解碼器最經典的當機方式。
        var t = Build(0, 0, [0, 0], Lapic(0, 0, 1));
        Assert.Empty(AcpiMadt.Structures(t));
    }

    [Fact]
    public void 走訪_長度超出表尾代表表壞了_必須停()
    {
        var t = Build(0, 0, [0, 200]);   // 宣告長 200 但表沒那麼長
        Assert.Empty(AcpiMadt.Structures(t));
    }

    [Fact]
    public void 走訪_非MADT簽章回空()
    {
        var t = Build(0, 0, Lapic(0, 0, 1));
        "XXXX"u8.CopyTo(t.AsSpan());
        Assert.Empty(AcpiMadt.Structures(t));
    }

    // ── 逐欄位偏移 ──────────────────────────────────────────────────────

    [Fact]
    public void LocalApic_欄位偏移正確()
    {
        var t = Build(0, 0, Lapic(procId: 7, apicId: 42, flags: 3));
        var apic = Assert.Single(AcpiMadt.LocalApics(t));

        Assert.Equal(7, apic.AcpiProcessorId);
        Assert.Equal(42, apic.ApicId);
        Assert.Equal(3u, apic.Flags);
    }

    [Fact]
    public void IoApic_欄位偏移正確()
    {
        var t = Build(0, 0, IoApic(id: 2, address: 0xFEC00000, gsiBase: 24));
        var io = Assert.Single(AcpiMadt.IoApics(t));

        Assert.Equal(2, io.Id);
        Assert.Equal(0xFEC00000u, io.Address);
        Assert.Equal(24u, io.GlobalSystemInterruptBase);
    }

    [Fact]
    public void 中斷來源覆寫_欄位偏移正確()
    {
        var t = Build(0, 0, Override(bus: 0, source: 2, gsi: 9, flags: 0x000F));
        var ov = Assert.Single(AcpiMadt.InterruptOverrides(t));

        Assert.Equal(0, ov.Bus);
        Assert.Equal(2, ov.Source);
        Assert.Equal(9u, ov.GlobalSystemInterrupt);
        Assert.Equal(0x000Fu, ov.Flags);
    }

    [Fact]
    public void x2APIC_兩個欄位都是32位元_這是它與型別零的實質差別()
    {
        // 用超過 255 的值——若偏移或型別寫錯成 byte，這裡會立刻看得出來。
        var t = Build(0, 0, X2Apic(x2apicId: 0x0000_0123, uid: 0x0000_0456, flags: 1));
        var x2 = Assert.Single(AcpiMadt.LocalX2Apics(t));

        Assert.Equal(0x123u, x2.X2ApicId);
        Assert.Equal(0x456u, x2.AcpiProcessorUid);
        Assert.Equal(1u, x2.Flags);
    }

    [Fact]
    public void x2APIC_NMI_欄位偏移正確()
    {
        var t = Build(0, 0, X2ApicNmi(flags: 0x0005, uid: 300, lint: 1));
        var nmi = Assert.Single(AcpiMadt.LocalX2ApicNmis(t));

        Assert.Equal(300u, nmi.AcpiProcessorUid);
        Assert.Equal(1, nmi.Lint);
        Assert.Equal(0x0005, nmi.Flags);
    }

    [Fact]
    public void 長度不足的子結構要略過_不猜缺失欄位()
    {
        // 型別 0 但長度只有 6（不足 8）：欄位讀不到，略過而不是讀出越界的值。
        var shortStruct = new byte[] { 0, 6, 1, 2, 0, 0 };
        var t = Build(0, 0, shortStruct);

        Assert.Empty(AcpiMadt.LocalApics(t));
        Assert.Single(AcpiMadt.Structures(t));   // 走訪看得到它，但解碼器不認
    }

    // ── 型別統計與未收錄 ────────────────────────────────────────────────

    [Fact]
    public void 型別統計_未收錄的型別也要留下來()
    {
        // 型別 99 是我不認得的——「表裡有一個我不認得的結構」本身是事實。
        var unknown = new byte[] { 99, 4, 0, 0 };
        var t = Build(0, 0, Lapic(0, 0, 1), Lapic(1, 1, 1), unknown);

        var counts = AcpiMadt.CountByType(t);
        Assert.Equal(2, counts[0]);
        Assert.Equal(1, counts[99]);
        Assert.Null(AcpiMadt.DescribeType(99));   // 認不得要回 null，不是回「未知」
    }

    [Fact]
    public void 型別名稱_已知型別都有中文名且互異()
    {
        byte[] known = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var names = known.Select(AcpiMadt.DescribeType).ToList();

        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ── 真實機器上的形狀（不猜、只驗界線）──────────────────────────────

    [Fact]
    public void 空表或過短表不得丟例外()
    {
        Assert.Empty(AcpiMadt.Structures([]));
        Assert.Empty(AcpiMadt.LocalApics(new byte[20]));
        Assert.Empty(AcpiMadt.IoApics(new byte[20]));
        Assert.Empty(AcpiMadt.InterruptOverrides(new byte[20]));
        Assert.Empty(AcpiMadt.LocalX2Apics(new byte[20]));
        Assert.Empty(AcpiMadt.LocalX2ApicNmis(new byte[20]));
        Assert.Empty(AcpiMadt.CountByType(new byte[20]));
    }

    [Fact]
    public void 多處理器與多IOAPIC_順序要照表內順序()
    {
        var t = Build(0, 0,
            Lapic(0, 10, 1), Lapic(1, 11, 1), Lapic(2, 12, 1),
            IoApic(8, 0xFEC00000, 0), IoApic(9, 0xFEC01000, 24));

        var apics = AcpiMadt.LocalApics(t);
        Assert.Equal([10, 11, 12], apics.Select(a => (int)a.ApicId));

        var ios = AcpiMadt.IoApics(t);
        Assert.Equal([8, 9], ios.Select(i => (int)i.Id));
    }
}
