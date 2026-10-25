using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// U10-TS-0192「時鐘：會話期漂移（開掃到結束之累計差）」：<c>ts.clock.session.drift</c>。
/// </summary>
/// <remarks>
/// <para>
/// 量的是：牆鐘（<c>DateTime.UtcNow</c>，可被 NTP 校時）與單調鐘
/// （<c>QueryPerformanceCounter</c>，不回頭）在會話起訖兩次取樣間的<b>累計差</b>。
/// 零特權、唯讀、零網路。
/// </para>
/// <para>
/// 誠實界線：①這個數字包含 NTP 校時——量到漂移是「系統鐘被動過」，不是 RTC 硬體誤差；
/// ②取樣窗太短（&lt;60 秒）時，系統鐘的更新粒度（1–15.6 ms 量級）會把短窗漂移整個淹掉，
/// 此時標 Unknown 拒報，不給一個被粒度噪音主導的數字；③本服務不自動長跑——
/// 會話起點由呼叫端（掃描起點）提供，終點在收集時取樣，中間隔多久由呼叫端決定並標註。
/// </para>
/// </remarks>
public static class ClockSessionDriftService
{
    private const string Category = "系統";
    public const string Key = "ts.clock.session.drift";
    public const string Source = "DateTime.UtcNow vs QueryPerformanceCounter（Stopwatch；唯讀取樣）";

    /// <summary>一次取樣：牆鐘時刻與單調鐘刻。</summary>
    public sealed record ClockSample(DateTimeOffset Utc, long QpcTimestamp);

    /// <summary>取當下樣本（呼叫端存住會話起點）。</summary>
    public static ClockSample Capture() => new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());

    /// <summary>收集會話期漂移。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ClockSample sessionStart, (DateTimeOffset Utc, long QpcTimestamp)? probeEnd = null,
        double? qpcFrequency = null)
    {
        (DateTimeOffset Utc, long QpcTimestamp) end;
        if (probeEnd is { } e) end = e;
        else { var s = Capture(); end = (s.Utc, s.QpcTimestamp); }
        double frequency = qpcFrequency ?? Stopwatch.Frequency;
        return [BuildFact(at, sessionStart, end, frequency)];
    }

    /// <summary>純函式：起訖樣本 → 漂移事實。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, ClockSample start,
        (DateTimeOffset Utc, long QpcTimestamp) end, double qpcFrequency)
    {
        double qpcElapsedSec = (end.QpcTimestamp - start.QpcTimestamp) / qpcFrequency;
        double utcElapsedSec = (end.Utc - start.Utc).TotalSeconds;
        if (qpcElapsedSec < 0 || utcElapsedSec < 0)
            return new HardwareFact(Key, Category, "會話期時鐘漂移", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "取樣次序異常（終點早於起點）——樣本不可信");
        if (qpcElapsedSec < 60)
            return new HardwareFact(Key, Category, "會話期時鐘漂移",
                $"取樣窗僅 {qpcElapsedSec:0.#} 秒", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "取樣窗不足 60 秒——系統鐘的更新粒度（1–15.6 ms 量級）會淹掉短窗漂移，拒報數字");
        double driftMs = (utcElapsedSec - qpcElapsedSec) * 1000.0;
        string direction = Math.Abs(driftMs) < 0.5 ? "無可測漂移（<0.5 ms）"
            : driftMs > 0 ? $"牆鐘快了 {driftMs:0.##} ms（含 NTP 校時的任何介入）"
            : $"牆鐘慢了 {-driftMs:0.##} ms（含 NTP 校時的任何介入）";
        return new HardwareFact(Key, Category, "會話期時鐘漂移",
            $"{direction}；取樣窗 {qpcElapsedSec:0.#} 秒（牆鐘 {utcElapsedSec:0.#} 秒 vs 單調鐘 {qpcElapsedSec:0.###} 秒）",
            "ms", Source, FactTrustLevel.Measured, false, at, driftMs,
            FactAvailability.Present, null);
    }
}
