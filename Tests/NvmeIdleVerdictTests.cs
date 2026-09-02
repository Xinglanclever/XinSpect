using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「閒置之後第一筆讀取為什麼慢」的歸因。
///
/// 宣告值（電源狀態的離開延遲）與實測值（閒置 N 毫秒後單筆 4K 讀取的耗時）擺在一起才有意義：
/// 兩者相符才說得出「這是省電狀態爬回來的成本」；差得遠就必須承認歸因不成立，而不是硬套一個解釋。
/// </summary>
public class NvmeIdleVerdictTests
{
    /// <summary>造一顆「有一個深睡狀態、離開要 8 ms」的碟。</summary>
    private static List<NvmePowerStateRow> Drive(uint deepExitUs = 8_000) =>
    [
        new() { State = 0, NonOperational = false, MaxPowerW = 9, RelRead = 0, RelReadLatency = 0, RelWrite = 0, RelWriteLatency = 0 },
        new() { State = 1, NonOperational = false, MaxPowerW = 4, RelRead = 1, RelReadLatency = 1, RelWrite = 1, RelWriteLatency = 1 },
        new()
        {
            State = 3, NonOperational = true, MaxPowerW = 0.05,
            EntryLatencyUs = 5_000, ExitLatencyUs = deepExitUs,
            RelRead = 0, RelReadLatency = 0, RelWrite = 0, RelWriteLatency = 0,
        },
    ];

    [Fact]
    public void 沒有量測資料時不下判決()
    {
        var v = NvmePowerDecoder.Verdict(Drive(), apstSupported: true, []);
        Assert.Contains("尚未量測", v.Headline);
        Assert.Equal(0, v.Severity);
    }

    [Fact]
    public void 閒置後變慢的幅度與宣告的離開延遲相符時歸因於電源狀態()
    {
        var samples = new List<IdleLatencySample>
        {
            new(0, 120),        // 基線：連續讀取
            new(50, 130),
            new(500, 7_900),    // 約等於宣告的 8 ms
            new(2000, 8_200),
        };
        var v = NvmePowerDecoder.Verdict(Drive(), true, samples);

        Assert.Contains("電源狀態", v.Headline);
        Assert.Contains("PS3", v.Detail);
        Assert.True(v.Severity >= 1);
    }

    [Fact]
    public void 變慢幅度遠小於宣告值時說沒有真的進到那個狀態()
    {
        var samples = new List<IdleLatencySample>
        {
            new(0, 120),
            new(2000, 1_200),   // 多出約 1.1 ms：確實有停頓，但遠小於宣告的 8 ms
        };
        var v = NvmePowerDecoder.Verdict(Drive(), true, samples);

        Assert.Contains("沒有", v.Headline);
        Assert.Contains("8", v.Detail);      // 要把宣告值講出來，讀者才知道在跟什麼比
    }

    [Fact]
    public void 完全沒有變慢就說沒有觀察到()
    {
        var samples = new List<IdleLatencySample> { new(0, 120), new(1000, 118) };
        var v = NvmePowerDecoder.Verdict(Drive(), true, samples);

        Assert.Contains("沒有", v.Headline);
        Assert.Equal(0, v.Severity);
    }

    [Fact]
    public void 沒有任何非運作狀態宣告離開延遲時不歸因()
    {
        var states = new List<NvmePowerStateRow>
        {
            new() { State = 0, NonOperational = false, MaxPowerW = 9, RelRead = 0, RelReadLatency = 0, RelWrite = 0, RelWriteLatency = 0 },
        };
        var samples = new List<IdleLatencySample> { new(0, 120), new(2000, 9_000) };
        var v = NvmePowerDecoder.Verdict(states, apstSupported: false, samples);

        Assert.Contains("無法歸因", v.Headline);
        Assert.DoesNotMatch(@"PS\d", v.Detail);   // 沒有可歸因的狀態就不要編一個編號出來
    }

    [Fact]
    public void 變慢幅度遠大於宣告值時不硬套電源狀態()
    {
        var samples = new List<IdleLatencySample> { new(0, 120), new(2000, 120_000) };  // 120 ms，是宣告的十五倍
        var v = NvmePowerDecoder.Verdict(Drive(), true, samples);

        Assert.Contains("超出", v.Headline);
        Assert.Contains("8", v.Detail);
    }

    [Fact]
    public void APST不支援時要說降態可能由主機端決定()
    {
        var samples = new List<IdleLatencySample> { new(0, 120), new(2000, 7_900) };
        var v = NvmePowerDecoder.Verdict(Drive(), apstSupported: false, samples);
        Assert.Contains("主機", v.Detail);
    }

    [Fact]
    public void 基線取閒置最短的那一筆而不是第一筆()
    {
        // 樣本順序打亂：基線必須是 idle 最短的 50 ms 那筆（130 µs），不是清單第一筆
        var samples = new List<IdleLatencySample> { new(2000, 8_100), new(50, 130), new(500, 7_800) };
        var v = NvmePowerDecoder.Verdict(Drive(), true, samples);
        Assert.Contains("電源狀態", v.Headline);
    }
}
