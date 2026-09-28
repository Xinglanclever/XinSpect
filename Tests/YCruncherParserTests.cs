using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// y-cruncher 壓測輸出解析：主判據是退出碼＋是否跑滿時長，輔以錯誤字樣掃描；一律附原始日誌尾巴。
/// </summary>
public class YCruncherParserTests
{
    [Fact]
    public void 跑滿時長且乾淨退出_判為通過()
        => Assert.Equal(StressOutcome.Passed,
            YCruncherParser.Parse(0, 3600, 3600, "Validating...\nAll tests passed.").Outcome);

    [Fact]
    public void 輸出含錯誤字樣_判為偵測到運算錯誤()
        => Assert.Equal(StressOutcome.ErrorDetected,
            YCruncherParser.Parse(0, 1200, 3600, "Iteration 5\nERROR: Coefficient is too large\n").Outcome);

    [Fact]
    public void 非零退出碼_判為偵測到運算錯誤()
        => Assert.Equal(StressOutcome.ErrorDetected,
            YCruncherParser.Parse(1, 3600, 3600, "aborted").Outcome);

    [Fact]
    public void 乾淨退出但沒跑滿時長_判為不完整不作通過()
        => Assert.Equal(StressOutcome.Incomplete,
            YCruncherParser.Parse(0, 100, 3600, "stopped early").Outcome);

    [Fact]
    public void 略早於時限但在容差內_仍算通過()
        => Assert.Equal(StressOutcome.Passed,
            YCruncherParser.Parse(0, 3595, 3600, "done").Outcome);

    [Fact]
    public void 一律附上原始日誌尾巴供人核對()
        => Assert.Contains("VALIDATION OK",
            YCruncherParser.Parse(0, 3600, 3600, "line1\nline2\nVALIDATION OK").LogTail);
}
