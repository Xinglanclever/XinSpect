using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SRAT 事實輸出的守門（ULTIMATE-1000 階段二 P2-007）。
/// 解碼器的逐欄位測試在 <see cref="AcpiSratTests"/>；這裡測事實層的三態與措辭。
/// </summary>
public class AcpiSratFactsTests
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
        body.AddRange(new byte[12]);
        foreach (var s in structures) body.AddRange(s);

        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "SRAT"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        "TESTID"u8.CopyTo(table.AsSpan(10));
        body.CopyTo(table, AcpiTable.HeaderLength);
        return table;
    }

    private static byte[] ProcAffinity(uint domain, byte apicId, bool enabled = true)
    {
        var b = new byte[16];
        b[0] = 0; b[1] = 16;
        b[2] = (byte)(domain >> 8);
        b[3] = apicId;
        BitConverter.GetBytes(enabled ? 1u : 0u).CopyTo(b, 4);
        b[9] = (byte)(domain & 0xFF);
        return b;
    }

    private static byte[] MemAffinity(uint domain, ulong baseAddr, ulong length, bool enabled = true)
    {
        var b = new byte[40];
        b[0] = 1; b[1] = 40;
        BitConverter.GetBytes(domain).CopyTo(b, 2);
        BitConverter.GetBytes(baseAddr).CopyTo(b, 8);
        BitConverter.GetBytes(length).CopyTo(b, 16);
        BitConverter.GetBytes(enabled ? 1u : 0u).CopyTo(b, 28);
        return b;
    }

    private static byte[] GenericAffinity(uint domain, uint handle)
    {
        var b = new byte[16];
        b[0] = 5; b[1] = 16;
        BitConverter.GetBytes(domain).CopyTo(b, 4);
        BitConverter.GetBytes(handle).CopyTo(b, 8);
        return b;
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 來源不可用要標不支援()
    {
        var f = Assert.Single(AcpiSratFactsService.Collect(new FakeSource(false), At),
            x => x.Key == AcpiSratFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    [Fact]
    public void 沒有SRAT表時要說沒有它不代表沒有NUMA()
    {
        var f = Assert.Single(AcpiSratFactsService.Collect(new FakeSource(true), At),
            x => x.Key == AcpiSratFactsService.SummaryKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不代表沒有 NUMA", f.UnavailableReason);
        Assert.Contains("只代表韌體沒有用它描述歸屬", f.UnavailableReason);
    }

    [Fact]
    public void 表短於表頭時標讀取失敗()
    {
        var b = Build(ProcAffinity(0, 1));
        Array.Resize(ref b, AcpiTable.HeaderLength + 2);

        var f = Assert.Single(AcpiSratFactsService.Collect(new FakeSource(true, b), At),
            x => x.Key == AcpiSratFactsService.SummaryKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 彙總 ────────────────────────────────────────────────────────────

    [Fact]
    public void 彙總要報出處理器與記憶體歸屬數()
    {
        var t = Build(ProcAffinity(0, 0), ProcAffinity(0, 1),
            MemAffinity(0, 0, 0x4000_0000));
        var s = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.SummaryKey);

        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Equal(2.0, s.NumericValue);
        Assert.Contains("2 個處理器歸屬", s.Value);
        Assert.Contains("1 段記憶體歸屬", s.Value);
    }

    [Fact]
    public void 表存在但沒有任何歸屬時要指認它()
    {
        // 空表（只有一個我認不得的型別）——「被提供了卻沒有內容」值得指認。
        var unknown = new byte[] { 99, 4, 0, 0 };
        var t = Build(unknown);
        var s = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.SummaryKey);

        Assert.Contains("值得指認", s.UnavailableReason);
        Assert.Contains("那與「沒有 NUMA」不同", s.UnavailableReason);
    }

    // ── 處理器歸屬 ──────────────────────────────────────────────────────

    [Fact]
    public void 處理器歸屬要報出APIC對到哪個節點()
    {
        var t = Build(ProcAffinity(1, 8));
        var p = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.ProcKey);

        Assert.Equal(FactAvailability.Present, p.Availability);
        Assert.Contains("APIC 8→節點 1", p.Value);
    }

    [Fact]
    public void 有處理器標為停用時要說那是韌體自述()
    {
        var t = Build(ProcAffinity(0, 0, enabled: true), ProcAffinity(0, 1, enabled: false));
        var p = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.ProcKey);

        Assert.Equal(FactAvailability.Unknown, p.Availability);
        Assert.Contains("不代表 OS 不會用它們", p.UnavailableReason);
    }

    [Fact]
    public void 沒有傳統APIC歸屬時標不適用並與x2APIC互相印證()
    {
        var unknown = new byte[] { 99, 4, 0, 0 };
        var t = Build(unknown);
        var p = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.ProcKey);

        Assert.Equal(FactAvailability.NotApplicable, p.Availability);
        Assert.Contains("互相印證", p.UnavailableReason);
    }

    // ── 記憶體歸屬 ──────────────────────────────────────────────────────

    [Fact]
    public void 記憶體歸屬要算出大小並標節點()
    {
        // 0x4000_0000 = 1 GiB
        var t = Build(MemAffinity(2, 0x1_0000_0000, 0x4000_0000));
        var m = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.MemKey);

        Assert.Equal(FactAvailability.Present, m.Availability);
        Assert.Contains("節點 2", m.Value);
        Assert.Contains("1 GiB", m.Value);
    }

    [Fact]
    public void 沒有記憶體歸屬時要說它回答不了關鍵問題()
    {
        var t = Build(ProcAffinity(0, 0));
        var m = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.MemKey);

        Assert.Equal(FactAvailability.Unknown, m.Availability);
        Assert.Contains("回答不了", m.UnavailableReason);
        Assert.Contains("延遲解讀的關鍵", m.UnavailableReason);
    }

    // ── 鄰近域 ──────────────────────────────────────────────────────────

    [Fact]
    public void 單一鄰近域要說跨節點行為無從驗證()
    {
        var t = Build(ProcAffinity(0, 0), MemAffinity(0, 0, 0x1000));
        var d = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.DomainKey);

        Assert.Equal(1.0, d.NumericValue);
        Assert.Contains("無從驗證", d.UnavailableReason);
        Assert.Contains("未驗證不代表可用", d.UnavailableReason);
    }

    [Fact]
    public void 多個鄰近域要全部列出且不帶未驗證的說明()
    {
        var t = Build(ProcAffinity(0, 0), ProcAffinity(1, 1), MemAffinity(0, 0, 0x1000));
        var d = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.DomainKey);

        Assert.Equal(2.0, d.NumericValue);
        Assert.Contains("節點 0", d.Value);
        Assert.Contains("節點 1", d.Value);
        Assert.Null(d.UnavailableReason);
    }

    // ── 一般啟動器 ──────────────────────────────────────────────────────

    [Fact]
    public void 有一般啟動器時才產出那把鍵()
    {
        var withGeneric = Build(ProcAffinity(0, 0), GenericAffinity(1, 0x10000));
        var facts = AcpiSratFactsService.Collect(new FakeSource(true, withGeneric), At);
        var g = facts.Single(f => f.Key == AcpiSratFactsService.GenericKey);

        Assert.Contains("控制代碼 65536", g.Value);
        Assert.Contains("超出處理器與記憶體", g.UnavailableReason);

        // 沒有一般啟動器時，那把鍵不該出現——它是條件產出，不是固定欄位。
        var without = Build(ProcAffinity(0, 0));
        Assert.DoesNotContain(AcpiSratFactsService.GenericKey,
            AcpiSratFactsService.Collect(new FakeSource(true, without), At).Select(f => f.Key));
    }

    // ── 未收錄 ──────────────────────────────────────────────────────────

    [Fact]
    public void 未收錄型別要標Unknown()
    {
        var t = Build(ProcAffinity(0, 0), new byte[] { 99, 4, 0, 0 });
        var u = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiSratFactsService.UnrecordedKey);

        Assert.Equal(FactAvailability.Unknown, u.Availability);
        Assert.Contains("型別 99", u.Value);
    }

    // ── 覆蓋申報與目錄 ──────────────────────────────────────────────────

    [Fact]
    public void SRAT要登記進解碼器覆蓋申報()
    {
        Assert.True(AcpiDeep.HasDedicatedDecoder("SRAT"));
        Assert.True(AcpiDeep.HasDedicatedDecoder("srat"));
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var t = Build(ProcAffinity(0, 0), GenericAffinity(1, 0x1000));
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var unknown = AcpiSratFactsService.Collect(new FakeSource(true, t), At)
            .Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "SRAT 事實產出了目錄裡沒有的鍵：\n" + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
