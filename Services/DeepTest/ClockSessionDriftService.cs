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
/// 誠實界線：①這個數字包含 NTP 校時、兩鐘速率差與取樣誤差，不能單憑差值判定哪個鐘有誤；
/// ②取樣窗太短（&lt;60 秒）時，保守標 Unknown 拒報；兩次非同步取樣仍包含排程與讀鐘誤差，
/// 顯示的小數位不代表量測精度；③本服務不自動長跑——
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
        if (!double.IsFinite(qpcFrequency) || qpcFrequency <= 0)
            return Invalid("QPC 頻率必須為正且有限——無法換算取樣窗");

        // 先在整數域相減，避免 long 溢位，也避免兩個很大的近鄰刻先轉 double 後丟失差值。
        Int128 elapsedTicks = (Int128)end.QpcTimestamp - start.QpcTimestamp;
        if (elapsedTicks < 0)
            return Invalid("單調鐘取樣次序異常（終點早於起點）——樣本不可信");
        double qpcElapsedSec = (double)elapsedTicks / qpcFrequency;
        if (!double.IsFinite(qpcElapsedSec))
            return Invalid("QPC 取樣窗換算超出有限數值範圍——樣本不可信");
        // UTC 可以被校時回撥；樣本先後只由單調鐘判斷，負的牆鐘差正是需要保留的證據。
        double utcElapsedSec = (end.Utc - start.Utc).TotalSeconds;
        if (qpcElapsedSec < 60)
            return new HardwareFact(Key, Category, "會話期時鐘漂移", $"取樣窗僅 {qpcElapsedSec:0.#} 秒", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "取樣窗不足 60 秒——保守拒報短窗累計差；未校準取樣誤差");
        double driftMs = (utcElapsedSec - qpcElapsedSec) * 1000.0;
        if (!double.IsFinite(driftMs))
            return Invalid("時鐘累計差超出有限數值範圍——樣本不可信");
        string direction = Math.Abs(driftMs) < 0.5 ? "兩次取樣累計差 <0.5 ms（不代表量測精度）"
            : driftMs > 0 ? $"牆鐘快了 {driftMs:0.##} ms（含 NTP 校時的任何介入）"
            : $"牆鐘慢了 {-driftMs:0.##} ms（含 NTP 校時的任何介入）";
        return new HardwareFact(Key, Category, "會話期時鐘漂移",
            $"{direction}；取樣窗 {qpcElapsedSec:0.#} 秒（牆鐘 {utcElapsedSec:0.#} 秒 vs 單調鐘 {qpcElapsedSec:0.###} 秒）；含排程與非同步讀鐘誤差",
            "ms", Source, FactTrustLevel.Measured, false, at, driftMs,
            FactAvailability.Present, null);

        HardwareFact Invalid(string reason) =>
            new(Key, Category, "會話期時鐘漂移", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason);
    }
}
