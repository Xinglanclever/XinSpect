using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 44 與結構政策事實的守門（ULTIMATE-1000 階段二 P2-029 後半／P2-030／P2-031）。
///
/// <para>
/// <b>這一組的核心是「結構長度」的兩種方向：</b>
/// 比規範<b>長</b>是常態（韌體用了較新的欄位），比規範<b>短</b>才嚴重
/// ——短代表欄位被截掉，而解碼器對那些位移的讀取會落到字串表或表尾，
/// <b>而那些值看起來仍然像值</b>。
/// </para>
/// </summary>
public class SmbiosStructureFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SmbiosStruct Struct(byte type, byte[] data, ushort handle = 0x0100,
        params string[] strings)
        => new(type, handle, data, strings);

    /// <summary>Type 4：插槽字串@0x04，長度 0x1A。</summary>
    private static SmbiosStruct Type4(string socket, ushort handle = 0x0400)
    {
        var d = new byte[0x1A];
        d[0x04] = 1;
        return Struct(4, d, handle, socket);
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有結構時要標不支援()
    {
        var facts = SmbiosStructureFactsService.Collect(At, () => []);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
    }

    [Fact]
    public void 讀取失敗時要標讀取失敗()
    {
        var facts = SmbiosStructureFactsService.Collect(At,
            () => throw new InvalidOperationException("注入"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    // ── Type 44：未開工（P2-029 的後半退回）────────────────────────────

    // 欄位佈局沒有可靠依據，所以那把鍵不產出，也沒有測試。

    // ── P2-030：尾巴欄位 ────────────────────────────────────────────────

    [Fact]
    public void 沒有尾巴欄位時要標Present()
    {
        // Type 0 的規範長度是 0x12，剛好給 0x12。
        var t0 = Struct(0, new byte[0x12]);
        var facts = SmbiosStructureFactsService.Collect(At, () => [t0]);
        var t = facts.Single(f => f.Key == SmbiosStructureFactsService.TrailingKey);

        Assert.Equal(FactAvailability.Present, t.Availability);
        Assert.Equal(0.0, t.NumericValue);
    }

    [Fact]
    public void 比規範長要標Unknown並說出解碼器只讀到哪裡()
    {
        // Type 1 規範長度 0x08，給 0x1B（UUID）。
        var t1 = Struct(1, new byte[0x1B]);
        var facts = SmbiosStructureFactsService.Collect(At, () => [t1]);
        var t = facts.Single(f => f.Key == SmbiosStructureFactsService.TrailingKey);

        Assert.Equal(FactAvailability.Unknown, t.Availability);
        Assert.Equal(1.0, t.NumericValue);
        Assert.Contains("Type 1", t.Value);
        Assert.Contains("沒讀到」會被誤當成「沒有那個欄位", t.UnavailableReason);
    }

    [Fact]
    public void 比規範短要標讀取失敗並說那比多出欄位嚴重()
    {
        // Type 9 規範長度 0x0C，只給 0x08。
        var t9 = Struct(9, new byte[0x08]);
        var facts = SmbiosStructureFactsService.Collect(At, () => [t9]);
        var t = facts.Single(f => f.Key == SmbiosStructureFactsService.TrailingKey);

        Assert.Equal(FactAvailability.ReadError, t.Availability);
        Assert.Contains("**短於規範長度**", t.UnavailableReason);
        Assert.Contains("那些值看起來仍然像值", t.UnavailableReason);
    }

    [Fact]
    public void 沒有已知規範長度的型別時要標不適用()
    {
        // Type 99 不在 SpecLength 裡。
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(99, new byte[0x10])]);
        var t = facts.Single(f => f.Key == SmbiosStructureFactsService.TrailingKey);

        Assert.Equal(FactAvailability.NotApplicable, t.Availability);
        Assert.Contains("憑印象填規範長度會製造假的超出", t.UnavailableReason);
    }

    // ── P2-031：邊界 ────────────────────────────────────────────────────

    [Fact]
    public void 邊界一致時要標Present()
    {
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(0, new byte[0x12], 0x0100), Struct(1, new byte[0x1B], 0x0101)]);
        var b = facts.Single(f => f.Key == SmbiosStructureFactsService.BoundsKey);

        Assert.Equal(FactAvailability.Present, b.Availability);
        Assert.Null(b.UnavailableReason);
    }

    [Fact]
    public void 控制代碼重複要標讀取失敗並說關聯可能指錯()
    {
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(0, new byte[0x12], 0x0100), Struct(3, new byte[0x09], 0x0100)]);
        var b = facts.Single(f => f.Key == SmbiosStructureFactsService.BoundsKey);

        Assert.Equal(FactAvailability.ReadError, b.Availability);
        Assert.Contains("控制代碼重複", b.Value);
        Assert.Contains("指到錯的結構", b.UnavailableReason);
    }

    [Fact]
    public void 單一實例型別出現多次要指出來()
    {
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(0, new byte[0x12], 0x0100), Struct(0, new byte[0x12], 0x0101)]);
        var b = facts.Single(f => f.Key == SmbiosStructureFactsService.BoundsKey);

        Assert.Equal(FactAvailability.ReadError, b.Availability);
        Assert.Contains("單一實例型別出現多次", b.Value);
        Assert.Contains("Type 0", b.Value);
    }

    [Fact]
    public void 結構短於四要指出來()
    {
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(0, new byte[0x12], 0x0100), Struct(9, new byte[3], 0x0101)]);
        var b = facts.Single(f => f.Key == SmbiosStructureFactsService.BoundsKey);

        Assert.Equal(FactAvailability.ReadError, b.Availability);
        Assert.Contains("短於最小固定長度 4 bytes", b.Value);
    }

    [Fact]
    public void 邊界問題要說不代表值錯了()
    {
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(0, new byte[0x12], 0x0100), Struct(0, new byte[0x12], 0x0100)]);
        var b = facts.Single(f => f.Key == SmbiosStructureFactsService.BoundsKey);

        Assert.Contains("不代表值錯了", b.UnavailableReason);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = SmbiosStructureFactsService.Collect(At, () => [Struct(0, new byte[0x12])])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var failed = SmbiosStructureFactsService.Collect(At, () => [])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, failed);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = SmbiosStructureFactsService.Collect(At,
            () => [Struct(0, new byte[0x12]), Type4("CPU0")]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "結構政策事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
