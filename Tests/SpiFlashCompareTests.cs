using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// BIOS 區比對（WP4 第二層）的契約：逐 4KB 塊比對與差異位移、大小不符誠實拒比、
/// RPE 重疊標注、一致不誤報。以複合假件驗證，不碰硬體。
/// </summary>
public class SpiFlashCompareTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private const ulong FlashBase = 0xFFF00000;
    private const ulong SpiBar = 0xFED10000;

    private static readonly uint[] Fregs =
    [
        SyntheticFixtures.EncodeFreg(0x000, 0x00F),
        SyntheticFixtures.EncodeFreg(0x010, 0x04F), // BIOS 區 0x10000-0x4FFFF（256 KiB）
        SyntheticFixtures.EncodeFreg(0x050, 0x0FF),
        0, 0, 0,
    ];

    private static byte[] MakeFlash()
    {
        var flash = new byte[0x100000];
        var rng = new Random(42);
        rng.NextBytes(flash.AsSpan(0x10000, 0x4000));
        return flash;
    }

    [Fact]
    public void 完全一致_給一致結論_差異塊零()
    {
        var flash = MakeFlash();
        var reference = flash[0x10000..0x50000].ToArray();

        var fact = SpiFlashCompareService.Compare(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, flash), reference, At);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("一致（64 個 4KB 塊全部相同）", fact.Value);
        Assert.Equal(0, fact.NumericValue);
    }

    [Fact]
    public void 一塊差異_報差異塊數與快閃位移()
    {
        var flash = MakeFlash();
        var reference = flash[0x10000..0x50000].ToArray();
        flash[0x20000 + 0x10] ^= 0xFF; // 第二個 4KB 塊改一個位元組

        var fact = SpiFlashCompareService.Compare(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, flash), reference, At);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.StartsWith("差異 1 個 4KB 塊", fact.Value);
        Assert.Contains("0x20000", fact.Value); // 快閃位移＝BIOS 區起點 0x10000＋第 16 塊
        Assert.Contains("不等於被改壞", fact.Value); // 判讀權在使用者的誠實提示
        Assert.Equal(1, fact.NumericValue);
    }

    [Fact]
    public void 大小不符_誠實拒比()
    {
        var flash = MakeFlash();
        var fact = SpiFlashCompareService.Compare(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, flash),
            new byte[0x30000], At); // 192KB ≠ 256KB

        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("大小不符", fact.UnavailableReason);
        Assert.Contains("不猜", fact.UnavailableReason);
    }

    [Fact]
    public void RPE重疊_文字帶標注_被擋頁計為差異()
    {
        var flash = MakeFlash();
        flash[0x20000..0x21000].AsSpan().Fill(0xFF); // PRx 擋住的頁在實機會讀成全 F
        var reference = flash[0x10000..0x50000].ToArray();
        reference[0x10000] ^= 0xFF;                  // 參考映像該頁有真實內容 → 必然差異
        uint prx = SyntheticFixtures.EncodePrx(0x020, 0x02F, writeProtect: false, readProtect: true);

        var fact = SpiFlashCompareService.Compare(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, flash, prx: prx), reference, At);

        Assert.Contains("PRx 讀保護（RPE）重疊", fact.Value);
        Assert.True((int)fact.NumericValue! >= 1);
    }

    [Fact]
    public void 後端不可用_整組三態()
    {
        var fact = SpiFlashCompareService.Compare(new FakeSpiPci(), new NotLoadedMmioReader(), new byte[0x40000], At);
        Assert.Equal(FactAvailability.InsufficientPrivilege, fact.Availability);
    }

    [Fact]
    public void 比對結果槽_收錄替換_重載清空()
    {
        var svc = new EvidenceLabService();
        svc.AddSpiCompareFact(new HardwareFact("spi.bios_compare", "韌體安全", "BIOS 區比對（vs 參考映像）",
            "差異 3 個 4KB 塊", "", "s", FactTrustLevel.Measured, false, At, 3));
        svc.AddSpiCompareFact(new HardwareFact("spi.bios_compare", "韌體安全", "BIOS 區比對（vs 參考映像）",
            "一致（64 個 4KB 塊全部相同）", "", "s", FactTrustLevel.Measured, false, At, 0));

        var rows = svc.FirmwareSecurityRows.Where(r => r.Name == "BIOS 區比對（vs 參考映像）").ToList();
        Assert.Single(rows); // 最多保留最近一次
        Assert.Contains("一致", Assert.Single(rows).ValueText);

        // 重載驅動相依事實（假件）後：舊比對如實清空。
        svc.ReloadDriverBackedFacts(new FakeSpiPci(), new DeniedMsr(), new NotLoadedMmioReader(),
            new EmptyAcpiSource(), new UnavailableIoPortAccess("x"));
        Assert.DoesNotContain(svc.FirmwareSecurityRows, r => r.Name == "BIOS 區比對（vs 參考映像）");
    }

    private sealed class DeniedMsr : IKernelMsrReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public ulong? ReadMsr(uint index) => null;
    }

    private sealed class EmptyAcpiSource : IAcpiTableSource
    {
        public bool Available => false;
        public string? UnavailableReason => "列舉失敗（測試假件）";
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    private sealed class FakeSpiPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register) =>
            (device, function, register) switch
            {
                (0x1F, 5, 0x00) => 0x06C0_8086,
                (0x1F, 5, 0x10) => (uint)SpiBar,
                _ => 0xFFFF_FFFF,
            };
    }

    private sealed class FakeSpiFlashMmio(uint[] fregs, byte[] flash, uint prx = 0) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public byte[]? ReadBlock(ulong address, int length)
        {
            if (address == SpiBar)
            {
                var block = new byte[0x88];
                for (int i = 0; i < fregs.Length; i++)
                    BitConverter.GetBytes(fregs[i]).CopyTo(block, 0x54 + i * 4);
                BitConverter.GetBytes(prx).CopyTo(block, 0x74);
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
