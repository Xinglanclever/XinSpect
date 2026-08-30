using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 電源政策解碼測試。最重要的一條是 CurrentMhz 必須明說它不是實際時脈——
/// 本機 CallNtPowerInformation 恆回 2601 MHz，而 MPERF/APERF 實測有效時脈是 4186 MHz。
/// </summary>
public class PowerPolicyTests
{
    [Fact]
    public void 目前頻率_必須明說不是核心實際時脈並導向頻率真相卡片()
    {
        string s = PowerPolicyDecoder.DescribeCurrentMhz(2601, 2601, 0);
        Assert.Contains("2,601 MHz", s);
        Assert.Contains("不是核心實際時脈", s);
        Assert.Contains("頻率真相", s);
        Assert.Contains("⚠", s);
    }

    [Fact]
    public void 目前頻率_政策限頻時列出上限與百分比()
    {
        string s = PowerPolicyDecoder.DescribeCurrentMhz(1300, 2600, 1300);
        Assert.Contains("政策上限 1,300 MHz", s);
        Assert.Contains("50%", s);
    }

    [Fact]
    public void 目前頻率_上限與標稱相同時不謊稱有限頻()
    {
        Assert.DoesNotContain("政策上限", PowerPolicyDecoder.DescribeCurrentMhz(2601, 2601, 2601));
    }

    [Fact]
    public void 目前頻率_作業系統沒回報時不猜()
    {
        Assert.StartsWith("—", PowerPolicyDecoder.DescribeCurrentMhz(0, 0, 0));
    }

    [Fact]
    public void 逐核彙總_本機實測三十六顆全部一致且未被限頻()
    {
        var cores = new List<ProcessorPowerSample>();
        for (uint i = 0; i < 36; i++) cores.Add(new ProcessorPowerSample(i, 2601, 2601, 2601, 3, 0));

        string s = PowerPolicyDecoder.SummarizeProcessors(cores);
        Assert.Contains("36 顆", s);
        Assert.Contains("標稱上限一致", s);
        Assert.Contains("回報頻率一致", s);
        Assert.Contains("沒有任何核心被政策限頻", s);
        Assert.Contains("C3", s);
        Assert.DoesNotContain("⚠", s);
    }

    [Fact]
    public void 逐核彙總_有核心被限頻時明確警示顆數()
    {
        var cores = new List<ProcessorPowerSample>
        {
            new(0, 2601, 2601, 2601, 3, 0),
            new(1, 2601, 1300, 1300, 3, 0),
            new(2, 2601, 1300, 1300, 3, 0),
        };
        string s = PowerPolicyDecoder.SummarizeProcessors(cores);
        Assert.Contains("⚠ 2 顆被政策限頻", s);
        Assert.Contains("回報頻率分歧", s);
    }

    [Fact]
    public void 逐核彙總_標稱上限不一致時警示混插或大小核()
    {
        var cores = new List<ProcessorPowerSample>
        {
            new(0, 3200, 3200, 3200, 3, 0),
            new(1, 2400, 2400, 2400, 3, 0),
        };
        Assert.Contains("⚠ 標稱上限不一致", PowerPolicyDecoder.SummarizeProcessors(cores));
    }

    [Fact]
    public void 逐核彙總_沒有資料時照實說而不是回零顆()
    {
        Assert.StartsWith("—", PowerPolicyDecoder.SummarizeProcessors([]));
    }

    [Fact]
    public void 核心停放_一百趴表示不停放()
    {
        var r = PowerPolicyDecoder.DescribeCoreParking("核心停放：最多可用核心", 100);
        Assert.Equal("100%", r.Value);
        Assert.Contains("不停放", r.Note);
        Assert.Equal(0, r.Severity);
    }

    [Fact]
    public void 核心停放_低於一百趴時說明對延遲敏感工作的影響()
    {
        var r = PowerPolicyDecoder.DescribeCoreParking("核心停放：最少可用核心", 25);
        Assert.Equal("25%", r.Value);
        Assert.Contains("間歇卡頓", r.Note);
        Assert.Equal(1, r.Severity);
    }

    [Fact]
    public void 核心停放_讀不到時顯示破折號不用預設值頂替()
    {
        var r = PowerPolicyDecoder.DescribeCoreParking("核心停放", null);
        Assert.Equal("—", r.Value);
        Assert.Equal(0, r.Severity);
    }

    [Fact]
    public void ASPM_三個官方值各有說明最大節能標為值得注意()
    {
        Assert.Equal("關閉", PowerPolicyDecoder.DescribeAspm(0).Value);
        Assert.Equal("中度節能", PowerPolicyDecoder.DescribeAspm(1).Value);

        var max = PowerPolicyDecoder.DescribeAspm(2);
        Assert.Equal("最大節能", max.Value);
        Assert.Contains("NVMe", max.Note);
        Assert.Equal(1, max.Severity);
    }

    [Fact]
    public void ASPM_非官方值原樣呈現不硬翻譯()
    {
        var r = PowerPolicyDecoder.DescribeAspm(7);
        Assert.Equal("0x7", r.Value);
        Assert.Contains("不翻譯", r.Note);
    }

    [Fact]
    public void USB選擇性暫停_啟用時說明可能造成音訊介面斷連()
    {
        var on = PowerPolicyDecoder.DescribeUsbSuspend(1);
        Assert.Equal("啟用", on.Value);
        Assert.Contains("音訊", on.Note);
        Assert.Equal(1, on.Severity);

        Assert.Equal("停用", PowerPolicyDecoder.DescribeUsbSuspend(0).Value);
        Assert.Equal("—", PowerPolicyDecoder.DescribeUsbSuspend(null).Value);
    }

    [Fact]
    public void Turbo政策_官方零到五逐項命名關閉時標為值得注意()
    {
        var off = PowerPolicyDecoder.DescribeBoostMode(0);
        Assert.Contains("停用", off.Value);
        Assert.Equal(1, off.Severity);

        Assert.Contains("積極", PowerPolicyDecoder.DescribeBoostMode(2).Value);
        Assert.Contains("有效率地積極", PowerPolicyDecoder.DescribeBoostMode(4).Value);
        Assert.Equal(0, PowerPolicyDecoder.DescribeBoostMode(1).Severity);
    }

    [Fact]
    public void Turbo政策_超出官方範圍時不翻譯()
    {
        var r = PowerPolicyDecoder.DescribeBoostMode(9);
        Assert.Equal("0x9", r.Value);
        Assert.Contains("不翻譯", r.Note);
    }

    [Fact]
    public void 處理器狀態範圍_最小等於最大即為鎖頻()
    {
        var r = PowerPolicyDecoder.DescribeProcessorStateRange(50, 50);
        Assert.Equal("50% – 50%", r.Value);
        Assert.Contains("鎖在標稱的 50%", r.Note);
        Assert.Equal(1, r.Severity);
    }

    [Fact]
    public void 處理器狀態範圍_最小一百趴時說明永不降頻()
    {
        var r = PowerPolicyDecoder.DescribeProcessorStateRange(100, 100);
        Assert.Contains("鎖", r.Note);   // 100/100 同時滿足鎖頻，鎖頻先判定
        Assert.Equal(1, r.Severity);

        var r2 = PowerPolicyDecoder.DescribeProcessorStateRange(100, 110);
        Assert.Contains("永不降頻", r2.Note);
    }

    [Fact]
    public void 處理器狀態範圍_正常區間不加警示()
    {
        var r = PowerPolicyDecoder.DescribeProcessorStateRange(5, 100);
        Assert.Equal("5% – 100%", r.Value);
        Assert.Equal(0, r.Severity);
    }

    [Fact]
    public void 處理器狀態範圍_缺值時不做推論()
    {
        Assert.Equal("—", PowerPolicyDecoder.DescribeProcessorStateRange(null, 100).Value);
        Assert.Equal("—", PowerPolicyDecoder.DescribeProcessorStateRange(5, null).Value);
    }

    [Fact]
    public void 睡眠矩陣_八列且不支援S3時解釋現代待命取代之()
    {
        var rows = PowerPolicyDecoder.DescribeSleepStates(
            s1: false, s2: false, s3: false, s4: true, s5: true,
            hiberFile: true, fastS4: true, aoac: false);

        Assert.Equal(8, rows.Count);
        Assert.Contains("現代待命", rows[2].Note);
        Assert.Equal("不支援", rows[2].Value);
        Assert.Equal("支援", rows[3].Value);
        Assert.Equal("存在", rows[5].Value);
    }

    [Fact]
    public void 睡眠矩陣_沒有休眠檔時說明快速啟動也不能用()
    {
        var rows = PowerPolicyDecoder.DescribeSleepStates(
            s1: false, s2: false, s3: true, s4: false, s5: true,
            hiberFile: false, fastS4: false, aoac: false);

        Assert.Equal("不存在", rows[5].Value);
        Assert.Contains("快速啟動都無法使用", rows[5].Note);
        Assert.Equal("", rows[2].Note);   // S3 支援時不需要解釋
    }

    [Fact]
    public void 睡眠矩陣_現代待命支援時說明睡眠期間背景仍會執行()
    {
        var rows = PowerPolicyDecoder.DescribeSleepStates(
            s1: false, s2: false, s3: false, s4: true, s5: true,
            hiberFile: true, fastS4: true, aoac: true);

        Assert.Equal("支援", rows[7].Value);
        Assert.Contains("S0ix", rows[7].Note);
    }

    [Fact]
    public void 界線說明_必須說明唯讀且區分風險等級不同於刷韌體()
    {
        Assert.Contains("唯讀", PowerPolicyDecoder.ScopeNotice);
        Assert.Contains("不會像刷韌體那樣把機器弄壞", PowerPolicyDecoder.ScopeNotice);
        Assert.Contains("場景設定檔", PowerPolicyDecoder.ScopeNotice);
    }
}
