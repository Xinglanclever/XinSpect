using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0127 音訊插孔描述的契約：KSJACK_DESCRIPTION 緩衝區解碼（KSMULTIPLE_ITEM 標頭校驗、
/// 計數越界、截斷拒絕）、零插孔不冒充、無端點三態。純解碼，不碰真 COM。
/// </summary>
public class DeepTestAudioJackTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static byte[] JackBuffer(uint count, params (uint ConnType, uint Geo, uint Color, bool Connected)[] jacks)
    {
        var data = new List<byte>();
        uint declaredSize = 8 + count * 28;
        Put(data, declaredSize);   // KSMULTIPLE_ITEM.Size
        Put(data, count);          // KSMULTIPLE_ITEM.Count
        foreach (var j in jacks)
        {
            Put(data, 0);          // ChannelMapping
            Put(data, j.Color);
            Put(data, j.ConnType);
            Put(data, j.Geo);
            Put(data, 0);          // GenLocation
            Put(data, 0);          // PortConnection
            Put(data, j.Connected ? 1u : 0u);
        }
        return [.. data];
    }

    private static void Put(List<byte> b, uint v)
    {
        b.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
    }

    [Fact]
    public void 解碼_兩個插孔_逐欄正確()
    {
        var jacks = AudioJackDetectService.ParseJackBuffer(JackBuffer(2,
            (1, 0x2, 0x00FF00, true),   // 3.5mm、外部前方、綠色、已連接
            (5, 0x1, 0x000000, false))); // 光纖、外部後方、黑、未連接
        Assert.Equal(2, jacks.Count);
        Assert.Equal(1u, jacks[0].ConnectionType);
        Assert.Equal(0x2u, jacks[0].GeoLocation);
        Assert.Equal(0x00FF00u, jacks[0].Color);
        Assert.True(jacks[0].IsConnected);
        Assert.Equal(5u, jacks[1].ConnectionType);
        Assert.False(jacks[1].IsConnected);
    }

    [Fact]
    public void 解碼_計數越界與標頭不符_如實拒絕()
    {
        Assert.ThrowsAny<Exception>(() => AudioJackDetectService.ParseJackBuffer(JackBuffer(0)));          // count 0
        Assert.ThrowsAny<Exception>(() => AudioJackDetectService.ParseJackBuffer(JackBuffer(300)));        // count > 256
        var badHeader = JackBuffer(1, (1, 2, 3, true));
        badHeader[0] = 99; // 宣告 Size 與 8+28 不符
        Assert.ThrowsAny<Exception>(() => AudioJackDetectService.ParseJackBuffer(badHeader));
        var truncated = JackBuffer(1, (1, 2, 3, true));
        Assert.ThrowsAny<Exception>(() => AudioJackDetectService.ParseJackBuffer(truncated[..20]));        // 截斷
        Assert.ThrowsAny<Exception>(() => AudioJackDetectService.ParseJackBuffer([1, 2, 3, 4]));           // 連標頭都不足
    }

    [Fact]
    public void 描述_插孔文字與誠實轉述()
    {
        var endpoint = new AudioJackDetectService.EndpointJacks("測試喇叭",
        [
            new AudioJackDetectService.JackRow(1, 0x2, 0, true),
            new AudioJackDetectService.JackRow(5, 0x1, 0, false),
        ]);
        var (value, availability, reason) = AudioJackDetectService.Describe(endpoint);
        Assert.Equal(FactAvailability.Present, availability);
        Assert.Null(reason);
        Assert.Contains("2 個插孔", value);
        Assert.Contains("3.5mm", value);
        Assert.Contains("外部前方", value);
        Assert.Contains("光纖", value);
        Assert.Contains("IsConnected 位元照實轉述", value);
    }

    [Fact]
    public void 描述_零插孔_Unknown不是沒有插孔()
    {
        var (value, availability, reason) = AudioJackDetectService.Describe(
            new AudioJackDetectService.EndpointJacks("虛擬裝置", []));
        Assert.Equal(FactAvailability.Unknown, availability);
        Assert.Equal("", value);
        Assert.Contains("不以 0 冒充", reason);
    }

    [Fact]
    public void 收集_無端點_標NotSupported()
    {
        var facts = AudioJackDetectService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(AudioJackDetectService.Key, f.Key);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 收集_多端點_索引鍵與內容()
    {
        var endpoints = new[]
        {
            new AudioJackDetectService.EndpointJacks("喇叭",
                [new AudioJackDetectService.JackRow(1, 0x2, 0, true)]),
            new AudioJackDetectService.EndpointJacks("HDMI", []),
        };
        var facts = AudioJackDetectService.Collect(At, () => endpoints);
        Assert.Equal(2, facts.Count);
        Assert.Equal("ts.aud.jackdetect.0", facts[0].Key);
        Assert.Equal(FactAvailability.Present, facts[0].Availability);
        Assert.Equal(FactAvailability.Unknown, facts[1].Availability);
        Assert.Null(facts[1].NumericValue);
    }

    [Fact]
    public void 類型與位置代碼_未知代碼照實帶原值()
    {
        Assert.Equal("3.5mm 類比", AudioJackDetectService.DescribeConnectionType(1));
        Assert.Contains("42", AudioJackDetectService.DescribeConnectionType(42));
        Assert.Contains("77", AudioJackDetectService.DescribeGeoLocation(77));
    }
}
