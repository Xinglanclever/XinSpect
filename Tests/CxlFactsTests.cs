using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP19 CXL：ACPI CEDT 表的 CFMWS（CXL Fixed Memory Window）解碼契約。
/// 佈局依 CXL 規格手算（記錄頭 8 bytes＋BaseHPA@16＋WindowSize@24＋Ways@32，記錄長度走 RecordLength）；
/// 本機無 CEDT＝NotApplicable（無此硬體），不是錯誤。
/// </summary>
public class CxlFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>手算 CEDT：36 bytes 表頭＋一筆 CFMWS（RecordLength=40）。</summary>
    private static byte[] Cedt(ulong baseHpa, ulong windowSize, uint ways)
    {
        var cedt = new byte[36 + 40];
        byte[] sig = [(byte)'C', (byte)'E', (byte)'D', (byte)'T'];
        sig.CopyTo(cedt, 0);
        cedt[4] = (byte)(36 + 40);
        cedt[36] = 0;                     // Type 0＝CFMWS
        cedt[38] = 40;                    // Record Length
        cedt[43] = 1;                     // Hw Supp Ver
        for (int i = 0; i < 8; i++) cedt[36 + 16 + i] = (byte)(baseHpa >> (8 * i));
        for (int i = 0; i < 8; i++) cedt[36 + 24 + i] = (byte)(windowSize >> (8 * i));
        cedt[36 + 32] = (byte)ways;
        cedt[9] = (byte)((256 - cedt.Aggregate(0, (acc, b) => acc + b) % 256) % 256); // 校驗和
        return cedt;
    }

    private sealed class FakeAcpi : IAcpiTableSource
    {
        private readonly byte[][] _tables;
        public FakeAcpi(params byte[][] tables) { _tables = tables; }
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => _tables;
    }

    [Fact]
    public void CEDT解碼_CFMWS逐欄位釘值()
    {
        var windows = XinSpect.CedtDecoder.DecodeCfmws(Cedt(0x1000_0000_0000, 0x40_0000_0000, 1));
        var w = Assert.Single(windows);
        Assert.Equal(0x1000_0000_0000ul, w.BaseHpa);
        Assert.Equal(0x40_0000_0000ul, w.WindowSize);
        Assert.Equal(1u, w.InterleaveWays);
        Assert.Equal(1u, w.HwSuppVer);
    }

    [Fact]
    public void CEDT解碼_過短與壞記錄長度不越界()
    {
        Assert.Empty(XinSpect.CedtDecoder.DecodeCfmws(new byte[38]));           // 只有表頭
        var badLen = new byte[36 + 8];
        badLen[38] = 3;                                                          // RecordLength < 4
        Assert.Empty(XinSpect.CedtDecoder.DecodeCfmws(badLen));
    }

    [Fact]
    public void CXL事實_有CEDT解窗口_無CEDT標不適用()
    {
        var present = XinSpect.CxlFactsService.Collect(At, new FakeAcpi(Cedt(0x1000_0000_0000, 0x40_0000_0000, 1)));
        var w = Assert.Single(present, f => f.Key == "cxl.cfmws.0");
        Assert.Equal(FactAvailability.Present, w.Availability);
        Assert.Contains("0x100000000000", w.Value);
        Assert.Contains("256 GiB", w.Value);

        var absent = XinSpect.CxlFactsService.Collect(At, new FakeAcpi());
        var na = Assert.Single(absent, f => f.Key == "cxl.cfmws.count");
        Assert.Equal(FactAvailability.NotApplicable, na.Availability);
        Assert.Contains("沒有 CEDT", na.UnavailableReason);
    }
}
