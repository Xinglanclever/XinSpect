namespace XinSpect;

/// <summary>
/// 把主檢視模型上的真實讀值搬進 <see cref="UpgradeFacts"/>，供 <see cref="UpgradeAdvisor"/> 規則引擎分析。
/// 這裡只做搬運與單位換算：讀不到的欄位一律留 null／0／空字串，讓引擎自行略過對應規則——
/// 絕不以「一般情況」補值，也不在此處下任何結論。
/// </summary>
internal static class UpgradeFactsCollector
{
    /// <summary>長期樣本的查詢窗（小時）。超出保留天數時查詢自然只會回傳現有資料。</summary>
    private const int WindowHours = 72;

    public static UpgradeFacts Collect(MainViewModel vm)
    {
        var f = new UpgradeFacts();
        Memory(vm, f);
        Compute(vm, f);
        Storage(vm, f);
        Platform(vm, f);
        History(vm, f);
        return f;
    }

    /// <summary>把「—」與空白一律視為「沒讀到」。</summary>
    private static string Clean(string? s)
        => string.IsNullOrWhiteSpace(s) || s == "—" ? "" : s.Trim();

    // ── 記憶體 ──────────────────────────────────────────────────────────────

    private static void Memory(MainViewModel vm, UpgradeFacts f)
    {
        var mods = vm.Modules;
        f.MemModules = mods.Count;
        f.MemTotalGb = mods.Sum(m => m.CapacityGB);
        // 沒有 SMBIOS 模組表（虛擬機或權限不足）時，退回感測引擎回報的實體總量
        if (f.MemTotalGb <= 0 && vm.Live is { MemTotalGB: > 0 } live) f.MemTotalGb = live.MemTotalGB;
        f.MemChannelsText = Clean(vm.Timings.ChannelsText);
        f.MemSpeedMhz = mods.Count > 0 ? mods.Max(m => m.ConfiguredSpeedMHz) : 0;
        f.MemRatedMhz = mods.Count > 0 ? mods.Max(m => m.RatedSpeedMHz) : 0;
    }

    // ── 處理器與顯示卡 ──────────────────────────────────────────────────────

    private static void Compute(MainViewModel vm, UpgradeFacts f)
    {
        f.CpuName = Clean(vm.Cpu.Name);
        f.CpuCores = vm.Cpu.Cores;
        f.CpuThreads = vm.Cpu.Threads;

        var g = vm.Live?.PrimaryGpu;
        f.HasGpu = g is not null;
        f.GpuName = Clean(g?.Name);
        f.GpuVramGb = g is { VramTotalMB: > 0 } ? g.VramTotalMB / 1024.0 : 0;
        f.HasDiscreteGpu = HasDiscrete(vm);

        // 天梯名次只在啟動時的近似比對真的命中時才填（未命中即留 0，引擎不會談名次）
        var r = vm.Ranking;
        if (r.LocalCpu is { Rank: > 0 } lc) { f.CpuRank = lc.Rank; f.CpuRankTotal = r.CpuTotal(lc.IsLaptop); }
        if (r.LocalGpu is { Rank: > 0 } lg) { f.GpuRank = lg.Rank; f.GpuRankTotal = r.GpuTotal(lg.IsLaptop); }
    }

    /// <summary>
    /// 是否有獨立顯示卡。判不出來時一律回傳 true——寧可少給一條建議，
    /// 也不要對已經有獨顯的機器喊「加一張獨立顯示卡」。
    /// </summary>
    private static bool HasDiscrete(MainViewModel vm)
    {
        var gpus = vm.Live?.Gpus;
        if (gpus is null || gpus.Count == 0) return true;   // 沒讀到顯示卡就不下判斷

        string[] discrete = ["geforce", "rtx", "gtx", "quadro", "tesla", "titan",
                             "radeon rx", "radeon pro", "firepro", "instinct", "arc a", "arc b"];
        string[] integrated = ["uhd graphics", "hd graphics", "iris", "vega graphics",
                               "radeon graphics", "basic display", "basic render"];

        foreach (var g in gpus)
        {
            string n = g.Name.ToLowerInvariant();
            if (discrete.Any(n.Contains)) return true;
            // 名稱看不出來但有大容量專屬顯示記憶體，視為獨顯
            if (!integrated.Any(n.Contains) && g.VramTotalMB >= 3072) return true;
        }
        return gpus.All(g => integrated.Any(g.Name.ToLowerInvariant().Contains)) ? false : true;
    }

    // ── 儲存 ────────────────────────────────────────────────────────────────

    private static void Storage(MainViewModel vm, UpgradeFacts f)
    {
        var disks = vm.PhysicalDisks;
        f.DiskCount = disks.Count;
        f.HddCount = disks.Count(d => d.Kind == DiskKind.Hdd);
        // 系統碟＝磁碟 0（Windows 慣例）；讀不到索引時不下判斷
        var sys = disks.FirstOrDefault(d => d.Index == 0);
        f.SystemDiskIsHdd = sys is { Kind: DiskKind.Hdd };

        var c = vm.Volumes.Volumes.FirstOrDefault(v => v.Name.StartsWith('C'));
        f.SystemFreePercent = c is { TotalBytes: > 0 } ? 100.0 * c.FreeBytes / c.TotalBytes : -1;

        var drives = vm.Live?.Drives;
        if (drives is not null && drives.Count > 0)
        {
            var lives = drives.Where(d => d.RemainingLife.HasValue).Select(d => d.RemainingLife!.Value).ToList();
            if (lives.Count > 0) f.WorstDiskLife = lives.Min();

            var temps = drives.Where(d => d.TempC.HasValue).Select(d => d.TempC!.Value).ToList();
            if (temps.Count > 0) f.MaxDiskTempC = temps.Max();
        }

        var bad = disks.FirstOrDefault(d => d.HealthSeverity >= Severity.Warning);
        if (bad is not null)
        {
            f.DiskHealthWarning = true;
            f.DiskHealthDetail = Clean(bad.HealthDetail).Length > 0
                ? $"{bad.Model}：{bad.HealthDetail}" : bad.Model;
        }
    }

    // ── 平台 ────────────────────────────────────────────────────────────────

    private static void Platform(MainViewModel vm, UpgradeFacts f)
    {
        f.PowerPlan = Clean(vm.Profiles.PowerPlanText);
        // 有電池即視為筆電；讀不到電池資訊時維持 false（建議文字會用桌機版說法）
        try { f.IsLaptop = new BatteryService().Read().Present; } catch { }
    }

    // ── 長期樣本 ────────────────────────────────────────────────────────────

    private static void History(MainViewModel vm, UpgradeFacts f)
    {
        f.ThrottleSeen = vm.Events.All.Any(e => e.Kind == EventKind.Throttle);

        var to = DateTime.UtcNow;
        var series = vm.History.Query(to.AddHours(-WindowHours), to);
        if (series.Count == 0) return;

        // 分鐘級取樣：一點約一分鐘；秒級：一點約一秒
        f.HistoryMinutes = series.SecondLevel ? series.Count / 60 : series.Count;

        f.CpuLoadP95 = P95(series, HistoryMetrics.CpuLoad);
        f.CpuTempP95 = P95(series, HistoryMetrics.CpuTemp);
        f.CpuTempMax = Max(series, HistoryMetrics.CpuTemp);
        f.MemLoadP95 = P95(series, HistoryMetrics.MemLoad);
        f.GpuLoadP95 = P95(series, HistoryMetrics.GpuLoad);
        f.GpuTempP95 = P95(series, HistoryMetrics.GpuTemp);
    }

    // 這台機器沒有該項讀值時（HasData 為 false），一律回傳 null 而非 0，避免被當成「真的很低」
    private static double? P95(HistorySeries s, int metric)
        => s.HasData(metric) ? s.Summarize(metric).P95 : null;

    private static double? Max(HistorySeries s, int metric)
        => s.HasData(metric) ? s.Summarize(metric).Max : null;
}
