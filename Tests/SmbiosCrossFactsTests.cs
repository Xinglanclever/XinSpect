using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS 與 WMI 對照的守門（ULTIMATE-1000 階段二 P2-033）。
///
/// <para>
/// <b>這一組的核心是三分法，不是二分法：</b>一致／不一致／<b>無從對照</b>。
/// 把第三種算進第二種會製造假的落差——而那是這類對照最常見的錯誤，
/// 因為「一邊沒有值」看起來就像「兩邊不一樣」。
/// </para>
/// </summary>
public class SmbiosCrossFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SmbiosStruct Struct(byte type, byte[] data, ushort handle = 0x0100,
        params string[] strings)
        => new(type, handle, data, strings);

    private static SmbiosStruct Type0(string vendor, string version)
    {
        var d = new byte[0x12];
        d[0x04] = 1; d[0x05] = 2;
        return Struct(0, d, 0x0100, vendor, version);
    }

    private static SmbiosStruct Type1(string vendor, string product)
    {
        var d = new byte[0x1B];
        d[0x04] = 1; d[0x05] = 2;
        return Struct(1, d, 0x0101, vendor, product);
    }

    private static SmbiosStruct Type2(string vendor, string product)
    {
        var d = new byte[0x08];
        d[0x04] = 1; d[0x05] = 2;
        return Struct(2, d, 0x0102, vendor, product);
    }

    /// <summary>
    /// Type 4：執行緒數是 u16@0x25，所以長度至少要 0x27。
    ///
    /// <para>
    /// 第一版寫 <c>new byte[0x1A]</c>（我心裡想的是「Type 4 的最小長度」），
    /// 卻往 <c>0x25</c> 寫——**起點 < 長度 ≠ 起點 ＋ 寬度 ≤ 長度**。
    /// 這是我第四次犯這個錯（Type0 的 &gt; 0x14、Boot 的 &gt; 0x0A、
    /// Type19 的 0x0F、這裡），所以它現在由
    /// <see cref="TestArrayBoundsTests"/> 靜態檢查。
    /// </para>
    /// </summary>
    private static SmbiosStruct Type4(ushort threads, ushort handle = 0x0400)
    {
        var d = new byte[0x27];
        BitConverter.GetBytes(threads).CopyTo(d, 0x25);
        return Struct(4, d, handle);
    }

    private static SmbiosCrossFactsService.OsView Os(
        string? bv = null, string? bver = null, string? sv = null, string? sp = null,
        string? brv = null, string? brp = null, int? cpus = null)
        => new(bv, bver, sv, sp, brv, brp, cpus);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有結構時要標不支援()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(), () => []);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
    }

    [Fact]
    public void 讀取失敗時要標讀取失敗()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(),
            () => throw new InvalidOperationException("注入"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    // ── 一致 ────────────────────────────────────────────────────────────

    [Fact]
    public void 兩邊相同時要說一致()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(bv: "AMI", bver: "2.85"),
            () => [Type0("AMI", "2.85")]);
        var b = facts.Single(f => f.Key == SmbiosCrossFactsService.BiosKey);

        Assert.Equal(FactAvailability.Present, b.Availability);
        Assert.Contains("兩邊一致", b.Value);
        Assert.Null(b.UnavailableReason);
    }

    [Fact]
    public void 大小寫不同要算一致()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(bv: "ami", bver: "2.85"), () => [Type0("AMI", "2.85")]);
        var b = facts.Single(f => f.Key == SmbiosCrossFactsService.BiosKey);

        Assert.Equal(FactAvailability.Present, b.Availability);
    }

    // ── 不一致 ──────────────────────────────────────────────────────────

    [Fact]
    public void 兩邊不同時要並列而且不說是哪一個錯了()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(sv: "Dell Inc.", sp: "OptiPlex 7090"),
            () => [Type1("Dell Inc.", "Precision 7920")]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.SystemKey);

        Assert.Equal(FactAvailability.Unknown, s.Availability);
        Assert.Contains("SMBIOS：Dell Inc. Precision 7920", s.Value);
        Assert.Contains("WMI：Dell Inc. OptiPlex 7090", s.Value);
        Assert.Contains("不是「哪一個錯了」", s.UnavailableReason);
        Assert.Contains("版本與時機本來就可能不同", s.UnavailableReason);
    }

    // ── 無從對照（三分法的第三種）────────────────────────────────────────

    [Fact]
    public void 只有SMBIOS有值時要說無從對照而不是不一致()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(),
            () => [Type1("Dell Inc.", "Precision 7920")]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.SystemKey);

        Assert.Equal(FactAvailability.Unknown, s.Availability);
        Assert.Contains("WMI 無值", s.Value);
        Assert.Contains("無從對照」而不是「不一致", s.UnavailableReason);
        Assert.Contains("製造假的落差", s.UnavailableReason);
    }

    [Fact]
    public void 只有WMI有值時也要說無從對照()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(sv: "Dell Inc.", sp: "Precision 7920"),
            () => [Struct(3, new byte[0x09])]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.SystemKey);

        Assert.Contains("SMBIOS 無值", s.Value);
        Assert.Contains("無從對照", s.UnavailableReason);
    }

    [Fact]
    public void 兩邊都沒有值時要標不適用()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(),
            () => [Struct(3, new byte[0x09])]);
        var b = facts.Single(f => f.Key == SmbiosCrossFactsService.BiosKey);

        Assert.Equal(FactAvailability.NotApplicable, b.Availability);
        Assert.Contains("兩邊都沒有值", b.Value);
    }

    // ── 處理器數：兩個不同的量 ──────────────────────────────────────────

    [Fact]
    public void 處理器數要說出那是兩個不同的量()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(cpus: 16),
            () => [Type4(16)]);
        var c = facts.Single(f => f.Key == SmbiosCrossFactsService.CpuCountKey);

        Assert.Contains("SMBIOS 宣告 1 顆實體處理器", c.Value);
        Assert.Contains("合計 16 執行緒", c.Value);
        Assert.Contains("WMI 回報 16 個邏輯處理器", c.Value);
        Assert.Contains("不是同一個量", c.UnavailableReason);
    }

    [Fact]
    public void OS看到的比SMBIOS宣告的多時要指出落差()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(cpus: 32),
            () => [Type4(threads: 16)]);
        var c = facts.Single(f => f.Key == SmbiosCrossFactsService.CpuCountKey);

        Assert.Contains("具體的落差", c.UnavailableReason);
        Assert.Contains("不判斷誰對", c.UnavailableReason);
    }

    [Fact]
    public void 只有一邊有處理器數時要說無從對照()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(),
            () => [Type4(16)]);
        var c = facts.Single(f => f.Key == SmbiosCrossFactsService.CpuCountKey);

        Assert.Contains("無從對照", c.UnavailableReason);
    }

    // ── 彙總：不給一致率 ────────────────────────────────────────────────

    [Fact]
    public void 彙總要說出可對照與無從對照的組數()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(bv: "AMI", bver: "2.85", cpus: 16),
            () => [Type0("AMI", "2.85"), Type4(16)]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.ComparableKey);

        Assert.Contains("可對照", s.Value);
        Assert.Contains("無從對照", s.Value);
        Assert.Equal(FactAvailability.Present, s.Availability);
    }

    [Fact]
    public void 彙總要說出它不給一致率()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(bv: "AMI", bver: "2.85", sv: "X", sp: "Y"),
            () => [Type0("AMI", "2.85"), Type1("X", "Y")]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.ComparableKey);

        Assert.Contains("不給一致率", s.UnavailableReason);
        Assert.Contains("會被讀成評分", s.UnavailableReason);
    }

    [Fact]
    public void 全部一致時要提醒那不代表兩個獨立來源()
    {
        var facts = SmbiosCrossFactsService.Collect(At,
            Os(bv: "AMI", bver: "2.85", sv: "X", sp: "Y", brv: "Z", brp: "W"),
            () => [Type0("AMI", "2.85"), Type1("X", "Y"), Type2("Z", "W")]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.ComparableKey);

        Assert.Contains("不代表兩邊是同一個來源", s.UnavailableReason);
        Assert.Contains("WMI 直接轉述 SMBIOS", s.UnavailableReason);
    }

    [Fact]
    public void 沒有可對照的組時要標不適用()
    {
        var facts = SmbiosCrossFactsService.Collect(At, Os(),
            () => [Struct(3, new byte[0x09])]);
        var s = facts.Single(f => f.Key == SmbiosCrossFactsService.ComparableKey);

        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Equal(0.0, s.NumericValue);
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = SmbiosCrossFactsService.Collect(At, Os(bv: "A"),
            () => [Type0("A", "1")])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var failed = SmbiosCrossFactsService.Collect(At, Os(), () => [])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, failed);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = SmbiosCrossFactsService.Collect(At, Os(bv: "A", bver: "1"),
            () => [Type0("A", "1")]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "對照事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
