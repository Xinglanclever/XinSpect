namespace XinSpect;

/// <summary>一段 loopback 訊號的統計（峰值／RMS 為 0–1 的滿格比例）。</summary>
public sealed record LoopbackSignalStats(double Peak, double Rms, bool Clipping);

/// <summary>
/// WP38 訊號層：WASAPI loopback（NAudio WasapiLoopbackCapture）擷取系統混音輸出，
/// 計算峰值／RMS／削波——量測「訊號層」的第一步；示波器圖形屬 UI，不在本服務範圍。
/// 純數學（<see cref="LoopbackSignalStats"/>）以金標向量釘死；擷取層極薄（無音訊裝置環境
/// NAudio 會 throw，呼叫方以三態如實標）。全 0 樣本是「靜音」——有效資料，不是讀取失敗。
/// </summary>
public static class LoopbackSignalService
{
    /// <summary>一段時間的樣本 → 統計。樣本為 IEEE float（−1..1）。</summary>
    public static LoopbackSignalStats Compute(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) throw new ArgumentException("樣本為空——沒有資料不能計算統計", nameof(samples));
        double sumSquares = 0;
        float peak = 0;
        int clipped = 0;
        foreach (var s in samples)
        {
            double a = Math.Abs((double)s);
            if (a > peak) peak = (float)a;
            sumSquares += (double)s * s;
            if (a >= 1.0) clipped++;
        }
        double rms = Math.Sqrt(sumSquares / samples.Length);
        // 連續滿格才叫削波；單點 ±1.0 可能是合法峰值
        return new LoopbackSignalStats(peak, rms, clipped >= 3);
    }

    /// <summary>滿格比例 → dBFS。0 以 −120 dBFS 底線呈現（不輸出 −∞ 的「無意義值」）。</summary>
    public static double ToDbfs(double fullScale)
        => fullScale <= 0 ? -120.0 : 20 * Math.Log10(fullScale);
}
