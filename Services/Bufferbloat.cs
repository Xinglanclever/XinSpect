namespace XinSpect;

/// <summary>Bufferbloat 評級結果：等級、白話、嚴重度，與滿載時的延遲膨脹（毫秒）。</summary>
public sealed record BufferbloatResult(string Grade, string Label, Severity Severity, double InflationMs);

/// <summary>
/// Bufferbloat（緩衝膨脹）評級：純函式、可測。看的是「滿載時延遲比閒置時多了多少」
/// （loaded − idle）——路由器/數據機在滿載時把封包排進大佇列，延遲會從個位數 ms 暴漲到上百 ms，
/// 這正是「測速數字很漂亮、實際玩遊戲/開視訊卻很卡」的元兇。分級門檻沿用 DSLReports/Waveform 慣例。
/// </summary>
public static class Bufferbloat
{
    public static BufferbloatResult Grade(double idleMs, double loadedMs)
    {
        double inflation = Math.Max(0, loadedMs - idleMs);   // 滿載延遲不該低於閒置；量測抖動造成的負值夾為 0
        return inflation switch
        {
            <= 5   => new("A+", "極佳（滿載幾乎不增延遲）", Severity.Good, inflation),
            <= 30  => new("A",  "良好", Severity.Good, inflation),
            <= 60  => new("B",  "尚可", Severity.Warning, inflation),
            <= 200 => new("C",  "偏高（滿載時明顯卡頓）", Severity.Serious, inflation),
            _      => new("F",  "嚴重（路由器佇列嚴重排隊）", Severity.Critical, inflation),
        };
    }
}
