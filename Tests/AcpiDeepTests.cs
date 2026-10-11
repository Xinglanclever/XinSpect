using Xunit;

namespace XinSpect.Tests;

/// <summary>Vol2 AC 域（ACPI 表層深化）解碼器與服務事實的測試：合成表驅動，不碰韌體。</summary>
public sealed class AcpiDeepTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    // ── AC-006 DSDT/SSDT 字串掃描 ─────────────────────────────────────────

    [Fact]
    public void AML字串掃描_只取可列印區段且不把表頭算進去()
    {
        var t = BuildTable("DSDT", 2, "OEMAAA", "DSDT0001", 80, body =>
        {
            System.Text.Encoding.ASCII.GetBytes("\\_SB.PCI0").CopyTo(body, 4);
            System.Text.Encoding.ASCII.GetBytes("abc").CopyTo(body, 30); // 3 字，低於門檻
        });

        var strings = AcpiDeep.AmlStrings(t);

        Assert.Contains("\\_SB.PCI0", strings);
        Assert.DoesNotContain("abc", strings);
        Assert.DoesNotContain(strings, s => s.Contains("DSDT")); // 表頭在 offset < 36，不應被掃進來
    }

    [Fact]
    public void AML字串掃描_門檻可調到單字元()
    {
        var t = BuildTable("SSDT", 1, "OEMAAA", "SSDT0001", 48,
            body => System.Text.Encoding.ASCII.GetBytes("ab").CopyTo(body, 0));
        Assert.Contains("ab", AcpiDeep.AmlStrings(t, minLength: 1));
        Assert.Empty(AcpiDeep.AmlStrings(t, minLength: 4));
    }

    // ── AC-008 第三方 SSDT 對照 ──────────────────────────────────────────

    [Fact]
    public void SSDT來源對照_與主DSDT廠商不同者才列入()
    {
        var headers = new List<AcpiTableHeader>
        {
            HeaderOf("DSDT", "OEMAAA", "DSDT0001"),
            HeaderOf("SSDT", "OEMAAA", "SSDT0001"),
            HeaderOf("SSDT", "VENDORB", "SSDT0002"),
        };

        var report = AcpiDeep.SsdtSources(headers);

        Assert.Equal("OEMAAA", report.PlatformOemId);
        Assert.Equal(["VENDORB/SSDT0002"], report.ThirdPartyOemIds);
    }

    [Fact]
    public void SSDT來源對照_無DSDT時基準為空且所有SSDT都算非主廠商()
    {
        var report = AcpiDeep.SsdtSources([HeaderOf("SSDT", "VENDORB", "SSDT0001")]);
        Assert.Equal("", report.PlatformOemId);
        Assert.Single(report.ThirdPartyOemIds);
    }

    // ── AC-009 NFIT ──────────────────────────────────────────────────────

    [Fact]
    public void NFIT子結構清單_依長度逐個走完()
    {
        var t = BuildNfit(spaIndex: 0, spaAddress: 0x1000, spaLength: 0x2000, deviceHandle: 7, regionId: 3, ways: 2, regionSize: 0x1000);

        var structs = AcpiDeep.NfitStructures(t);

        Assert.Equal(2, structs.Count);
        Assert.Equal(0, structs[0].Type);
        Assert.Equal(60, structs[0].Length);
        Assert.Equal(1, structs[1].Type);
        Assert.Equal(48, structs[1].Length);
    }

    [Fact]
    public void NFIT_長度欄不可信即停不硬讀()
    {
        var t = BuildTable("NFIT", 1, "OEMAAA", "NFIT0001", 48, body =>
        {
            BitConverter.GetBytes((ushort)0).CopyTo(body, 0);
            BitConverter.GetBytes((ushort)2).CopyTo(body, 2); // 長度 2 < 標頭 4，不可信
        });

        Assert.Empty(AcpiDeep.NfitStructures(t));
        Assert.Empty(AcpiDeep.NfitSpaRanges(t));
        Assert.Empty(AcpiDeep.NfitMemoryDevices(t));
    }

    [Fact]
    public void NFIT_SPA範圍_解出位址與長度()
    {
        var spa = AcpiDeep.NfitSpaRanges(BuildNfit(0, 0x1_0000_0000, 0x4000_0000, 7, 3, 2, 0x1000));

        var one = Assert.Single(spa);
        Assert.Equal(0x1_0000_0000ul, one.Address);
        Assert.Equal(0x4000_0000ul, one.Length);
    }

    [Fact]
    public void NFIT_記憶體裝置_解出控制代碼與交錯通道()
    {
        var dev = AcpiDeep.NfitMemoryDevices(BuildNfit(0, 0x1000, 0x2000, deviceHandle: 0x1234, regionId: 5, ways: 4, regionSize: 0x8000));

        var one = Assert.Single(dev);
        Assert.Equal(0x1234u, one.DeviceHandle);
        Assert.Equal((ushort)5, one.RegionId);
        Assert.Equal((ushort)4, one.InterleaveWays);
        Assert.Equal(0x8000ul, one.RegionSize);
    }

    [Fact]
    public void NFIT_非NFIT簽章回空()
    {
        Assert.Empty(AcpiDeep.NfitStructures(BuildTable("HPET", 1, "OEMAAA", "HPET0001", 60)));
    }

    // ── AC-010/011/012/013/014 單表欄位 ──────────────────────────────────

    [Fact]
    public void MPAM_只認簽章()
    {
        Assert.True(AcpiDeep.IsMpam(BuildTable("MPAM", 1, "OEMAAA", "MPAM0001", 48)));
        Assert.False(AcpiDeep.IsMpam(BuildTable("NFIT", 1, "OEMAAA", "NFIT0001", 48)));
    }

    [Fact]
    public void BGRT_版本狀態與影像位置()
    {
        var t = BuildTable("BGRT", 1, "OEMAAA", "BGRT0001", 60, body =>
        {
            BitConverter.GetBytes((ushort)1).CopyTo(body, 0);   // version @36
            body[2] = 0x01;                                     // status @38：bit0 已顯示
            body[3] = 0x00;                                     // image_type @39
            BitConverter.GetBytes((uint)1920).CopyTo(body, 12); // offset_x @48
            BitConverter.GetBytes((uint)1080).CopyTo(body, 16); // offset_y @52
        });

        var info = AcpiDeep.Bgrt(t);

        Assert.NotNull(info);
        Assert.Equal((ushort)1, info!.Value.Version);
        Assert.True(info.Value.Displayed);
        Assert.Equal(1920u, info.Value.OffsetX);
        Assert.Equal(1080u, info.Value.OffsetY);
    }

    [Fact]
    public void BGRT_過短表回null()
        => Assert.Null(AcpiDeep.Bgrt(BuildTable("BGRT", 1, "OEMAAA", "BGRT0001", 40)));

    [Fact]
    public void SPCR_介面型別與基底位址()
    {
        var t = BuildTable("SPCR", 2, "OEMAAA", "SPCR0001", 80, body =>
        {
            body[0] = 0x01;                                       // interface_type @36
            body[4] = 0x00;                                       // address_space_id @40（系統記憶體）
            BitConverter.GetBytes(0xFE03_F000ul).CopyTo(body, 8);  // address @44
        });

        var info = AcpiDeep.Spcr(t);

        Assert.NotNull(info);
        Assert.Equal((byte)1, info!.Value.InterfaceType);
        Assert.Equal(0xFE03_F000ul, info.Value.BaseAddress);
    }

    [Fact]
    public void DBG2_除錯裝置個數()
    {
        var t = BuildTable("DBG2", 1, "OEMAAA", "DBG2 001", 48, body =>
        {
            BitConverter.GetBytes(44u).CopyTo(body, 0); // info_offset @36
            BitConverter.GetBytes(2u).CopyTo(body, 4);  // info_count @40
        });

        var info = AcpiDeep.Dbg2(t);

        Assert.NotNull(info);
        Assert.Equal(2u, info!.Value.InfoCount);
        Assert.Equal(44u, info.Value.InfoOffset);
    }

    [Fact]
    public void HPET_全欄位()
    {
        var t = BuildTable("HPET", 1, "OEMAAA", "HPET0001", 60, body =>
        {
            BitConverter.GetBytes(0x8086_A201u).CopyTo(body, 0);  // id @36
            body[4] = 0x00;                                       // address_space_id @40
            BitConverter.GetBytes(0xFED0_0000ul).CopyTo(body, 8); // address @44
            body[16] = 0x03;                                      // sequence @52
            BitConverter.GetBytes((ushort)0x0080).CopyTo(body, 18); // minimum_tick @54
        });

        var info = AcpiDeep.Hpet(t);

        Assert.NotNull(info);
        Assert.Equal(0x8086_A201u, info!.Value.HardwareId);
        Assert.Equal(0xFED0_0000ul, info.Value.BaseAddress);
        Assert.Equal((byte)3, info.Value.Sequence);
        Assert.Equal((ushort)0x0080, info.Value.MinimumTick);
    }

    // ── 服務層：事實產出與三態 ────────────────────────────────────────────

    [Fact]
    public void 服務_ACPI表層十項_有表的出值沒表的如實N_A()
    {
        var facts = AcpiService.Collect(new FakeSource(true, null,
        [
            BuildTable("DSDT", 2, "OEMAAA", "DSDT0001", 80, b => System.Text.Encoding.ASCII.GetBytes("\\_SB.PCI0").CopyTo(b, 8)),
            HeaderOfBytes("SSDT", "VENDORB", "SSDT0001", 48),
            BuildTable("MCFG", 1, "OEMAAA", "MCFG0001", 44 + 16, b =>
            {
                BitConverter.GetBytes(0xE000_0000ul).CopyTo(b, 8);  // 基底 @44
                BitConverter.GetBytes((ushort)0).CopyTo(b, 16);     // segment @52
                b[18] = 0x00; b[19] = 0xFF;                          // bus 0-255
            }),
            BuildTable("HPET", 1, "OEMAAA", "HPET0001", 60, b => BitConverter.GetBytes(0x8086_A201u).CopyTo(b, 0)),
            BuildTable("BGRT", 1, "OEMAAA", "BGRT0001", 60, b => BitConverter.GetBytes((ushort)1).CopyTo(b, 0)),
            BuildTable("DBG2", 1, "OEMAAA", "DBG2 001", 48, b => BitConverter.GetBytes(1u).CopyTo(b, 4)),
            BuildTable("MPAM", 1, "OEMAAA", "MPAM0001", 48),
        ]), At);

        // AC-018 匯總：7 張表、無校驗和錯誤
        var summary = facts.Single(f => f.Key == "ac.tables.summary");
        Assert.Contains("7 張表", summary.Value);
        Assert.Contains("校驗和錯誤 0 張", summary.Value);

        // AC-006 字串掃描
        Assert.Contains("\\_SB.PCI0", facts.Single(f => f.Key == "ac.aml.str.sample").Value);

        // AC-008 第三方 SSDT（VENDORB ≠ OEMAAA）
        Assert.Equal("1", facts.Single(f => f.Key == "ac.ssdt.third.count").Value);

        // AC-011/012/013/014/015
        Assert.Equal("0x8086a201", facts.Single(f => f.Key == "ac.hpet.full.id").Value);
        Assert.Contains("版本 1", facts.Single(f => f.Key == "ac.bgrt").Value);
        Assert.Equal("1", facts.Single(f => f.Key == "ac.dbg2.devices").Value);
        Assert.Contains("存在", facts.Single(f => f.Key == "ac.mpam").Value);
        Assert.Equal("1", facts.Single(f => f.Key == "ac.mcfg.full.count").Value);

        // AC-009/012 沒有表 → NotApplicable 且寫原因（讀不到不等於沒有）
        var nfit = facts.Single(f => f.Key == "ac.nfit.summary");
        Assert.Equal(FactAvailability.NotApplicable, nfit.Availability);
        Assert.Contains("非 NVDIMM", nfit.UnavailableReason ?? nfit.Value);
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(f => f.Key == "ac.spcr").Availability);
    }

    [Fact]
    public void 服務_空表集_十項鍵仍在且三態誠實()
    {
        var facts = AcpiService.Collect(new FakeSource(true, null, []), At);

        foreach (string key in new[] { "ac.tables.summary", "ac.aml.str.count", "ac.ssdt.third.count", "ac.nfit.summary", "ac.mpam", "ac.bgrt", "ac.spcr", "ac.dbg2", "ac.hpet.full.id", "ac.mcfg.full.count" })
            Assert.Contains(facts, f => f.Key == key);

        Assert.Equal("0", facts.Single(f => f.Key == "ac.ssdt.third.count").Value);
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(f => f.Key == "ac.mpam").Availability);
    }

    // ── 工具 ─────────────────────────────────────────────────────────────

    private sealed class FakeSource(bool available, string? reason, byte[][] tables) : IAcpiTableSource
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public IReadOnlyList<byte[]> ReadAll() => tables;
    }

    private static AcpiTableHeader HeaderOf(string sig, string oemId, string oemTableId)
        => new(sig, 48, 1, oemId, oemTableId, true);

    private static byte[] HeaderOfBytes(string sig, string oemId, string oemTableId, int len)
        => BuildTable(sig, 1, oemId, oemTableId, len);

    /// <summary>合成 NFIT：一筆 SPA 範圍（型別 0，60 bytes）＋一筆記憶體裝置（型別 1，48 bytes）。
    /// 子結構起於表 offset 40（＝body 起點 36 之後 4 bytes）。</summary>
    private static byte[] BuildNfit(ushort spaIndex, ulong spaAddress, ulong spaLength, uint deviceHandle, ushort regionId, ushort ways, ulong regionSize)
        => BuildTable("NFIT", 1, "OEMAAA", "NFIT0001", 40 + 60 + 48, body =>
        {
            // 子結構 0：SPA 範圍（表 40 起 → body 4 起）
            BitConverter.GetBytes((ushort)0).CopyTo(body, 4);
            BitConverter.GetBytes((ushort)60).CopyTo(body, 6);
            BitConverter.GetBytes(spaIndex).CopyTo(body, 8);
            BitConverter.GetBytes(0x1234u).CopyTo(body, 16);       // proximity domain
            BitConverter.GetBytes(spaAddress).CopyTo(body, 36);
            BitConverter.GetBytes(spaLength).CopyTo(body, 44);
            // 子結構 1：記憶體裝置（表 100 起 → body 64 起）
            BitConverter.GetBytes((ushort)1).CopyTo(body, 64);
            BitConverter.GetBytes((ushort)48).CopyTo(body, 66);
            BitConverter.GetBytes(deviceHandle).CopyTo(body, 68);
            BitConverter.GetBytes(regionId).CopyTo(body, 74);
            BitConverter.GetBytes(regionSize).CopyTo(body, 80);
            BitConverter.GetBytes(ways).CopyTo(body, 106);
        });

    /// <summary>合成 ACPI 表：表頭 + 可選 body 寫入動作，最後補校驗和（和 mod 256 = 0）。</summary>
    private static byte[] BuildTable(string sig, byte revision, string oemId, string oemTableId, int totalLength, Action<byte[]>? body = null)
    {
        var t = new byte[totalLength];
        System.Text.Encoding.ASCII.GetBytes(sig).CopyTo(t, 0);
        BitConverter.GetBytes((uint)totalLength).CopyTo(t, 4);
        t[8] = revision;
        PadAscii(oemId, 6).CopyTo(t, 10);
        PadAscii(oemTableId, 8).CopyTo(t, 16);
        if (body is not null)
        {
            var bodyBytes = new byte[totalLength - AcpiTable.HeaderLength]; // 注意：t[36..] 對陣列是複製，寫進去不會回到 t
            body(bodyBytes);
            bodyBytes.CopyTo(t, AcpiTable.HeaderLength);
        }
        int sum = 0;
        for (int i = 0; i < t.Length; i++) if (i != 9) sum += t[i];
        t[9] = (byte)((256 - (sum % 256)) % 256);
        return t;
    }

    private static byte[] PadAscii(string s, int len)
    {
        var b = new byte[len];
        System.Text.Encoding.ASCII.GetBytes(s).AsSpan(0, System.Math.Min(s.Length, len)).CopyTo(b);
        return b;
    }
}
