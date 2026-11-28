namespace XinSpect;

/// <summary>GP-004：Raw Input 前景接收端的 OS 移動報告到達頻率；不是實體 USB 輪詢頻率。</summary>
public static class MousePollRateFactsService
{
    private const string Cat = "遊戲與繪圖";
    private const string Source = "WM_INPUT / GetRawInputData；QPC 接收端到達間隔（唯讀、不注入輸入）";
    public const string PollRateKey = "gp.pollrate";

    // 保留既有注入接縫；Samples 是有效相鄰間隔數，不是報告總數。
    public sealed record PollSample(int Samples, double MedianIntervalUs, double Hz, bool DeviceReadable, int WindowMs)
    {
        public FactAvailability Availability { get; init; } = FactAvailability.Present;
        public string? Reason { get; init; }
        public string? DeviceId { get; init; }
        public double? P95IntervalUs { get; init; }
        public double? JitterMadUs { get; init; }
        public DateTimeOffset? StartedAtUtc { get; init; }
        public DateTimeOffset? MeasuredAtUtc { get; init; }
        public int? Reports { get; init; }
        public int? IdleGaps { get; init; }
    }
    public delegate PollSample? Probe();

    public static PollSample Summarize(IReadOnlyList<double> intervalsUs, int windowMs, bool deviceReadable = true)
    {
        var valid = intervalsUs.Where(v => double.IsFinite(v) && v > 0).ToArray();
        var (median, p95, mad) = RawMouseSampling.Distribution(valid);
        return new(valid.Length, median, median > 0 ? 1_000_000 / median : 0, deviceReadable, windowMs)
        { P95IntervalUs = p95, JitterMadUs = mad };
    }

    /// <summary>中位間隔倒數與常見檔位的 ±10% 標籤；不識別 USB 設定或實體輪詢。</summary>
    public static string NearestRateLabel(double hz)
    {
        if (!double.IsFinite(hz) || hz <= 0) return "未量到";
        double[] tiers = [125, 250, 500, 1000, 2000, 4000, 8000];
        double best = tiers[0], bestError = double.MaxValue;
        foreach (double tier in tiers)
        {
            double error = Math.Abs(hz - tier) / tier;
            if (error < bestError) { bestError = error; best = tier; }
        }
        return bestError <= .10 ? $"≈{best:N0} Hz 檔" : $"未對到常見檔位（量到 {hz:N0} Hz）";
    }

    public static HardwareFact Collect(DateTimeOffset at, Probe? probe = null)
    {
        PollSample? sample;
        try { sample = (probe ?? ReadSample)(); }
        catch { sample = null; }
        if (sample is null) return Unavailable(at, FactAvailability.ReadError,
            "滑鼠輸入取樣失敗——不從裝置型號推論回報率。");
        var measured = sample.MeasuredAtUtc ?? at;
        if (sample.Availability != FactAvailability.Present)
            return Unavailable(measured, sample.Availability, sample.Reason ?? "尚未取得可用的 Raw Input 取樣。");
        // 舊注入接縫的零樣本契約保留；生產通路明確傳 Unknown，不以靜止推論無硬體。
        if (sample.Samples == 0) return Unavailable(measured, FactAvailability.NotApplicable,
            $"取樣窗（{sample.WindowMs} ms）沒有可用間隔，不是 0 Hz；以 0 Hz 代表滑鼠回報率在此定義下物理上不存在。無輸入也可能是靜止或遠端輸入。");
        if (sample.Samples < 0 || sample.WindowMs <= 0 || !double.IsFinite(sample.Hz) || sample.Hz <= 0 ||
            !double.IsFinite(sample.MedianIntervalUs) || sample.MedianIntervalUs <= 0)
            return Unavailable(measured, FactAvailability.ReadError, "取樣的間隔、頻率或時間窗無效。");

        string details = sample.DeviceId is null ? "" : $"裝置 {sample.DeviceId}・";
        if (sample.P95IntervalUs is { } p95) details += $"P95 間隔 {p95:N1} µs・";
        if (sample.JitterMadUs is { } mad) details += $"抖動 MAD {mad:N1} µs・";
        if (sample.StartedAtUtc is { } start) details += $"UTC {start:O} 至 {measured:O}・";
        if (sample.Reports is { } reports) details += $"原始報告 {reports} 筆・停頓排除 {sample.IdleGaps ?? 0} 次・";
        return new(PollRateKey, Cat, "滑鼠 OS 回報到達頻率",
            $"{NearestRateLabel(sample.Hz)}・中位間隔 {sample.MedianIntervalUs:N0} µs・樣本 {sample.Samples} 筆（取樣窗 {sample.WindowMs} ms）。" +
            details + (sample.DeviceReadable ? "" : "裝置名稱讀不到・") +
            "取樣期間必須被記住：這是單裝置相對移動 WM_INPUT 的 QPC 到達間隔；" +
            "中位間隔倒數是 OS 接收端觀察值，不是實體 USB 輪詢、端到端延遲或精度認證。" +
            "排除大於 200 ms 的停頓；排程、訊息佇列、合併、無線與遠端通路會改變分布。" +
            "省電模式下作業系統會主動降低取樣率的情形須另行驗證，不能由這個數值推定原因。" +
            "不從型號推論：包裝上的行銷數字不能填補實測。",
            "Hz", Source, FactTrustLevel.Measured, false, measured, sample.Hz);
    }

    internal const int SampleWindowMs = RawMouseSampling.MaximumDurationMs;
    internal const double IdleGapUs = RawMouseSampling.IdleGapUs;
    internal static PollSample ReadSample() => RawMouseSampleStore.Shared.Read(DateTimeOffset.UtcNow);
    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(PollRateKey, Cat, "滑鼠 OS 回報到達頻率", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
