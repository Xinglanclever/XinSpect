namespace XinSpect;

/// <summary>
/// Deep Bench 的可信度摘要。所有數字只來自有限樣本；NaN/Infinity 不會被改成零。
/// </summary>
public static class DeepBenchMeasurementStatistics
{
    // 索引軸只是同配置重複輪的編號，不是量測條件；其餘軸值組合代表不同的量測配置。
    private static readonly string[] IndexAxisKeys = ["round", "sample"];

    /// <summary>
    /// 判斷一個指標的池化樣本是否跨多種量測配置。
    /// 除去索引軸後各點仍有不同軸值組合（例如 kernel×threads、blockBytes×queueDepth），
    /// 池化平均就對應不到任何真實配置，不得以單一平均呈現。
    /// </summary>
    public static bool PoolsDistinctConfigurations(DeepBenchMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        if (metric.Points.Count <= 1) return false;
        return metric.Points
            .Select(point => string.Join("\n", point.Axes
                .Where(pair => !IndexAxisKeys.Contains(pair.Key, StringComparer.Ordinal))
                .Select(pair => $"{pair.Key}={pair.Value}")
                .OrderBy(text => text, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Count() > 1;
    }

    /// <summary>把一個量測點的軸值組合寫成穩定排序的可讀文字。</summary>
    public static string DescribeAxes(IReadOnlyDictionary<string, string> axes) =>
        axes.Count == 0
            ? "無軸"
            : string.Join(" ", axes
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

    public static DeepBenchMeasurementSummary FromSamples(IEnumerable<double>? samples)
    {
        double[] finite = (samples ?? []).Where(value => double.IsFinite(value)).OrderBy(value => value).ToArray();
        int invalid = (samples ?? []).Count(value => double.IsFinite(value) == false);
        if (finite.Length == 0)
        {
            return new DeepBenchMeasurementSummary(
                0, invalid, 0, 0, 0, 0, 0, 0, 0, DeepBenchConfidence.Insufficient,
                "無有效樣本——不推算、不給可信度。");
        }

        double mean = finite.Average();
        double median = Percentile(finite, 0.50);
        int outlierCount = finite.Length >= 5 ? CountIqrOutliers(finite) : 0;
        double cv = mean == 0 ? double.PositiveInfinity : Math.Abs(mean) == 0 ? double.PositiveInfinity : StandardDeviation(finite, mean) / Math.Abs(mean) * 100.0;
        var confidence = finite.Length == 1 || mean == 0 || !double.IsFinite(cv)
            ? DeepBenchConfidence.Insufficient
            : cv < 2.0 ? DeepBenchConfidence.High
            : cv < 8.0 ? DeepBenchConfidence.Medium
            : DeepBenchConfidence.Low;

        string summary =
            $"樣本 {finite.Length} 筆，無效 {invalid} 筆；median {median:0.###}，mean {mean:0.###}，CV {FormatCv(cv)}，離群 {outlierCount} 筆，可信度 {confidence}。";

        return new DeepBenchMeasurementSummary(
            finite.Length,
            invalid,
            mean,
            median,
            Percentile(finite, 0.95),
            Percentile(finite, 0.99),
            finite[^1],
            double.IsFinite(cv) ? cv : 0,
            outlierCount,
            confidence,
            summary);
    }

    /// <summary>最近排名百分位：排序後取第 ceil(p*n) 筆，不內插、不虛造不存在的樣本。</summary>
    private static double Percentile(double[] sorted, double percentile)
    {
        int rank = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length), 1, sorted.Length);
        return sorted[rank - 1];
    }

    private static int CountIqrOutliers(double[] sorted)
    {
        double q1 = Percentile(sorted, 0.25);
        double q3 = Percentile(sorted, 0.75);
        double iqr = q3 - q1;
        if (iqr == 0) return 0;
        double low = q1 - 1.5 * iqr;
        double high = q3 + 1.5 * iqr;
        return sorted.Count(value => value < low || value > high);
    }

    private static double StandardDeviation(double[] values, double mean)
    {
        double sum = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sum / values.Length);
    }

    private static string FormatCv(double cv) => double.IsFinite(cv) ? $"{cv:0.###}%" : "無法計算（mean=0）";
}

public sealed record DeepBenchMeasurementSummary(
    int Count,
    int InvalidSampleCount,
    double Mean,
    double Median,
    double P95,
    double P99,
    double Max,
    double CvPercent,
    int OutlierCount,
    DeepBenchConfidence Confidence,
    string SummaryText);
