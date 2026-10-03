using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 音訊端點事實（WP23）的契約：WAVEFORMATEX 解碼（PCM／浮點／EXTENSIBLE／垃圾）、
/// 端點列舉呈現、無端點三態。以注入端點清單驗證，不碰真 COM。
/// </summary>
public class AudioEndpointFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Fmt(ushort tag, ushort channels, uint rate, ushort bits, bool extensible = false)
    {
        var w = new byte[extensible ? 40 : 18];
        Put(w, 0, tag); Put(w, 2, channels); Put(w, 4, rate);
        Put(w, 8, rate * channels * (uint)(bits / 8));
        Put(w, 12, (ushort)(channels * bits / 8)); Put(w, 14, bits);
        if (extensible) { Put(w, 16, (ushort)22); Put(w, 18, bits); Put(w, 20, 0x3); }
        return w;
    }

    private static void Put(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void Put(byte[] b, int off, uint v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }

    [Theory]
    [InlineData(1u, 2u, 48000u, 16u, "PCM")]
    [InlineData(3u, 2u, 48000u, 32u, "IEEE 浮點")]
    public void WaveFormat解析_PCM與浮點(uint tag, uint channels, uint rate, uint bits, string tagText)
    {
        var f = AudioFormatDecoder.Parse(Fmt((ushort)tag, (ushort)channels, rate, (ushort)bits));
        Assert.NotNull(f);
        Assert.Equal(channels, f!.Value.Channels);
        Assert.Equal(rate, f.Value.SamplesPerSec);
        Assert.Equal(bits, f.Value.Bits);
        Assert.Contains(tagText, f.Value.TagText);
    }

    [Fact]
    public void WaveFormat解析_EXTENSIBLE與垃圾與過短()
    {
        var ext = AudioFormatDecoder.Parse(Fmt(0xFFFE, 2, 48000, 24, extensible: true));
        Assert.Contains("可延伸", ext!.Value.TagText);

        var garbage = AudioFormatDecoder.Parse(Fmt(1, 0, 48000, 16));      // 聲道 0
        Assert.Null(garbage);
        var zeroRate = AudioFormatDecoder.Parse(Fmt(1, 2, 0, 16));         // 取樣率 0
        Assert.Null(zeroRate);
        Assert.Null(AudioFormatDecoder.Parse(new byte[10]));               // 過短
    }

    [Fact]
    public void 端點列舉_名稱與格式成列_解析失敗帶原始大小()
    {
        var facts = AudioEndpointFactsService.Collect(At, endpoints: () =>
        [
            ("喇叭（Realtek）", Fmt(1, 2, 48000, 16)),
            ("麥克風（USB）", null),
            ("HDMI 輸出", new byte[30]), // 全零緩衝：聲道 0 → 不解碼垃圾
        ]);

        Assert.Equal(3, facts.Count);
        var spk = Assert.Single(facts, f => f.Key == "audio.endpoint.0");
        Assert.Contains("喇叭（Realtek）", spk.Name);
        Assert.Contains("2 聲道、48000 Hz、16-bit（PCM）", spk.Value);

        var mic = Assert.Single(facts, f => f.Key == "audio.endpoint.1");
        Assert.Contains("未提供或過短", mic.Value);

        var hdmi = Assert.Single(facts, f => f.Key == "audio.endpoint.2");
        Assert.Contains("格式無法解析（原始 30 位元組", hdmi.Value);
    }

    [Fact]
    public void 無端點_三態()
    {
        var facts = AudioEndpointFactsService.Collect(At, endpoints: () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("無作用中音訊端點", f.UnavailableReason);
    }
}
