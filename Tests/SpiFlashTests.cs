using Xunit;

namespace XinSpect.Tests;

public sealed class SpiFlashTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // HSFSTS1（SPIBAR+0x04，現代 PCH 佈局）：bit0 FDONE、bit1 FCERR、bit2 AEL、bit11 WRSDIS、bit13 FDOPSS、bit15 FLOCKDN。
    [Fact]
    public void HSFSTS_FLOCKDN開_判為已鎖保護()
    {
        var d = SpiFlash.DecodeHsfsts(0x8000);
        Assert.True(d.FlashLockDown);
        Assert.False(d.WriteStatusDisable);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict);
        Assert.Equal(0u, d.UnknownBits);
    }

    [Fact]
    public void HSFSTS_全清_判為未鎖()
    {
        var d = SpiFlash.DecodeHsfsts(0x0000);
        Assert.False(d.FlashLockDown);
        Assert.Equal(ChipsetSecurityVerdict.Unprotected, d.Verdict);
    }

    [Fact]
    public void HSFSTS_狀態旗號與寫入停用並存_未知位元保留原值()
    {
        // bits 0,1,2（FDONE/FCERR/AEL）+ bit11（WRSDIS）+ bit15（FLOCKDN）+ bit10（未定義→保留）
        var d = SpiFlash.DecodeHsfsts(0x8C07);
        Assert.True(d.FlashCycleDone);
        Assert.True(d.FlashCycleError);
        Assert.True(d.AccessErrorLog);
        Assert.True(d.WriteStatusDisable);
        Assert.True(d.FlashLockDown);
        Assert.False(d.DescriptorOverridePinStrap);
        Assert.Equal(0x400u, d.UnknownBits);
    }

    [Fact]
    public void HSFSTS_FDOPSS位元_如實解出不渲染判決()
    {
        Assert.True(SpiFlash.DecodeHsfsts(0x2000).DescriptorOverridePinStrap);
        Assert.False(SpiFlash.DecodeHsfsts(0x0000).DescriptorOverridePinStrap);
    }

    // FRAP（SPIBAR+0x50）：bits[3:0] BRWA（區域寫入遮罩，bit1=BIOS 區）、bits[7:4] BRRA。
    [Fact]
    public void FRAP_BRWA_bit1開_代表主機軟體可寫BIOS區()
    {
        var d = SpiFlash.DecodeFrap(0x00000002);
        Assert.Equal(0x2, d.BiosRegionWriteMask);
        Assert.Equal(0x0, d.BiosRegionReadMask);
        Assert.True(d.BiosRegionHostWritable);
        Assert.Equal(0u, d.UnknownBits);
    }

    [Fact]
    public void FRAP_BRWA_bit1關_代表主機軟體不可寫BIOS區()
    {
        var d = SpiFlash.DecodeFrap(0x00000004);
        Assert.False(d.BiosRegionHostWritable);
    }

    [Fact]
    public void FRAP_高位未知_保留原值不臆測()
    {
        var d = SpiFlash.DecodeFrap(0x00010002);
        Assert.Equal(0x10000u, d.UnknownBits);
    }

    // FREGx（SPIBAR+0x54 起）：bits[14:0] 區域基底（4KB 單位）、bits[30:16] 區域上限；全 0 = 未使用。
    [Fact]
    public void FREG_全零_標未使用()
    {
        var d = SpiFlash.DecodeFreg(0);
        Assert.True(d.Empty);
    }

    [Fact]
    public void FREG_基底上限_如實解出()
    {
        // 基底 0x400（=0x400000）、上限 0x7FF 編碼在 bits[30:16]（=0x7FF0000）→ 4 MiB BIOS 區
        var d = SpiFlash.DecodeFreg(0x07FF0400);
        Assert.False(d.Empty);
        Assert.Equal(0x400, d.Base4k);
        Assert.Equal(0x7FF, d.Limit4k);
    }

    // PRx（SPIBAR+0x74 起）：bit15 WPE（寫保護）、bit31 RPE（讀保護）、基底/上限同 FREG 佈局。
    [Fact]
    public void PRx_寫讀保護與範圍_如實解出()
    {
        var d = SpiFlash.DecodePrx(0x840F8400); // RPE + WPE + 基底 0x400 + 上限 0x40F
        Assert.True(d.WriteProtect);
        Assert.True(d.ReadProtect);
        Assert.Equal(0x400, d.Base4k);
        Assert.Equal(0x40F, d.Limit4k);
    }

    [Fact]
    public void PRx_全零_無保護()
    {
        var d = SpiFlash.DecodePrx(0);
        Assert.False(d.WriteProtect);
        Assert.False(d.ReadProtect);
    }

    // ===== 服務層三態：PCI 取 SPIBAR、MMIO 讀暫存器；任何一環讀不到都如實標示 =====

    [Fact]
    public void 服務_PCI不可用_四組事實全標權限不足()
    {
        var facts = SpiFlashService.Collect(new FakePci(available: false, reason: "WinRing0 未載入", vendorDevice: 0, bar: null),
            new FakeMmio(null), At);
        Assert.Equal(4, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
            Assert.Contains("WinRing0", f.UnavailableReason);
            Assert.Equal("", f.Value);
            Assert.Null(f.NumericValue);
        });
    }

    [Fact]
    public void 服務_SPI控制器無回應_標不適用()
    {
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0xFFFFFFFFu, null), new FakeMmio(null), At);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 服務_非Intel裝置_標不適用不誤讀()
    {
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0x12345678u, null), new FakeMmio(null), At);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 服務_驅動未載_標權限不足且帶已知SPIBAR位址()
    {
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0x00008086u, 0xFED10000u), new FakeMmio(null, available: false), At);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability);
            Assert.Contains("缺自家核心驅動", f.UnavailableReason);
            Assert.Contains("0xFED10000", f.UnavailableReason);
        });
    }

    [Fact]
    public void 服務_MMIO讀取失敗_標讀取失敗()
    {
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0x00008086u, 0xFED10000u), new FakeMmio(null, available: true, nullResult: true), At);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 服務_讀到鎖定旗號與BIOS可寫_如實解碼()
    {
        // HSFSTS=0x8000（FLOCKDN 開）、FRAP=0x2（主機可寫 BIOS 區）、FREG1=0x07FF0400（BIOS 4 MiB）、PR 全停用
        var block = SpiBlock(hsfsts: 0x8000, frap: 0x00000002, freg1: 0x07FF0400);
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0x00008086u, 0xFED10000u), new FakeMmio(block), At);

        var hsf = facts.Single(x => x.Key == "spi.hsfsts");
        Assert.Equal(FactAvailability.Present, hsf.Availability);
        Assert.Equal(FactTrustLevel.Measured, hsf.Trust);
        Assert.Contains("已鎖定", hsf.Value);

        var frap = facts.Single(x => x.Key == "spi.frap");
        Assert.Contains("可寫", frap.Value);

        var regions = facts.Single(x => x.Key == "spi.regions");
        Assert.Contains("BIOS", regions.Value);
        Assert.Contains("4 MiB", regions.Value);

        var prr = facts.Single(x => x.Key == "spi.prr");
        Assert.Contains("全部停用", prr.Value);
    }

    [Fact]
    public void 服務_FRAP未開BIOS寫入_如實報不可寫()
    {
        var block = SpiBlock(hsfsts: 0x8000, frap: 0x00000004);
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0x00008086u, 0xFED10000u), new FakeMmio(block), At);
        Assert.Contains("不可寫", facts.Single(x => x.Key == "spi.frap").Value);
    }

    [Fact]
    public void 服務_保護範圍啟用_如實列出範圍()
    {
        var block = SpiBlock(hsfsts: 0x8000, frap: 0x2, pr0: 0x840F8400); // PR0 寫+讀保護 0x400000-0x40FFFF
        var facts = SpiFlashService.Collect(new FakePci(true, null, 0x00008086u, 0xFED10000u), new FakeMmio(block), At);
        Assert.Contains("0x400000", facts.Single(x => x.Key == "spi.prr").Value);
        Assert.DoesNotContain("全部停用", facts.Single(x => x.Key == "spi.prr").Value);
    }

    private static byte[] SpiBlock(uint hsfsts, uint frap, uint freg1 = 0, uint pr0 = 0)
    {
        var b = new byte[0x88];
        BitConverter.GetBytes(hsfsts).CopyTo(b, 0x04);
        BitConverter.GetBytes(frap).CopyTo(b, 0x50);
        BitConverter.GetBytes(freg1).CopyTo(b, 0x58);
        BitConverter.GetBytes(pr0).CopyTo(b, 0x74);
        return b;
    }

    private sealed class FakePci(bool available, string? reason, uint vendorDevice, uint? bar) : IPciConfigReader
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public uint? ReadDword(byte bus, byte device, byte function, uint register)
            => !available ? null : register == 0x00 ? vendorDevice : bar;
    }

    private sealed class FakeMmio(byte[]? block, bool available = true, bool nullResult = false) : IMmioReader
    {
        public bool Available => available;
        public string? UnavailableReason => available ? null : NotLoadedMmioReader.Reason;
        public byte[]? ReadBlock(ulong physicalAddress, int length)
            => !available || nullResult ? null : block;
    }
}
