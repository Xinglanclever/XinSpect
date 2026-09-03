using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Resizable BAR 的判讀（純函式）。
///
/// <para>
/// 這一份守的核心是一個很容易寫錯的判斷：<b>不能拿覆蓋率去打分數</b>。BAR 尺寸一律是 2 的次方，
/// 所以 12 GB 顯示記憶體的卡最大只拿得到 8 GB 的視窗（覆蓋率 67%）——那是規格使然，
/// 不是設定沒開好。用「覆蓋率要接近 100% 才算生效」這種規則，會把一台正常的機器判成有問題。
/// </para>
/// </summary>
public class ResizableBarDecoderTests
{
    private const ulong Mb = 1024UL * 1024;
    private const ulong Gb = 1024UL * Mb;

    // ── 大小換算 ────────────────────────────────────────────────

    [Theory]
    [InlineData(0UL, "—")]
    [InlineData(256UL * 1024 * 1024, "256 MB")]
    [InlineData(8UL * 1024 * 1024 * 1024, "8 GB")]
    [InlineData(12UL * 1024 * 1024 * 1024, "12 GB")]
    public void 大小以1024進位換算(ulong bytes, string expected)
        => Assert.Equal(expected, ResizableBarDecoder.SizeText(bytes));

    [Fact]
    public void 視窗大小由起訖位址算出且兩端都含在內()
    {
        // 0x0 – 0xFFFFFFF 是 256 MB（不是 256 MB − 1）
        Assert.Equal(256 * Mb, new BarRange(0, 0x0FFF_FFFF).Bytes);
        Assert.Equal(8 * Gb, new BarRange(0x40_0000_0000, 0x41_FFFF_FFFF).Bytes);
        // 反向或空範圍不能算成天文數字
        Assert.Equal(0UL, new BarRange(0x2000, 0x1000).Bytes);
    }

    // ── 判定 ────────────────────────────────────────────────────

    [Fact]
    public void 停在傳統孔徑就是未生效()
    {
        foreach (ulong size in new[] { 256 * Mb, 512 * Mb })
        {
            var v = ResizableBarDecoder.Verdict(size, 8 * Gb);
            Assert.Equal("未生效", v.Text);
            Assert.Equal(Severity.Warning, v.Severity);
            // 要說得出可以去檢查什麼，不然使用者知道沒生效也沒用
            Assert.Contains("BIOS", v.Detail);
            Assert.Contains("UEFI", v.Detail);
        }
    }

    [Fact]
    public void 明顯大於傳統孔徑就是已撐開()
    {
        var v = ResizableBarDecoder.Verdict(8 * Gb, 8 * Gb);
        Assert.Equal("已撐開", v.Text);
        Assert.Equal(Severity.Good, v.Severity);
    }

    [Fact]
    public void 二的次方造成的不完全覆蓋不算未生效()
    {
        // 12 GB 的卡拿到 8 GB 視窗：覆蓋率 67%，但這是規格上限，必須判成已撐開
        var v = ResizableBarDecoder.Verdict(8 * Gb, 12 * Gb);
        Assert.Equal("已撐開", v.Text);
        Assert.Equal(Severity.Good, v.Severity);
        Assert.Contains("2 的次方", v.Detail);
    }

    [Fact]
    public void 讀不到視窗就說讀不到而不猜()
    {
        var v = ResizableBarDecoder.Verdict(0, 8 * Gb);
        Assert.Equal("讀不到記憶體視窗", v.Text);
        Assert.Equal(Severity.Neutral, v.Severity);
    }

    [Fact]
    public void 讀不到顯示記憶體總量時仍能判定撐開與否但不提覆蓋率()
    {
        var open = ResizableBarDecoder.Verdict(8 * Gb, 0);
        Assert.Equal("已撐開", open.Text);
        Assert.Contains("讀不到顯示記憶體總量", open.Detail);

        // 視窗大小本身就足以判定「沒撐開」，不需要知道顯示記憶體有多大
        Assert.Equal("未生效", ResizableBarDecoder.Verdict(256 * Mb, 0).Text);
    }

    // ── 列的衍生欄位 ────────────────────────────────────────────

    private static BarDeviceRow Row(ulong vram, params (ulong Base, ulong End)[] ranges) => new()
    {
        Name = "測試顯示卡",
        Location = "PCI bus 1, device 0, function 0",
        Ranges = [.. ranges.Select(r => new BarRange(r.Base, r.End))],
        VramBytes = vram,
        IsPci = true,
    };

    [Fact]
    public void 最大視窗取所有已指派範圍裡最大的那一段()
    {
        var row = Row(8 * Gb,
            (0xF000_0000, 0xF0FF_FFFF),                 // 16 MB
            (0x40_0000_0000, 0x41_FFFF_FFFF),           // 8 GB
            (0xF100_0000, 0xF11F_FFFF));                // 2 MB
        Assert.Equal(8 * Gb, row.LargestBytes);
        Assert.Equal("8 GB", row.LargestText);
        Assert.Equal("已撐開", row.Verdict);
        Assert.Equal("3 段", row.RangeCountText);
    }

    [Fact]
    public void 沒有任何已指派範圍時每個欄位都寫破折號而不是零()
    {
        var row = Row(0);
        Assert.Equal("—", row.LargestText);
        Assert.Equal("—", row.VramText);
        Assert.Equal("—", row.CoverageText);
        Assert.Equal("讀不到記憶體視窗", row.Verdict);
    }

    [Fact]
    public void 覆蓋率要算得出來且以百分比呈現()
    {
        var row = Row(12 * Gb, (0x40_0000_0000, 0x41_FFFF_FFFF));   // 8 GB ÷ 12 GB
        Assert.Equal("66.7 %", row.CoverageText);
    }

    /// <summary>虛擬顯示裝置：在顯示卡類別裡，但不是 PCI 裝置、沒有 BAR 也沒有顯示記憶體。</summary>
    private static BarDeviceRow Virtual() => new()
    {
        Name = "某模擬器的虛擬顯示卡",
        Location = "—",
        Ranges = [],
        VramBytes = 0,
        IsPci = false,
    };

    [Fact]
    public void 虛擬顯示裝置判為不適用而不是讀不到()
    {
        // 這是實機驗證抓到的問題：模擬器與串流軟體會裝虛擬顯示卡，它們也在顯示卡類別裡，
        // 但沒有 BAR。報「讀不到記憶體視窗」是把正常情形講成疑似故障。
        var row = Virtual();
        Assert.Equal("不適用", row.Verdict);
        Assert.Equal(Severity.Neutral, row.Severity);
        Assert.Contains("虛擬顯示裝置", row.Detail);
        Assert.DoesNotContain("BIOS", row.Detail);
    }

    // ── 摘要 ────────────────────────────────────────────────────

    [Fact]
    public void 沒有顯示卡時明說而不是留白()
    {
        string s = ResizableBarDecoder.Summarize([]);
        Assert.Contains("沒有列出任何顯示卡", s);
    }

    [Fact]
    public void 摘要要分開數已撐開與仍是傳統孔徑()
    {
        var rows = new List<BarDeviceRow>
        {
            Row(8 * Gb, (0x40_0000_0000, 0x41_FFFF_FFFF)),   // 已撐開
            Row(4 * Gb, (0xF000_0000, 0xFFFF_FFFF)),         // 256 MB → 未生效
            Row(0),                                          // 讀不到
            Virtual(),                                       // 虛擬顯示裝置 → 不適用
        };
        string s = ResizableBarDecoder.Summarize(rows);
        Assert.Contains("共 4 個顯示裝置", s);
        Assert.Contains("1 張已撐開", s);
        Assert.Contains("1 張仍是傳統孔徑", s);
        Assert.Contains("1 張讀不到視窗", s);
        Assert.Contains("1 個虛擬顯示裝置", s);
        // 要說清楚這是實際生效的視窗，不是能力宣稱值
        Assert.Contains("已指派資源", s);
    }
}
