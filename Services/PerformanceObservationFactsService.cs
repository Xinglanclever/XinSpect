using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 效能觀測（Vol2 第 323–332 項）：PO-001 分頁池（<c>po.pool</c>）、PO-002 記憶體壓縮（<c>po.compression</c>）、
/// PO-003 頁面錯誤（<c>po.pgflt</c>）、PO-004 ReadyBoost（<c>po.rboost</c>）、PO-008 系統 handle（<c>po.handles</c>）、
/// PO-009 GDI 物件（<c>po.gdi</c>）、PO-011 頻率波動（<c>po.freqvar</c>）、PO-013 計時器解析度（<c>po.timerres</c>）、
/// PO-006 服務崩潰重啟史（<c>po.svccrash</c>）、PO-012 狀態遷移頻率（<c>po.trans</c>，推測級聲明）。
/// </summary>
/// <remarks>
/// <para>
/// <b>來源：</b>分頁池重用 <see cref="MemoryPoolService"/>（NtQuerySystemInformation 唯讀）；
/// handle／GDI 走 <c>GetGuiResources</c> 與 <c>GetProcessHandleCount</c>；其餘為 WMI／登錄／事件唯讀。
/// </para>
/// <para>
/// <b>紅線：</b>① 全部唯讀——不呼叫 <c>SetProcessWorkingSetSize</c>、不注入、不開裝置；
/// ② 計數類 0 是值不是「沒有」（API 失敗一律三態）；③ 不列行程名稱、路徑或 handle 目標；
/// ④ 趨勢類標明「瞬時取樣非長期趨勢」，缺樣本不宣稱；⑤ PO-012 明確標「推測級」。
/// </para>
/// </remarks>
public static class PerformanceObservationFactsService
{
    private const string Cat = "效能觀測";

    public const string PoolKey = "po.pool";
    public const string CompressionKey = "po.compression";
    public const string PageFaultKey = "po.pgflt";
    public const string ReadyBoostKey = "po.rboost";
    public const string HandlesKey = "po.handles";
    public const string GdiKey = "po.gdi";
    public const string FreqVarKey = "po.freqvar";
    public const string TimerResKey = "po.timerres";
    public const string SvcCrashKey = "po.svccrash";
    public const string TransitionKey = "po.trans";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public readonly record struct PoolReading(double PagedMB, double NonPagedMB, double CommitUsedMB);

    public delegate PoolReading? PoolProbe();
    public delegate (double CompressedMB, double TotalMB)? CompressionProbe();
    public delegate (ulong Faults, uint Rate)? PageFaultProbe();
    public delegate (bool Enabled, bool Present)? ReadyBoostProbe();
    public delegate (int ProcessHandles, int SystemHandles)? HandleProbe();
    public delegate (uint Gdi, uint User)? GdiProbe();
    public delegate IReadOnlyList<double>? FreqProbe();
    public delegate double? TimerResProbe();
    public delegate IReadOnlyList<(string Service, int Count)>? SvcCrashProbe();
    public delegate (long ContextSwitches, long Interrupts)? TransitionProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PoolProbe? pool = null,
        CompressionProbe? compression = null,
        PageFaultProbe? pgflt = null,
        ReadyBoostProbe? readyBoost = null,
        HandleProbe? handles = null,
        GdiProbe? gdi = null,
        FreqProbe? freq = null,
        TimerResProbe? timerRes = null,
        SvcCrashProbe? svcCrash = null,
        TransitionProbe? transition = null)
        =>
        [
            PoolFact(at, pool),
            CompressionFact(at, compression),
            PageFaultFact(at, pgflt),
            ReadyBoostFact(at, readyBoost),
            HandlesFact(at, handles),
            GdiFact(at, gdi),
            FreqVarFact(at, freq),
            TimerResFact(at, timerRes),
            SvcCrashFact(at, svcCrash),
            TransitionFact(at, transition),
        ];

    // ── PO-001 分頁池 ──────────────────────────────────────────────────

    public static HardwareFact PoolFact(DateTimeOffset at, PoolProbe? probe = null)
    {
        PoolReading? p;
        try { p = (probe ?? ReadPool)(); }
        catch { p = null; }

        if (p is null)
            return Unavailable(at, PoolKey, Cat, "分頁池", FactAvailability.ReadError,
                "核心記憶體池查詢失敗——讀不到就是不猜");

        var v = p.Value;
        return new HardwareFact(PoolKey, Cat, "分頁池",
            $"分頁池 {v.PagedMB:0.#} MB、非分頁池 {v.NonPagedMB:0.#} MB、認可 {v.CommitUsedMB:0.#} MB。" +
            "分頁池持續上升是核心洩漏的常見徵兆，但單次取樣看不出趨勢——需入歷史倉比對。本項不做紅黃判決。",
            "MB", "NtQuerySystemInformation（唯讀）", FactTrustLevel.Measured, false, at, v.PagedMB);
    }

    private static PoolReading? ReadPool()
    {
        var s = MemoryPoolService.Read();
        return s is null ? null : new PoolReading(s.PagedPoolMB, s.NonPagedPoolMB, s.CommitUsedMB);
    }

    // ── PO-002 記憶體壓縮 ──────────────────────────────────────────────

    public static HardwareFact CompressionFact(DateTimeOffset at, CompressionProbe? probe = null)
    {
        (double CompressedMB, double TotalMB)? s;
        try { s = (probe ?? ReadCompression)(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(CompressionKey, Cat, "記憶體壓縮", "", "",
                "Win32_PerfRawData（記憶體壓縮）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "本機無記憶體壓縮計數器（可能已停用或平台不支援）——查無不代填");

        var v = s.Value;
        if (v.TotalMB <= 0)
            return new HardwareFact(CompressionKey, Cat, "記憶體壓縮",
                "目前沒有被壓縮的記憶體（總量 0）——0 是值：這個瞬間沒有可壓縮頁，不代表壓縮功能停用。",
                "MB", "Win32_PerfRawData（唯讀）", FactTrustLevel.Reported, false, at, 0);

        double ratio = v.CompressedMB / v.TotalMB * 100;
        string zeroNote = v.CompressedMB <= 0 ? "0 是值：這個瞬間沒有被壓縮的頁，不代表壓縮功能停用。" : "";
        return new HardwareFact(CompressionKey, Cat, "記憶體壓縮",
            $"壓縮中 {v.CompressedMB:0.#} MB／可壓縮總量 {v.TotalMB:0.#} MB（{ratio:0.#}%）。" + zeroNote +
            "這是取樣瞬間的佔用，不是壓縮率效益；高的佔用可能只是待命頁多，不等於記憶體不足。",
            "MB", "Win32_PerfRawData（唯讀）", FactTrustLevel.Measured, false, at, v.CompressedMB);
    }

    private static (double, double)? ReadCompression()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Compressed, Compressible FROM Win32_PerfRawData_PerfOS_Memory");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    if (!name.Contains("Memory", StringComparison.OrdinalIgnoreCase)) continue;
                    double compressed = Convert.ToDouble(o["Compressed"] ?? 0.0) / 1024 / 1024;
                    double compressible = Convert.ToDouble(o["Compressible"] ?? 0.0) / 1024 / 1024;
                    return (compressed, compressible);
                }
            return null;
        }
        catch { return null; }
    }

    // ── PO-003 頁面錯誤 ────────────────────────────────────────────────

    public static HardwareFact PageFaultFact(DateTimeOffset at, PageFaultProbe? probe = null)
    {
        (ulong Faults, uint Rate)? s;
        try { s = (probe ?? ReadPageFault)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PageFaultKey, Cat, "頁面錯誤", FactAvailability.ReadError,
                "頁面錯誤計數器不可讀——讀不到就是不猜，不畫成 0");

        var v = s.Value;
        return new HardwareFact(PageFaultKey, Cat, "頁面錯誤",
            $"累計頁面錯誤 {v.Faults} 次、目前速率 {v.Rate}/秒。" +
            "頁面錯誤是常態（軟錯誤只要從待命頁取回）；高<b>硬</b>錯誤才代表讀磁碟——本值的速率是取樣瞬間，" +
            "不是平均值。入歷史倉才能談趨勢。",
            "次/秒", "Win32_PerfFormattedData（唯讀）", FactTrustLevel.Measured, false, at, v.Rate);
    }

    private static (ulong, uint)? ReadPageFault()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PageFaultsPersec, PageFaultsPersec_Base FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    return (Convert.ToUInt64(o["PageFaultsPersec_Base"] ?? 0UL),
                            Convert.ToUInt32(o["PageFaultsPersec"] ?? 0U));
            return null;
        }
        catch { return null; }
    }

    // ── PO-004 ReadyBoost ──────────────────────────────────────────────

    public static HardwareFact ReadyBoostFact(DateTimeOffset at, ReadyBoostProbe? probe = null)
    {
        (bool Enabled, bool Present)? s;
        try { s = (probe ?? (() => ReadReadyBoost()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, ReadyBoostKey, Cat, "ReadyBoost", FactAvailability.ReadError,
                "ReadyBoost 設定讀取失敗——讀不到就是不猜");

        var v = s.Value;
        if (!v.Present)
            return new HardwareFact(ReadyBoostKey, Cat, "ReadyBoost",
                "本機沒有 ReadyBoost 裝置（沒有可用的快閃快取）。本項不適用——未使用不代表有問題。",
                "", "登錄 ReadyBoost（唯讀）", FactTrustLevel.Reported, false, at, 0,
                FactAvailability.NotApplicable, "沒有 ReadyBoost 裝置：此功能無從量測");

        return new HardwareFact(ReadyBoostKey, Cat, "ReadyBoost",
            v.Enabled ? "ReadyBoost 已啟用（使用快閃裝置做為快取）。" : "ReadyBoost 裝置存在但未啟用。",
            "", "登錄 ReadyBoost（唯讀）", FactTrustLevel.Reported, false, at, v.Enabled ? 1 : 0);
    }

    private static (bool, bool) ReadReadyBoost()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\EMDMgmt");
            if (key is null) return (false, false);
            string[] subs = key.GetSubKeyNames();
            if (subs.Length == 0) return (false, false);
            return (true, true);
        }
        catch { return (false, false); }
    }

    // ── PO-008 系統 handle ─────────────────────────────────────────────

    public static HardwareFact HandlesFact(DateTimeOffset at, HandleProbe? probe = null)
    {
        (int ProcessHandles, int SystemHandles)? s;
        try { s = (probe ?? ReadHandles)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, HandlesKey, Cat, "系統 handle", FactAvailability.ReadError,
                "handle 計數查詢失敗——讀不到就是不猜");

        var v = s.Value;
        return new HardwareFact(HandlesKey, Cat, "系統 handle",
            $"本行程 handle {v.ProcessHandles}、系統 handle 累計 {v.SystemHandles}。" +
            "系統 handle 只增不減是 handle 洩漏的徵兆，但單次讀值只是快照——需長期取樣；" +
            "本項只列總數，<b>不列 handle 目標或行程名稱</b>。",
            "個", "GetProcessHandleCount／效能計數器（唯讀）", FactTrustLevel.Measured, false, at, v.SystemHandles);
    }

    private static (int, int)? ReadHandles()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            int proc = p.HandleCount;
            using var searcher = new ManagementObjectSearcher(
                "SELECT HandleCount FROM Win32_PerfFormattedData_PerfOS_Objects");
            int sys = 0;
            foreach (ManagementObject o in searcher.Get())
                using (o) { sys = Convert.ToInt32(o["HandleCount"] ?? 0); break; }
            return (proc, sys);
        }
        catch { return null; }
    }

    // ── PO-009 GDI 物件 ────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    private const uint GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1;

    public static HardwareFact GdiFact(DateTimeOffset at, GdiProbe? probe = null)
    {
        (uint Gdi, uint User)? s;
        try { s = (probe ?? (() => ReadGdi()))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, GdiKey, Cat, "GDI 物件", FactAvailability.ReadError,
                "GetGuiResources 查詢失敗——讀不到就是不猜");

        var v = s.Value;
        return new HardwareFact(GdiKey, Cat, "GDI 物件",
            $"本行程 GDI 物件 {v.Gdi}、User 物件 {v.User}。這是<b>本行程</b>的用量（視窗／繪圖資源），" +
            "不是系統總量；GDI 洩漏的典型徵兆是長期單調上升，單次讀值看不出來。",
            "個", "GetGuiResources（唯讀）", FactTrustLevel.Measured, false, at, v.Gdi);
    }

    private static (uint, uint) ReadGdi()
    {
        using var p = Process.GetCurrentProcess();
        return (GetGuiResources(p.Handle, GR_GDIOBJECTS), GetGuiResources(p.Handle, GR_USEROBJECTS));
    }

    // ── PO-011 頻率波動譜 ──────────────────────────────────────────────

    public static HardwareFact FreqVarFact(DateTimeOffset at, FreqProbe? probe = null)
    {
        IReadOnlyList<double>? series;
        try { series = (probe ?? (() => null))(); }
        catch { series = null; }

        if (series is null || series.Count < 2)
            return Unavailable(at, FreqVarKey, Cat, "頻率波動", FactAvailability.NotApplicable,
                "頻率樣本少於兩點——不足以計算變異係數，不宣稱");

        double mean = series.Average();
        if (mean <= 0)
            return Unavailable(at, FreqVarKey, Cat, "頻率波動", FactAvailability.Unknown,
                "頻率讀值為 0——無從計算變異，可能感測器未提供");

        double sd = Math.Sqrt(series.Select(x => (x - mean) * (x - mean)).Sum() / series.Count);
        double cv = sd / mean * 100;
        return new HardwareFact(FreqVarKey, Cat, "頻率波動",
            $"樣本 {series.Count}、平均 {mean:0} MHz、變異係數 {cv:0.#}%。" +
            "變異係數是<b>波動程度</b>不是效能判決：高波動可能是省電調速或負載變化；" +
            "低波動也可能是被電源計畫鎖頻。樣本量有限時 CV 不穩定。",
            "%", "感測器取樣（唯讀）", FactTrustLevel.Measured, false, at, cv);
    }

    // ── PO-013 計時器解析度 ────────────────────────────────────────────

    public static HardwareFact TimerResFact(DateTimeOffset at, TimerResProbe? probe = null)
    {
        double? ms;
        try { ms = (probe ?? ReadTimerRes)(); }
        catch { ms = null; }

        if (ms is null)
            return Unavailable(at, TimerResKey, Cat, "計時器解析度", FactAvailability.ReadError,
                "計時器解析度查詢失敗——讀不到就是不猜");

        return new HardwareFact(TimerResKey, Cat, "計時器解析度",
            $"系統計時器解析度 {ms.Value:0.###} ms。" +
            "程式呼叫 timeBeginPeriod 提高解析度後若未還原，會讓系統保持高頻喚醒而增加耗電——" +
            "本工具<b>不呼叫 timeBeginPeriod</b>，只陳述目前值；高解析度對遊戲／音訊有利、對待機耗電不利。",
            "ms", "NtQueryTimerResolution（唯讀）", FactTrustLevel.Measured, false, at, ms.Value);
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);

    private static double? ReadTimerRes()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out _, out uint current) != 0) return null;
            return current / 10000.0;   // 100ns 單位 → ms
        }
        catch { return null; }
    }

    // ── PO-006 服務崩潰重啟史 ──────────────────────────────────────────

    public static HardwareFact SvcCrashFact(DateTimeOffset at, SvcCrashProbe? probe = null)
    {
        IReadOnlyList<(string Service, int Count)>? rows;
        try { rows = (probe ?? ReadSvcCrash)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, SvcCrashKey, Cat, "服務崩潰重啟", FactAvailability.ReadError,
                "服務控制事件記錄不可讀——讀不到就是不猜，不畫成 0 次");

        if (rows.Count == 0)
            return new HardwareFact(SvcCrashKey, Cat, "服務崩潰重啟",
                "近 30 天沒有服務崩潰重啟事件（7031／7034）——記錄可能已被清理，不畫成「沒問題」。",
                "次", "事件記錄 System（唯讀）", FactTrustLevel.Reported, false, at, 0);

        int total = rows.Sum(r => r.Count);
        return new HardwareFact(SvcCrashKey, Cat, "服務崩潰重啟",
            $"近 30 天 {total} 次服務崩潰重啟（{rows.Count} 個服務）。" +
            "重啟是<b>痕跡不是根因</b>；服務名稱不列入值（只列計數）。反覆崩潰的服務要查自己的記錄檔。",
            "次", "事件記錄 System（唯讀）", FactTrustLevel.Reported, false, at, total);
    }

    private static IReadOnlyList<(string, int)>? ReadSvcCrash()
    {
        try
        {
            using var log = new EventLog("System");
            var since = DateTime.Now.AddDays(-30);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (EventLogEntry e in log.Entries)
            {
                if (e.TimeGenerated < since) continue;
                if (e.InstanceId is not (7031 or 7034)) continue;
                string name = e.Source ?? "unknown";
                counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
            }
            return counts.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        catch { return null; }
    }

    // ── PO-012 狀態遷移頻率 ────────────────────────────────────────────

    public static HardwareFact TransitionFact(DateTimeOffset at, TransitionProbe? probe = null)
    {
        (long ContextSwitches, long Interrupts)? s;
        try { s = (probe ?? ReadTransition)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, TransitionKey, Cat, "狀態遷移頻率", FactAvailability.ReadError,
                "內容切換／中斷計數不可讀——讀不到就是不猜");

        var v = s.Value;
        return new HardwareFact(TransitionKey, Cat, "狀態遷移頻率",
            $"內容切換累計 {v.ContextSwitches}、中斷累計 {v.Interrupts}。" +
            "<b>推測級</b>：這是系統總計的瞬時值，<b>不是</b> C/P-state 切換率本身——" +
            "C/P-state 駐留需要 MSR 配合，跨狀態推論僅供參考，不當事實使用。",
            "次", "效能計數器（唯讀，推測級）", FactTrustLevel.Reported, false, at, v.ContextSwitches);
    }

    private static (long, long)? ReadTransition()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ContextSwitchesPersec, InterruptsPersec FROM Win32_PerfFormattedData_PerfOS_System");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    return (Convert.ToInt64(o["ContextSwitchesPersec"] ?? 0L),
                            Convert.ToInt64(o["InterruptsPersec"] ?? 0L));
            return null;
        }
        catch { return null; }
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
