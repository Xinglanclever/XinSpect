using Xunit;

namespace XinSpect.Tests;

/// <summary>命令面板模糊比對：四級評分的相對順序與邊界。</summary>
public class FuzzyMatchTests
{
    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.True(FuzzyMatch.Score("", "處理器") > 0);
        Assert.True(FuzzyMatch.Score("", null) > 0);
    }

    [Fact]
    public void EmptyTarget_NeverMatches()
    {
        Assert.Equal(0, FuzzyMatch.Score("cpu", null));
        Assert.Equal(0, FuzzyMatch.Score("cpu", ""));
    }

    [Fact]
    public void Prefix_BeatsContains_BeatsSubsequence()
    {
        int prefix = FuzzyMatch.Score("cpu", "cpu 超頻");
        int contains = FuzzyMatch.Score("cpu", "進階 cpu 設定");
        int subseq = FuzzyMatch.Score("cpu", "c-p-u");
        Assert.True(prefix > contains, $"前綴 {prefix} 應勝過子字串 {contains}");
        Assert.True(contains > subseq, $"子字串 {contains} 應勝過子序列 {subseq}");
        Assert.True(subseq > 0);
    }

    [Fact]
    public void ShorterTarget_ScoresHigher_AtSameTier()
    {
        // 同為前綴命中時，較短的標題更可能是使用者要的那一項
        Assert.True(FuzzyMatch.Score("處理器", "處理器") > FuzzyMatch.Score("處理器", "處理器超頻進階設定"));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
        => Assert.Equal(FuzzyMatch.Score("CPU", "cpu 超頻"), FuzzyMatch.Score("cpu", "cpu 超頻"));

    [Fact]
    public void NoCommonCharacters_ScoresZero()
        => Assert.Equal(0, FuzzyMatch.Score("xyz", "處理器"));

    [Fact]
    public void OutOfOrderCharacters_AreNotASubsequence()
        => Assert.Equal(0, FuzzyMatch.Score("upc", "cpu"));

    [Fact]
    public void Best_TakesHighestWeightedField()
    {
        var fields = new (string?, int)[] { ("記憶體", 100), ("ram 時序", 80) };
        int best = FuzzyMatch.Best("ram", fields);
        Assert.True(best > 0);
        // 標題不命中時仍須由關鍵字欄位撐起分數
        Assert.Equal(0, FuzzyMatch.Score("ram", "記憶體"));
    }

    [Fact]
    public void Best_ReturnsZero_WhenNoFieldMatches()
        => Assert.Equal(0, FuzzyMatch.Best("zzz", new (string?, int)[] { ("記憶體", 100), ("ram", 80) }));
}
