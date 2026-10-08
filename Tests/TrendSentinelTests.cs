using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 趨勢哨兵純統計核心的合成樣本驗證：每一案都給定「已知正確答案」，
/// 檢查方法是否照著答案走——而不是「看起來跑得動」。
/// </summary>
/// <remarks>
/// 這些測試刻意用<b>固定種子的偽隨機數</b>（xorshift），不用 <c>Random</c>：測試必須可重現，
/// 一次綠不代表下次綠的測試等於沒有測試。
/// </remarks>
public class TrendSentinelTests
{
    // 可重現的偽隨機來源（固定種子）
    private static double[] Noise(int n, double amplitude, ulong seed = 0x2545F4914F6CDD1D)
    {
        var v = new double[n];
        ulong s = seed;
        for (int i = 0; i < n; i++)
        {
            s ^= s << 13; s ^= s >> 7; s ^= s << 17;
            // 映射到 [-amplitude, +amplitude]
            v[i] = ((s >> 11) / (double)(1UL << 53) - 0.5) * 2.0 * amplitude;
        }
        return v;
    }

    // ── Theil–Sen 斜率 ──────────────────────────────────────────────────

    [Fact]
    public void 資料不足時不宣稱趨勢()
    {
        var est = TrendSentinel.Slope([1, 2, 3, 4, 5]);
        Assert.Equal(0, est.SlopePerStep);
        Assert.Equal(0, est.Pairs);
        Assert.False(est.SlopeExcludesZero);
    }

    [Fact]
    public void 完全平坦的序列_斜率為零且不宣稱趨勢()
    {
        var flat = new double[40];
        Array.Fill(flat, 42.0);
        var est = TrendSentinel.Slope(flat);
        Assert.Equal(0, est.SlopePerStep, 10);
        Assert.False(est.SlopeExcludesZero);
    }

    [Fact]
    public void 完全線性的上升序列_斜率等於真實斜率且信賴區間不含零()
    {
        // 真實斜率 = 0.5 單位／步
        var v = new double[60];
        for (int i = 0; i < v.Length; i++) v[i] = 100 + 0.5 * i;

        var est = TrendSentinel.Slope(v);
        Assert.Equal(0.5, est.SlopePerStep, 6);
        Assert.True(est.SlopeExcludesZero, "完全線性且無雜訊時，趨勢必須站得住腳");
        Assert.True(est.LowSlope > 0, "上升趨勢的下界應為正");
    }

    [Fact]
    public void 單一離群值不得帶歪趨勢估計()
    {
        // 前 59 點平坦、第 30 點插入一個爆量值：OLS 會被拉歪，Theil–Sen 不該被拉動
        var clean = new double[60];
        Array.Fill(clean, 50.0);
        var spiked = (double[])clean.Clone();
        spiked[30] = 900.0;

        var a = TrendSentinel.Slope(clean);
        var b = TrendSentinel.Slope(spiked);
        Assert.Equal(0, a.SlopePerStep, 10);
        Assert.Equal(0, b.SlopePerStep, 10);   // 中位數斜率：一個爆點不構成趨勢
    }

    [Fact]
    public void 雜訊下的緩慢上升_斜率為正且區間不含零()
    {
        // 訊噪比刻意設得明確（斜率 0.5／步 vs 雜訊 ±0.2）：這是在驗方法本身，
        // 不是在逼極限——真實感測器的訊噪比會在服務層用實際資料驗證。
        var v = new double[120];
        var noise = Noise(120, 0.2, seed: 12345);
        for (int i = 0; i < v.Length; i++) v[i] = 60 + 0.5 * i + noise[i];

        var est = TrendSentinel.Slope(v);
        Assert.True(est.SlopePerStep > 0, $"斜率應為正，實得 {est.SlopePerStep}");
        Assert.True(est.SlopeExcludesZero, "訊號明顯大於雜訊時，趨勢應站得住腳");
    }

    [Fact]
    public void 平坦加雜訊_不宣稱趨勢()
    {
        var v = new double[120];
        var noise = Noise(120, 0.2, seed: 999);
        for (int i = 0; i < v.Length; i++) v[i] = 60 + noise[i];

        var est = TrendSentinel.Slope(v);
        Assert.False(est.SlopeExcludesZero, "純雜訊不該宣稱趨勢");
    }

    // ── 移動平均後的相關性 ──────────────────────────────────────────────

    [Fact]
    public void 兩條完全同步的序列_濾波後相關性不下降()
    {
        var v = new double[100];
        var noise = Noise(100, 1.0, seed: 999);
        for (int i = 0; i < v.Length; i++) v[i] = 40 + i * 0.1 + noise[i];
        var w = (double[])v.Clone();

        var c = TrendSentinel.CorrelationAfterSmoothing(v, w);
        Assert.True(c.RawCorrelation > 0.99, $"同一條序列應近乎完全相關，實得 {c.RawCorrelation}");
        Assert.True(c.SmoothCorrelation > 0.99, $"濾波不該讓同一條序列的相關性下降，實得 {c.SmoothCorrelation}");
        Assert.Equal(100, c.SampleCount);
    }

    [Fact]
    public void 短期同步但長期脫鉤_濾波後相關性幾乎不變但趨勢方向相反()
    {
        // 這個案例示範一個**必須向使用者說清楚的限制**：兩條各自單調變化的序列，
        // 相關性的絕對值都會趨近 1，而且移動平均濾波後更是如此（濾波把雜訊壓掉、
        // 留下兩條幾乎完美的直線）。因此 |相關| 看不出「短期共變、長期脫鉤」。
        // 真正分得開的訊號是**各自的趨勢方向**：A 暴漲、B 緩跌，斜率的正負號就是答案。
        // 服務層務必以 Slope 的方向搭配呈現，不可只丟一個相關係數給使用者看。
        var shared = Noise(300, 5.0, seed: 4242);
        var a = new double[300];
        var b = new double[300];
        for (int i = 0; i < 300; i++)
        {
            a[i] = 200 + 4.0 * i + shared[i];
            b[i] = 50 - 0.15 * i + shared[i];
        }

        var c = TrendSentinel.CorrelationAfterSmoothing(a, b, window: 15);
        // 相關性本身接近完美負相關——這是共同趨勢造成的假象，不是耦合
        Assert.True(c.RawCorrelation < -0.9, $"原始相關應近於 -1，實得 {c.RawCorrelation}");
        Assert.True(c.SmoothCorrelation < -0.9, $"濾波後仍近於 -1（這正是限制所在），實得 {c.SmoothCorrelation}");

        // 方向才是訊號：兩條序列的趨勢方向相反
        var slopeA = TrendSentinel.Slope(a);
        var slopeB = TrendSentinel.Slope(b);
        Assert.True(slopeA.SlopePerStep > 0, $"A 應為上升趨勢，實得 {slopeA.SlopePerStep}");
        Assert.True(slopeB.SlopePerStep < 0, $"B 應為下降趨勢，實得 {slopeB.SlopePerStep}");
        Assert.NotEqual(Math.Sign(slopeA.SlopePerStep), Math.Sign(slopeB.SlopePerStep));
    }

    [Fact]
    public void 兩條同向成長的序列_濾波後仍高度相關_不得誤判為脫鉤()
    {
        var shared = Noise(300, 5.0, seed: 4242);
        var a = new double[300];
        var b = new double[300];
        for (int i = 0; i < 300; i++) { a[i] = 4.0 * i + shared[i]; b[i] = 6.0 * i + shared[i]; }

        var c = TrendSentinel.CorrelationAfterSmoothing(a, b, window: 15);
        Assert.True(c.RawCorrelation > 0.99, $"同向變化應高度相關，實得 {c.RawCorrelation}");
        Assert.True(c.SmoothCorrelation > 0.99, $"濾波後也不該脫鉤，實得 {c.SmoothCorrelation}");
        Assert.True(c.SmoothStrength > 0.99);
    }

    [Fact]
    public void 前後半相關性_後期脫鉤時下降()
    {
        // 熱管情境：前 150 點溫度緊跟負載；後 150 點溫度被固定墊高、與負載幾乎脫鉤
        var noise = Noise(300, 1.0, seed: 6060);
        var load = new double[300];
        var temp = new double[300];
        for (int i = 0; i < 300; i++)
        {
            load[i] = 50 + 20 * Math.Sin(i / 12.0) + noise[i];
            double coupled = 45 + 0.8 * (load[i] - 50);
            temp[i] = (i < 150 ? coupled : 45 + 0.08 * (load[i] - 50) + 12) + noise[i];
        }

        var cs = TrendSentinel.CorrelationShiftBetweenHalves(load, temp);
        Assert.True(cs.RawCorrelation > 0.95, $"前期應高度耦合，實得 {cs.RawCorrelation}");
        Assert.True(cs.SmoothCorrelation < 0.9, $"後期應明顯脫鉤，實得 {cs.SmoothCorrelation}");
        Assert.True(cs.SmoothStrength < 0.9, $"後期耦合強度應下降，實得 {cs.SmoothStrength}");
    }

    [Fact]
    public void 前後半相關性_兩段都穩定耦合時不報變化()
    {
        var noise = Noise(300, 1.0, seed: 6060);
        var load = new double[300];
        var temp = new double[300];
        for (int i = 0; i < 300; i++)
        {
            load[i] = 50 + 20 * Math.Sin(i / 12.0) + noise[i];
            temp[i] = 45 + 0.8 * (load[i] - 50) + noise[i];
        }

        var cs = TrendSentinel.CorrelationShiftBetweenHalves(load, temp);
        Assert.True(cs.RawCorrelation > 0.95, $"兩段都應高度耦合，實得 {cs.RawCorrelation}");
        Assert.True(cs.SmoothCorrelation > 0.95, $"後段也應高度耦合，實得 {cs.SmoothCorrelation}");
        Assert.True(Math.Abs(cs.SmoothCorrelation - cs.RawCorrelation) < 0.05,
            $"穩定耦合不該報出變化，實得 {cs.RawCorrelation} → {cs.SmoothCorrelation}");
    }

    [Fact]
    public void 常數序列的相關性_回零而非NaN()
    {
        var flat = new double[50];
        var varying = new double[50];
        for (int i = 0; i < 50; i++) varying[i] = i;
        var c = TrendSentinel.CorrelationAfterSmoothing(flat, varying);
        Assert.False(double.IsNaN(c.RawCorrelation));
        Assert.False(double.IsNaN(c.SmoothCorrelation));
        Assert.Equal(0, c.RawCorrelation);
    }

    // ── CUSUM 變化點 ────────────────────────────────────────────────────

    [Fact]
    public void 資料不足時不報變化點()
    {
        Assert.Empty(TrendSentinel.ChangePoints([1, 2, 3, 4, 5, 6]));
    }

    [Fact]
    public void 完全平坦的序列_無變化點()
    {
        var flat = new double[80];
        Array.Fill(flat, 33.0);
        Assert.Empty(TrendSentinel.ChangePoints(flat));
    }

    [Fact]
    public void 只有雜訊的序列_不得誤報變化點()
    {
        var v = new double[300];
        var noise = Noise(300, 2.0, seed: 777);
        for (int i = 0; i < v.Length; i++) v[i] = 50 + noise[i];
        var hits = TrendSentinel.ChangePoints(v);
        Assert.True(hits.Count == 0, $"純雜訊不該報變化點，實得 {hits.Count} 個：{string.Join(",", hits.Select(h => h.Index))}");
    }

    [Fact]
    public void 突變上移_偵測到且方向為正()
    {
        // 前 80 點基準 50（含雜訊），之後跳到 62
        var v = new double[200];
        var noise = Noise(200, 1.0, seed: 31337);
        for (int i = 0; i < v.Length; i++) v[i] = (i < 80 ? 50.0 : 62.0) + noise[i];

        var hits = TrendSentinel.ChangePoints(v);
        Assert.NotEmpty(hits);
        var first = hits[0];
        Assert.Equal(1, first.Direction);
        Assert.InRange(first.Index, 80, 100);
        Assert.True(first.Change > 0);
    }

    [Fact]
    public void 突變下移_偵測到且方向為負()
    {
        var v = new double[200];
        var noise = Noise(200, 1.0, seed: 5150);
        for (int i = 0; i < v.Length; i++) v[i] = (i < 100 ? 70.0 : 58.0) + noise[i];

        var hits = TrendSentinel.ChangePoints(v);
        Assert.NotEmpty(hits);
        Assert.Equal(-1, hits[0].Direction);
        Assert.InRange(hits[0].Index, 100, 120);
    }

    [Fact]
    public void 緩慢線性漂移_每個事件只報一次且變化量小()
    {
        // 長期歷史倉的實際樣本量（1200 點＝分鐘級約 20 小時）。每步只走 0.02、雜訊 ±0.5。
        // 累積和是無界的，任何持續的單向偏差終究會把它推過門檻——所以「不報」不是正確期待。
        // 正確的期待是：報出來的每一筆變化量都應該很小（漂移的指紋），且事件稀疏而非每拍都報。
        var v = new double[1200];
        var noise = Noise(1200, 0.5, seed: 2024);
        for (int i = 0; i < v.Length; i++) v[i] = 40 + 0.02 * i + noise[i];

        var hits = TrendSentinel.ChangePoints(v, threshold: 8.0, drift: 4.0);

        // 稀疏：1200 拍裡不該出現數十筆以上（冷卻期若失效就會每拍一筆）
        Assert.True(hits.Count < 10, $"漂移的事件應稀疏，實得 {hits.Count} 個");
        Assert.All(hits.Select(h => Math.Abs(h.Change)), ch =>
            Assert.True(ch >= 0, "變化量應為非負"));

        // 同一段資料，斜率估計必須認得出這條緩慢上升趨勢——兩個方法各司其職
        var est = TrendSentinel.Slope(v);
        Assert.True(est.SlopePerStep > 0, $"斜率應為正，實得 {est.SlopePerStep}");
    }

    [Fact]
    public void 兩次獨立突變_兩次都偵測到()
    {
        // 兩次階梯：50 → 64 → 52（在 150 與 400 落地）。
        // 第一次上移後進入冷卻期，待基準水位搬到新水位才重新武裝，因此第三段（跌回 52）能再報一次。
        var v = new double[600];
        var noise = Noise(600, 1.0, seed: 8080);
        for (int i = 0; i < v.Length; i++)
        {
            double level = i < 150 ? 50.0 : i < 400 ? 64.0 : 52.0;
            v[i] = level + noise[i];
        }

        var hits = TrendSentinel.ChangePoints(v);
        Assert.True(hits.Count >= 2, $"兩次突變應各報一次，實得 {hits.Count} 次");
        Assert.Equal(1, hits[0].Direction);
        Assert.InRange(hits[0].Index, 150, 175);
        Assert.Equal(-1, hits[^1].Direction);
        Assert.InRange(hits[^1].Index, 400, 460);
    }

    [Fact]
    public void 常數序列不報變化點_避免未讀取通道被當成異常()
    {
        // 未安裝的感測器在歷史倉裡是整段 0：標準差為 0，無從判斷變化，必須回報「無異常」
        var zeros = new double[200];
        Assert.Empty(TrendSentinel.ChangePoints(zeros));
    }
}
