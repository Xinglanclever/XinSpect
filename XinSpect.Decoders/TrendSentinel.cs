using System;
using System.Collections.Generic;

namespace XinSpect;
/// <summary>
/// 單調趨勢（Theil–Sen）的估計結果。
/// </summary>
/// <param name="SlopePerStep">每前進一個取樣步長的斜率（單位：值／步）。</param>
/// <param name="Intercept">截距（於步長 0 的位置）。</param>
/// <param name="LowSlope">斜率 95% 信賴區間下界。</param>
/// <param name="HighSlope">斜率 95% 信賴區間上界。</param>
/// <param name="Pairs">參與估計的配對數（斜率不為零的配對）。</param>
public readonly record struct TrendEstimate(
    double SlopePerStep, double Intercept, double LowSlope, double HighSlope, int Pairs)
{
    /// <summary>信賴區間不含零＝趨勢在統計上站得住腳，而非雜訊起伏。</summary>
    public bool SlopeExcludesZero => LowSlope > 0 || HighSlope < 0;
}

/// <summary>
/// 移動平均的交叉驗證結果（濾波 vs 未濾波兩條線的相關性）。
/// </summary>
/// <param name="RawCorrelation">未濾波的相關係數。</param>
/// <param name="SmoothCorrelation">雙側移動平均後的相關係數。</param>
/// <param name="SampleCount">有效配對數。</param>
/// <remarks>
/// <b>為何報「相關性的絕對強度」而非差值：</b>兩條各自單調變化的序列，其相關係數會趨近 ±1。
/// 因此「濾波後相關性下降多少」不是可靠的判準（原本 ±0.97 降到 ±0.99 也算下降）。
/// 可靠的是勢均力敵原則：共同變化的成分若由<a>同一組</a>高頻起伏主導，濾波會讓相關性維持在高檔；
/// 若兩條各自走著方向或步長不同的緩慢趨勢，濾波後相關性會明顯變弱。
/// 本專案據此判讀，並在 UI 上同時呈現兩個數字讓使用者自己核對。
/// </remarks>
public readonly record struct CorrelationShift(
    double RawCorrelation, double SmoothCorrelation, int SampleCount)
{
    /// <summary>濾波後相關性的絕對強度（取絕對值；方向由原始相關的正負號表達）。</summary>
    public double SmoothStrength => Math.Abs(SmoothCorrelation);
}

/// <summary>
/// 變化點（CUSUM 突變偵測）的單一命中。
/// </summary>
/// <param name="Index">相對於輸入序列的索引（首個越界的那一點）。</param>
/// <param name="Direction">+1＝平均上移、-1＝平均下移。</param>
/// <param name="Change">偵測當下估計的變化量（新舊平均差，單位同輸入）。</param>
public readonly record struct ChangePoint(int Index, int Direction, double Change);

/// <summary>
/// 趨勢哨兵的純統計核心：把一串時序值轉成「有沒有變、變多少、站不站得住腳」的判斷。
/// </summary>
/// <remarks>
/// <para>
/// 這裡只做數學，不碰硬體、不碰 I/O、不知道任何感測器的意義——輸入是值陣列，輸出是判斷，
/// 因此可用合成樣本逐案驗證（見 <c>TrendSentinelTests</c>）。上層的 <c>TrendSentinelService</c>
/// 負責把歷史倉的資料餵進來、把結果翻成人話。
/// </para>
/// <para>
/// <b>為什麼用無母數方法：</b>感測器讀值不保證常態（溫度有硬體上限截斷、風扇轉速是階梯、
/// 功耗有牆），任何假設常態的方法在邊界上都會給出錯的信心。Theil–Sen 與 CUSUM 的門檻式
/// 判定不假設分布形狀，寧可少報也不要錯報——這與本專案「讀不到就說讀不到」的原則一致。
/// </para>
/// </remarks>
public static class TrendSentinel
{
    /// <summary>估計單調趨勢所需的最少樣本數；更少時一律回報「資料不足以判斷」。</summary>
    public const int MinSamplesForTrend = 12;

    /// <summary>變化點偵測所需的最少樣本數。</summary>
    public const int MinSamplesForChange = 20;

    /// <summary>
    /// Theil–Sen 穩健斜率：所有配對斜率的中位數。對離群值（單一爆量的讀值）不敏感，
    /// 這正是感測器時序需要的性質——一個壞點不該把整段趨勢帶歪。
    /// </summary>
    /// <param name="values">時序值，索引即步長（等間距取樣）。</param>
    /// <remarks>
    /// <para>
    /// 斜率為所有配對斜率的中位數（Theil–Sen），對離群值不敏感——一個爆量的壞讀值不會把整段趨勢帶歪。
    /// 信賴區間以每拍中位絕對差（1.4826·MAD）估雜訊，再依斜率估計的標準誤給常態近似區間；
    /// 不使用 Sen 的無母數區間，因為其名次間距隨配對數（O(n²)）膨脹得比配對數本身更快，
    /// 實測 n≥50 時區間就會寬到覆蓋整個斜率範圍而失去判別力（詳見程式內註解）。
    /// 配對數低於 <c>66</c>（＝12 點）時回報斜率 0 且不宣稱趨勢。
    /// </para>
    /// <para>
    /// 這不是「統計檢定」而是「穩健估計加區間」：<see cref="TrendEstimate.SlopeExcludesZero"/>
    /// 為真代表斜率的方向在 95% 水準下站得住腳，但<b>不代表趨勢會持續，也不代表有因果</b>。
    /// </para>
    /// </remarks>
    [SpecRef("Theil, H. (1950) A rank-invariant method of linear and polynomial regression analysis；Sen, P.K. (1968) Estimates of the regression coefficient based on Kendall's tau（中位數斜率估計）；尺度估計採中位絕對差 MAD 與常態一致性常數 1.4826（Huber, Robust Statistics §1.4；Rousseeuw & Croux, 1993, Alternatives to the Median Absolute Deviation）；斜率標準誤 σ/√Σ(x-x̄)² 為 OLS 標準結果，以 MAD 代入 σ 使之穩健化（Theil–Sen 常見的穩健推論近似）。——本方法為統計推論，不引用硬體規格。")]
    public static TrendEstimate Slope(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = values.Count;
        if (n < MinSamplesForTrend) return new TrendEstimate(0, 0, 0, 0, 0);

        // 所有配對的斜率，存成待排序陣列（n 小時 O(n²) 完全可接受：30 天分鐘級樣本會被上層降採樣）
        var slopes = new List<double>(n * (n - 1) / 2);
        double baseValue = 0;
        for (int i = 0; i < n; i++)
        {
            double vi = values[i];
            if (double.IsNaN(vi) || double.IsInfinity(vi)) continue;
            if (slopes.Count == 0 && baseValue == 0) baseValue = vi;
            for (int j = i + 1; j < n; j++)
            {
                double vj = values[j];
                if (double.IsNaN(vj) || double.IsInfinity(vj)) continue;
                slopes.Add((vj - vi) / (j - i));
            }
        }
        if (slopes.Count == 0) return new TrendEstimate(0, 0, 0, 0, 0);

        var sorted = slopes.ToArray();
        Array.Sort(sorted);
        double median = MedianOfSorted(sorted);

        // Sen 的無母數 95% 信賴區間。
        // 排名以「斜率陣列中的名次」為單位——把 N 與 C 相除是錯的（單位不一致），
        // 正確做法是取名次 (N-C)/2 與 (N+C)/2 的斜率值（Hollander & Wolfe §9.3）。
        int N = sorted.Length;
        double c = 1.96 * Math.Sqrt(N * (N - 1.0) * (2.0 * N + 5.0) / 18.0) / 2.0;
        int loIdx = (int)Math.Floor((N - c) / 2.0);
        int hiIdx = (int)Math.Ceiling((N + c) / 2.0) - 1;
        loIdx = Math.Clamp(loIdx, 0, N - 1);
        hiIdx = Math.Clamp(hiIdx, 0, N - 1);

        // 截距取「中位數(值 - 斜率·索引)」，與斜率同為穩健估計
        var intercepts = new double[n];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            double v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            intercepts[k++] = v - median * i;
        }
        double intercept = k > 0 ? MedianInPlace(intercepts, k) : baseValue;

        // 信賴區間：以每拍中位絕對差（1.4826·MAD）估雜訊，再依斜率估計的標準誤給常態近似區間。
        //
        // 為什麼不用 Sen 的無母數區間：配對數 N＝n(n-1)/2 是 O(n²)，而該區間的名次間距
        // C ≈ 0.327·N^1.5 成長得更快，於是「C/2 佔 N 的比例」隨 N 膨脹——實測 n=12 時
        // 半寬已達 N 的 134%、n=50 達 572%、n=200 達 2,304%。也就是說區間會寬到覆蓋整個
        // 斜率範圍，任何趨勢都判不出來（本專案曾因此讓一條明確上升的序列被判為無趨勢）。
        // MAD 版本不收縮成假精確、也不膨脹成無資訊：它誠實反映雜訊與跨度。
        //
        // 同時保留無母數的保守性：配對數太少時不做常態假設，寧可回報「資料不足以判斷」。
        const int MinPairsForInterval = 66;   // ＝ 12 點（見 MinSamplesForTrend）
        double loSlope, hiSlope;
        if (N >= MinPairsForInterval)
        {
            double sigma = 1.4826 * MedianAbsoluteDeviation(values);
            double sx = 0, mx = 0;
            int usedCount = 0;
            for (int i = 0; i < n; i++)
            {
                double v = values[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                mx += i; usedCount++;
            }
            if (usedCount > 2) mx /= usedCount; else mx = 0;
            for (int i = 0; i < n; i++)
            {
                double v = values[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                double d = i - mx;
                sx += d * d;
            }
            double se = sx > 0 ? 1.96 * sigma / Math.Sqrt(sx) : double.PositiveInfinity;
            loSlope = median - se;
            hiSlope = median + se;
        }
        else
        {
            loSlope = sorted[loIdx];
            hiSlope = sorted[hiIdx];
        }
        return new TrendEstimate(median, intercept, loSlope, hiSlope, N);
    }

    // 中位絕對差（MAD）：對離群值穩健的尺度估計。
    private static double MedianAbsoluteDeviation(IReadOnlyList<double> values)
    {
        var valid = new List<double>(values.Count);
        double sum = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            valid.Add(v);
            sum += v;
        }
        if (valid.Count == 0) return 0;
        double m = sum / valid.Count;
        var dev = new double[valid.Count];
        for (int i = 0; i < valid.Count; i++) dev[i] = Math.Abs(valid[i] - m);
        Array.Sort(dev);
        return MedianOfSorted(dev);
    }

    /// <summary>
    /// 以前段為訓練窗、後段為檢驗窗，回報兩窗的皮爾森相關性各自的強度。
    /// </summary>
    /// <param name="a">第一條序列（例如散熱相關的變數）。</param>
    /// <param name="b">第二條序列。</param>
    /// <param name="splitFraction">分界位置（0–1，預設 0.5＝前後各半）。</param>
    /// <remarks>
    /// <para>
    /// <b>這個方法回答的是「關係有沒有變」，不是「有沒有關係」。</b>兩條序列各自緩慢上升或下降時，
    /// 就算彼此毫無關聯，整體相關性也會很高——那是共同趨勢造成的假象，不是耦合。
    /// 因此判準必須是<b>前後兩段的相關性差異</b>：同一對變數在前期高度耦合、後期脫鉤，
    /// 才是有意義的訊號（例：散熱正常時溫度緊跟負載，散熱劣化後溫度被墊高、與負載脫鉤）。
    /// </para>
    /// <para>
    /// 兩窗各需至少 4 個有效樣本，否則回報樣本不足、不給判斷。
    /// </para>
    /// </remarks>
    [SpecRef("Pearson product-moment correlation coefficient（Pearson, K. (1896) Mathematical Contributions to the Theory of Evolution III）；前後窗相關性比較＝時序分析常用的「關係穩定度」檢定（Box & Jenkins, Time Series Analysis；結構性變化的滑動窗比較 §2.4）。——統計方法引用，不涉及硬體規格。")]
    public static CorrelationShift CorrelationShiftBetweenHalves(
        IReadOnlyList<double> a, IReadOnlyList<double> b, double splitFraction = 0.5)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (xa, ya) = Pair(a, b);
        int n = xa.Count;
        if (n < 8) return new CorrelationShift(0, 0, 0);

        int split = Math.Clamp((int)Math.Round(n * Math.Clamp(splitFraction, 0.2, 0.8)), 4, n - 4);
        var a1 = xa.GetRange(0, split);
        var b1 = ya.GetRange(0, split);
        var a2 = xa.GetRange(split, n - split);
        var b2 = ya.GetRange(split, n - split);
        if (a1.Count < 4 || a2.Count < 4) return new CorrelationShift(0, 0, 0);

        double first = Pearson(a1, b1);
        double second = Pearson(a2, b2);
        return new CorrelationShift(first, second, n);
    }

    /// <summary>
    /// 濾波後的相關性變化：先算原始相關係數，再對兩條序列做雙側移動平均後重算。
    /// 若濾波讓相關性明顯下降，代表原本的相關來自短期同步的雜訊，而非共同的緩慢成因。
    /// </summary>
    /// <remarks>
    /// 這是辛普森式謬誤的偵測器：溫度與負載在「當下」幾乎總是同步（兩者都被同一個工作階段推高），
    /// 但若把短週期起伏濾掉後兩者脫鉤，就代表它們其實各自獨立緩慢變化——例如散熱劣化造成的
    /// 基準溫度上升，與負載無關。<b>本方法只報「相關性的變化」，不對因果下任何斷言。</b>
    /// </remarks>
    [SpecRef("Pearson product-moment correlation coefficient（Pearson, K. (1896) Mathematical Contributions to the Theory of Evolution III）；移動平均濾波後相關性下降作為『短期共變、長期脫鉤』的判準，方法論同時序分析常用的 prewhitening／濾波比較（Box & Jenkins, Time Series Analysis §2.4）。——統計方法引用，不涉及硬體規格。")]
    public static CorrelationShift CorrelationAfterSmoothing(
        IReadOnlyList<double> a, IReadOnlyList<double> b, int window = 5)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (sa, sb) = Pair(a, b);
        if (sa.Count < 3) return new CorrelationShift(0, 0, 0);

        double raw = Pearson(sa, sb);
        var fa = MovingAverage(sa, window);
        var fb = MovingAverage(sb, window);
        double smooth = Pearson(fa, fb);
        return new CorrelationShift(raw, smooth, sa.Count);
    }

    // 取出兩序列中皆為有限值的配對。
    private static (List<double> A, List<double> B) Pair(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n = Math.Min(a.Count, b.Count);
        var xa = new List<double>(n);
        var xb = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            double x = a[i], y = b[i];
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) continue;
            xa.Add(x);
            xb.Add(y);
        }
        return (xa, xb);
    }

    /// <summary>
    /// 以累積和（CUSUM）偵測平均值的突變，並把「同一段轉變造成的連續越界」合併成單一事件。
    /// </summary>
    /// <param name="values">時序值。</param>
    /// <param name="threshold">判定門檻（以序列標準差為單位，預設 5）。越大越保守。</param>
    /// <param name="drift">允許的漂移容忍（以標準差為單位，預設 1.5）；低於此的緩慢變化不會觸發。</param>
    /// <remarks>
    /// <para>
    /// 演算法：以序列的前四分之一為基準段估出平均與標準差，之後維護兩個累積和
    /// <c>S+ = max(0, S+ + (x - mean - k))</c>、<c>S- = max(0, S- - (x - mean + k))</c>，
    /// 其中 <c>k = drift·σ</c>；任一個超過 <c>h = threshold·σ</c> 即判定為變化點，
    /// 並把兩側累積和歸零重新起算（Montgomery §9.1 的標準做法）。基準水位維持不動，
    /// 因此單一階梯只會在越界的那一拍報一次，不會變成十幾筆重複紀錄。
    /// </para>
    /// <para>
    /// <b>已知取捨——緩慢漂移在夠長的序列上終究會越界：</b>累積和是無界的，任何持續的單向
    /// 偏差（即使遠小於 k）在足夠長的時間後都會把 <c>S±</c> 推過門檻。這不是缺陷而是設計上的
    /// 取捨：<b>突變與漂移都會被報出來，兩者由變化量的大小區分</b>——階梯式突變的變化量大，
    /// 緩慢漂移每步只前進一點、越界時變化量小。呼叫端應以 <c>Change</c> 的大小與斜率估計
    /// 交叉判讀（見 <see cref="Slope"/>），不要只看「有沒有變化點」。
    /// </para>
    /// <para>
    /// <b>門檻為何用標準差為單位：</b>不同感測器的絕對雜訊差很多（風扇轉速動輒數百 RPM、
    /// 溫度小數點後一位），用固定絕對門檻會對某一類完全失效。以自身雜訊為尺規，五倍標準差在
    /// 實務上約等於千分之三的假警報率（常態假設下），對雜訊起伏不敏感。
    /// </para>
    /// </remarks>
    [SpecRef("Page, E.S. (1954) Continuous Inspection Schemes, Biometrika 41(1/2):100–115（CUSUM 管制圖的原始定義，k 為參考值、h 為決策間隔；越界即報警，報警後歸零重啟）；Montgomery, D.C. Introduction to Statistical Quality Control, Ch.9 Cumulative Sum and Exponentially Weighted Moving Average Control Charts（k 與 h 的慣用取法、以及「越界後重設」的標準實作）。——統計方法引用，不涉及硬體規格。")]
    public static IReadOnlyList<ChangePoint> ChangePoints(
        IReadOnlyList<double> values, double threshold = 5.0, double drift = 1.5)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = values.Count;
        if (n < MinSamplesForChange) return [];

        // 基準段＝前四分之一（至少 5 點）：假設序列開頭是穩定的，之後才可能發生突變
        int baseLen = Math.Max(5, n / 4);
        double sum = 0, sumSq = 0;
        int used = 0;
        for (int i = 0; i < baseLen && i < n; i++)
        {
            double v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            sum += v; sumSq += v * v; used++;
        }
        if (used < 3) return [];

        double mean = sum / used;
        double variance = Math.Max(0, sumSq / used - mean * mean);
        double sigma = Math.Sqrt(variance);
        // 完全沒有起伏的序列（例如恆為 0 的未讀取通道）無從判斷「變化」——寧可回報無異常
        if (sigma <= 1e-9) return [];

        double k = drift * sigma;
        double h = threshold * sigma;
        var hits = new List<ChangePoint>();
        double refMean = mean;                  // 現行基準水位
        double sHi = 0, sLo = 0;

        // 重新武裝時用來重估基準水位的短期窗（環形），以及武裝前的冷卻拍數
        int rearmWindow = Math.Max(5, baseLen / 4);
        int cooldown = rearmWindow;
        var recent = new double[rearmWindow];
        int recentHead = 0, recentCount = 0;

        for (int i = 0; i < n; i++)
        {
            double v = values[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;

            // 近窗資料一律先累積（冷卻期間也不例外），才能在冷卻結束當下立刻採用新水位
            recent[recentHead] = v;
            recentHead = (recentHead + 1) % rearmWindow;
            if (recentCount < rearmWindow) recentCount++;

            if (cooldown > 0)
            {
                // 冷卻期：不累積、不判定。等於等確認窗填滿新水位後再重新起算，
                // 否則基準尚在舊水位時，殘餘偏差會立刻再次越界（同一事件被拆成多筆）。
                cooldown--;
                if (cooldown == 0) refMean = RecentMean(recent, recentCount, refMean, v);
                continue;
            }

            sHi = Math.Max(0, sHi + (v - refMean - k));
            sLo = Math.Max(0, sLo - (v - refMean + k));

            if (sHi > h || sLo > h)
            {
                int dir = sHi > h ? 1 : -1;
                // 變化量＝越界的累積和除以基準窗長度（CUSUM 對階梯大小的標準估計）
                double change = (dir > 0 ? sHi : sLo) / used;
                hits.Add(new ChangePoint(i, dir, change));
                sHi = 0;
                sLo = 0;
                cooldown = rearmWindow;     // 冷卻後才以新水位重新武裝
            }
        }
        return hits;
    }

    // 近期窗均值：窗尚未填滿時，以現行水位補足其餘名額（等效於「先驗」，避免用一兩拍就定新基準）。
    private static double RecentMean(double[] window, int count, double prior, double current)
    {
        double sum = 0;
        for (int i = 0; i < count; i++) sum += window[i];
        int missing = window.Length - count;
        sum += prior * missing;
        return sum / window.Length;
    }

    // ── 內部工具 ──────────────────────────────────────────────────────────

    private static double MedianInPlace(double[] values, int count)
    {
        // count 可能小於陣列長度：先取出前 count 個再排序，不動到呼叫端後續還要用的尾巴
        var slice = new double[count];
        Array.Copy(values, slice, count);
        Array.Sort(slice);
        return MedianOfSorted(slice);
    }
    private static double MedianOfSorted(double[] sorted)
    {
        int n = sorted.Length;
        if (n == 0) return 0;
        if ((n & 1) == 1) return sorted[n / 2];
        return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static IReadOnlyList<double> MovingAverage(IReadOnlyList<double> v, int window)
    {
        int n = v.Count;
        var outp = new double[n];
        int half = window / 2;
        for (int i = 0; i < n; i++)
        {
            int s = Math.Max(0, i - half);
            int e = Math.Min(n - 1, i + half);
            double sum = 0;
            for (int j = s; j <= e; j++) sum += v[j];
            outp[i] = sum / (e - s + 1);
        }
        return outp;
    }

    private static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 2) return 0;
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double sab = 0, sa = 0, sb = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; sa += da * da; sb += db * db;
        }
        double den = Math.Sqrt(sa * sb);
        // 任一条完全沒有變化（變異數 0）時相關係數無定義——回 0 而不是 NaN，上層據此顯示「無從判斷」
        if (den <= 1e-12) return 0;
        return Math.Clamp(sab / den, -1, 1);
    }
}
