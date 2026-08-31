namespace XinSpect;

/// <summary>
/// 把畫面上已經量到的讀值搬進 <see cref="BottleneckFacts"/>，供 <see cref="BottleneckAnalyzer"/> 分析。
/// 這裡只做搬運、單位換算與「有沒有讀到」的判斷：讀不到就留 null，讓引擎自己略過該條規則並登記進
/// Unknowns。<b>不在此處下任何結論</b>，也不替使用者跑量測——各頁的量測一律由使用者發動。
/// </summary>
/// <remarks>
/// 與 <see cref="UpgradeFactsCollector"/> 同一個作法，但取的東西不同：升級建議看的是「這台機器的配置」，
/// 卡點診斷看的是「此刻與最近的行為」，所以這裡大量依賴那些跑過才有值的深入量測服務。
/// </remarks>
internal static class BottleneckFactsCollector
{
    /// <summary>長期樣本的查詢窗（小時）。超出保留天數時查詢自然只會回傳現有資料。</summary>
    private const int WindowHours = 72;

    public static BottleneckFacts Collect(MainViewModel vm)
    {
        var f = new BottleneckFacts();
        Thresholds(vm, f);
        Live(vm, f);
        Cores(vm, f);
        Fans(vm, f);
        Sticky(vm, f);
        CoreTime(vm, f);
        MemoryTruth(vm, f);
        Mca(vm, f);
        Policy(vm, f);
        Ceiling(vm, f);
        TopDown(vm, f);
        History(vm, f);
        return f;
    }

    // ── 使用者自己的門檻 ────────────────────────────────────────────────────

    private static void Thresholds(MainViewModel vm, BottleneckFacts f)
    {
        f.CpuTempThreshold = vm.Settings.CpuTempThreshold;
        f.MemLoadThreshold = vm.Settings.MemLoadThreshold;
    }

    // ── 當下讀值 ────────────────────────────────────────────────────────────

    private static void Live(MainViewModel vm, BottleneckFacts f)
    {
        var live = vm.Live;
        if (live is not null)
        {
            // 負載讀不到時 SensorService 會回 0；0 % 與「沒量到」在這裡差很多——
            // 前者會讓「卡在顯示卡」寫出「處理器只有 0 %」這種不存在的證據，所以比照時脈留 null
            f.CpuLoad = live.CpuLoad > 0 ? live.CpuLoad : null;

            f.CpuTempC = live.CpuTemp;
            f.CpuPowerW = live.CpuPowerW;
            f.CpuClockMhz = live.CpuClock > 0 ? live.CpuClock : null;
            f.MemLoad = live.MemLoad;
            f.MemTotalGb = live.MemTotalGB > 0 ? live.MemTotalGB : null;

            // 顯示卡：只有真的有讀到那張卡才填，沒有獨顯或讀不到就留 null
            if (live.PrimaryGpu is { } g)
            {
                f.GpuLoad = g.LoadPercent;
                f.GpuTempC = g.TempC;
                if (g.VramTotalMB > 0)
                {
                    f.GpuVramUsedGb = g.VramUsedMB / 1024.0;
                    f.GpuVramTotalGb = g.VramTotalMB / 1024.0;
                }
            }

            var drives = live.Drives;
            if (drives.Count > 0)
            {
                var temps = drives.Where(d => d.TempC.HasValue).Select(d => d.TempC!.Value).ToList();
                if (temps.Count > 0) f.MaxDiskTempC = temps.Max();

                var lives = drives.Where(d => d.RemainingLife.HasValue).Select(d => d.RemainingLife!.Value).ToList();
                if (lives.Count > 0) f.WorstDiskLifePercent = lives.Min();
            }
        }

        // 系統碟剩餘空間：讀不到 C: 或容量為 0 時留 null（不當成「滿了」）
        var c = vm.Volumes.Volumes.FirstOrDefault(v => v.Name.StartsWith('C'));
        if (c is { TotalBytes: > 0 }) f.SystemFreePercent = 100.0 * c.FreeBytes / c.TotalBytes;
    }

    // ── 逐核負載分佈 ────────────────────────────────────────────────────────

    private static void Cores(MainViewModel vm, BottleneckFacts f)
    {
        var cores = vm.Live?.CpuCores;
        if (cores is null || cores.Count == 0) return;

        var loads = cores.Select(x => x.LoadPercent).OrderBy(x => x).ToList();
        f.CoreCount = loads.Count;
        f.MaxCoreLoad = loads[^1];
        // 中位數：偶數個取中間兩個的平均，避免核心數為偶數時偏一邊
        f.MedianCoreLoad = loads.Count % 2 == 1
            ? loads[loads.Count / 2]
            : (loads[loads.Count / 2 - 1] + loads[loads.Count / 2]) / 2.0;
    }

    // ── 風扇 ────────────────────────────────────────────────────────────────

    private static void Fans(MainViewModel vm, BottleneckFacts f)
    {
        var fans = vm.Live?.FanControls;
        if (fans is null || fans.Count == 0) return;

        f.FanCount = fans.Count;
        // 與 FanBlade 同一條判斷：只有「輸出 > 5 % 而轉速真的讀到 0」才算停轉；
        // 沒有轉速感測（Rpm 為 null）的風扇不算——那是量不到，不是沒轉。
        f.FansCommandedButStopped = fans.Count(x =>
            !double.IsNaN(x.CurrentPercent) && x.CurrentPercent > 5 && x.Rpm is <= 1);
    }

    // ── 黏滯節流位元 ────────────────────────────────────────────────────────

    private static void Sticky(MainViewModel vm, BottleneckFacts f)
    {
        var rows = vm.ThermalSticky.Rows;
        if (rows.Count == 0) return;   // 還沒讀，三個位元一律留 null

        // 整個暫存器為 0 時該服務會多加一列可信度警告——照它的判斷，不自己重算
        f.StickyUnreliable = rows.Any(r => r.Name.Contains("可信度"));
        if (f.StickyUnreliable) return;

        foreach (var r in rows)
        {
            if (r.Name.Contains("溫度牆紀錄")) f.ThermalLogSeen = r.State == "曾觸發";
            else if (r.Name.Contains("PL2")) f.PowerLimitLogSeen = r.State == "曾觸發";
            else if (r.Name.Contains("目前封裝熱狀態")) f.ThrottlingNow = r.State == "正在降頻中";
        }
    }

    // ── 逐核歸因（DPC／中斷） ───────────────────────────────────────────────

    private static void CoreTime(MainViewModel vm, BottleneckFacts f)
    {
        var rows = vm.CoreTime.Rows;
        if (rows.Count == 0) return;

        var worst = rows.OrderByDescending(r => r.DpcPercent).First();
        f.MaxDpcPercent = worst.DpcPercent;
        f.WorstDpcCore = worst.Name;
        f.MaxInterruptPercent = rows.Max(r => r.InterruptPercent);
    }

    // ── 記憶體真實面貌 ──────────────────────────────────────────────────────

    private static void MemoryTruth(MainViewModel vm, BottleneckFacts f)
    {
        if (vm.MemoryTruth.Reading is not { } r) return;
        f.CommitGb = r.CommitGb;
        f.CommitLimitGb = r.LimitGb;
        f.CommitPeakGb = r.PeakGb;
        f.PhysicalGb = r.PhysicalGb;
    }

    // ── MCA ─────────────────────────────────────────────────────────────────

    private static void Mca(MainViewModel vm, BottleneckFacts f)
    {
        // 「—」＝還沒讀；「無法讀取」＝讀失敗。兩者都不能當成「沒有事件」。
        string s = vm.Mca.Summary;
        if (s == "—" || s.StartsWith("無法讀取")) return;

        var rows = vm.Mca.Rows;
        f.McaUncorrected = rows.Count(r => r.Kind == "不可修正");
        f.McaCorrectedBanks = rows.Count(r => r.Kind == "可修正");
    }

    // ── 電源政策（轉述該服務自己標記過的項目） ──────────────────────────────

    private static void Policy(MainViewModel vm, BottleneckFacts f)
    {
        var p = vm.PowerPolicy;
        f.PowerPlanName = p.PlanName is { Length: > 0 } n && n != "—" ? n : "";
        // 只轉述該服務標記為「真的壓住效能」的項目。注意度（Severity）是著色用的，方向兩邊都算——
        // 「最小處理器狀態 100 %」注意度是 1，但它並沒有讓機器變慢，拿它當卡點是錯的。
        foreach (var row in p.Settings.Where(x => x.LimitsPerformance))
            f.PolicyFlags.Add((row.Name, row.Value));
    }

    // ── 效能天花板 ──────────────────────────────────────────────────────────

    private static void Ceiling(MainViewModel vm, BottleneckFacts f)
    {
        var c = vm.Ceiling;
        if (!c.HasVerdict) return;
        f.CeilingHasVerdict = true;
        f.CeilingHeadline = c.VerdictHeadline;
        f.CeilingDetail = c.VerdictDetail;
        f.CeilingSeverity = c.VerdictSeverity;
    }

    // ── Top-down ────────────────────────────────────────────────────────────

    private static void TopDown(MainViewModel vm, BottleneckFacts f)
    {
        var b = vm.TopDown.Buckets;
        if (b.Count == 0) return;
        f.TdRetiring = Pick(b, "退休");
        f.TdBadSpec = Pick(b, "錯誤推測");
        f.TdFrontend = Pick(b, "前端受限");
        f.TdBackend = Pick(b, "後端受限");
    }

    private static double? Pick(IEnumerable<TopDownBucket> buckets, string prefix)
        => buckets.FirstOrDefault(x => x.Name.StartsWith(prefix))?.Percent;

    // ── 長期樣本 ────────────────────────────────────────────────────────────

    private static void History(MainViewModel vm, BottleneckFacts f)
    {
        // 事件紀錄是持久化的，沒有時間界線就會把三個月前的一次降頻講成「此刻的卡點」。
        // 界線與長期樣本用同一個查詢窗，這樣證據裡寫的時間長度和讀的資料是同一段。
        f.HistoryWindowMinutes = WindowHours * 60;
        var since = DateTime.Now.AddHours(-WindowHours);
        var throttles = vm.Events.All.Where(e => e.Kind == EventKind.Throttle && e.Time >= since).ToList();
        f.ThrottleEventSeen = throttles.Count > 0;
        if (throttles.Count > 0)
        {
            var latest = throttles.Max(e => e.Time);
            f.ThrottleEventText = throttles.Count == 1
                ? $"最近一次 {latest:MM-dd HH:mm}"
                : $"共 {throttles.Count} 次，最近一次 {latest:MM-dd HH:mm}";
        }

        var to = DateTime.UtcNow;
        var series = vm.History.Query(to.AddHours(-WindowHours), to);
        if (series.Count == 0) return;

        // 分鐘級取樣：一點約一分鐘；秒級：一點約一秒。不足一分鐘的秒級樣本也算 1 分鐘，
        // 否則 HistoryMinutes 會是 0，看起來像「完全沒有歷史樣本」
        f.HistoryMinutes = series.SecondLevel ? Math.Max(1, series.Count / 60) : series.Count;

        f.CpuLoadP95 = P95(series, HistoryMetrics.CpuLoad);
        f.CpuTempP95 = P95(series, HistoryMetrics.CpuTemp);
        f.MemLoadP95 = P95(series, HistoryMetrics.MemLoad);
        f.GpuLoadP95 = P95(series, HistoryMetrics.GpuLoad);
    }

    // 沒有該項讀值時回傳 null 而非 0，避免「沒量到」被當成「真的很低」
    private static double? P95(HistorySeries s, int metric)
        => s.HasData(metric) ? s.Summarize(metric).P95 : null;
}
