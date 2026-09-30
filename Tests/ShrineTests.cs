using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「拜神」的測試：乖度的分數與門檻、未量到的誠實處理、開機時長換算，
/// 以及最重要的一條——祈福語永遠不許變成診斷或命令（「你該清灰」那種句子不准出現）。
/// </summary>
[Collection(WpfCollection.Name)]
public class ShrineTests
{
    // ── 乖度（演算法是玩笑，測試不是玩笑）──────────────────────

    [Fact]
    public void 乖度_無藍屏得滿分且判定超級乖()
    {
        var (score, verdict, detail) = ShrineMath.Obedience(0, 3600);
        Assert.Equal(100, score);
        Assert.Equal("超級乖", verdict);
        Assert.Contains("近 7 天藍屏 0 筆", detail);
        Assert.Contains("本次開機已乖 1 小時 0 分鐘", detail);
    }

    [Theory]
    [InlineData(1, 75)]
    [InlineData(2, 50)]
    [InlineData(3, 25)]
    [InlineData(4, 0)]   // 見底，不得為負
    [InlineData(9, 0)]   // 超過見底也壓在 0
    public void 乖度_藍屏每筆扣二十五分且有下限(int bsod, int expected)
    {
        var (score, _, _) = ShrineMath.Obedience(bsod, 60);
        Assert.Equal(expected, score);
    }

    [Fact]
    public void 乖度_傾印讀不到時拒絕評分而不是當作零()
    {
        var (score, verdict, detail) = ShrineMath.Obedience(null, 60);
        Assert.Null(score);
        Assert.Equal("—", verdict);
        Assert.Contains("拒絕亂猜", detail);
    }

    [Theory]
    [InlineData(100, "超級乖")]
    [InlineData(90, "超級乖")]
    [InlineData(89, "大致乖")]
    [InlineData(70, "大致乖")]
    [InlineData(69, "有點叛逆")]
    [InlineData(40, "有點叛逆")]
    [InlineData(39, "該補貨了")]
    [InlineData(0, "該補貨了")]
    public void 判級_四段門檻文字(int score, string expected)
        => Assert.Equal(expected, ShrineMath.VerdictOf(score));

    // ── 開機時長換算 ──────────────────────────────────────────

    [Theory]
    [InlineData(95 * 3600 + 40 * 60, "3 天 23 小時")]   // 95h40m → 3 天 23 小時
    [InlineData(2 * 3600 + 5 * 60, "2 小時 5 分鐘")]
    [InlineData(7 * 60, "7 分鐘")]
    [InlineData(0, "0 分鐘")]
    [InlineData(-5, "0 分鐘")]   // 防禦：負數不該炸
    public void 開機時長_三段格式且負數不炸(long seconds, string expected)
        => Assert.Equal(expected, ShrineMath.UptimeText(seconds));

    // ── 祈福語紅線：可以許願，不准下診斷 ─────────────────────

    [Fact]
    public void 祈福語_全是許願句且不含診斷或命令字眼()
    {
        Assert.NotEmpty(ShrineMath.Blessings);
        // 誠實紅線：可以說「願你的溫度牆不出現」，不可以說「你該清灰」——
        // 後者是替使用者下診斷，是量測工具不該越過的線
        string[] forbidden = ["清灰", "重灌", "換電源", "換散熱", "該休息", "建議你", "你該", "請更換", "請檢查"];
        foreach (var b in ShrineMath.Blessings)
        {
            Assert.StartsWith("願", b);
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, b);
        }
    }

    [Fact]
    public void 祈福語_同種子必同句且循環不越界()
    {
        Assert.Equal(ShrineMath.PickBlessing(7), ShrineMath.PickBlessing(7));
        for (int i = -3; i < ShrineMath.Blessings.Length * 2 + 3; i++)
        {
            var text = ShrineMath.PickBlessing(i);
            Assert.Contains(text, ShrineMath.Blessings);
        }
        // int.MinValue 的絕對值超出 int 範圍；抽籤函式不得在這個邊界炸掉
        Assert.Contains(ShrineMath.PickBlessing(int.MinValue), ShrineMath.Blessings);
    }

    // ── 視窗本身：可以建構、可以排版，未量到時畫面顯示「—」────

    /// <summary>在 STA 執行緒上跑 WPF 工作；卡住必須視為測試失敗，不可以默默通過。</summary>
    private static void OnSta(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "拜神視窗測試逾時（30 秒未完成）。");
        Assert.Null(failure);
    }

    [Fact]
    public void 拜神視窗_可建構排版且未量到時顯示一槓()
    {
        OnSta(() =>
        {
            WpfEnv.Ensure();
            var win = new ShrineWindow(null, 3600);
            win.Measure(new Size(600, 800));
            win.Arrange(new Rect(0, 0, 600, 800));
            win.UpdateLayout();
            Assert.Equal("—", win.ScoreText.Text);
        });
    }

    [Fact]
    public void 拜神視窗_乖乖本體使用實際包裝資源()
    {
        OnSta(() =>
        {
            WpfEnv.Ensure();
            var win = new ShrineWindow(0, 60);
            var image = win.FindName("GuaiImage") as System.Windows.Controls.Image;
            Assert.NotNull(image);
            Assert.NotNull(image.Source);
        });
    }

    [Fact]
    public void 拜神視窗_動態效果關閉時按拜只顯示句子不播動畫()
    {
        OnSta(() =>
        {
            WpfEnv.Ensure();
            var win = new ShrineWindow(0, 60);
            using (Motion.Suspend())
            {
                win.PrayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, win.BlessingCard.Visibility);
                Assert.Equal(1, win.BlessingCard.Opacity);
                Assert.False(string.IsNullOrWhiteSpace(win.BlessingText.Text));
                Assert.Equal(0, win.Smoke1.Opacity);
            }
        });
    }
}