using System.Security.Cryptography;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SPI 快閃地圖與 BIOS 區雜湊（WP4）的契約：單次大讀與 4KB 分塊兩條路徑都算出正確雜湊、
/// 全 F 頁與 RPE 讀保護如實標注、中途失敗帶位移、地圖無法推導不猜。以複合假件驗證，不碰硬體。
/// </summary>
public class SpiFlashHashTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private const ulong FlashBase = 0xFFF00000; // 1MB 快閃映射在 4GB 頂端
    private const ulong SpiBar = 0xFED10000;

    // FREG：描述符 0x000-0x00F、BIOS 0x010-0x04F（256KB）、ME 0x050-0x0FF → 快閃 1MB
    private static readonly uint[] Fregs =
    [
        SyntheticFixtures.EncodeFreg(0x000, 0x00F),
        SyntheticFixtures.EncodeFreg(0x010, 0x04F),
        SyntheticFixtures.EncodeFreg(0x050, 0x0FF),
        0, 0, 0,
    ];

    private static byte[] MakeFlash()
    {
        var flash = new byte[0x100000]; // 1MB
        var rng = new Random(42);
        rng.NextBytes(flash.AsSpan(0x10000, 0x4000));       // BIOS 區前 16KB 有內容
        Array.Fill(flash, (byte)0xFF, 0x20000, 0x1000);     // BIOS 區內一頁全 F
        return flash;
    }

    [Fact]
    public void 全鏈成功_地圖與雜湊都成列且雜湊正確()
    {
        var flash = MakeFlash();
        var facts = SpiFlashHashService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, flash), At);

        var map = Assert.Single(facts, f => f.Key == "spi.flash_map");
        Assert.Equal(FactAvailability.Present, map.Availability);
        Assert.Contains("1 MiB", map.Value);
        Assert.Contains("0xFFF00000", map.Value);
        Assert.Contains("BIOS 區快閃位移 0x10000-0x4FFFF（256 KiB）", map.Value);

        var hash = Assert.Single(facts, f => f.Key == "spi.bios_hash");
        Assert.Equal(FactAvailability.Present, hash.Availability);
        string expected = Convert.ToHexStringLower(SHA256.HashData(flash.AsSpan(0x10000, 0x40000)));
        Assert.Contains($"SHA-256={expected}", hash.Value);
        Assert.Contains("256 KiB", hash.Value);
        Assert.Contains("1 個全 F 4KB 頁", hash.Value); // 擦除頁如實標注
    }

    [Fact]
    public void 分塊路徑_上限4096的後端仍算出同一雜湊且文字帶分塊說明()
    {
        var flash = MakeFlash();
        var facts = SpiFlashHashService.Collect(new FakeSpiPci(),
            new FakeSpiFlashMmio(Fregs, flash, maxRead: 4096), At);

        var hash = Assert.Single(facts, f => f.Key == "spi.bios_hash");
        Assert.Equal(FactAvailability.Present, hash.Availability);
        string expected = Convert.ToHexStringLower(SHA256.HashData(flash.AsSpan(0x10000, 0x40000)));
        Assert.Contains(expected, hash.Value);
        Assert.Contains("分塊讀取", hash.Value);
    }

    [Fact]
    public void 分塊途中失敗_讀取錯誤帶中斷位移()
    {
        var flash = MakeFlash();
        ulong failAt = FlashBase + 0x10000 + 8UL * 4096; // BIOS 區第 9 個 4KB 塊
        var facts = SpiFlashHashService.Collect(new FakeSpiPci(),
            new FakeSpiFlashMmio(Fregs, flash, maxRead: 4096, failAt: failAt), At);

        var hash = Assert.Single(facts, f => f.Key == "spi.bios_hash");
        Assert.Equal(FactAvailability.ReadError, hash.Availability);
        Assert.Contains($"0x{failAt:X}", hash.UnavailableReason);
        Assert.Contains("已讀 32768", hash.UnavailableReason);
        Assert.Null(hash.NumericValue);
    }

    [Fact]
    public void RPE讀保護重疊BIOS區_文字帶標注()
    {
        var flash = MakeFlash();
        var fregs = (uint[])Fregs.Clone();
        // PRx：base4k 0x020-0x02F 落在 BIOS 區（0x010-0x04F）內、RPE 開
        uint prx = SyntheticFixtures.EncodePrx(0x020, 0x02F, writeProtect: false, readProtect: true);
        var facts = SpiFlashHashService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(fregs, flash, prx: prx), At);

        var hash = Assert.Single(facts, f => f.Key == "spi.bios_hash");
        Assert.Contains("PRx 讀保護（RPE）範圍重疊", hash.Value);
    }

    [Fact]
    public void FREG全空與BIOS未配置_如實不猜()
    {
        var flash = MakeFlash();
        var empty = SpiFlashHashService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio([0, 0, 0, 0, 0, 0], flash), At);
        Assert.All(empty, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("無從推導", Assert.Single(empty, f => f.Key == "spi.flash_map").UnavailableReason);

        var noBios = SpiFlashHashService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(
            [SyntheticFixtures.EncodeFreg(0x000, 0x00F), 0, SyntheticFixtures.EncodeFreg(0x010, 0x1FF), 0, 0, 0], flash), At);
        Assert.Equal(FactAvailability.Present, Assert.Single(noBios, f => f.Key == "spi.flash_map").Availability);
        Assert.Equal(FactAvailability.NotApplicable, Assert.Single(noBios, f => f.Key == "spi.bios_hash").Availability);
        Assert.Contains("未配置", Assert.Single(noBios, f => f.Key == "spi.bios_hash").UnavailableReason);
    }

    [Fact]
    public void MMIO不可用_兩筆三態()
    {
        var facts = SpiFlashHashService.Collect(new FakeSpiPci(), new NotLoadedMmioReader(), At);
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
    }

    private sealed class FakeSpiPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register) =>
            (device, function, register) switch
            {
                (0x1F, 5, 0x00) => 0x06C0_8086, // Intel SPI 控制器
                (0x1F, 5, 0x10) => (uint)SpiBar, // 映射型 BAR（基底 4KB 對齊，bit0=0）
                _ => 0xFFFF_FFFF,
            };
    }

    /// <summary>複合假件：SPIBAR 視窗（含 FREG/PRx）＋快閃映射視窗；可編程單次讀取上限與失敗點。</summary>
    private sealed class FakeSpiFlashMmio(uint[] fregs, byte[] flash, int? maxRead = null, ulong? failAt = null, uint prx = 0) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public string? LastFailReason => "測試假件：讀取被拒";

        public byte[]? ReadBlock(ulong address, int length)
        {
            if (maxRead is { } cap && length > cap) return null;
            if (failAt is { } at && address >= at) return null;

            if (address == SpiBar)
            {
                var block = new byte[0x88];
                for (int i = 0; i < fregs.Length; i++)
                    BitConverter.GetBytes(fregs[i]).CopyTo(block, 0x54 + i * 4);
                BitConverter.GetBytes(prx).CopyTo(block, 0x74); // PR0（RPE 測試用）
                return block;
            }
            if (address >= FlashBase && address - FlashBase < (ulong)flash.Length)
            {
                ulong off = address - FlashBase;
                if (off + (ulong)length > (ulong)flash.Length) return null;
                return flash[(int)off..(int)(off + (ulong)length)];
            }
            return null;
        }
    }
}
