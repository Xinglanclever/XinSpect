using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>歷史查詢結果的統計與桶化：百分位、區段彙整、降採樣。</summary>
public class HistorySeriesTests
{
    private const int M = HistoryMetrics.Count;

    /// <summary>造一段每指標同值的序列，方便手算驗算。</summary>
    private static HistorySeries Make(
        int n, Func<int, float> avg, Func<int, float>? min = null, Func<int, float>? max = null,
        bool second = false)
    {
        var t = new DateTime[n];
        var a = new float[n * M];
        var lo = new float[n * M];
        var hi = new float[n * M];
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            t[i] = origin.AddMinutes(i);
            for (int m = 0; m < M; m++)
            {
                a[i * M + m] = avg(i);
                lo[i * M + m] = (min ?? avg)(i);
                hi[i * M + m] = (max ?? avg)(i);
            }
        }
        return new HistorySeries { Times = t, Avg = a, Min = lo, Max = hi, SecondLevel = second };
    }

    // ── 百分位 ───────────────────────────────────────────────────────────

    [Fact]
    public void Percentile_Empty_IsZero() => Assert.Equal(0, HistorySeries.Percentile([], 95));

    [Fact]
    public void Percentile_SingleValue_IsThatValue()
        => Assert.Equal(42, HistorySeries.Percentile([42], 95));

    [Fact]
    public void Percentile_Interpolates_BetweenNeighbours()
    {
        // 1..10 的 P95：位置 = 0.95 × 9 = 8.55 → 9 + (10−9) × 0.55
        double[] v = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(9.55, HistorySeries.Percentile(v, 95), 6);
    }

    [Fact]
    public void Percentile_UnsortedInput_SortsFirst()
    {
        double[] v = [10, 1, 5, 3, 8];
        Assert.Equal(HistorySeries.Percentile([1, 3, 5, 8, 10], 50), HistorySeries.Percentile(v, 50), 6);
    }

    [Fact]
    public void Percentile_ClampsOutOfRangeP()
    {
        double[] v = [1, 2, 3, 4, 5];
        Assert.Equal(5, HistorySeries.Percentile(v, 200));
        Assert.Equal(1, HistorySeries.Percentile(v, -20));
    }

    [Fact]
    public void Percentile_DoesNotMutateCaller()
    {
        double[] v = [9, 4, 7];
        HistorySeries.Percentile(v, 95);
        Assert.Equal(9, v[0]);
        Assert.Equal(4, v[1]);
        Assert.Equal(7, v[2]);
    }

    // ── 區段彙整 ─────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_Empty_IsAllZero()
    {
        var (mn, avg, mx, p95) = Make(0, _ => 0).Summarize(HistoryMetrics.CpuTemp);
        Assert.Equal(0, mn);
        Assert.Equal(0, avg);
        Assert.Equal(0, mx);
        Assert.Equal(0, p95);
    }

    [Fact]
    public void Summarize_TakesMinFromMinColumn_MaxFromMaxColumn()
    {
        // 平均 0,10,20,30,40；最小 = 平均−5；最大 = 平均+5
        var s = Make(5, i => i * 10, i => i * 10 - 5, i => i * 10 + 5);
        var (mn, avg, mx, p95) = s.Summarize(HistoryMetrics.CpuLoad);
        Assert.Equal(-5, mn, 4);
        Assert.Equal(20, avg, 4);
        Assert.Equal(45, mx, 4);
        // P95 取各點代表值（平均）的分布：0.95 × 4 = 3.8 → 30 + 10 × 0.8
        Assert.Equal(38, p95, 4);
    }

    // ── 降採樣 ───────────────────────────────────────────────────────────

    [Fact]
    public void Downsample_FewerPointsThanColumns_ReturnsSameInstance()
    {
        var s = Make(10, i => i);
        Assert.Same(s, s.Downsample(64));
    }

    [Fact]
    public void Downsample_DegenerateColumns_ReturnsSameInstance()
    {
        var s = Make(10, i => i);
        Assert.Same(s, s.Downsample(1));
        Assert.Same(s, s.Downsample(0));
    }

    [Fact]
    public void Downsample_BucketsAverages_AndKeepsEnvelope()
    {
        // 0..99 桶成 10 欄：每桶 10 點，首桶平均 = (0+…+9)/10 = 4.5
        var s = Make(100, i => i);
        var d = s.Downsample(10);
        Assert.Equal(10, d.Count);
        int m = HistoryMetrics.GpuLoad;
        Assert.Equal(4.5, d.A(0, m), 3);
        Assert.Equal(94.5, d.A(9, m), 3);
        // 極值是「極值的極值」，桶化後整段包絡不縮水
        Assert.Equal(0, d.L(0, m), 3);
        Assert.Equal(99, d.H(9, m), 3);
    }

    [Fact]
    public void Downsample_UnevenBuckets_StillProducesExactColumnCount()
    {
        var d = Make(7, i => i).Downsample(3);
        Assert.Equal(3, d.Count);
        Assert.True(d.Times[0] < d.Times[1] && d.Times[1] < d.Times[2]);
    }

    [Fact]
    public void Downsample_PreservesGranularityFlag()
    {
        Assert.True(Make(100, i => i, second: true).Downsample(8).SecondLevel);
        Assert.False(Make(100, i => i).Downsample(8).SecondLevel);
    }
}

/// <summary>七項指標的中介資料必須等長，否則圖例與匯出表頭會錯位。</summary>
public class HistoryMetricsTests
{
    [Fact]
    public void MetadataArrays_AllMatchCount()
    {
        Assert.Equal(HistoryMetrics.Count, HistoryMetrics.Titles.Length);
        Assert.Equal(HistoryMetrics.Count, HistoryMetrics.Units.Length);
        Assert.Equal(HistoryMetrics.Count, HistoryMetrics.Colors.Length);
        Assert.Equal(HistoryMetrics.Count, HistoryMetrics.FixedMax.Length);
    }

    [Fact]
    public void PercentMetrics_AreFixedTo100()
    {
        Assert.Equal(100, HistoryMetrics.FixedMax[HistoryMetrics.CpuLoad]);
        Assert.Equal(100, HistoryMetrics.FixedMax[HistoryMetrics.MemLoad]);
        Assert.Equal(100, HistoryMetrics.FixedMax[HistoryMetrics.GpuLoad]);
        // 溫度／頻率／容量依區間自動縮放
        Assert.Null(HistoryMetrics.FixedMax[HistoryMetrics.CpuTemp]);
        Assert.Null(HistoryMetrics.FixedMax[HistoryMetrics.CpuClock]);
        Assert.Null(HistoryMetrics.FixedMax[HistoryMetrics.GpuVram]);
    }
}

/// <summary>
/// 歷史倉的取樣、分鐘結算、查詢與磁碟往返。每個測試各自使用一個暫存資料夾，
/// 絕不觸碰 %APPDATA% 的真實歷史檔。
/// </summary>
public class HistoryStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存夾清不掉不算失敗 */ }
    }

    /// <summary>七個指標同值的一組取樣。</summary>
    private static float[] All(float v)
    {
        var a = new float[HistoryMetrics.Count];
        for (int i = 0; i < a.Length; i++) a[i] = v;
        return a;
    }

    /// <summary>從整分鐘起算（並退開 10 分鐘），避免取樣意外跨越真實的分鐘邊界。</summary>
    private static DateTime Origin => new DateTime(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute,
        DateTimeKind.Utc).AddMinutes(-10);

    [Fact]
    public void MinuteRecord_ClosesOnlyWhenMinuteChanges()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        store.Sample(All(10), t0);
        store.Sample(All(20), t0.AddSeconds(30));
        Assert.Equal(0, store.MinuteCount);          // 同一分鐘內只累加
        store.Sample(All(30), t0.AddMinutes(1));     // 跨分鐘才結算
        Assert.Equal(1, store.MinuteCount);
        Assert.Equal(3, store.SecondCount);
    }

    [Fact]
    public void Query_SpanningBeforeSecondBuffer_ReturnsMinuteAggregate()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        store.Sample(All(10), t0);
        store.Sample(All(20), t0.AddSeconds(30));
        store.Sample(All(40), t0.AddMinutes(1));
        store.Sample(All(60), t0.AddMinutes(1).AddSeconds(30));
        store.Flush();                               // 結算未滿一分鐘的尾段

        var s = store.Query(t0.AddMinutes(-5), t0.AddMinutes(5));
        Assert.False(s.SecondLevel);
        Assert.Equal(2, s.Count);
        int m = HistoryMetrics.CpuTemp;
        Assert.Equal(10, s.L(0, m), 3);
        Assert.Equal(15, s.A(0, m), 3);
        Assert.Equal(20, s.H(0, m), 3);
        Assert.Equal(40, s.L(1, m), 3);
        Assert.Equal(50, s.A(1, m), 3);
        Assert.Equal(60, s.H(1, m), 3);
    }

    [Fact]
    public void Query_InsideSecondBuffer_ReturnsRawSamples()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        for (int i = 0; i < 5; i++) store.Sample(All(i * 10), t0.AddSeconds(i * 10));

        var s = store.Query(t0, t0.AddMinutes(1));
        Assert.True(s.SecondLevel);
        Assert.Equal(5, s.Count);
        int m = HistoryMetrics.MemLoad;
        Assert.Equal(0, s.A(0, m), 3);
        Assert.Equal(40, s.A(4, m), 3);
        // 原始取樣沒有區間可言：最小／平均／最大同值
        Assert.Equal(s.A(2, m), s.L(2, m));
        Assert.Equal(s.A(2, m), s.H(2, m));
    }

    [Fact]
    public void Query_InvertedRange_IsEmpty()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        store.Sample(All(1), t0);
        Assert.Equal(0, store.Query(t0.AddMinutes(1), t0).Count);
    }

    [Fact]
    public void DiskRoundTrip_ReloadsMinuteRecordsVerbatim()
    {
        var t0 = Origin;
        using (var store = new HistoryStore(_dir))
        {
            store.Sample(All(11), t0);
            store.Sample(All(21), t0.AddSeconds(30));
            store.Sample(All(31), t0.AddMinutes(1));
            store.Flush();
            Assert.Equal(2, store.MinuteCount);
            Assert.True(store.DiskBytes > 0);
        }

        // 重新開檔：秒級環不落地，故查詢一律走分鐘級鏡射
        var again = new HistoryStore(_dir);
        Assert.Equal(2, again.MinuteCount);
        Assert.Equal(0, again.SecondCount);

        var s = again.Query(t0.AddMinutes(-5), t0.AddMinutes(5));
        Assert.Equal(2, s.Count);
        int m = HistoryMetrics.CpuClock;
        Assert.Equal(11, s.L(0, m), 3);
        Assert.Equal(16, s.A(0, m), 3);
        Assert.Equal(21, s.H(0, m), 3);
        Assert.Equal(31, s.A(1, m), 3);
    }

    [Fact]
    public void CorruptFile_IsTreatedAsNoHistory()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.bin"), "這不是歷史檔");
        var store = new HistoryStore(_dir);
        Assert.Equal(0, store.MinuteCount);
        Assert.Null(store.OldestUtc);
    }

    [Fact]
    public void NonFiniteSamples_AreStoredAsZero()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        var bad = All(float.NaN);
        bad[HistoryMetrics.GpuTemp] = float.PositiveInfinity;
        store.Sample(bad, t0);

        var s = store.Query(t0, t0.AddSeconds(30));
        Assert.Equal(1, s.Count);
        Assert.Equal(0, s.A(0, HistoryMetrics.CpuLoad), 3);
        Assert.Equal(0, s.A(0, HistoryMetrics.GpuTemp), 3);
    }

    [Fact]
    public void Updated_FiresOncePerClosedMinute()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        int n = 0;
        store.Updated += () => n++;
        store.Sample(All(1), t0);
        store.Sample(All(1), t0.AddMinutes(1));
        store.Sample(All(1), t0.AddMinutes(2));
        Assert.Equal(2, n);
    }

    [Fact]
    public void OldestUtc_FallsBackToSecondBuffer()
    {
        var t0 = Origin;
        using var store = new HistoryStore(_dir);
        store.Sample(All(5), t0);
        Assert.Equal(t0, store.OldestUtc);           // 尚無分鐘紀錄 → 取秒級最早
        store.Sample(All(5), t0.AddMinutes(1));
        Assert.Equal(t0, store.OldestUtc);           // 分鐘紀錄以整分鐘為時刻
    }

    [Fact]
    public void Clear_EmptiesMemoryAndDeletesFile()
    {
        var t0 = Origin;
        var store = new HistoryStore(_dir);
        store.Sample(All(50), t0);
        store.Sample(All(50), t0.AddMinutes(1));
        Assert.Equal(1, store.MinuteCount);
        Assert.True(File.Exists(store.FilePath));

        store.Clear();
        Assert.Equal(0, store.MinuteCount);
        Assert.Equal(0, store.SecondCount);
        Assert.Null(store.OldestUtc);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void RetentionDays_IsClampedToSupportedRange()
    {
        using var store = new HistoryStore(_dir) { RetentionDays = 999 };
        Assert.Equal(120, store.RetentionDays);
        store.RetentionDays = 0;
        Assert.Equal(1, store.RetentionDays);
    }

    [Fact]
    public void ExpiredRecords_AreDroppedOnLoad()
    {
        // 保留 120 天時寫入 40 天前的紀錄；以預設 30 天重新載入即應全數剔除
        var old = DateTime.UtcNow.AddDays(-40);
        using (var store = new HistoryStore(_dir) { RetentionDays = 120 })
        {
            store.Sample(All(7), old);
            store.Sample(All(7), old.AddMinutes(1));
            store.Flush();
            Assert.Equal(2, store.MinuteCount);
        }
        Assert.Equal(0, new HistoryStore(_dir).MinuteCount);
    }

    // ── 區間邊界（分鐘級查詢改以二分搜尋定位後仍須完全一致）──────────────────

    // 十分鐘、每分鐘一筆的分鐘級紀錄；第 i 筆的值為 i（0..9）。
    // 寫完後重新載入：新實例的秒級緩衝是空的，因此所有查詢必然走分鐘級路徑。
    private HistoryStore TenMinutes(DateTime t0)
    {
        using (var w = new HistoryStore(_dir))
        {
            for (int i = 0; i < 10; i++) w.Sample(All(i), t0.AddMinutes(i));
            w.Flush();
        }
        return new HistoryStore(_dir);
    }

    [Fact]
    public void MinuteQuery_IsInclusiveAtBothEnds()
    {
        var t0 = Origin.AddHours(-2);
        using var store = TenMinutes(t0);
        Assert.Equal(0, store.SecondCount);           // 確認真的在測分鐘級路徑

        var s = store.Query(t0.AddMinutes(2), t0.AddMinutes(4));
        Assert.False(s.SecondLevel);
        Assert.Equal(3, s.Count);                     // 第 2、3、4 分鐘
        int m = HistoryMetrics.CpuTemp;
        Assert.Equal(2, s.A(0, m), 3);
        Assert.Equal(4, s.A(2, m), 3);
    }

    [Fact]
    public void MinuteQuery_ExcludesPointsJustOutsideTheRange()
    {
        var t0 = Origin.AddHours(-2);
        using var store = TenMinutes(t0);
        // 右緣停在第 4 分鐘前一刻：第 4 筆不得入列
        var s = store.Query(t0.AddMinutes(2), t0.AddMinutes(4).AddTicks(-1));
        Assert.Equal(2, s.Count);
        Assert.Equal(t0.AddMinutes(3), s.Times[^1]);
    }

    [Fact]
    public void MinuteQuery_RangeEntirelyBeforeOrAfterData_IsEmpty()
    {
        var t0 = Origin.AddHours(-2);
        using var store = TenMinutes(t0);
        Assert.Equal(0, store.Query(t0.AddMinutes(-30), t0.AddMinutes(-1)).Count);
        Assert.Equal(0, store.Query(t0.AddMinutes(20), t0.AddMinutes(40)).Count);
    }

    [Fact]
    public void MinuteQuery_WiderRangeReturnsEverything()
    {
        var t0 = Origin.AddHours(-2);
        using var store = TenMinutes(t0);
        var s = store.Query(t0.AddDays(-1), t0.AddDays(1));
        Assert.Equal(10, s.Count);
        Assert.Equal(t0, s.Times[0]);
        Assert.Equal(t0.AddMinutes(9), s.Times[^1]);
    }

    [Fact]
    public void MinuteQuery_TimesStayAscending()
    {
        var t0 = Origin.AddHours(-2);
        using var store = TenMinutes(t0);
        var s = store.Query(t0, t0.AddMinutes(9));
        for (int i = 1; i < s.Count; i++) Assert.True(s.Times[i] > s.Times[i - 1]);
    }

    [Fact]
    public void MinuteQuery_AppendsTheUnclosedCurrentMinuteExactlyOnce()
    {
        var t0 = Origin.AddHours(-2);
        var store = new HistoryStore(_dir);
        store.Sample(All(10), t0);
        store.Sample(All(20), t0.AddMinutes(1));      // 結算第 0 分鐘，第 1 分鐘仍在累加
        store.Sample(All(40), t0.AddMinutes(1).AddSeconds(30));

        var s = store.Query(t0.AddMinutes(-5), t0.AddMinutes(5));
        Assert.Equal(2, s.Count);                    // 已結算的一筆 + 尚未結算的當前分鐘
        int m = HistoryMetrics.CpuTemp;
        Assert.Equal(10, s.A(0, m), 3);
        Assert.Equal(30, s.A(1, m), 3);              // (20 + 40) / 2
        Assert.Equal(t0.AddMinutes(1), s.Times[1]);
        store.Dispose();
    }

    [Fact]
    public void MinuteQuery_ExcludesTheCurrentMinuteWhenOutsideTheRange()
    {
        var t0 = Origin.AddHours(-2);
        var store = new HistoryStore(_dir);
        store.Sample(All(10), t0);
        store.Sample(All(20), t0.AddMinutes(1));      // 當前分鐘 = t0+1
        var s = store.Query(t0.AddMinutes(-5), t0);   // 右緣止於第 0 分鐘
        Assert.Equal(1, s.Count);
        Assert.Equal(t0, s.Times[0]);
        store.Dispose();
    }
}

/// <summary>熱降頻判定：溫度達門檻且頻率低於本機觀測上限的 85 %。純函式，無需硬體。</summary>
public class ThermalThrottleTests
{
    [Theory]
    [InlineData(null, 3000.0, 4000.0, false)]     // 沒有溫度讀值
    [InlineData(96.0, 3000.0, 0.0, false)]        // 尚未建立頻率上限
    [InlineData(96.0, 0.0, 4000.0, false)]        // 頻率讀值缺失
    [InlineData(94.9, 1000.0, 4000.0, false)]     // 溫度未達門檻
    [InlineData(95.0, 3400.0, 4000.0, false)]     // 3400 = 4000 × 0.85，未低於門檻
    [InlineData(95.0, 3399.0, 4000.0, true)]
    [InlineData(101.0, 1200.0, 4000.0, true)]
    public void Thresholds(double? tempC, double clock, double refClock, bool expected)
        => Assert.Equal(expected, EventsService.IsThermalThrottling(tempC, clock, refClock));
}

/// <summary>時間軸單筆事件的顯示欄位。</summary>
public class TimelineEventTests
{
    private static TimelineEvent Ev(DateTime t) => new() { Time = t, Kind = EventKind.App, Title = "x" };

    [Fact]
    public void KindText_CoversEveryKind()
    {
        foreach (var k in Enum.GetValues<EventKind>())
        {
            var ev = new TimelineEvent { Time = DateTime.Now, Kind = k, Title = "x" };
            Assert.NotEqual("其他", ev.KindText);
            Assert.False(string.IsNullOrWhiteSpace(ev.KindText));
        }
    }

    [Fact]
    public void AgoText_ScalesUnits()
    {
        Assert.Contains("秒前", Ev(DateTime.Now.AddSeconds(-5)).AgoText);
        Assert.Contains("分鐘前", Ev(DateTime.Now.AddMinutes(-5)).AgoText);
        Assert.Contains("小時前", Ev(DateTime.Now.AddHours(-5)).AgoText);
        Assert.Contains("天前", Ev(DateTime.Now.AddDays(-5)).AgoText);
        Assert.Equal("剛剛", Ev(DateTime.Now.AddMinutes(5)).AgoText);
    }
}

/// <summary>事件時間軸服務：去重、排序、上限、篩選、區間取件與落地往返。</summary>
public class EventsServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存夾清不掉不算失敗 */ }
    }

    private static int Count(System.ComponentModel.ICollectionView v)
    {
        int n = 0;
        foreach (var _ in v) n++;
        return n;
    }

    private static TimelineEvent First(System.ComponentModel.ICollectionView v)
    {
        foreach (var o in v) return (TimelineEvent)o!;
        throw new InvalidOperationException("檢視為空");
    }

    [Fact]
    public void Add_PutsNewestFirst_AndRoundTripsThroughDisk()
    {
        var svc = new EventsService(_dir);
        Assert.False(svc.HasEvents);
        Assert.Equal("尚無事件", svc.LatestText);

        svc.Add(EventKind.Bench, "跑分完成", "SuperPI 1M");
        Assert.True(svc.HasEvents);
        Assert.Equal("跑分完成", svc.All[0].Title);
        Assert.Contains("跑分完成", svc.LatestText);
        Assert.True(File.Exists(Path.Combine(_dir, "events.json")));

        var reloaded = new EventsService(_dir);
        Assert.Single(reloaded.All);
        Assert.Equal("跑分完成", reloaded.All[0].Title);
        Assert.Equal("SuperPI 1M", reloaded.All[0].Detail);
        Assert.Equal(EventKind.Bench, reloaded.All[0].Kind);
    }

    [Fact]
    public void Add_DedupesSameKindAndTitleWithinWindow()
    {
        var svc = new EventsService(_dir);
        Assert.NotNull(svc.Add(EventKind.Tune, "風扇曲線已套用"));
        Assert.Null(svc.Add(EventKind.Tune, "風扇曲線已套用"));          // 30 秒內同類同題
        Assert.NotNull(svc.Add(EventKind.Tune, "風扇曲線已還原"));
        Assert.NotNull(svc.Add(EventKind.Bench, "風扇曲線已套用"));      // 類別不同不算重複
        Assert.Null(svc.Add(EventKind.App, "   "));                      // 空白標題不記
        Assert.Equal(3, svc.All.Count);
    }

    [Fact]
    public void KindFilter_LimitsViewToOneCategory()
    {
        var svc = new EventsService(_dir);
        svc.Add(EventKind.Alert, "處理器溫度過高");
        svc.Add(EventKind.Bench, "烤機結束");
        Assert.Equal(2, Count(svc.View));

        svc.KindFilter = (int)EventKind.Bench + 1;      // 0 = 全部，其餘為 Kind + 1
        Assert.Equal(1, Count(svc.View));
        Assert.Equal("烤機結束", First(svc.View).Title);

        svc.KindFilter = 0;
        Assert.Equal(2, Count(svc.View));
    }

    [Fact]
    public void Search_MatchesTitleOrDetail_CaseInsensitively()
    {
        var svc = new EventsService(_dir);
        svc.Add(EventKind.Smart, "NVMe 剩餘壽命 99 % → 98 %", "S.M.A.R.T. 回報的剩餘壽命下降");
        svc.Add(EventKind.Bench, "烤機結束", "AVX2 十分鐘");

        svc.Search = "nvme";                            // 大小寫不敏感
        Assert.Equal(1, Count(svc.View));
        svc.Search = "avx2";                            // 命中細節欄
        Assert.Equal(1, Count(svc.View));
        svc.Search = "不存在的關鍵字";
        Assert.Equal(0, Count(svc.View));
        svc.Search = "";
        Assert.Equal(2, Count(svc.View));
    }

    [Fact]
    public void InRange_IsAscendingAndBounded()
    {
        var svc = new EventsService(_dir);
        svc.Add(EventKind.App, "甲");
        svc.Add(EventKind.App, "乙");
        svc.Add(EventKind.App, "丙");

        var now = DateTime.UtcNow;
        var list = svc.InRange(now.AddMinutes(-5), now.AddMinutes(5));
        Assert.Equal(3, list.Count);
        Assert.Equal("甲", list[0].Title);               // 繪圖需要時間遞增
        Assert.Equal("丙", list[2].Title);
        Assert.Empty(svc.InRange(now.AddHours(1), now.AddHours(2)));
    }

    [Fact]
    public void EventCount_IsCappedWithOldestDropped()
    {
        var svc = new EventsService(_dir);
        for (int i = 0; i < 850; i++) svc.Add(EventKind.App, $"事件 {i}");
        Assert.Equal(800, svc.All.Count);
        Assert.Equal("事件 849", svc.All[0].Title);      // 最新在前
        Assert.Equal("事件 50", svc.All[^1].Title);      // 最舊者已被擠出
    }

    [Fact]
    public void Clear_EmptiesMemoryAndDisk()
    {
        var svc = new EventsService(_dir);
        svc.Add(EventKind.Alert, "顯示卡溫度過高");
        svc.Clear();
        Assert.False(svc.HasEvents);
        Assert.Equal("尚無事件", svc.LatestText);
        Assert.Empty(svc.All);
        Assert.Empty(new EventsService(_dir).All);       // 落地檔亦已清空
    }

    [Fact]
    public void NoteAppStartStop_AreRecordedAsSessionEvents()
    {
        var svc = new EventsService(_dir);
        svc.NoteAppStart();
        svc.NoteAppStop();
        Assert.Equal(2, svc.All.Count);
        Assert.All(svc.All, e => Assert.Equal(EventKind.App, e.Kind));
        Assert.Equal("工作階段", svc.All[0].KindText);
    }
}
