using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ATA IDENTIFY DEVICE（256 words）的解碼。
/// </summary>
/// <remarks>
/// 這支解碼器餵的是驗機規則裡最容易產生假結論的兩條：假容量碟（宣稱容量 vs 可定址 LBA）與
/// 貼牌碟（宣稱轉速 vs 屬性集）。所以「讀不到」與「讀到 0」必須分得一清二楚——
/// 部分 USB 外接盒與 RAID 控制器不轉送 ATA 指令，回的是一整片 0；
/// 若把那片 0 解成「標稱轉速 0」，再往下就會變成「這是固態碟」這種憑空捏造的結論。
/// </remarks>
public class AtaIdentifyTests
{
    /// <summary>ATA 字串以 word 為單位交換位元組——這正是最容易寫錯、而且錯了還「看起來像字」的地方。</summary>
    private static void PutAtaString(byte[] d, int firstWord, string s, int words)
    {
        var padded = s.PadRight(words * 2).Substring(0, words * 2);
        for (int i = 0; i < words; i++)
        {
            d[(firstWord + i) * 2] = (byte)padded[i * 2 + 1];
            d[(firstWord + i) * 2 + 1] = (byte)padded[i * 2];
        }
    }

    private static byte[] Synthetic(
        string model = "INTEL SSDPELKX010T8", string serial = "PHLJ000000AA", string firmware = "VCV10370",
        ulong lba48 = 1_953_525_168, uint lba28 = 268_435_455, ushort rotation = 1, ushort word80 = 0x07F0)
    {
        var d = new byte[512];
        PutAtaString(d, 27, model, 20);
        PutAtaString(d, 10, serial, 10);
        PutAtaString(d, 23, firmware, 4);
        BitConverter.GetBytes(lba28).CopyTo(d, 60 * 2);
        BitConverter.GetBytes(lba48).CopyTo(d, 100 * 2);
        BitConverter.GetBytes(rotation).CopyTo(d, 217 * 2);
        BitConverter.GetBytes(word80).CopyTo(d, 80 * 2);
        return d;
    }

    [Fact]
    public void 字串欄位的位元序要還原_不得留下交換過的亂碼()
    {
        var info = AtaIdentify.Decode(Synthetic())!;
        Assert.Equal("INTEL SSDPELKX010T8", info.Model);
        Assert.Equal("PHLJ000000AA", info.Serial);
        Assert.Equal("VCV10370", info.Firmware);
        Assert.DoesNotContain('\0', info.Model);
    }

    [Fact]
    public void 全零緩衝區判為讀不到_不得解成標稱轉速0的固態碟()
    {
        Assert.Null(AtaIdentify.Decode(new byte[512]));
        Assert.Null(AtaIdentify.Decode(new byte[256]));      // 長度不足
        Assert.Null(AtaIdentify.Decode(null!));
    }

    [Theory]
    [InlineData((ushort)1, true, false)]          // 規格：0x0001＝非旋轉裝置
    [InlineData((ushort)5400, false, true)]
    [InlineData((ushort)7200, false, true)]
    [InlineData((ushort)0, false, false)]         // 未回報：兩者皆不成立，不准猜
    [InlineData((ushort)0xFFFF, false, false)]    // 保留值：同樣不猜
    public void 標稱轉速決定機械或固態_未回報時兩者皆不成立(ushort rate, bool solid, bool mechanical)
    {
        var info = AtaIdentify.Decode(Synthetic(rotation: rate))!;
        Assert.Equal(rate, info.RotationRate);
        Assert.Equal(solid, info.IsSolidState);
        Assert.Equal(mechanical, info.IsMechanical);
    }

    [Fact]
    public void 可定址容量_以48位元欄位為準_為零時才退回28位元()
    {
        Assert.Equal(1_953_525_168ul, AtaIdentify.Decode(Synthetic())!.TotalLba);
        Assert.Equal(268_435_455ul, AtaIdentify.Decode(Synthetic(lba48: 0))!.TotalLba);
    }

    [Fact]
    public void 高16位元的雜訊不得汙染48位元LBA()
    {
        var d = Synthetic(lba48: 0);
        BitConverter.GetBytes(0xDEAD_0000_0000_0000ul | 1_000_000ul).CopyTo(d, 100 * 2);
        Assert.Equal(1_000_000ul, AtaIdentify.Decode(d)!.TotalLba);
    }

    [Fact]
    public void 容量以每磁區512位元組換算成廠商標示的GB()
        => Assert.Equal(1000.2, AtaIdentify.Decode(Synthetic())!.CapacityGB, 1);

    [Theory]
    [InlineData((ushort)0x07F0, 10)]      // 最高設定位元＝10 → ACS-3
    [InlineData((ushort)0x0010, 4)]       // 只有 bit4 → ATA/ATAPI-4
    [InlineData((ushort)0x0000, 0)]       // 未回報
    [InlineData((ushort)0xFFFF, 0)]       // 保留值視為未回報
    public void ACS版本取最高設定位元(ushort word80, int expected)
        => Assert.Equal(expected, AtaIdentify.Decode(Synthetic(word80: word80))!.AcsMajorVersion);
}
