using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「五族共和」五行輪的幾何與名單。
///
/// 這一份守的主要是一件會安靜畫錯的事：<b>五角星要跨兩格連線</b>。跨一格照樣是一條閉合的、
/// 經過每個節點一次的路徑，畫出來卻是外圈那個五邊形——圖不會壞、不會拋例外，只是變成另一張圖。
/// </summary>
public class FiveElementsTests
{
    [Fact]
    public void 五個節點就是五行且順時針從正上方的金開始()
    {
        Assert.Equal(5, FiveElements.Nodes.Count);
        Assert.Equal(["金", "木", "水", "火", "土"], FiveElements.Nodes.Select(n => n.Element));
        Assert.Equal([0d, 72, 144, 216, 288], FiveElements.Nodes.Select(n => n.AngleDeg));
    }

    [Fact]
    public void 每個位置都有名字而且不重複()
    {
        var names = FiveElements.Nodes.Select(n => n.Label).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 零度指向正上方而九十度指向右方()
    {
        // 螢幕座標系：y 軸向下，所以「上」是 −1。這個號誤搞反，整張圖會上下顛倒。
        var up = new FiveElementNode("金", "", 0).Unit;
        Assert.Equal(0, up.X, 9);
        Assert.Equal(-1, up.Y, 9);

        var right = new FiveElementNode("", "", 90).Unit;
        Assert.Equal(1, right.X, 9);
        Assert.Equal(0, right.Y, 9);
    }

    [Fact]
    public void 節點落在指定圓心與半徑上()
    {
        var (x, y) = FiveElements.Nodes[0].At(100, 200, 50);
        Assert.Equal(100, x, 9);
        Assert.Equal(150, y, 9);   // 正上方 → y 減少

        foreach (var n in FiveElements.Nodes)
        {
            var (px, py) = n.At(100, 200, 50);
            Assert.Equal(50, Math.Sqrt((px - 100) * (px - 100) + (py - 200) * (py - 200)), 6);
        }
    }

    [Fact]
    public void 木在右上而水在右下這一點要和參考圖一致()
    {
        // 使用者給的那張傳統五行圖：金正上、木右上、水右下、火左下、土左上。
        // 這裡把「右上」寫成可驗證的條件（x > 0 且 y < 0），排錯了會被抓到。
        var byEl = FiveElements.Nodes.ToDictionary(n => n.Element, n => n.Unit);
        Assert.True(byEl["木"].X > 0 && byEl["木"].Y < 0, "木應在右上");
        Assert.True(byEl["水"].X > 0 && byEl["水"].Y > 0, "水應在右下");
        Assert.True(byEl["火"].X < 0 && byEl["火"].Y > 0, "火應在左下");
        Assert.True(byEl["土"].X < 0 && byEl["土"].Y < 0, "土應在左上");
    }

    [Fact]
    public void 連線順序畫出來是五角星而不是五邊形()
    {
        var order = FiveElements.StarOrder;
        Assert.Equal(5, order.Count);
        Assert.Equal(5, order.Distinct().Count());          // 每個節點都經過一次
        Assert.Equal([0, 1, 2, 3, 4], order.OrderBy(i => i));

        // 關鍵：每一步（含收尾那一步）都要跨兩格。跨一格就是五邊形。
        for (int i = 0; i < order.Count; i++)
        {
            int step = (order[(i + 1) % order.Count] - order[i] + 5) % 5;
            Assert.Equal(2, step);
        }
    }

    [Fact]
    public void 圓心的字就是五族共和()
        => Assert.Equal("五族共和", FiveElements.Caption);
}

/// <summary>
/// 五行輪<b>真的畫得出來</b>。
///
/// 這條測試存在的理由：UI 煙霧測試只做 Measure／Arrange，而自繪元件的 <c>OnRender</c> 要等到
/// 真的算繪才會被呼叫——所以那裡漏掉的錯（筆刷為 null、幾何開了 figure 沒關、FormattedText 參數
/// 不合法）在煙霧測試裡完全看不到，得等使用者切到「關於」頁才炸。這裡把它算繪進一張離屏點陣圖，
/// 逼 <c>OnRender</c> 真的跑一次。
/// </summary>
[Collection(WpfCollection.Name)]
public class FiveElementsWheelRenderTests
{
    [Fact]
    public void 五行輪算繪不會拋例外且畫出非空白的內容()
    {
        Exception? failure = null;
        bool anyInk = false;

        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                var wheel = new FiveElementsWheel();
                wheel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = wheel.DesiredSize;
                wheel.Arrange(new System.Windows.Rect(new System.Windows.Point(0, 0), size));
                wheel.UpdateLayout();

                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height),
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(wheel);

                // 全透明＝什麼都沒畫到。抓的是「元件安靜地畫了一片空白」這種失敗。
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                var px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);
                for (int i = 3; i < px.Length && !anyInk; i += 4)
                    if (px[i] != 0) anyInk = true;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "五行輪算繪逾時");

        Assert.Null(failure);
        Assert.True(anyInk, "五行輪算繪出一整片透明，等於什麼都沒畫");
    }
}
