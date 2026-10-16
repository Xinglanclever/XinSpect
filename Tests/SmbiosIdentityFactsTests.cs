using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// SMBIOS Type 0–3／19／32 事實的守門（ULTIMATE-1000 階段二 P2-019／P2-024／P2-025）。
/// </summary>
public class SmbiosIdentityFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static SmbiosStruct Struct(byte type, byte[] data, params string[] strings)
        => new(type, 0x0100, data, strings);

    private static SmbiosStruct Type0(string vendor, string version, string date,
        byte romSize, ulong characteristics)
    {
        // 0x18 而非 0x14：ROM 大小走 0xFF 分支時讀 DwordAt(0x14)，
        // 那需要索引 0x14..0x17 都有效＝長度至少 0x18。
        var d = new byte[0x18];
        d[0x04] = 1; d[0x05] = 2; d[0x08] = 3;
        d[0x09] = romSize;
        d[0x11] = (byte)(characteristics & 0xFF);
        d[0x12] = (byte)(characteristics >> 8);
        return Struct(0, d, vendor, version, date);
    }

    private static SmbiosStruct Type1(string vendor, string product, string serial, byte[]? uuid = null)
    {
        var d = new byte[0x1B];
        d[0x04] = 1; d[0x05] = 2; d[0x07] = 3;
        if (uuid is not null) Array.Copy(uuid, 0, d, 0x08, 16);
        return Struct(1, d, vendor, product, serial);
    }

    private static SmbiosStruct Type2(string vendor, string product) 
    {
        var d = new byte[0x08];
        d[0x04] = 1; d[0x05] = 2;
        return Struct(2, d, vendor, product);
    }

    private static SmbiosStruct Type3(byte chassisType, bool locked, string vendor)
    {
        var d = new byte[0x09];
        d[0x04] = 1;
        d[0x05] = (byte)(chassisType | (locked ? 0x80 : 0));
        return Struct(3, d, vendor);
    }

    private static SmbiosStruct Type32(byte status)
    {
        // 0x0E 而非 0x0B：POST 結果讀 0x08..0x0D。
        var d = new byte[0x0E];
        d[0x06] = status;
        return Struct(32, d);
    }

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有結構時要標不支援()
    {
        var facts = SmbiosIdentityFactsService.Collect(At, () => []);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
    }

    [Fact]
    public void 讀取失敗時要標讀取失敗()
    {
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => throw new InvalidOperationException("注入的失敗"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.All(facts, f => Assert.Contains("注入的失敗", f.UnavailableReason));
    }

    /// <summary>
    /// <b>失敗路徑與成功路徑必須給出同一組鍵。</b>
    ///
    /// <para>
    /// 這一條是被自己咬出來的：<c>Uuid</c> 我寫好了、
    /// 編譯過了、<b>但 Collect 沒有回傳它們</b>——而編譯器不會抱怨，
    /// 因為它們是合法的 private static 方法。表現在報告上就是「那一項不見了」。
    /// </para>
    /// </summary>
    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = SmbiosIdentityFactsService.Collect(At, () => [
            Type0("V", "1.0", "01/01/2026", 0x0F, 0),
            Type1("V", "P", "S", new byte[16]),
            Type2("V", "P"),
            Type3(3, false, "V"),
            Type32(0),
        ]).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        var failed = SmbiosIdentityFactsService.Collect(At, () => [])
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, failed);
    }

    [Fact]
    public void 成功路徑不得有重複的鍵()
    {
        var keys = SmbiosIdentityFactsService.Collect(At, () => [
            Type0("V", "1.0", "01/01/2026", 0x0F, 0),
            Type1("V", "P", "S", new byte[16]),
            Type2("V", "P"),
            Type3(3, false, "V"),
            Type32(0),
        ]).Select(f => f.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Type 0 ──────────────────────────────────────────────────────────

    [Fact]
    public void BIOS資訊要含廠商版本與發行日()
    {
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => [Type0("American Megatrends Inc.", "2.85", "03/15/2026", 0x0F, 0)]);
        var b = facts.Single(f => f.Key == SmbiosIdentityFactsService.BiosKey);

        Assert.Contains("廠商 American Megatrends Inc.", b.Value);
        Assert.Contains("版本 2.85", b.Value);
        Assert.Contains("發行日 03/15/2026", b.Value);
    }

    [Fact]
    public void BIOS特性旗標要解出UEFI與可升級()
    {
        // bit3 可升級、bit11 支援 UEFI、bit12 為 UEFI 平台 → 0x1808
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => [Type0("V", "1.0", "d", 0x0F, 0x1808)]);
        var b = facts.Single(f => f.Key == SmbiosIdentityFactsService.BiosKey);

        Assert.Contains("支援升級", b.Value);
        Assert.Contains("支援 UEFI", b.Value);
        Assert.Contains("為 UEFI 平台", b.Value);
    }

    [Fact]
    public void BIOS缺席時要說少一個來源()
    {
        var facts = SmbiosIdentityFactsService.Collect(At, () => [Type1("V", "P", "S")]);
        var b = facts.Single(f => f.Key == SmbiosIdentityFactsService.BiosKey);

        Assert.Equal(FactAvailability.NotApplicable, b.Availability);
        Assert.Contains("只能靠 WMI 那一個來源", b.UnavailableReason);
    }

    // ── Type 1 與 UUID ──────────────────────────────────────────────────

    [Fact]
    public void 系統資訊要標為敏感()
    {
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => [Type1("Dell Inc.", "Precision 7920", "ABC123")]);
        var s = facts.Single(f => f.Key == SmbiosIdentityFactsService.SystemKey);

        Assert.True(s.Sensitive);
        Assert.Contains("Dell Inc.", s.Value);
        Assert.Contains("ABC123", s.Value);
    }

    [Fact]
    public void UUID要依little_endian佈局重排前三欄()
    {
        // SMBIOS 存的是 little-endian：bytes[0..3] 要反過來才是顯示的順序。
        byte[] stored = [0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66,
                         0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        var facts = SmbiosIdentityFactsService.Collect(At, () => [Type1("V", "P", "S", stored)]);
        var u = facts.Single(f => f.Key == SmbiosIdentityFactsService.UuidKey);

        Assert.Equal("00112233-4455-6677-8899-AABBCCDDEEFF", u.Value);
    }

    [Fact]
    public void UUID全為零要標未設定而不是一個UUID()
    {
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => [Type1("V", "P", "S", new byte[16])]);
        var u = facts.Single(f => f.Key == SmbiosIdentityFactsService.UuidKey);

        Assert.Equal(FactAvailability.NotApplicable, u.Availability);
        Assert.Contains("全為 0（未設定）", u.Value);
        Assert.Contains("誤判為同一台", u.UnavailableReason);
    }

    [Fact]
    public void UUID全為FF也要標未設定()
    {
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => [Type1("V", "P", "S", Enumerable.Repeat((byte)0xFF, 16).ToArray())]);
        var u = facts.Single(f => f.Key == SmbiosIdentityFactsService.UuidKey);

        Assert.Equal(FactAvailability.NotApplicable, u.Availability);
        Assert.Contains("全為 0xFF（未設定）", u.Value);
    }

    [Fact]
    public void UUID要標為敏感()
    {
        byte[] stored = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var facts = SmbiosIdentityFactsService.Collect(At, () => [Type1("V", "P", "S", stored)]);
        var u = facts.Single(f => f.Key == SmbiosIdentityFactsService.UuidKey);

        Assert.True(u.Sensitive);
        Assert.Contains("能唯一指認一台機器", u.UnavailableReason);
    }

    // ── Type 2 與 3 ─────────────────────────────────────────────────────

    [Fact]
    public void 主機板資訊要含廠商與產品名()
    {
        var facts = SmbiosIdentityFactsService.Collect(At,
            () => [Type2("ASUSTeK", "ROG STRIX X670E")]);
        var b = facts.Single(f => f.Key == SmbiosIdentityFactsService.BoardKey);

        Assert.Contains("ASUSTeK", b.Value);
        Assert.Contains("ROG STRIX X670E", b.Value);
    }

    [Fact]
    public void 機箱類型要去掉鎖的位元()
    {
        // 0x83 = 型別 3（桌面機）＋ bit7 鎖存在。不去掉 bit7 會變成型別 131。
        var facts = SmbiosIdentityFactsService.Collect(At, () => [Type3(0x03, locked: true, "V")]);
        var c = facts.Single(f => f.Key == SmbiosIdentityFactsService.ChassisKey);

        Assert.Contains("機箱鎖存在", c.Value);
        Assert.DoesNotContain("131", c.Value);
        Assert.Contains("不是「現在鎖著」", c.UnavailableReason);
    }

    // ── Type 19：未開工（P2-024 退回）────────────────────────────────────

    // 欄位佈局沒有可靠依據，所以那兩把鍵不產出，也沒有測試。
    // 一個位址位移不確定的解碼器會給出看起來合理的錯誤位址——那比沒有更糟。

    // ── Type 32 ─────────────────────────────────────────────────────────

    [Fact]
    public void 上次開機狀態為零要標正常並說那是歷史()
    {
        var facts = SmbiosIdentityFactsService.Collect(At, () => [Type32(0)]);
        var b = facts.Single(f => f.Key == SmbiosIdentityFactsService.BootStatusKey);

        Assert.Equal(FactAvailability.Present, b.Availability);
        Assert.Equal("正常", b.Value);
        Assert.Contains("不是現在的健康狀態", b.UnavailableReason);
    }

    [Fact]
    public void 上次開機狀態非零要說它可能是很久以前的事()
    {
        var facts = SmbiosIdentityFactsService.Collect(At, () => [Type32(5)]);
        var b = facts.Single(f => f.Key == SmbiosIdentityFactsService.BootStatusKey);

        Assert.Equal(FactAvailability.Unknown, b.Availability);
        Assert.Contains("硬體失敗", b.Value);
        Assert.Contains("可能是很久以前的事", b.UnavailableReason);
    }

    // ── 目錄 ────────────────────────────────────────────────────────────

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = SmbiosIdentityFactsService.Collect(At, () => [
            Type0("V", "1", "d", 0x0F, 0),
            Type1("V", "P", "S", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }),
            Type2("V", "P"), Type3(3, false, "V"), Type32(0),
        ]);
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "Type 0–3／19／32 產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
