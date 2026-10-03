using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP22 記憶體壓力探測的契約（**危險項**）：① 沒有明確同意一律拒跑（同意閘門）；
/// ② 樣本填填／驗證是純函式（注入的翻轉必被抓到）；③ 探測結果誠實命名——
/// 這是「自擁有記憶體內的反覆讀寫壓力探測」，usermode 無 clflush 不能宣稱保證觸發
/// Rowhammer；④ UI／文件處處標註危險。
/// </summary>
public class RowhammerProbeTests
{
    [Fact]
    public void 同意閘門_無同意拒跑()
    {
        Assert.Throws<InvalidOperationException>(() =>
            XinSpect.RowhammerProbeService.RunConsentedProbe(userConsent: false, targetMegabytes: 4));
    }

    [Fact]
    public void 樣本驗證_注入翻轉必被抓_完好回零()
    {
        Span<byte> page = stackalloc byte[256];
        XinSpect.RowhammerProbeService.FillPattern(page, seed: 0x5A);
        Assert.Equal(0, XinSpect.RowhammerProbeService.VerifyPattern(page, seed: 0x5A));
        page[123] ^= 0x01;                       // 模擬位元翻轉
        Assert.Equal(1, XinSpect.RowhammerProbeService.VerifyPattern(page, seed: 0x5A));
        page[123] ^= 0x01;
        page[200] ^= 0x80;
        Assert.Equal(1, XinSpect.RowhammerProbeService.VerifyPattern(page, seed: 0x5A));
    }

    [Fact]
    public void 危險聲明_處處成文()
    {
        string help = XinSpect.RowhammerProbeService.DangerNotice;
        Assert.Contains("危險", help);
        Assert.Contains("可能損壞資料", help);
        Assert.Contains("建議使用專用測試機", help);
        Assert.Contains("非保證觸發 Rowhammer", help);
    }

    [Fact]
    public void 探測_小緩衝跑完_結果欄位完整()
    {
        var result = XinSpect.RowhammerProbeService.RunConsentedProbe(userConsent: true, targetMegabytes: 2);
        Assert.Equal(2u * 1024 * 1024, result.AllocatedBytes);
        Assert.True(result.Iterations > 0);
        Assert.True(result.ElapsedMs >= 0);
        // 本機正常記憶體應零翻轉——但結果結構允許非零並逐位址回報
        Assert.Equal(0, result.Flips);
    }
}
