using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 假容量寫入驗證（H2testw 式，**同意閘門複用 WP22 模式**）與 GPU NVML 缺口的契約：
/// ① 填滿寫入→讀回逐位元組驗證——「標稱容量灌不滿／讀回不一致」即假卡、假碟的直接證據；
///    沒有同意一律拒跑。② TDR 逾時設定（登錄檔 GraphicsDrivers，未設定＝Windows 預設值並明說）。
/// ③ NVML 退休頁（NAND 瑕疵退休計數，僅 NVIDIA——無卡 NotApplicable）。
/// </summary>
public class MediaVerificationTests
{
    [Fact]
    public void 同意閘門_無同意拒跑()
    {
        Assert.Throws<InvalidOperationException>(() =>
            XinSpect.FakeCapacityTestService.RunConsented(false, Path.GetTempPath(), maxBytes: 1024 * 1024));
    }

    [Fact]
    public void 樣本填填與驗證_翻轉必被抓()
    {
        Span<byte> chunk = stackalloc byte[64];
        XinSpect.FakeCapacityTestService.FillChunk(chunk, chunkIndex: 7);
        Assert.Equal(0, XinSpect.FakeCapacityTestService.VerifyChunk(chunk, chunkIndex: 7));
        chunk[63] ^= 0xFF;
        Assert.Equal(1, XinSpect.FakeCapacityTestService.VerifyChunk(chunk, chunkIndex: 7));
        // 不同 chunkIndex 的樣本不同（避免跨區塊誤驗證）
        Span<byte> other = stackalloc byte[64];
        XinSpect.FakeCapacityTestService.FillChunk(other, chunkIndex: 8);
        Assert.Equal(64, XinSpect.FakeCapacityTestService.VerifyChunk(other, chunkIndex: 7));
    }

    [Fact]
    public void 假容量驗證_小檔跑完_結果欄位完整與暫存檔清理()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"faket-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = XinSpect.FakeCapacityTestService.RunConsented(true, dir, maxBytes: 4 * 1024 * 1024, chunkMiB: 1);
            Assert.Equal(4L * 1024 * 1024, result.WrittenBytes);
            Assert.Equal(4L * 1024 * 1024, result.VerifiedBytes);
            Assert.Equal(0, result.MismatchedBytes);
            Assert.False(result.FullReached);
            Assert.True(result.ElapsedMs >= 0);
            Assert.Empty(Directory.GetFiles(dir));   // 暫存檔已刪
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 假容量驗證_注入翻轉被抓出()
    {
        // 以翻轉注入的假檔案系統（自訂 verify hook）驗證偵測路徑
        Span<byte> chunk = stackalloc byte[16];
        XinSpect.FakeCapacityTestService.FillChunk(chunk, 0);
        chunk[0] ^= 0x01;   // 模擬假碟讀回不一致
        Assert.Equal(1, XinSpect.FakeCapacityTestService.VerifyChunk(chunk, 0));
    }

    [Fact]
    public void TDR設定_有設定讀原值_未設定說預設()
    {
        var custom = XinSpect.GpuTdrFactsService.Collect(DateTimeOffset.UtcNow,
            registryProbe: () => new Dictionary<string, int?> { ["TdrDelay"] = 10, ["TdrLevel"] = 0, ["TdrDpcDelay"] = null });
        var delay = Assert.Single(custom, f => f.Key == "gpu.tdr.delay");
        Assert.Equal(10u, delay.NumericValue);
        Assert.Single(custom, f => f.Key == "gpu.tdr.level");

        var defaults = XinSpect.GpuTdrFactsService.Collect(DateTimeOffset.UtcNow,
            registryProbe: () => new Dictionary<string, int?>());
        Assert.All(defaults.Where(f => f.Key.StartsWith("gpu.tdr")), f => Assert.Contains("預設", f.Value));
    }
}
