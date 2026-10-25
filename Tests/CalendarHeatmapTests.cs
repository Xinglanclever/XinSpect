using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 日曆熱圖（U10-VZ-0098／<c>viz.calendar.heatmap</c>）的<b>數學守門</b>：
/// 月曆格配置與強度換算是純函式，用合成資料直接驗證，不開 UI。
/// 「空日留白」的紀律由服務端只回有事件的日＋控制項不著色沒事件的格共同守著；
/// 這裡釘住的是配置算式與強度上限。
/// </summary>
public class CalendarHeatmapTests
{
    [Fact]
    public void 追蹤鍵_以U10目錄正式鍵為準()
        => Assert.Equal("viz.calendar.heatmap", VizTrackingKeys.CalendarHeatmapFactKey);

    // ── 月曆配置 ───────────────────────────────────────────────────────────

    [Fact]
    public void 天數_平年二月28_閏年二月29_月底月份如實()
    {
        Assert.Equal(28, CalendarHeatmapMath.DaysInMonth(2026, 2));
        Assert.Equal(29, CalendarHeatmapMath.DaysInMonth(2024, 2));
        Assert.Equal(31, CalendarHeatmapMath.DaysInMonth(2026, 10));
        Assert.Equal(31, CalendarHeatmapMath.DaysInMonth(2026, 99));   // 非法月夾進範圍，不丟例外
    }

    [Fact]
    public void 首欄_週一開欄_2026年10月1日是週四落在第3欄()
    {
        Assert.Equal(3, CalendarHeatmapMath.FirstColumn(2026, 10));    // 週一=0 … 週四=3
        Assert.Equal(6, CalendarHeatmapMath.FirstColumn(2026, 11));    // 2026-11-01 是週日 → 最右欄
    }

    [Fact]
    public void 首欄_週日落在最後一欄_週一落在第一欄()
    {
        // 2026-02-01 是週日 → 第 6 欄（週一開欄的最右）
        Assert.Equal(6, CalendarHeatmapMath.FirstColumn(2026, 2));
        // 2026-11-02 是週一（11-01 週日）→ 但 FirstColumn 看的是 1 號：
        // 2026-03-02 是週一，3 月 1 號是週日
        Assert.Equal(6, CalendarHeatmapMath.FirstColumn(2026, 3));
        Assert.Equal(0, CalendarHeatmapMath.CellOf(new DateOnly(2026, 3, 2)).Col);   // 週一＝第 0 欄
    }

    [Fact]
    public void 格位_月初月底_行列連續不重疊()
    {
        // 2026-10：1 號週四 → (0,3)；31 號週六 → 33 格 → 第 4 列第 5 欄
        Assert.Equal((0, 3), CalendarHeatmapMath.CellOf(new DateOnly(2026, 10, 1)));
        Assert.Equal((4, 5), CalendarHeatmapMath.CellOf(new DateOnly(2026, 10, 31)));
        Assert.Equal(5, CalendarHeatmapMath.WeekCount(2026, 10));
    }

    [Fact]
    public void 格位_全月格位互不重疊且落在週數範圍內()
    {
        int weeks = CalendarHeatmapMath.WeekCount(2026, 10);
        var seen = new HashSet<(int, int)>();
        for (int day = 1; day <= 31; day++)
        {
            var (row, col) = CalendarHeatmapMath.CellOf(new DateOnly(2026, 10, day));
            Assert.InRange(row, 0, weeks - 1);
            Assert.InRange(col, 0, 6);
            Assert.True(seen.Add((row, col)), $"10 月 {day} 日格位 ({row},{col}) 與他日重疊。");
        }
    }

    // ── 強度 ───────────────────────────────────────────────────────────────

    [Fact]
    public void 強度_以當月最大計數為滿格_超過夾限()
    {
        Assert.Equal(0.0, CalendarHeatmapMath.Intensity(0, 5), 9);
        Assert.Equal(0.5, CalendarHeatmapMath.Intensity(5, 10), 9);
        Assert.Equal(1.0, CalendarHeatmapMath.Intensity(10, 10), 9);
        Assert.Equal(1.0, CalendarHeatmapMath.Intensity(99, 10), 9);   // 夾限不爆
    }

    [Fact]
    public void 強度_沒有任何資料_全月零_由呼叫端決定怎麼呈現不知道()
        => Assert.Equal(0.0, CalendarHeatmapMath.Intensity(3, 0), 9);

    // ── 服務端的逐日折算 ───────────────────────────────────────────────────

    private static (DateTime TimeLocal, Severity Severity) Ev(int day, int hour, Severity sev)
        => (new DateTime(2026, 10, day, hour, 0, 0), sev);

    [Fact]
    public void 逐日異常_只計警告及以上_中性與良好不算()
    {
        var events = new[]
        {
            Ev(3, 9, Severity.Warning), Ev(3, 10, Severity.Critical),
            Ev(4, 8, Severity.Good), Ev(4, 9, Severity.Neutral),
            Ev(5, 8, Severity.Serious),
        };
        var counts = SensorDashboardService.DailyAnomalies(events, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(2, counts.Count);                     // 4 日沒有異常：不造假格
        Assert.Contains(counts, c => c.Day == new DateOnly(2026, 10, 3) && c.Count == 2);
        Assert.Contains(counts, c => c.Day == new DateOnly(2026, 10, 5) && c.Count == 1);
    }

    [Fact]
    public void 逐日異常_只回區間內_區間顛倒時自動交換()
    {
        var events = new[]
        {
            Ev(3, 9, Severity.Warning), Ev(20, 9, Severity.Critical), Ev(25, 9, Severity.Warning),
        };
        // 傳成「迄早於起」：自動交換成 1–20 日，25 日在區間外不計
        var counts = SensorDashboardService.DailyAnomalies(events, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 1));

        Assert.Equal(2, counts.Count);
        Assert.Equal(new DateOnly(2026, 10, 3), counts[0].Day);
        Assert.Equal(new DateOnly(2026, 10, 20), counts[1].Day);
    }

    [Fact]
    public void 逐日異常_沒有事件_回空清單_日曆全月留白()
        => Assert.Empty(SensorDashboardService.DailyAnomalies([], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
}
