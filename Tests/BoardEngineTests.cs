using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 兩套棋類 perft 引擎（<see cref="ChessEngine"/> 西洋棋、<see cref="XiangqiEngine"/> 中國象棋）。
/// </summary>
/// <remarks>
/// 這組測試的價值在於它測的是<b>數學常數</b>，不是「本程式目前的行為」。起始局面到某個深度的
/// 合法走法葉節點數是公開且唯一的，所以這裡沒有「先跑一次看看輸出多少、再把它寫進斷言」的餘地：
/// 引擎算錯就是不等於這些數字。也因為如此，同一個計數在跑分時可以反過來當運算正確性的檢核碼
/// （見 <see cref="ChessBenchService"/>）——一台機器算出別的數，代表它算錯了，而不是它比較慢。
///
/// 規則層面沒有另外逐條斷言（蹩馬腿、象眼、過河、九宮、白臉將、炮的砲架），理由是：
/// 任何一條規則寫錯，都會讓後面幾個深度的節點數整批偏離；要同時湊中 44 / 1,920 / 79,666 / 3,290,240
/// 四個獨立常數而規則仍然是錯的，實務上不可能。逐條測試會多一堆程式碼，卻比不上這四個數字嚴格。
/// </remarks>
public class BoardEngineTests
{
    // ── 西洋棋：公認 perft 值 ────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 20L)]
    [InlineData(2, 400L)]
    [InlineData(3, 8_902L)]
    [InlineData(4, 197_281L)]
    public void 西洋棋起始局面的葉節點數等於公認值(int depth, long expected)
        => Assert.Equal(expected, new ChessEngine().PerftLeaves(depth));

    [Fact]
    public void 西洋棋造訪節點數是各層之和_與葉節點數是兩種不同的量()
    {
        // PerftNodes 連內部節點一起數（跑分吞吐用），PerftLeaves 只數葉節點（標準 perft 定義）。
        // 兩者都對，但不可混用；混用會讓歷次成績在不知不覺間變成不同單位。
        var e = new ChessEngine();
        Assert.Equal(1 + 20 + 400 + 8_902 + 197_281, e.PerftNodes(4));
        Assert.Equal(197_281, e.PerftLeaves(4));
    }

    // ── 中國象棋：公認 perft 值 ──────────────────────────────────────────────

    [Theory]
    [InlineData(1, 44L)]
    [InlineData(2, 1_920L)]
    [InlineData(3, 79_666L)]
    public void 象棋起始局面的葉節點數等於公認值(int depth, long expected)
        => Assert.Equal(expected, new XiangqiEngine().PerftLeaves(depth));

    [Fact]
    public void 象棋第四層也對得上_深一層才驗得到炮吃子與蹩腿的交互作用()
        => Assert.Equal(3_290_240L, new XiangqiEngine().PerftLeaves(4));

    // ── 跑分迴圈依賴的兩個性質 ──────────────────────────────────────────────

    [Fact]
    public void 重設後可重複量測_每次結果一致()
    {
        // 跑分是「Reset + Perft」反覆數千次；若 Reset 沒把狀態清乾淨，
        // 第二次起的節點數就會漂掉，而那會被誤判成這台機器算錯。
        var chess = new ChessEngine();
        var xq = new XiangqiEngine();
        for (int i = 0; i < 3; i++)
        {
            chess.Reset();
            xq.Reset();
            Assert.Equal(8_902L, chess.PerftLeaves(3));
            Assert.Equal(1_920L, xq.PerftLeaves(2));
        }
    }

    [Fact]
    public void 多執行緒各持獨立實例時互不干擾()
    {
        // 「每執行緒一份引擎所以無鎖」這句話，只有在引擎確實沒有共用可變狀態時才成立。
        var results = new long[8];
        Parallel.For(0, results.Length, i =>
        {
            var e = new XiangqiEngine();
            results[i] = e.PerftLeaves(3);
        });
        Assert.All(results, r => Assert.Equal(79_666L, r));
    }
}
