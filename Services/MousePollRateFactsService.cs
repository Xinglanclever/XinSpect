namespace XinSpect;

/// <summary>
/// 滑鼠輪詢率實測（Vol2 第 GP-004 項）：<c>gp.pollrate</c>——輸入裝置回報率。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項的難點不是量測，是「量不到時怎麼說」。</b>輪詢率要靠連續讀 HID 輸入報告
/// 量時間間隔，而這需要一個<b>作用中的輸入裝置</b>——沒有滑鼠、遠端連線、
/// 或使用者根本沒動滑鼠時，量到的數字是 0 而不是「1000 Hz」。
/// </para>
/// <para>
/// <b>紅線：</b>① <b>沒量到＝不適用，不是 0 Hz</b>——「這台機器沒接滑鼠」與
/// 「滑鼠回報率 0」是完全不同的兩件事，後者在物理上不存在；
/// ② <b>取樣期間必須標註</b>：量測值只在取樣窗內有效，不同窗量到不同數字是正常的
/// （尤其是省電模式下 OS 會降取樣率）；
/// ③ <b>不從裝置型號推論</b>：登錄裡的宣稱值（如「1000Hz 電競滑鼠」）是行銷數字，
/// 不是實測值，兩者不可混。
/// </para>
/// </remarks>
public static class MousePollRateFactsService
{
    private const string Cat = "遊戲與繪圖";
    private const string Source = "Raw Input 取樣（唯讀，不注入任何輸入）";

    /// <summary>GP-004 滑鼠輪詢率。</summary>
    public const string PollRateKey = "gp.pollrate";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>
    /// （樣本數, 中位間隔微秒, 換算 Hz, 裝置名稱可讀, 取樣窗毫秒）。
    /// <see cref="Samples"/> 為 0 ＝取樣窗內沒有任何輸入事件。
    /// </summary>
    public sealed record PollSample(int Samples, double MedianIntervalUs, double Hz,
        bool DeviceReadable, int WindowMs);

    public delegate PollSample? Probe();

    // ── 判讀（可單獨測試的純函式）─────────────────────────────────────────

    /// <summary>
    /// 由間隔樣本算輪詢率。<b>用中位數不用平均</b>：使用者移動滑鼠的節奏本身不均勻，
    /// 平均值會被少數長間隔（手停下來）拉低，中位數反映的是「裝置在報告時報得多快」。
    /// </summary>
    /// <param name="intervalsUs">相鄰兩個輸入報告的間隔（微秒）。</param>
    /// <param name="windowMs">取樣窗長度。</param>
    public static PollSample Summarize(IReadOnlyList<double> intervalsUs, int windowMs,
        bool deviceReadable = true)
    {
        if (intervalsUs.Count == 0)
            return new PollSample(0, 0, 0, deviceReadable, windowMs);

        var sorted = intervalsUs.OrderBy(x => x).ToList();
        // 中位數：偶數取中間兩個的平均（與本專案其他中位數的取法一致）。
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        double hz = median > 0 ? 1_000_000.0 / median : 0;
        return new PollSample(sorted.Count, median, hz, deviceReadable, windowMs);
    }

    /// <summary>
    /// 把量到的 Hz 對到最接近的<b>常見回報率檔位</b>。
    /// <b>這是標籤不是判決</b>——量到 998 Hz 與 1002 Hz 都是 1000 Hz 檔，
    /// 但量到 700 Hz 不該被硬歸成 1000 Hz，所以門檻取相對誤差 10%。
    /// </summary>
    public static string NearestRateLabel(double hz)
    {
        if (hz <= 0) return "未量到";
        double[] tiers = [125, 250, 500, 1000, 2000, 4000, 8000];
        double best = tiers[0];
        double bestErr = double.MaxValue;
        foreach (double t in tiers)
        {
            double err = Math.Abs(hz - t) / t;
            if (err < bestErr) { bestErr = err; best = t; }
        }
        return bestErr <= 0.10
            ? $"≈{best:N0} Hz 檔"
            : $"未對到常見檔位（量到 {hz:N0} Hz）";
    }

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static HardwareFact Collect(DateTimeOffset at, Probe? probe = null)
    {
        PollSample? s;
        try { s = (probe ?? ReadSample)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, FactAvailability.ReadError,
                "滑鼠輸入取樣失敗——讀不到就是不猜（不從裝置型號推論回報率）");

        var v = s;

        // 取樣窗內一個事件都沒有 → 不適用。這不是 0 Hz。
        if (v.Samples == 0)
            return Unavailable(at, FactAvailability.NotApplicable,
                $"取樣窗（{v.WindowMs} ms）內沒有收到任何滑鼠輸入——不適用，不是 0 Hz。" +
                "「這台機器沒接滑鼠」與「滑鼠回報率 0」是完全不同的兩件事，後者在物理上不存在；" +
                "遠端連線、無輸入裝置、或取樣期間沒人動滑鼠都會落到這一態");

        var vv = v;
        string label = NearestRateLabel(vv.Hz);
        string device = vv.DeviceReadable
            ? ""
            : "・<b>裝置名稱讀不到</b>（型號不在值裡，回報率本來就不需要型號）";

        return new HardwareFact(PollRateKey, Cat, "滑鼠輪詢率",
            $"{label}・中位間隔 {vv.MedianIntervalUs:N0} µs・樣本 {vv.Samples} 筆" +
            $"（取樣窗 {vv.WindowMs} ms）{device}。" +
            "<b>取樣期間必須被記住</b>：量測值只在這個窗內有效——" +
            "不同窗量到不同數字是正常的（省電模式下作業系統會主動降低取樣率，" +
            "所以同一隻滑鼠在電池模式可能量到 125 Hz、接電時 1000 Hz）。" +
            "<b>用中位數不用平均</b>：手的移動節奏本身不均勻，" +
            "平均值會被「手停下來」的長間隔拉低；中位數反映的是裝置在報告時報得多快。" +
            "<b>不從型號推論</b>：登錄或包裝上的「1000 Hz 電競滑鼠」是行銷數字，" +
            "與本機實測值不可混為一談。",
            "Hz", Source, FactTrustLevel.Measured, false, at, vv.Hz);
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 取樣窗長度。500 ms 足夠量到 125 Hz 的 60+ 個樣本，又短到不會讓收集流程卡住。
    /// </summary>
    internal const int SampleWindowMs = 500;

    /// <summary>兩個相鄰輸入報告間隔超過這個值就視為「使用者停頓」，不列入樣本。</summary>
    internal const double IdleGapUs = 200_000;   // 200 ms

    /// <summary>
    /// 真實通路：<b>本版刻意不實作即時取樣</b>——
    /// 讀 Raw Input 需要在收集流程裡掛一個訊息迴圈並等 500 ms，
    /// 而事實收集是同步的啟動路徑。掛在哪裡都得改架構，不值得為一個事實鍵做這件事。
    /// 所以回 <c>null</c>（由呼叫端標 ReadError 並說明），並在
    /// <see cref="Summarize"/> 裡保留完整的判讀邏輯——接縫已經在了，
    /// 接上取樣器時只要替換這一個方法。
    /// </summary>
    internal static PollSample? ReadSample() => null;

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(PollRateKey, Cat, "滑鼠輪詢率", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
