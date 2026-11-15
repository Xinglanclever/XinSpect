using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class EdidTimingsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static byte[] DellEdid() => File.ReadAllBytes(Path.Combine(Root(), "Tests", "Fixtures", "edid-dell-u2412m.bin"));

    [Fact]
    public void RealEdidDecodesEstablishedAndDtdWithFullTimingMatch()
    {
        var result = EdidTimings.Parse(DellEdid());
        Assert.True(result.BaseValid);
        Assert.Empty(result.Issues);
        Assert.Contains(result.Timings, m => m.Encoding == "Established I/II" && m.Offset == 35
            && m.Width == 640 && m.Height == 480 && m.DmtCode == 0x04);
        var dtd = Assert.Single(result.Timings.Where(m => m.Encoding == "Base DTD"));
        Assert.Equal((1920, 1200), (dtd.Width, dtd.Height));
        Assert.Equal(0x44, dtd.DmtCode);
        Assert.Contains("完整時序比對", dtd.MatchBasis);
        Assert.Equal((154000, 1968, 2000, 2080, 1203, 1209, 1235),
            (dtd.Detailed!.ClockKhz, dtd.Detailed.HSyncStart, dtd.Detailed.HSyncEnd, dtd.Detailed.HTotal,
                dtd.Detailed.VSyncStart, dtd.Detailed.VSyncEnd, dtd.Detailed.VTotal));
        Assert.Equal((518, 324), (dtd.Detailed.ImageWidthMm, dtd.Detailed.ImageHeightMm));
    }

    [Fact]
    public void InvalidBaseChecksumStopsDecoding()
    {
        byte[] edid = DellEdid();
        edid[20] ^= 1;
        var result = EdidTimings.Parse(edid);
        Assert.False(result.BaseValid);
        Assert.Empty(result.Timings);
        Assert.Contains("checksum", result.Issues[0]);
    }

    [Fact]
    public void CtaDtdIsDecodedAndMatchedSeparately()
    {
        byte[] baseEdid = DellEdid();
        var edid = new byte[256];
        Buffer.BlockCopy(baseEdid, 0, edid, 0, 128);
        edid[126] = 1;
        SetChecksum(edid, 0);
        edid[128] = 2;
        edid[129] = 3;
        edid[130] = 4;
        Buffer.BlockCopy(baseEdid, 54, edid, 132, 18);
        SetChecksum(edid, 128);

        var result = EdidTimings.Parse(edid);
        Assert.True(result.BaseValid);
        Assert.Empty(result.Issues);
        var cta = Assert.Single(result.Timings.Where(m => m.Encoding == "CTA DTD"));
        Assert.Equal(0x44, cta.DmtCode);
        Assert.Equal(1920, cta.Width);
    }

    [Fact]
    public void FactsRetainDeclaredTimingAndReportRegistryScope()
    {
        var facts = EdidTimingFactsService.Collect(
            new EdidReadSnapshot([("Dell U2412M", DellEdid())], []), DateTimeOffset.UnixEpoch);
        var fact = Assert.Single(facts);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("DMT 0x44", fact.Value);
        Assert.Contains("Standard 的 DMT 僅為幾何候選", fact.Value);
    }

    [Fact]
    public void ReadErrorsAreNotReportedAsNoDisplays()
    {
        var facts = EdidTimingFactsService.Collect(new EdidReadSnapshot([], ["存取被拒"]), DateTimeOffset.UnixEpoch);
        var fact = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("存取被拒", fact.UnavailableReason);
        Assert.Null(fact.NumericValue);
    }

    private static void SetChecksum(byte[] edid, int start)
    {
        int sum = 0;
        for (int i = 0; i < 127; i++) sum += edid[start + i];
        edid[start + 127] = (byte)(-sum);
    }
}
