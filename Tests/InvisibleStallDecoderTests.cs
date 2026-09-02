using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 隱形停頓：SMI 次數與 C-state 駐留。
///
/// 這一份守兩件事。其一，<b>SMI 只數得出次數，數不出時間</b>——系統管理中斷發生在作業系統
/// 完全看不見的模式裡，沒有任何暫存器記錄它待了多久。乘上一個「每次大概幾微秒」就是編造，
/// 所以判決只能談頻率，不能談損失了多少毫秒。其二，駐留計數器是以 TSC 為刻度累加的，
/// 百分比一定要除以同一段的 TSC 差值；除錯了會得到超過 100% 這種不可能的數字。
/// </summary>
public class InvisibleStallDecoderTests
{
    /// <summary>1 GHz 的 TSC 跑一秒＝十億個刻度，方便手算。</summary>
    private const ulong OneSecond = 1_000_000_000;

    [Fact]
    public void 駐留百分比是駐留刻度除以同一段的TSC刻度()
    {
        Assert.Equal(30.0, InvisibleStallDecoder.Percent(300_000_000, OneSecond)!.Value, 6);
        Assert.Equal(0.0, InvisibleStallDecoder.Percent(0, OneSecond)!.Value, 6);
    }

    [Fact]
    public void TSC沒有前進時算不出百分比()
    {
        Assert.Null(InvisibleStallDecoder.Percent(1234, 0));
    }

    [Fact]
    public void 駐留計數器沒有前進時視為不支援而不是零駐留()
    {
        // 差值為 0 有兩種可能：真的完全沒進過那個狀態，或平台沒實作這個 MSR。
        // 兩者都顯示 0% 會誤導，所以「讀不到」與「讀到 0」必須分開表示。
        Assert.Equal("未實作或未開放", InvisibleStallDecoder.ResidencyText(null, OneSecond));
        Assert.Equal("0.00%", InvisibleStallDecoder.ResidencyText(0, OneSecond));
        Assert.Equal("12.35%", InvisibleStallDecoder.ResidencyText(123_456_789, OneSecond));
    }

    [Fact]
    public void 百分比超過一百時要夾住並如實標記()
    {
        // 計數器回捲或跨核讀到不同封裝時會出現這種值；不能就這樣印出 137%
        string t = InvisibleStallDecoder.ResidencyText(1_370_000_000, OneSecond);
        Assert.Contains("100", t);
        Assert.Contains("上限", t);
    }

    // ── SMI ───────────────────────────────────────────────────────────────

    [Fact]
    public void 完全沒有SMI時明說沒有並且不談時間()
    {
        var v = InvisibleStallDecoder.Judge(smiDelta: 0, seconds: 1.0, smiTotal: 0, deepestPackagePercent: 40);
        Assert.Contains("沒有", v.Headline);
        Assert.Equal(Severity.Good, v.Severity);
        Assert.DoesNotContain("毫秒", v.Detail);
        Assert.DoesNotContain("µs", v.Detail);
    }

    [Fact]
    public void 有少量SMI時說出速率但不換算成損失時間()
    {
        var v = InvisibleStallDecoder.Judge(smiDelta: 3, seconds: 1.0, smiTotal: 12_345, deepestPackagePercent: 40);
        Assert.Contains("3", v.Detail);
        Assert.Contains("12,345", v.Detail);
        // 關鍵：不准把次數乘上一個猜出來的單次耗時
        Assert.DoesNotContain("估計", v.Detail);
        Assert.DoesNotContain("約損失", v.Detail);
    }

    [Fact]
    public void SMI頻繁時要點名它是看不見的時間並列出常見來源()
    {
        var v = InvisibleStallDecoder.Judge(smiDelta: 240, seconds: 1.0, smiTotal: 500_000, deepestPackagePercent: 10);
        Assert.Equal(Severity.Serious, v.Severity);
        Assert.Contains("韌體", v.Detail);
        Assert.Contains("量不到", v.Detail);   // 必須明說「待了多久量不到」
    }

    [Fact]
    public void 封裝完全沒有進入深層省電時要指出有東西把它叫著()
    {
        var v = InvisibleStallDecoder.Judge(smiDelta: 0, seconds: 1.0, smiTotal: 0, deepestPackagePercent: 0);
        Assert.Contains("沒有進入", v.Detail);
        Assert.Equal(Severity.Warning, v.Severity);
    }

    [Fact]
    public void 駐留讀不到時不對省電狀態下結論()
    {
        var v = InvisibleStallDecoder.Judge(smiDelta: 0, seconds: 1.0, smiTotal: 0, deepestPackagePercent: null);
        Assert.Contains("讀不到", v.Detail);
        Assert.DoesNotContain("沒有進入", v.Detail);
    }

    [Fact]
    public void 取樣視窗為零時不下判決()
    {
        var v = InvisibleStallDecoder.Judge(0, 0, 0, 40);
        Assert.Contains("尚未量測", v.Headline);
        Assert.Equal(Severity.Neutral, v.Severity);
    }
}
