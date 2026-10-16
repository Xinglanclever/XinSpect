using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// DMAR 事實輸出的守門（ULTIMATE-1000 階段二 P2-010）。
/// 解碼器的逐欄位測試在 <see cref="AcpiDmarTests"/>；這裡測事實層的三態與措辭。
/// </summary>
public class AcpiDmarFactsTests
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
        var body = new List<byte> { 0x2A, 0x01 };
        body.AddRange(new byte[10]);
        foreach (var s in structures) body.AddRange(s);

        var table = new byte[AcpiTable.HeaderLength + body.Count];
        "DMAR"u8.CopyTo(table.AsSpan());
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        "TESTID"u8.CopyTo(table.AsSpan(10));
        body.CopyTo(table, AcpiTable.HeaderLength);
        return table;
    }

    private static byte[] Drhd(byte flags, ushort segment, ulong registerBase,
        params (ushort Start, ushort End)[] scopes)
    {
        var b = new byte[16 + scopes.Length * 4 + 4];
        BitConverter.GetBytes((ushort)0).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)b.Length).CopyTo(b, 2);
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
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, p);
        return b;
    }

    private static byte[] Rmrr(ushort segment, ulong baseAddress, ulong limit,
        params (ushort Start, ushort End)[] scopes)
    {
        var b = new byte[24 + scopes.Length * 4 + 4];
        BitConverter.GetBytes((ushort)1).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)b.Length).CopyTo(b, 2);
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

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 來源不可用要標不支援()
    {
        var f = Assert.Single(AcpiDmarFactsService.Collect(new FakeSource(false), At),
            x => x.Key == AcpiDmarFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    [Fact]
    public void 沒有DMAR表時要說沒有這張表不代表沒有IOMMU()
    {
        var f = Assert.Single(AcpiDmarFactsService.Collect(new FakeSource(true), At),
            x => x.Key == AcpiDmarFactsService.SummaryKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不代表沒有 IOMMU", f.UnavailableReason);
        Assert.Contains("只代表韌體沒有用它描述範圍", f.UnavailableReason);
    }

    [Fact]
    public void 表短於表頭時標讀取失敗()
    {
        var b = Build(Drhd(1, 0, 0x1000));
        Array.Resize(ref b, AcpiTable.HeaderLength + 1);

        var f = Assert.Single(AcpiDmarFactsService.Collect(new FakeSource(true, b), At),
            x => x.Key == AcpiDmarFactsService.SummaryKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 彙總 ────────────────────────────────────────────────────────────

    [Fact]
    public void 彙總要報出各項計數與三個旗標()
    {
        var t = Build(Drhd(1, 0, 0xFED90000), Rmrr(0, 0x1000, 0x1FFF));
        var facts = AcpiDmarFactsService.Collect(new FakeSource(true, t), At);
        var s = facts.Single(f => f.Key == AcpiDmarFactsService.SummaryKey);

        Assert.Equal(FactAvailability.Present, s.Availability);
        Assert.Contains("主機位址寬度 42 位元", s.Value);
        Assert.Contains("1 個硬體單位", s.Value);
        Assert.Contains("1 個保留記憶體區域", s.Value);
        Assert.Contains("中斷重映射 已宣告", s.Value);
    }

    [Fact]
    public void 沒有任何單位涵蓋全機時要說出那些裝置不在轉譯範圍內()
    {
        var t = Build(Drhd(flags: 0, segment: 0, registerBase: 0x1000));
        var facts = AcpiDmarFactsService.Collect(new FakeSource(true, t), At);
        var s = facts.Single(f => f.Key == AcpiDmarFactsService.SummaryKey);

        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("不在這份表的轉譯範圍內", s.Value);
    }

    [Fact]
    public void 沒有硬體單位時要說那只代表沒宣告轉譯範圍()
    {
        var t = Build(Rmrr(0, 0x1000, 0x1FFF));
        var facts = AcpiDmarFactsService.Collect(new FakeSource(true, t), At);

        var s = facts.Single(f => f.Key == AcpiDmarFactsService.SummaryKey);
        Assert.Contains("沒有宣告轉譯範圍", s.Value);

        var d = facts.Single(f => f.Key == AcpiDmarFactsService.DrhdKey);
        Assert.Equal(FactAvailability.Unknown, d.Availability);
        Assert.Contains("與「IOMMU 沒有運作」是兩件事", d.UnavailableReason);
    }

    // ── RMRR：例外清單 ──────────────────────────────────────────────────

    [Fact]
    public void 沒有保留區域時要說那不代表沒有其他路徑繞過轉譯()
    {
        var t = Build(Drhd(1, 0, 0x1000));
        var facts = AcpiDmarFactsService.Collect(new FakeSource(true, t), At);
        var r = facts.Single(f => f.Key == AcpiDmarFactsService.RmrrKey);

        Assert.Equal(0.0, r.NumericValue);
        Assert.Contains("不代表沒有其他路徑繞過轉譯", r.Value);
    }

    [Fact]
    public void 有保留區域時要算出洞多大並說那是設計上留的()
    {
        // 0x8F000000 ~ 0x8FFFFFFF = 0x01000000 bytes = 16 MB
        // （第一版在註解裡寫成 256 MB，而期望值也照著寫錯的那個數字填——
        //   守門抓到了算錯的人，不是算錯的程式。）
        var t = Build(Drhd(1, 0, 0x1000),
            Rmrr(0, 0x8F00_0000, 0x8FFF_FFFF, (0x0014, 0x0014)));
        var facts = AcpiDmarFactsService.Collect(new FakeSource(true, t), At);
        var r = facts.Single(f => f.Key == AcpiDmarFactsService.RmrrKey);

        Assert.Equal(FactAvailability.Unknown, r.Availability);
        Assert.Contains("16.0 MB", r.Value);
        Assert.Contains("1 段裝置", r.Value);
        Assert.Contains("設計上留的洞", r.UnavailableReason);
        Assert.Contains("這一項只陳述韌體宣告了例外", r.UnavailableReason);
    }

    [Fact]
    public void 保留區域的裝置範圍段不得把結束標記算成裝置()
    {
        var t = Build(Drhd(1, 0, 0x1000), Rmrr(0, 0x1000, 0x1FFF, (0x0014, 0x0014)));
        var r = AcpiDmarFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiDmarFactsService.RmrrKey);

        Assert.Contains("1 段裝置", r.Value);
        Assert.DoesNotContain("2 段裝置", r.Value);
    }

    // ── 覆蓋 ────────────────────────────────────────────────────────────

    [Fact]
    public void 有單位涵蓋全機時覆蓋要標Present()
    {
        var t = Build(Drhd(1, 0, 0x1000, (0x0002, 0x0002)));
        var c = AcpiDmarFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiDmarFactsService.CoverageKey);

        Assert.Equal(FactAvailability.Present, c.Availability);
        Assert.Null(c.UnavailableReason);
    }

    [Fact]
    public void 沒有單位涵蓋全機時要說描述的是部分覆蓋()
    {
        var t = Build(Drhd(0, 0, 0x1000, (0x0002, 0x0002), (0x0014, 0x0014)));
        var c = AcpiDmarFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiDmarFactsService.CoverageKey);

        Assert.Equal(FactAvailability.Unknown, c.Availability);
        Assert.Contains("部分覆蓋", c.UnavailableReason);
        Assert.Contains("不在本表的陳述範圍內", c.UnavailableReason);
    }

    // ── 未收錄型別 ──────────────────────────────────────────────────────

    [Fact]
    public void 未收錄型別要標Unknown()
    {
        var unknown = new byte[] { 99, 0, 4, 0 };
        var t = Build(Drhd(1, 0, 0x1000), unknown);
        var u = AcpiDmarFactsService.Collect(new FakeSource(true, t), At)
            .Single(f => f.Key == AcpiDmarFactsService.UnrecordedKey);

        Assert.Equal(FactAvailability.Unknown, u.Availability);
        Assert.Contains("型別 99", u.Value);
    }

    // ── 覆蓋申報與目錄 ──────────────────────────────────────────────────

    [Fact]
    public void DMAR要登記進解碼器覆蓋申報()
    {
        Assert.True(AcpiDeep.HasDedicatedDecoder("DMAR"));
        Assert.True(AcpiDeep.HasDedicatedDecoder("dmar"));
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var t = Build(Drhd(1, 0, 0x1000));
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var unknown = AcpiDmarFactsService.Collect(new FakeSource(true, t), At)
            .Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "DMAR 事實產出了目錄裡沒有的鍵：\n" + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
