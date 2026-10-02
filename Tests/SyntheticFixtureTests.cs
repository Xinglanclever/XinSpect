using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 合成已知答案測試（V7 M2）：獨立 encoder × 解碼器的往返＋黃金答案向量＋邊界（全 0／全 FF／截斷不崩潰）。
/// 這是 T 類「非自造驗證來源」的基礎設施——encoder 對 spec 硬編、decoder 對 spec 硬編，兩邊對不上就是 bug。
/// </summary>
public class SyntheticFixtureTests
{
    // ── PCIe AER ──

    [Fact]
    public void AER鏈_獨立編碼後解碼找回正確偏移與狀態()
    {
        var cfg = SyntheticFixtures.Config4k(
            [new(0x100, 0x0007, 0x140), new(0x140, 0x0001, null)],
            [(0x144, 0xDEADBEEF), (0x150, 0x00000001)]);

        Assert.Equal(0x140, PcieAer.FindAerCapOffset(cfg));
        var status = PcieAer.DecodeAer(cfg, 0x140);
        Assert.NotNull(status);
        Assert.Equal(0xDEADBEEFu, status!.Value.UncorrectableStatus);
        Assert.Equal(0x00000001u, status.Value.CorrectableStatus);
        Assert.True(status.Value.HasUncorrectable);
        Assert.True(status.Value.HasCorrectable);
    }

    [Fact]
    public void AER_黃金答案_表頭位元組序釘死()
    {
        // 金標：AER cap id 0x0001、無下一個 → 表頭 dword 0x0001_0001，LE 位元組 01 00 01 00。
        var cfg = SyntheticFixtures.Config4k([new(0x100, 0x0001, null)], []);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x01, 0x00 }, cfg[0x100..0x104]);

        // 獨立編碼的空間被獨立的解碼器讀回：capId=1、next=0。
        uint header = BitConverter.ToUInt32(cfg[0x100..0x104]);
        Assert.Equal(0x0001u, header & 0xFFFF);
        Assert.Equal(0u, (header >> 20) & 0xFFF);
    }

    [Fact]
    public void AER_全0與全FF與截斷都不崩潰且誠實回null()
    {
        Assert.Null(PcieAer.FindAerCapOffset(new byte[4096]));                     // 全 0：空表頭
        Assert.Null(PcieAer.FindAerCapOffset(Enumerable.Repeat((byte)0xFF, 4096).ToArray())); // 全 FF：防越界
        Assert.Null(PcieAer.FindAerCapOffset(new byte[0x104]));                    // 截斷
        // 全 0 空間在有效偏移上是合法的「零錯誤」狀態（不是 null）：HasX 為 false。
        var zeros = PcieAer.DecodeAer(new byte[4096], 0x140);
        Assert.NotNull(zeros);
        Assert.False(zeros!.Value.HasUncorrectable);
        Assert.False(zeros.Value.HasCorrectable);
        Assert.Null(PcieAer.DecodeAer(new byte[4096], 0xFF0));                     // 偏移越界
    }

    // ── PCH SPI ──

    [Fact]
    public void HSFSTS_往返全組合_未知位元原樣保留()
    {
        foreach (bool fdone in stackalloc[] { false, true })
        foreach (bool fcerr in stackalloc[] { false, true })
        foreach (bool ael in stackalloc[] { false, true })
        foreach (bool wrsdis in stackalloc[] { false, true })
        foreach (bool fdopss in stackalloc[] { false, true })
        foreach (bool flockdn in stackalloc[] { false, true })
        {
            uint unknown = 0x0000_4100; // 已知位元之外的雜訊
            uint raw = SyntheticFixtures.EncodeHsfsts(fdone, fcerr, ael, wrsdis, fdopss, flockdn, unknown);
            var d = SpiFlash.DecodeHsfsts(raw);
            Assert.Equal(fdone, d.FlashCycleDone);
            Assert.Equal(fcerr, d.FlashCycleError);
            Assert.Equal(ael, d.AccessErrorLog);
            Assert.Equal(wrsdis, d.WriteStatusDisable);
            Assert.Equal(fdopss, d.DescriptorOverridePinStrap);
            Assert.Equal(flockdn, d.FlashLockDown);
            Assert.Equal(unknown, d.UnknownBits);
        }
    }

    [Fact]
    public void HSFSTS_黃金答案_FLOCKDN與WRSDIS的絕對位元值()
    {
        // 金標（Intel PCH EDS）：FLOCKDN=bit15（0x8000）、WRSDIS=bit11（0x0800）——兩者同開＝0x8800。
        Assert.Equal(0x8800u, SyntheticFixtures.EncodeHsfsts(fdone: false, fcerr: false, ael: false,
            wrsdis: true, fdopss: false, flockdn: true, unknownBits: 0));
        var d = SpiFlash.DecodeHsfsts(0x8800);
        Assert.True(d.FlashLockDown);
        Assert.True(d.WriteStatusDisable);
        Assert.Equal(ChipsetSecurityVerdict.Protected, d.Verdict);
    }

    [Fact]
    public void FRAP_FREG_PRX_往返與金標()
    {
        // FRAP：BRWA bit1=1（准 host 寫 BIOS 區）、BRRA=0xF
        var frap = SpiFlash.DecodeFrap(SyntheticFixtures.EncodeFrap(0x2, 0xF));
        Assert.True(frap.BiosRegionHostWritable);
        Assert.Equal(0x2, frap.BiosRegionWriteMask);
        Assert.Equal(0xF, frap.BiosRegionReadMask);

        // FREG：基底 0x10、上限 0x7FFF（handoff §9 的 4KB 單位編碼）
        var freg = SpiFlash.DecodeFreg(SyntheticFixtures.EncodeFreg(0x10, 0x7FFF));
        Assert.Equal(0x10, freg.Base4k);
        Assert.Equal(0x7FFF, freg.Limit4k);
        Assert.False(freg.Empty);
        Assert.True(SpiFlash.DecodeFreg(0).Empty);

        // PRx：WPE＋RPE
        var prx = SpiFlash.DecodePrx(SyntheticFixtures.EncodePrx(0x20, 0x2F, writeProtect: true, readProtect: true));
        Assert.True(prx.WriteProtect);
        Assert.True(prx.ReadProtect);
        Assert.True(prx.Enabled);
        Assert.Equal(0x20, prx.Base4k);
        Assert.Equal(0x2F, prx.Limit4k);

        // PRx 金標：WPE=bit15（0x8000）→ 原始 dword 高低都在對的位置
        Assert.Equal(0x8000u, SyntheticFixtures.EncodePrx(0, 0, writeProtect: true, readProtect: false) & 0x8000);
    }
}
