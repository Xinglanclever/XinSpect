using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SPI 快閃熵圖服務的契約：分區切分、抹除／高熵／低熵分類如實、讀不到時三態而非假值、
/// 超上限誠實拒讀、RPE 重疊必須標註（不能讓「讀不到」被誤讀成「沒內容」）。
/// </summary>
public class SpiEntropyServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const ulong FlashBase = 0xFFF00000;   // 0x100000 快閃（1 MiB）映射在 4GB 頂端
    private const ulong SpiBar = 0xFED10000;

    // 描述符 0x000-0x00F、BIOS 0x010-0x04F（256 KiB）、ME 0x050-0x0FF（704 KiB）
    private static readonly uint[] Fregs =
    [
        SyntheticFixtures.EncodeFreg(0x000, 0x00F),
        SyntheticFixtures.EncodeFreg(0x010, 0x04F),
        SyntheticFixtures.EncodeFreg(0x050, 0x0FF),
        0, 0, 0,
    ];

    /// <summary>造一顆快閃：BIOS 區放偽隨機（高熵）、ME 區放全 F（抹除）、其餘全 0。</summary>
    private static byte[] MakeFlash()
    {
        var flash = new byte[0x100000];
        ulong s = 0x2545F4914F6CDD1D;
        for (int i = 0x10000; i < 0x50000; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            flash[i] = (byte)(s >> 24);
        }
        Array.Fill(flash, (byte)0xFF, 0x50000, 0xB0000);
        return flash;
    }

    [Fact]
    public void 可讀時_給出整體與分區熵摘要()
    {
        var fact = Assert.Single(SpiEntropyService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, MakeFlash()), At));

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("全區 1 MiB：256 塊", fact.Value);
        Assert.Contains("高熵", fact.Value);
        Assert.Contains("抹除", fact.Value);
        // 分區摘要必須列出有配置的區域
        Assert.Contains("描述符", fact.Value);
        Assert.Contains("BIOS", fact.Value);
        Assert.Contains("Intel ME", fact.Value);
        // 誠實界線必須在事實文字裡
        Assert.Contains("不判斷好壞或是否原廠", fact.Value);
    }

    [Fact]
    public void 高熵區與抹除區的分類要對得上實際內容()
    {
        var flash = MakeFlash();
        var blocks = EntropyMap.Analyze(flash);
        var regions = SpiEntropyService.AnalyzeRegions(Fregs, flash, blocks);

        // BIOS 區（0x10000-0x4FFFF，64 塊）是偽隨機 → 應該幾乎全為高熵
        var bios = regions.Single(r => r.RegionName == "BIOS");
        Assert.Equal(64, bios.Summary.Blocks);
        Assert.True(bios.Summary.HighEntropyBlocks >= 60,
            $"BIOS 區應幾乎全為高熵，實得 {bios.Summary.HighEntropyBlocks}/64");

        // ME 區（0x50000-0xFFFFF，176 塊）全 F → 應該全為抹除
        var me = regions.Single(r => r.RegionName == "Intel ME");
        Assert.Equal(176, me.Summary.Blocks);
        Assert.Equal(176, me.Summary.ErasedBlocks);
        Assert.Equal(0, me.Summary.HighEntropyBlocks);
    }

    [Fact]
    public void 全空FREG_不臆測快閃大小()
    {
        uint[] empty = [0, 0, 0, 0, 0, 0];
        var fact = Assert.Single(SpiEntropyService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(empty, MakeFlash()), At));

        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不猜", fact.UnavailableReason);
    }

    [Fact]
    public void 缺MMIO後端_標權限不足而非假值()
    {
        var fact = Assert.Single(SpiEntropyService.Collect(new FakeSpiPci(), new NotLoadedMmioReader(), At));

        Assert.Equal(FactAvailability.InsufficientPrivilege, fact.Availability);
        Assert.Equal("", fact.Value);
        Assert.Contains("SPIBAR", fact.UnavailableReason);
    }

    [Fact]
    public void 讀取中斷_標讀取失敗並指出中斷位移()
    {
        // 後端單次讀取上限 4 KiB（強制走分塊路徑），且從快閃位移 0x80000 起拒絕讀取：
        // 分塊讀到那裡會中斷，服務必須如實標讀取失敗並指出中斷位移，而不是回一份殘缺的熵圖。
        var fact = Assert.Single(SpiEntropyService.Collect(
            new FakeSpiPci(),
            new FakeSpiFlashMmio(Fregs, MakeFlash(), maxRead: 4096, failAt: FlashBase + 0x80000), At));

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("讀取失敗", fact.UnavailableReason);
        Assert.Contains("中斷於", fact.UnavailableReason);
    }

    [Fact]
    public void RPE讀保護重疊_必須標註而非當成抹除()
    {
        // PR0 對 BIOS 區設讀保護：讀出來會是全 F，但那是讀不到不是沒內容
        uint prx = SyntheticFixtures.EncodePrx(0x010, 0x04F, writeProtect: true, readProtect: true);
        var fact = Assert.Single(SpiEntropyService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, MakeFlash(), prx: prx), At));

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("讀保護", fact.Value);
        Assert.Contains("讀不到而非沒內容", fact.Value);
    }

    [Fact]
    public void 快閃過小_不足一個分析區塊時如實說明()
    {
        // 8 KiB 快閃＝恰好兩塊。註：4 KiB 的 FREG(0,0) 與「全空」同值（raw==0 即 Empty），
        // 因此本測試取兩塊而非一塊——那是格式的既有語意，不是這裡的特例。
        uint[] tiny = [SyntheticFixtures.EncodeFreg(0x000, 0x001), 0, 0, 0, 0, 0];  // 8 KiB
        var fact = Assert.Single(SpiEntropyService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(tiny, new byte[8192]), At));
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("2 塊", fact.Value);
        Assert.Contains("全區 8 KiB", fact.Value);
    }

    [Fact]
    public void 事實鍵如實申報()
    {
        var fact = Assert.Single(SpiEntropyService.Collect(new FakeSpiPci(), new FakeSpiFlashMmio(Fregs, MakeFlash()), At));
        Assert.Equal(SpiEntropyService.FactKey, fact.Key);
        Assert.Equal("韌體安全", fact.Category);
    }

    private sealed class FakeSpiPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public uint? ReadDword(byte bus, byte device, byte function, uint register) =>
            (device, function, register) switch
            {
                (0x1F, 5, 0x00) => 0x06C0_8086,          // Intel SPI 控制器
                (0x1F, 5, 0x10) => (uint)SpiBar,          // 映射型 BAR
                _ => 0xFFFF_FFFF,
            };
    }

    private sealed class FakeSpiFlashMmio(uint[] fregs, byte[] flash, ulong? failAt = null, uint prx = 0, int? maxRead = null) : IMmioReader
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
                BitConverter.GetBytes(prx).CopyTo(block, 0x74);
                return block;
            }
            // 映射基底由快閃大小反推（4GB 頂端 − 大小），與 SpiFlashMap 的慣例一致；
            // 用固定常數會讓「小快閃」的測試位址對不上，變成假失敗。
            ulong mappedBase = 0x1_0000_0000UL - (ulong)flash.Length;
            if (address >= mappedBase && address - mappedBase < (ulong)flash.Length)
            {
                ulong off = address - mappedBase;
                if (off + (ulong)length > (ulong)flash.Length) return null;
                return flash[(int)off..(int)(off + (ulong)length)];
            }
            return null;
        }
    }
}
