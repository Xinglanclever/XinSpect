using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// GPU 編解碼能力矩陣的守門：GUID 對照表完整性、偵測不丟例外、真機至少找到一張卡。
/// </summary>
public class GpuCodecServiceTests
{
    [Fact]
    public void 對照表至少覆蓋SDK全部62個解碼profile()
    {
        // SDK 10.0.28000 d3d11.h 有 62 個 D3D11_DECODER_PROFILE_*；少一個就是「看得到 GUID 但顯示未知」
        Assert.True(GpuCodecService.DecoderProfileNames.Count >= 62,
            $"DecoderProfileNames 只有 {GpuCodecService.DecoderProfileNames.Count} 個，SDK 標頭有 62 個");
    }

    [Fact]
    public void AV1_profile0的GUID結尾是5d2a不是5d50()
    {
        // 交接摘要特別標注的陷阱：憑記憶寫成 da5d50（錯），SDK 實際是 da5d2a
        var av1p0 = new Guid(0xb8be4ccb, 0xcf53, 0x46ba, 0x8d, 0x59, 0xd6, 0xb8, 0xa6, 0xda, 0x5d, 0x2a);
        Assert.True(GpuCodecService.DecoderProfileNames.ContainsKey(av1p0),
            "AV1 Profile0 GUID 沒在對照表裡——可能寫錯了結尾");
        Assert.Contains("AV1 Profile0", GpuCodecService.DecoderProfileNames[av1p0]);
    }

    [Fact]
    public void 常見編碼格式都有對照()
    {
        // 實務上硬解最常問的四種都該有中文名
        Assert.Contains(GpuCodecService.DecoderProfileNames.Values, v => v.Contains("H.264"));
        Assert.Contains(GpuCodecService.DecoderProfileNames.Values, v => v.Contains("HEVC Main10"));
        Assert.Contains(GpuCodecService.DecoderProfileNames.Values, v => v.Contains("VP9"));
        Assert.Contains(GpuCodecService.DecoderProfileNames.Values, v => v.Contains("AV1"));
    }

    [Fact]
    public void Probe不丟例外且回傳結果()
    {
        var result = GpuCodecService.Probe();
        Assert.NotNull(result);
        Assert.NotEmpty(result.Adapters);
        // 每個 adapter 要嘛有名字+資料，要嘛有 Error；不該兩者皆空
        foreach (var a in result.Adapters)
            Assert.True(a.Error is not null || (!string.IsNullOrEmpty(a.Name)),
                "Adapter 既沒名字也沒錯誤訊息——偵測邏輯漏了什麼");
    }

    [Fact]
    public void Probe在真機上至少找到一張顯示卡()
    {
        var result = GpuCodecService.Probe();
        Assert.True(result.Adapters.Count >= 1,
            $"只找到 {result.Adapters.Count} 張卡——這台機器至少有一張 GPU");
    }
}
