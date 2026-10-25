using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0128 端點音量/靜音核對的契約：狀態判讀（含 0% 與「讀不到」分離）、
/// 兩項皆讀不到標 ReadError、無端點標 NotSupported。
/// </summary>
public class DeepTestAudioEndpointStateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 描述_音量與靜音齊備_帶百分比()
    {
        var row = new AudioEndpointStateService.EndpointStateRow("喇叭", "渲染", 0.72, false);
        var (value, availability, reason) = AudioEndpointStateService.Describe(row);
        Assert.Equal(FactAvailability.Present, availability);
        Assert.Null(reason);
        Assert.Contains("72%", value);
        Assert.Contains("靜音 否", value);
        Assert.Contains("渲染", value);
    }

    [Fact]
    public void 描述_音量歸零_是讀到的真值不是讀不到()
    {
        var row = new AudioEndpointStateService.EndpointStateRow("喇叭", "渲染", 0.0, true);
        var (value, availability, reason) = AudioEndpointStateService.Describe(row);
        Assert.Equal(FactAvailability.Present, availability);
        Assert.Null(reason);
        Assert.Contains("歸零", value);
        Assert.Contains("真值", value);
        Assert.Contains("靜音 是", value);
    }

    [Fact]
    public void 描述_部分讀不到_逐項如實標()
    {
        var row = new AudioEndpointStateService.EndpointStateRow("奇怪端點", "擷取", null, null);
        var (value, availability, reason) = AudioEndpointStateService.Describe(row);
        Assert.Equal(FactAvailability.ReadError, availability);
        Assert.Contains("讀不到", reason);
        _ = value;
    }

    [Fact]
    public void 收集_無端點_標NotSupported()
    {
        var facts = AudioEndpointStateService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(AudioEndpointStateService.Key, f.Key);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 收集_多端點_索引鍵()
    {
        var endpoints = new[]
        {
            new AudioEndpointStateService.EndpointStateRow("喇叭", "渲染", 0.5, false),
            new AudioEndpointStateService.EndpointStateRow("麥克風", "擷取", null, null),
        };
        var facts = AudioEndpointStateService.Collect(At, () => endpoints);
        Assert.Equal(2, facts.Count);
        Assert.Equal("ts.aud.endpoint.state.0", facts[0].Key);
        Assert.Equal(FactAvailability.Present, facts[0].Availability);
        Assert.Equal(FactAvailability.ReadError, facts[1].Availability);
    }
}
