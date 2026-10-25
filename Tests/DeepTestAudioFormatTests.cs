using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0124 音訊格式支援枚舉的契約：矩陣探測判讀、全被拒如實列、例外標 ReadError、
/// 無端點標 NotSupported；不碰真 COM。
/// </summary>
public class DeepTestAudioFormatTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static AudioFormatProbeService.EndpointProbe Ep(string name, Func<AudioFormatProbeService.ProbeFormat, int> f) =>
        new(name, f);

    [Fact]
    public void 探測_支援集合按位元深度分組()
    {
        var endpoint = Ep("測試喇叭", fmt => fmt.SampleRate is 44100 or 48000 or 96000 ? 0 : 1);
        var (value, availability, reason) = AudioFormatProbeService.ProbeOne(
            endpoint, AudioFormatProbeService.DefaultMatrix);
        Assert.Equal(FactAvailability.Present, availability);
        Assert.Null(reason);
        Assert.Contains("16-bit：44100/48000", value);
        Assert.Contains("24-bit：44100/48000/96000", value);
        Assert.Contains("32f-bit：48000/96000", value);
    }

    [Fact]
    public void 探測_全組合被拒_照實列不猜原因()
    {
        var endpoint = Ep("獨占鎖定端點", _ => 1);
        var (value, availability, reason) = AudioFormatProbeService.ProbeOne(
            endpoint, AudioFormatProbeService.DefaultMatrix);
        Assert.Equal(FactAvailability.Present, availability);
        Assert.Null(reason);
        Assert.Contains("全組合被拒", value);
        Assert.DoesNotContain("因為", value);
    }

    [Fact]
    public void 探測_全部丟例外_標ReadError()
    {
        var endpoint = Ep("壞端點", _ => throw new InvalidOperationException("未初始化"));
        var (value, availability, reason) = AudioFormatProbeService.ProbeOne(
            endpoint, AudioFormatProbeService.DefaultMatrix);
        Assert.Equal(FactAvailability.ReadError, availability);
        Assert.Contains("例外", reason);
        _ = value;
    }

    [Fact]
    public void 收集_無端點_標NotSupported()
    {
        var facts = AudioFormatProbeService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(AudioFormatProbeService.Key, f.Key);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 收集_多端點_逐端點索引鍵()
    {
        var endpoints = new[]
        {
            Ep("喇叭", _ => 0),
            Ep("耳機", _ => 1),
        };
        var facts = AudioFormatProbeService.Collect(At,
            [new(44100, 16, false), new(48000, 16, false)], () => endpoints);
        Assert.Equal(2, facts.Count);
        Assert.Equal("ts.aud.formats.0", facts[0].Key);
        Assert.Equal("ts.aud.formats.1", facts[1].Key);
        Assert.Contains("44100/48000", facts[0].Value);
        Assert.Contains("全組合被拒", facts[1].Value);
    }
}
