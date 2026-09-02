using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// L3 未命中與 DRAM 實際流量的換算與自我驗證。
///
/// 這一份守的是「數字要嘛通得過自我驗證，要嘛只給原始計數」——計數器的事件編碼若在某個平台上
/// 不是我們以為的意思，換算出來的 GB/s 會是個看起來完全合理的假數字。與其相信規格書，
/// 不如先跑一段<b>已知會讀多少位元組</b>的負載，對不上就不換算。
/// </summary>
public class DramTrafficDecoderTests
{
    [Fact]
    public void 未命中次數乘以快取行大小得到位元組()
    {
        // 一百萬次未命中 × 64 位元組 = 64 MB
        Assert.Equal(64L * 1024 * 1024, DramTrafficDecoder.Bytes(1_048_576));
    }

    [Fact]
    public void 頻寬由位元組與秒數算出()
    {
        double gbps = DramTrafficDecoder.GbPerSec(1_073_741_824, 1.0);
        Assert.Equal(1.0, gbps, 3);
        Assert.Equal(0, DramTrafficDecoder.GbPerSec(1_000_000, 0));   // 沒有時間就不編一個速率
    }

    [Fact]
    public void 命中率由參照與未命中算出_參照為零時不算()
    {
        Assert.Equal(75.0, DramTrafficDecoder.HitPercent(references: 400, misses: 100)!.Value, 3);
        Assert.Null(DramTrafficDecoder.HitPercent(0, 0));
        // 未命中比參照還多（計數器溢位或事件不對）：不硬算出負數命中率
        Assert.Null(DramTrafficDecoder.HitPercent(100, 400));
    }

    // ── 自我驗證 ──────────────────────────────────────────────────────────

    [Fact]
    public void 量到的位元組與已知負載相符時通過驗證()
    {
        // 讀了 512 MB，計數器算出 520 MB：差 1.5%，在容許範圍內
        var v = DramTrafficDecoder.Validate(expectedBytes: 512L * 1024 * 1024, countedBytes: 520L * 1024 * 1024);
        Assert.True(v.Passed);
        Assert.Contains("通過", v.Text);
    }

    [Fact]
    public void 量到的位元組遠少於已知負載時不通過()
    {
        var v = DramTrafficDecoder.Validate(512L * 1024 * 1024, 8L * 1024 * 1024);
        Assert.False(v.Passed);
        Assert.Contains("只有", v.Text);
    }

    [Fact]
    public void 量到的位元組遠多於已知負載時也不通過()
    {
        // 多出一倍以上：事件可能把別的東西也算進來了，不能當成 DRAM 流量
        var v = DramTrafficDecoder.Validate(512L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
        Assert.False(v.Passed);
        Assert.Contains("多", v.Text);
    }

    [Fact]
    public void 完全沒有計數時不通過且要說計數器沒動()
    {
        var v = DramTrafficDecoder.Validate(512L * 1024 * 1024, 0);
        Assert.False(v.Passed);
        Assert.Contains("沒有前進", v.Text);
    }

    [Fact]
    public void 沒通過驗證時的呈現只給原始計數不給頻寬()
    {
        string s = DramTrafficDecoder.TrafficText(misses: 1_000_000, seconds: 1.0, validated: false);
        Assert.Contains("1,000,000", s);
        Assert.DoesNotContain("GB/s", s);
    }

    [Fact]
    public void 通過驗證時才換算成頻寬()
    {
        string s = DramTrafficDecoder.TrafficText(1_048_576, 1.0, validated: true);
        Assert.Contains("GB/s", s);
    }
}
