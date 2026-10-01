using Xunit;

namespace XinSpect.Tests;

public class DeepBenchHonestyTests
{
    [Fact]
    public void 深測中心必須明示不加權且不宣稱記憶體認證工具()
    {
        string combined = string.Join('\n',
            DeepBenchViewModel.NoScoreNotice,
            DeepBenchViewModel.ScopeNotice,
            DeepBenchViewModel.LoadWarning,
            DeepBenchViewModel.TempFileWarning,
            DeepBenchViewModel.LocalOnlyNotice);

        Assert.Contains("不加權", combined, StringComparison.Ordinal);
        Assert.Contains("NPU ONNX", combined, StringComparison.Ordinal);
        Assert.Contains("不保證", combined, StringComparison.Ordinal);
        Assert.Contains("XinSpect.deepbench.tmp", combined, StringComparison.Ordinal);
        Assert.Contains("不上傳", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("加權總分", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("MemTest86", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("自動上傳", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("保證 AES-NI", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void 失敗與取消結果不得被美化成成功量測()
    {
        DateTime now = DateTime.UtcNow;
        var result = new DeepBenchTestResult(
            "gpu.fp32-fp64-integer", Guid.NewGuid(), DeepBenchRunProfile.Quick, now, now,
            "failed", [], [], ["此項未產生可信量測。"], DeepBenchFailureKind.DriverRejected, "driver removed");

        var card = DeepBenchResultCard.From(result, "GPU");

        Assert.Contains("無有效量測", card.StateText, StringComparison.Ordinal);
        Assert.Contains("driver removed", card.Error, StringComparison.Ordinal);
        Assert.True(card.HasErrorText);
    }
}
