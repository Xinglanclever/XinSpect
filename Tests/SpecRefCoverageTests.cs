using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SpecRef 覆蓋率機器檢查（V7 WP44／§12.11「沒有 SpecRef 的欄位＝未驗證」）：
/// 註冊在 <see cref="SpecRefRegistry.CoveredDecoders"/> 的解碼器，每個公開靜態方法都必須附規格引用——
/// 解碼器新增方法而未附引用會直接紅燈，而不是靠記憶與自律。
/// </summary>
public class SpecRefCoverageTests
{
    [Fact]
    public void 覆蓋解碼器的每個公開方法都帶規格引用()
    {
        var missing = SpecRefRegistry.MethodsMissingRefs();
        Assert.True(missing.Count == 0,
            "以下解碼方法缺 SpecRef（V7 §12.11：沒有引用＝未驗證）：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void 引用內容不得為空或佔位()
    {
        var refs = SpecRefRegistry.AllReferences();
        Assert.NotEmpty(refs);
        Assert.All(refs, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Reference), $"{r.Member} 的引用是空的");
            Assert.False(r.Reference.Trim() is "TODO" or "TBD" or "待補", $"{r.Member} 的引用是佔位字串");
        });
    }

    [Fact]
    public void 覆蓋面與引用量如實申報()
    {
        // 釘住規模下限：這批解碼器的引用量只許往上走（縮水＝有人拆了引用，先查清楚）。
        Assert.True(SpecRefRegistry.CoveredDecoders.Length >= 12, "覆蓋解碼器不該縮水");
        Assert.True(SpecRefRegistry.AllReferences().Count >= 41,
            $"引用條目 {SpecRefRegistry.AllReferences().Count} 低於已知基線 32——查清楚是不是被拆了");
    }
}
