namespace XinSpect;

/// <summary>一段 loopback 訊號的統計（峰值／RMS 為 0–1 的滿格比例；MaxClippedRun 供顯示端如實標註）。</summary>
public sealed record LoopbackSignalStats(double Peak, double Rms, bool Clipping, int MaxClippedRun = 0);

/// <summary>
/// WP38 訊號層：WASAPI loopback（NAudio WasapiLoopbackCapture）擷取系統混音輸出，
/// 計算峰值／RMS／削波——量測「訊號層」的第一步；示波器圖形屬 UI，不在本服務範圍。
/// 純數學（<see cref="LoopbackSignalStats"/>）以金標向量釘死；擷取層極薄（無音訊裝置環境
/// NAudio 會 throw，呼叫方以三態如實標）。全 0 樣本是「靜音」——有效資料，不是讀取失敗。
/// </summary>
/// <remarks>
/// 削波門檻成文於百科第 5 部：「削波＝連續 ≥3 個 |x|≥1.0 樣本」。看的是<b>最長連續段</b>——
/// 舊實作數的是整段 buffer 的滿格總數，與成文規則不符：[1, 0.5, 1, 0.5, 1] 三個孤立滿格
/// （各是合法峰值）會被湊數湊成「削波」；[1,1,x,1,1,1] 的間歇兩連也會被誤判。
/// </remarks>
public static class LoopbackSignalService
{
    /// <summary>削波判定的連續滿格樣本數門檻（成文於百科第 5 部，不是感覺）。</summary>
    public const int ClipRunThreshold = 3;

    /// <summary>一段時間的樣本 → 統計。樣本為 IEEE float（−1..1）。</summary>
    public static LoopbackSignalStats Compute(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) throw new ArgumentException("樣本為空——沒有資料不能計算統計", nameof(samples));
        double sumSquares = 0;
        float peak = 0;
        int run = 0, maxRun = 0;
        foreach (var s in samples)
        {
            double a = Math.Abs((double)s);
            if (a > peak) peak = (float)a;
            sumSquares += (double)s * s;
            // 削波看連續（成文門檻 ClipRunThreshold）：追蹤當前連續段，斷掉就歸零
            if (a >= 1.0)
            {
                run++;
                if (run > maxRun) maxRun = run;
            }
            else
            {
                run = 0;
            }
        }
        double rms = Math.Sqrt(sumSquares / samples.Length);
        return new LoopbackSignalStats(peak, rms, maxRun >= ClipRunThreshold, maxRun);
    }

    /// <summary>滿格比例 → dBFS。0 以 −120 dBFS 底線呈現（不輸出 −∞ 的「無意義值」）。</summary>
    public static double ToDbfs(double fullScale)
        => fullScale <= 0 ? -120.0 : 20 * Math.Log10(fullScale);
}
