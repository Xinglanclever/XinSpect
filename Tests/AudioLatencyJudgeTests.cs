using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 音訊端點緩衝區的判讀（純函式）。核心要釘住三件事：
/// ①獨占模式配到的緩衝區才是延遲下限，共用模式那個值由引擎決定、所有應用程式共享；
/// ②要求值被裝置夾住是常態，不能講成「應用程式選了這麼大」；
/// ③同一個數字在「看影片」與「邊聽邊彈」兩種用途下的意義完全不同。
/// </summary>
public class AudioLatencyJudgeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static AudioEndpointSample Sample(
        string name = "Realtek Digital Output", bool def = false,
        int rate = 48000, int bits = 32, int ch = 2,
        double exMs = 21.3, int frames = 1024, double reqMs = 1.0, double shMs = 22.0,
        string err = "", bool soft = false)
        => new(name, def, rate, bits, ch, exMs, frames, reqMs, shMs, err, soft);

    // ── 延遲等級：已知答案 ────────────────────────────────────────────────

    [Theory]
    [InlineData(3.0, AudioLatencyJudge.LatencyTier.Realtime)]
    [InlineData(9.9, AudioLatencyJudge.LatencyTier.Realtime)]
    [InlineData(10.0, AudioLatencyJudge.LatencyTier.LowLatency)]
    [InlineData(19.9, AudioLatencyJudge.LatencyTier.LowLatency)]
    [InlineData(20.0, AudioLatencyJudge.LatencyTier.Standard)]
    [InlineData(39.9, AudioLatencyJudge.LatencyTier.Standard)]
    [InlineData(40.0, AudioLatencyJudge.LatencyTier.PlaybackOnly)]
    [InlineData(80.0, AudioLatencyJudge.LatencyTier.PlaybackOnly)]
    public void 延遲等級_門檻邊界要對得上(double ms, AudioLatencyJudge.LatencyTier expected)
        => Assert.Equal(expected, AudioLatencyJudge.Judge(Sample(exMs: ms)).Tier);

    [Fact]
    public void 可做即時監聽_要說明對演奏的意義()
    {
        var v = AudioLatencyJudge.Judge(Sample(exMs: 5.3));

        Assert.Equal(AudioLatencyJudge.LatencyTier.Realtime, v.Tier);
        Assert.Contains("即時監聽", v.Headline);
        Assert.Contains("演奏", v.Evidence);
    }

    [Fact]
    public void 只適合播放_要說明大緩衝不是故障()
    {
        // 軟體端點與無線裝置本來就用大緩衝換穩定，講成故障是誤導
        var v = AudioLatencyJudge.Judge(Sample(exMs: 80.0, soft: true));

        Assert.Equal(AudioLatencyJudge.LatencyTier.PlaybackOnly, v.Tier);
        Assert.Contains("不是故障", v.Evidence);
        Assert.Contains("軟體端點", v.Evidence);
    }

    // ── 要求值被夾住 ──────────────────────────────────────────────────────

    [Fact]
    public void 要求值被裝置夾住_要明說不是應用程式選的()
    {
        // 本機實況：要求 1 ms，實際配到 21.3 ms 或 80 ms
        var v = AudioLatencyJudge.Judge(Sample(reqMs: 1.0, exMs: 21.3));

        Assert.Contains("被裝置夾住", v.Evidence);
        Assert.Contains("不是應用程式選的", v.Evidence);
        Assert.Contains("要求 1 ms", v.Evidence);
    }

    [Fact]
    public void 要求值接近實配值_不得出現夾住說明()
    {
        var v = AudioLatencyJudge.Judge(Sample(reqMs: 20.0, exMs: 21.3));
        Assert.DoesNotContain("被裝置夾住", v.Evidence);
    }

    [Fact]
    public void 未指定週期_要說明用的是裝置預設()
    {
        var v = AudioLatencyJudge.Judge(Sample(reqMs: 0, exMs: 21.3));
        Assert.Contains("裝置預設", v.Evidence);
    }

    // ── 共用模式不得當成延遲指標 ──────────────────────────────────────────

    [Fact]
    public void 共用模式值_要標明是引擎決定且共享()
    {
        var v = AudioLatencyJudge.Judge(Sample(shMs: 22.0));

        Assert.Contains("共用模式", v.Evidence);
        Assert.Contains("共享", v.Evidence);
        Assert.Contains("不能當延遲指標", v.Evidence);
    }

    [Fact]
    public void 摘要_要把這條界線再講一次()
    {
        string text = AudioLatencyJudge.Summarize([Sample(def: true)]);
        Assert.Contains("不能拿來當延遲指標", text);
    }

    // ── 配不出來與讀不到 ──────────────────────────────────────────────────

    [Fact]
    public void 獨占模式配不出來_判為不可得並列出常見原因()
    {
        var v = AudioLatencyJudge.Judge(Sample(exMs: 0, frames: 0, err: "0x88890008"));

        Assert.Equal(AudioLatencyJudge.LatencyTier.Unavailable, v.Tier);
        Assert.Contains("0x88890008", v.Evidence);
        Assert.Contains("獨占", v.Evidence);
        Assert.Contains("不推測", v.Evidence);
    }

    [Fact]
    public void 配不出來時_不得給出任何延遲等級的結論()
    {
        var v = AudioLatencyJudge.Judge(Sample(exMs: 0, frames: 0));
        Assert.DoesNotContain("可做即時監聽", v.Headline);
        Assert.DoesNotContain("只適合播放", v.Headline);
    }

    [Fact]
    public void 沒有名稱_判為未知()
    {
        var v = AudioLatencyJudge.Judge(Sample(name: ""));
        Assert.Equal(AudioLatencyJudge.LatencyTier.Unknown, v.Tier);
    }

    // ── 軟體端點的標註 ────────────────────────────────────────────────────

    [Fact]
    public void 軟體端點_要標明判斷依名稱未經匯流排查證()
    {
        var v = AudioLatencyJudge.Judge(Sample(name: "CABLE Input (VB-Audio Virtual Cable)", soft: true));

        Assert.True(v.SoftwareEndpoint);
        Assert.Contains("名稱", v.Evidence);
        Assert.Contains("未經匯流排查證", v.Evidence);
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("喇叭 (Steam Streaming Speakers)")]
    [InlineData("Virtual Display Audio")]
    [InlineData("VoiceMeeter Input")]
    public void 名稱字樣辨識_常見軟體端點(string name)
        => Assert.True(AudioLatencyFactsService.LooksLikeSoftware(name));

    [Theory]
    [InlineData("Realtek Digital Output (Realtek(R) Audio)")]
    [InlineData("Speakers (USB Audio DAC)")]
    [InlineData("耳機 (High Definition Audio Device)")]
    public void 名稱字樣辨識_硬體端點不得誤判(string name)
        => Assert.False(AudioLatencyFactsService.LooksLikeSoftware(name));

    // ── 摘要 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 摘要_要指出預設輸出與最小的端點()
    {
        string text = AudioLatencyJudge.Summarize(
        [
            Sample(name: "Realtek", def: true, exMs: 21.3),
            Sample(name: "USB DAC", exMs: 5.3),
            Sample(name: "CABLE", exMs: 80.0, soft: true),
        ]);

        Assert.Contains("共 3 個輸出端點", text);
        Assert.Contains("USB DAC", text);
        Assert.Contains("5.3 ms", text);
        Assert.Contains("可做即時監聽", text);
        Assert.Contains("預設輸出是「Realtek」", text);
    }

    [Fact]
    public void 摘要_全部配不出來要如實說無從比較()
    {
        string text = AudioLatencyJudge.Summarize([Sample(exMs: 0, frames: 0), Sample(name: "X", exMs: 0, frames: 0)]);

        Assert.Contains("沒有任何一個能配置獨占模式", text);
        Assert.Contains("無從比較", text);
    }

    [Fact]
    public void 摘要_沒有端點要說讀不到而不是設定有問題()
    {
        string text = AudioLatencyJudge.Summarize([]);
        Assert.Contains("沒有讀到", text);
        Assert.Contains("不代表設定有問題", text);
    }

    // ── 事實收集 ──────────────────────────────────────────────────────────

    [Fact]
    public void 事實收集_逐端點陳列且帶NumericValue()
    {
        var facts = AudioLatencyFactsService.Collect(At,
            () => [Sample(name: "USB DAC", exMs: 5.3), Sample(name: "CABLE", exMs: 0, frames: 0, err: "0x88890008")]);

        var summary = Assert.Single(facts, f => f.Key == "audio.latency");
        Assert.Equal(FactTrustLevel.Measured, summary.Trust);

        var dac = Assert.Single(facts, f => f.Key == "audio.latency.USB DAC");
        Assert.Equal(5.3, dac.NumericValue);

        var cable = Assert.Single(facts, f => f.Key == "audio.latency.CABLE");
        Assert.Equal(FactAvailability.NotSupported, cable.Availability);
        Assert.Contains("0x88890008", cable.UnavailableReason);
    }

    [Fact]
    public void 事實收集_沒有端點為NotApplicable而非錯誤()
    {
        var facts = AudioLatencyFactsService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是設定有問題", f.UnavailableReason);
    }

    [Fact]
    public void 事實收集_擲回例外以ReadError回報()
    {
        var facts = AudioLatencyFactsService.Collect(At, () => throw new InvalidOperationException("模擬失敗"));
        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts).Availability);
    }
}
