using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// MADT 事實輸出的守門（ULTIMATE-1000 階段二 P2-006）。
/// 解碼器本身的逐欄位測試在 <see cref="AcpiMadtTests"/>；這裡測的是<b>事實層的三態</b>。
/// </summary>
public class AcpiMadtFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource(bool available, params byte[][] tables) : IAcpiTableSource
    {
        public bool Available { get; } = available;
        public string? UnavailableReason => Available ? null : "測試：來源不可用";
        public IReadOnlyList<byte[]> ReadAll() => tables;
    }

    private static byte[] Build(params byte[][] structures)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(0xFEE00000u));
        body.AddRange(BitConverter.GetBytes(1u));
        foreach (var s in structures) body.AddRange(s);

        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "MADT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        "TESTID"u8.CopyTo(table.AsSpan(10));
        body.CopyTo(table, AcpiTable.HeaderLength);
        return table;
    }

    private static byte[] Lapic(byte procId, byte apicId, uint flags)
    {
        var b = new byte[8];
        b[1] = 8; b[2] = procId; b[3] = apicId;
        BitConverter.GetBytes(flags).CopyTo(b, 4);
        return b;
    }

    private static byte[] IoApic(byte id, uint address, uint gsiBase)
    {
        var b = new byte[12];
        b[0] = 1; b[1] = 12; b[2] = id;
        BitConverter.GetBytes(address).CopyTo(b, 4);
        BitConverter.GetBytes(gsiBase).CopyTo(b, 8);
        return b;
    }

    private static byte[] Override(byte source, uint gsi)
    {
        var b = new byte[10];
        b[0] = 2; b[1] = 10; b[3] = source;
        BitConverter.GetBytes(gsi).CopyTo(b, 4);
        return b;
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 來源不可用要標不支援而不是不適用()
    {
        var f = Assert.Single(AcpiMadtFactsService.Collect(new FakeSource(false), At),
            x => x.Key == AcpiMadtFactsService.SummaryKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("需要管理員權限", f.UnavailableReason);
    }

    [Fact]
    public void 表列裡沒有MADT要標不適用而不是讀取失敗()
    {
        var f = Assert.Single(AcpiMadtFactsService.Collect(new FakeSource(true), At),
            x => x.Key == AcpiMadtFactsService.SummaryKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是讀取失敗", f.UnavailableReason);
    }

    [Fact]
    public void 表存在但壞掉要標讀取失敗()
    {
        var broken = Build(Lapic(0, 0, 1));
        Array.Resize(ref broken, AcpiTable.HeaderLength + 4);   // 截掉到不足表頭＋8

        var f = Assert.Single(AcpiMadtFactsService.Collect(new FakeSource(true, broken), At),
            x => x.Key == AcpiMadtFactsService.SummaryKey);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("如實拒解", f.UnavailableReason);
    }

    // ── 彙總與處理器數 ──────────────────────────────────────────────────

    [Fact]
    public void 彙總要報出處理器數與各種結構數()
    {
        var t = Build(Lapic(0, 10, 1), Lapic(1, 11, 1), IoApic(8, 0xFEC00000, 0), Override(2, 9));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var s = facts.Single(f => f.Key == AcpiMadtFactsService.SummaryKey);

        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Equal(2.0, s.NumericValue);
        Assert.Contains("0xFEE00000", s.Value);
        Assert.Contains("1 個 I/O APIC", s.Value);
        Assert.Contains("1 條中斷來源覆寫", s.Value);
    }

    [Fact]
    public void 沒有宣告任何處理器時要說出兩者並列不代為裁決()
    {
        var t = Build(IoApic(8, 0xFEC00000, 0));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var s = facts.Single(f => f.Key == AcpiMadtFactsService.SummaryKey);

        Assert.Equal(0.0, s.NumericValue);
        Assert.Contains("兩者並列陳述不代為裁決", s.UnavailableReason);
    }

    // ── 各清單的三態 ────────────────────────────────────────────────────

    [Fact]
    public void 沒有傳統LocalAPIC時標不適用並說出與x2APIC互相印證()
    {
        var t = Build(IoApic(8, 0xFEC00000, 0));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.LocalApicKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("互相印證", f.UnavailableReason);
    }

    [Fact]
    public void 全部LocalAPIC標為停用時要說那只是韌體自述()
    {
        // 旗標 bit0 = Enabled；全部為 0 是可疑的，但不能判成「OS 沒在用」。
        var t = Build(Lapic(0, 10, 0), Lapic(1, 11, 0));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.LocalApicKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不代表 OS 沒在用", f.UnavailableReason);
    }

    [Fact]
    public void 沒有I_O_APIC時要說不尋常但不判異常()
    {
        var t = Build(Lapic(0, 10, 1));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.IoApicKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("只陳述事實，不判異常", f.UnavailableReason);
    }

    [Fact]
    public void GSI基底重疊要指名並說出歸屬會變成不確定的()
    {
        var t = Build(IoApic(8, 0xFEC00000, 0), IoApic(9, 0xFEC01000, 0));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.IoApicKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("GSI 基底 0（2 顆）", f.UnavailableReason);
        Assert.Contains("不確定的", f.UnavailableReason);
    }

    // ── 中斷來源覆寫 ────────────────────────────────────────────────────

    [Fact]
    public void 沒有覆寫是常態_標Present並說明意義()
    {
        var t = Build(Lapic(0, 10, 1));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.OverrideKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("ISA IRQ 直接對應 GSI", f.Value);
    }

    [Fact]
    public void 有覆寫時要說它會影響中斷延遲量測的解讀()
    {
        var t = Build(Lapic(0, 10, 1), Override(2, 9));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.OverrideKey);

        Assert.Contains("來源2→GSI9", f.Value);
        Assert.Contains("中斷延遲類量測的解讀", f.UnavailableReason);
    }

    // ── 未收錄型別 ──────────────────────────────────────────────────────

    [Fact]
    public void 有未收錄型別時要標未確認並說覆蓋率不是百分之百()
    {
        var unknown = new byte[] { 99, 4, 0, 0 };
        var t = Build(Lapic(0, 10, 1), unknown);
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.UnrecordedKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("型別 99（1 個）", f.Value);
        Assert.Contains("未收錄不等於表有問題", f.UnavailableReason);
    }

    [Fact]
    public void 全部型別都認得時標Present()
    {
        var t = Build(Lapic(0, 10, 1), IoApic(8, 0xFEC00000, 0));
        var facts = AcpiMadtFactsService.Collect(new FakeSource(true, t), At);
        var f = facts.Single(x => x.Key == AcpiMadtFactsService.UnrecordedKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.UnavailableReason);
    }

    // ── 覆蓋申報 ────────────────────────────────────────────────────────

    [Fact]
    public void MADT要登記進解碼器覆蓋申報()
    {
        // DecodedSignatures 由測試釘住——新增解碼器時忘了登記，覆蓋申報就會說謊。
        Assert.True(AcpiDeep.HasDedicatedDecoder("MADT"));
        Assert.True(AcpiDeep.HasDedicatedDecoder("madt"));
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var t = Build(Lapic(0, 10, 1));
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var unknown = AcpiMadtFactsService.Collect(new FakeSource(true, t), At)
            .Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "MADT 事實產出了目錄裡沒有的鍵：\n" + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
