using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// EDID 色域解析：以合成的 128 位元組 EDID 區塊驗證標頭檢查、廠商代碼、
/// 顯示器名稱描述子與 10 位元色度座標的還原，以及覆蓋率計算。
/// </summary>
public class EdidParsingTests
{
    // sRGB 基色與 D65 白點（EDID 色度座標的教科書值）
    private const double SrRx = 0.640, SrRy = 0.330;
    private const double SrGx = 0.300, SrGy = 0.600;
    private const double SrBx = 0.150, SrBy = 0.060;
    private const double SrWx = 0.3127, SrWy = 0.3290;

    /// <summary>組出一塊語法正確的 EDID：正確標頭 + 廠商 DEL + 指定色度 + 0xFC 名稱描述子。</summary>
    private static byte[] BuildEdid(
        double rx, double ry, double gx, double gy, double bx, double by, double wx, double wy,
        string? monitorName = "XinSpect Panel", ushort mfrId = 0x10AC /* DEL */)
    {
        var e = new byte[128];

        // 固定標頭 00 FF FF FF FF FF FF 00
        e[0] = 0x00;
        for (int i = 1; i <= 6; i++) e[i] = 0xFF;
        e[7] = 0x00;

        e[8] = (byte)(mfrId >> 8);
        e[9] = (byte)(mfrId & 0xFF);

        // 每軸 10 位元：高 8 位元各佔一個位元組，低 2 位元集中於 e[25]（RG）與 e[26]（BW）
        void Put(double v, int highIndex, int lowIndex, int shift)
        {
            int q = (int)Math.Round(v * 1024);
            e[highIndex] = (byte)(q >> 2);
            e[lowIndex] |= (byte)((q & 0x3) << shift);
        }

        Put(rx, 27, 25, 6); Put(ry, 28, 25, 4);
        Put(gx, 29, 25, 2); Put(gy, 30, 25, 0);
        Put(bx, 31, 26, 6); Put(by, 32, 26, 4);
        Put(wx, 33, 26, 2); Put(wy, 34, 26, 0);

        // 第一個 18 位元組描述子（位移 54）：標籤 0xFC＝顯示器名稱
        if (monitorName is not null)
        {
            e[54] = 0; e[55] = 0; e[56] = 0; e[57] = 0xFC; e[58] = 0;
            for (int i = 0; i < monitorName.Length && i < 13; i++) e[59 + i] = (byte)monitorName[i];
        }

        return e;
    }

    private static byte[] SrgbEdid(string? name = "XinSpect Panel")
        => BuildEdid(SrRx, SrRy, SrGx, SrGy, SrBx, SrBy, SrWx, SrWy, name);

    [Fact]
    public void ValidHeader_AcceptsCanonicalHeader() => Assert.True(EdidService.ValidHeader(SrgbEdid()));

    [Fact]
    public void ValidHeader_RejectsCorruptedHeader()
    {
        var e = SrgbEdid();
        e[3] = 0x00;
        Assert.False(EdidService.ValidHeader(e));
    }

    [Fact]
    public void Parse_DecodesManufacturerId()
        => Assert.Equal("DEL", EdidService.Parse(SrgbEdid(), "後備名稱").Manufacturer);

    [Fact]
    public void Parse_ReadsMonitorNameDescriptor()
        => Assert.Equal("XinSpect Panel"[..13], EdidService.Parse(SrgbEdid(), "後備名稱").Name);

    [Fact]
    public void Parse_FallsBackWhenNoNameDescriptor()
        => Assert.Equal("後備名稱", EdidService.Parse(SrgbEdid(name: null), "後備名稱").Name);

    [Fact]
    public void Parse_SrgbPanel_CoversSrgbFully()
    {
        var info = EdidService.Parse(SrgbEdid(), "—");
        Assert.True(info.Valid);
        // 10 位元量化使各基色偏移不到 0.0005，覆蓋率與面積比皆應貼齊 100%
        Assert.InRange(int.Parse(info.SrgbText.Replace(" %", "")), 99, 100);
        Assert.Contains("100 %", info.AreaText);
    }

    [Fact]
    public void Parse_SrgbPanel_DoesNotCoverDciP3()
    {
        var info = EdidService.Parse(SrgbEdid(), "—");
        int dci = int.Parse(info.DciText.Replace(" %", ""));
        Assert.InRange(dci, 60, 95);   // sRGB 三角形無法涵蓋 DCI-P3
    }

    [Fact]
    public void Parse_RoundTripsChromaticityCoordinates()
    {
        var info = EdidService.Parse(SrgbEdid(), "—");
        // 10 位元量化：0.640 → 655/1024 ≈ 0.640
        Assert.Contains("R(0.640,0.330)", info.PrimariesText);
        Assert.Contains("G(0.300,0.600)", info.PrimariesText);
        Assert.Contains("B(0.150,0.060)", info.PrimariesText);
        // 白點 x = 0.3127 → 320/1024 = 0.3125（四捨五入顯示），y = 0.3290 → 337/1024 ≈ 0.329
        Assert.StartsWith("白點 (0.31", info.WhitePointText);
        Assert.Contains("0.329", info.WhitePointText);
    }

    [Fact]
    public void Parse_ZeroChromaticity_IsReportedInvalid()
    {
        var info = EdidService.Parse(BuildEdid(0, 0, 0, 0, 0, 0, 0, 0), "—");
        Assert.False(info.Valid);
        Assert.Contains("未提供有效色度資訊", info.Assessment);
    }

    [Fact]
    public void Parse_DegenerateTriangle_IsReportedInvalid()
    {
        // 三基色共線 → 面積為 0，須判為無效而非算出 0% 覆蓋率
        var info = EdidService.Parse(BuildEdid(0.3, 0.3, 0.4, 0.4, 0.5, 0.5, SrWx, SrWy), "—");
        Assert.False(info.Valid);
    }

    [Fact]
    public void Parse_WideGamutPanel_IsAssessedAsWideGamut()
    {
        // DCI-P3 基色：R(0.680,0.320) G(0.265,0.690) B(0.150,0.060)
        var info = EdidService.Parse(BuildEdid(0.680, 0.320, 0.265, 0.690, 0.150, 0.060, SrWx, SrWy), "—");
        Assert.True(info.Valid);
        Assert.Contains("廣色域", info.Assessment);
    }

    [Fact]
    public void Parse_NarrowGamutPanel_IsAssessedAsLow()
    {
        // 明顯內縮的三角形 → sRGB 覆蓋偏低
        var info = EdidService.Parse(BuildEdid(0.55, 0.33, 0.32, 0.50, 0.17, 0.12, SrWx, SrWy), "—");
        Assert.True(info.Valid);
        Assert.Contains("偏低", info.Assessment);
    }
}
