using System.IO;
using System.Text;
using XinSpect;

namespace XinSpectCli;

/// <summary>
/// 曦覽終端機（xsc）：Claude Code 式的互動 REPL，跑在使用者的真終端裡。
/// </summary>
/// <remarks>
/// <para>
/// 與主程式的關係：<b>獨立 exe</b>（像 BlueSquadronBridge 一樣是倉庫裡的第二個執行檔），
/// 以 ProjectReference 復用主倉庫的唯讀服務；它不是主程式的一部分，也沒有 WPF 視窗。
/// </para>
/// <para>
/// 終端體驗：ANSI 上色、↑↓ 歷史、Tab 補全、`top` 即時刷新（Ctrl+C 停）。
/// 所有硬體通路與主程式同一套唯讀服務——不新增寫入、不常駐、不連網。
/// </para>
/// </remarks>
internal static class Program
{
    // ── ANSI ──────────────────────────────────────────────────────────────
    private const string Dim = "\x1b[90m";
    private const string Red = "\x1b[91m";
    private const string Green = "\x1b[92m";
    private const string Blue = "\x1b[94m";
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";

    private static readonly string[] Commands =
    [
        "help", "ver", "pages", "page", "domain", "keys", "settings", "changelog",
        "facts", "fact", "sys", "top", "dash", "msr", "pci", "ipc",
        "redfish", "compliance", "merkle", "book", "百科", "clear", "exit",
    ];

    private static readonly List<string> History = [];
    private static IReadOnlyList<HardwareFact>? _cache;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.CursorVisible = true; } catch { /* 重導輸出時沒有游標可管 */ }

        if (args.Length > 0)
        {
            // xsc compliance --format sarif …：直接轉發既有 CLI 通路後退出
            return CliService.Run(args, Collect, Console.Out, Console.Error);
        }

        Tui.Welcome(AppInfo.ShortVersion,
            staticKeys: FactKeyCatalog.Count,
            dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
        Tui.Footer("● READY ・ 唯讀模式", "曦覽 XinSpect Console v" + AppInfo.ShortVersion);
        string? line;
        while (true)
        {
            Tui.InputFrame();
            line = ReadLineWithHistory();
            if (line is null) break;              // Ctrl+Z／EOF
            line = line.Trim();
            if (line.Length == 0) continue;
            History.Add(line);
            if (!Execute(line)) break;
        }
        return 0;
    }

    // ── 指令分派 ──────────────────────────────────────────────────────────

    private static bool Execute(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? line[(line.IndexOf(' ') + 1)..].Trim() : "";
        try
        {
            switch (cmd)
            {
                case "help" or "?": CommandBook.Run(rest.Length > 0 ? rest : null); return true;
                case "book" or "百科" or "手冊": Book.Run(rest.Length > 0 ? rest : null); return true;
                case "mascot" or "吉祥物": MascotPreview(rest); return true;
                case "ver":
                    Tui.Frame($"曦覽 XinSpect v{AppInfo.Version}",
                    [
                        $"代號：{AppInfo.Codename}",
                        "形式：便攜單一執行檔（GUI）＋ xsc 終端機（本程式）",
                        "承諾：唯讀 ・ 不寫硬體 ・ 不連網",
                    ]);
                    return true;

                case "pages": ListPages(); return true;
                case "page": PageFacts(rest); return true;
                case "domain": DomainFacts(rest); return true;
                case "keys": ListKeys(rest); return true;
                case "settings": ShowSettings(); return true;
                case "changelog": ShowChangelog(rest); return true;
                case "clear": Console.Write("\x1b[2J\x1b[H"); return true;
                case "exit" or "quit" or "q": return false;

                case "facts":
                    EnsureFacts();
                    Console.WriteLine($"{Green}{_cache!.Count}{Reset} 筆事實（本機一輪採集）。");
                    Console.WriteLine($"{Dim}用 fact <關鍵字> 查細節。{Reset}");
                    return true;

                case "fact":
                    EnsureFacts();
                    SearchFacts(rest);
                    return true;

                case "sys": LiveLine(); return true;
                case "dash": Dashboard(); return true;

                case "top": LiveTop(rest); return true;

                case "msr":
                    if (rest.Length == 0) { Console.WriteLine($"{Red}用法：msr <十六進位索引>，例如 msr 0x19C{Reset}"); return true; }
                    ReadMsr(rest);
                    return true;

                case "pci":
                    Pci(rest);
                    return true;

                case "ipc":
                    if (rest.Length == 0) { Console.WriteLine($"{Red}用法：ipc ping|status|posture|threats|baseline|snapshot|probe{Reset}"); return true; }
                    Ipc(rest);
                    return true;

                case "redfish":
                    if (rest.Length == 0) { Console.WriteLine($"{Red}用法：redfish <離線目錄>{Reset}"); return true; }
                    RunCli(["--redfish", rest]);
                    return true;

                case "compliance":
                    RunCli(rest.Length == 0 ? ["--compliance"] : ["--compliance", "--format", rest]);
                    return true;

                case "merkle":
                    RunCli(["--merkle"]);
                    return true;

                default:
                    Console.WriteLine($"{Red}未知指令：{cmd}{Reset}（help 看清單）");
                    return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}{ex.GetType().Name}：{ex.Message}{Reset}");
            return true;
        }
    }

    // ── 事實採集與查詢 ────────────────────────────────────────────────────

    /// <summary>吉祥物候選預覽：mascot（全部依序）／mascot a／mascot b，每個候選播一輪動畫並標名。</summary>
    private static void MascotPreview(string arg)
    {
        var names = new[] { "A ・ 我的設計（日核菱形）", "B ・ Gemini 設計" };
        int choice = arg.Trim().ToLowerInvariant() switch
        {
            "a" => 0,
            "b" => 1,
            _ => -1,
        };
        var list = choice >= 0 ? new[] { choice } : new[] { 0, 1 };
        foreach (var c in list)
        {
            Console.WriteLine($"{Tui.BrandRed}{Tui.Bold}候选 {names[c]}{Tui.Reset}");
            Tui.MascotChoice = c;
            if (!Console.IsInputRedirected)
            {
                for (int round = 0; round < 4; round++)
                {
                    Console.WriteLine(Tui.MascotBox(round % 2));
                    System.Threading.Thread.Sleep(350);
                    try { Console.Clear(); } catch { }
                }
            }
            else
            {
                foreach (var lineText in Tui.MascotFrames[0]) Console.WriteLine("  " + lineText);
            }
            Console.WriteLine();
        }
        Console.WriteLine($"{Tui.Dim}跟我說選 A 還是 B（或再看一次：mascot）。選定後我把吉祥物換上。{Tui.Reset}");
    }

    private static void EnsureFacts()
    {
        if (_cache is not null)
        {
            Console.WriteLine($"{Dim}（使用現有快取；重新採集請再跑一次 facts）{Reset}");
            return;
        }
        Tui.SpinnerStart();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _cache = Collect();
        sw.Stop();
        Tui.SpinnerStop($"facts ({FactKeyCatalog.Count:N0} 靜態鍵 · {FactKeyDynamicCatalog.Families.Count} 動態家族 · 耗時 {sw.Elapsed.TotalSeconds:0.00}s)");
        // ⎿ 結果行：快取筆數是真實收集到的數量（含動態展開），與靜態鍵數是兩個口徑，都如實報
        Console.WriteLine($"  {Dim}⎿{Reset}  已進快取 {_cache.Count:N0} 筆事實 · fact <關鍵字> 查單一 · dash 開全螢幕儀表板");
        Console.WriteLine();
    }

    private static void SearchFacts(string query)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        IEnumerable<HardwareFact> rows = _cache;
        if (query.Length > 0)
            rows = rows.Where(f => f.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(30).ToList();
        Tui.Table(
            ["", "KEY", "狀態", "值"],
            list.Select(f => new[]
            {
                f.Availability switch
                {
                    FactAvailability.Present => $"{Tui.SwissGreen}✓{Tui.Reset}",
                    FactAvailability.Unknown => $"{Tui.Dim}?{Tui.Reset}",
                    _ => $"{Tui.Amber}⚠{Tui.Reset}",
                },
                f.Key,
                f.Availability.ToString(),
                Trim(f.Value, 56),
            }));
        int total = query.Length == 0 ? _cache.Count
            : _cache.Count(f => f.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{Dim}… {list.Count} / {total} 筆（✓ 讀到 ・ ⚠ 讀取失敗/權限 ・ ? 未確認）{Reset}");
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ── GUI 全功能對照：頁面清單、逐頁資料、域過濾、鍵目錄、設定、沿革 ────

    private static string HintOf(XinSpect.PageDef p)
    {
        string h = p.Hint ?? "";
        return h.Length > 44 ? h[..44] + "…" : h;
    }

    private static void ListPages()
    {
        Console.WriteLine();
        Console.WriteLine($"{Bold}主頁面（{PageRegistry.Pages.Count}）{Reset}");
        Tui.Table(["KEY", "頁面", "說明"],
            PageRegistry.Pages.Select(p => new[] { p.Key, p.Title, HintOf(p) }));
        Console.WriteLine();
        Console.WriteLine($"{Bold}實用工具（{PageRegistry.Utilities.Count}）{Reset}");
        Tui.Table(["KEY", "工具", "說明"],
            PageRegistry.Utilities.Select(p => new[] { p.Key, p.Title, HintOf(p) }));
        Console.WriteLine($"{Dim}用 page <key> 看該頁對應的資料（先跑 facts 建快取）。{Reset}");
    }

    private static void PageFacts(string key)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        var def = PageRegistry.Pages.Concat(PageRegistry.Utilities)
            .FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                              || p.Title.Contains(key, StringComparison.Ordinal));
        if (def is null) { Console.WriteLine($"{Red}找不到頁面：{key}（pages 看清單）{Reset}"); return; }

        Console.WriteLine($"{Bold}{def.Title}{Reset} {Dim}[{def.Key}・{def.Group}] {def.Hint}{Reset}");
        // 頁面關鍵字＋標題＋鍵名套到事實搜尋；這就是「那一頁」在終端裡的等價資料。
        var terms = new List<string> { def.Key, def.Title };
        terms.AddRange(def.Keywords);
        var hits = _cache.Where(f => terms.Any(t =>
                f.Key.Contains(t, StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains(t, StringComparison.Ordinal)
                || f.Value.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(30).ToList();
        if (hits.Count == 0)
            Console.WriteLine($"{Dim}這一頁的資料不在事實快取的關鍵字範圍；domain <前綴> 或 keys <字> 更精準。{Reset}");
        foreach (var f in hits)
        {
            string avail = f.Availability == FactAvailability.Present ? Green : Red;
            Console.WriteLine($"  {Blue}{f.Key,-34}{Reset} {avail}{f.Availability,-10}{Reset} {Trim(f.Value, 56)}");
        }
        Console.WriteLine($"{Dim}{hits.Count} 筆（上限 30）。{Reset}");
    }

    private static void DomainFacts(string prefix)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        if (prefix.Length == 0)
        {
            var domains = _cache.Select(f => f.Key.Split('.')[0]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine($"{Bold}本機採集到的域（{domains.Count}）：{Reset}" + string.Join("  ", domains));
            Console.WriteLine($"{Dim}用法：domain <前綴>，例如 domain cpu{Reset}");
            return;
        }
        SearchFacts(prefix);
    }

    private static void ListKeys(string query)
    {
        var keys = FactKeyCatalog.Keys;
        IEnumerable<string> rows = keys;
        if (query.Length > 0) rows = rows.Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(40).ToList();
        foreach (var k in list) Console.WriteLine("  " + k);
        Console.WriteLine($"{Dim}{list.Count} / {keys.Count} 把靜態鍵（{FactKeyDynamicCatalog.Families.Count} 個動態家族另計）。{Reset}");
    }

    private static void ShowSettings()
    {
        string path = XinSpect.SettingsService.FilePath;
        if (!File.Exists(path)) { Console.WriteLine($"{Dim}還沒有設定檔（{path}）。{Reset}"); return; }
        Console.WriteLine($"{Dim}{path}{Reset}");
        Console.WriteLine(File.ReadAllText(path));
    }

    private static void ShowChangelog(string nText)
    {
        int n = int.TryParse(nText, out int v) && v > 0 ? v : 3;
        foreach (var e in XinSpect.ChangelogCatalog.Entries.Take(n))
            Tui.Frame($"v{e.Version} ・ {e.DateText}",
                new[] { e.Title, "" }.Concat(e.Items.Select(i => "・ " + Trim(i, 76))));
    }

    // ── 即時遙測 ──────────────────────────────────────────────────────────

    private static readonly List<double?> _cpuHist = [], _gpuHist = [], _memHist = [];

    private static void PushHistory((double? CpuT, double? GpuT, double? CpuL, double? MemL, double? CpuClock, double? CpuPower, double? MemUsed) r)
    {
        void Push(List<double?> h, double? v) { h.Add(v); if (h.Count > 40) h.RemoveAt(0); }
        Push(_cpuHist, r.CpuT); Push(_gpuHist, r.GpuT); Push(_memHist, r.MemL);
    }

    private static void LiveLine()
    {
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器（LHM 初始化失敗或沒有管理員）。{Reset}");
            return;
        }
        using (svc)
        {
            Tui.Section("即時快照", "sys 單拍 ・ top 2 持續 ・ dash 全螢幕");
            var r = svc.Read();
            PushHistory(r);
            Tui.Kpi("CPU 溫度", r.CpuT, " °C", 100, warnAbove: 75, critAbove: 85);
            Console.WriteLine($"      {Dim}走勢{Reset} {Tui.Sparkline(_cpuHist, 100, 24)}");
            Tui.Kpi("CPU 負載", r.CpuL, " %", 100);
            Tui.Kpi("GPU 溫度", r.GpuT, " °C", 100, warnAbove: 75, critAbove: 85);
            Console.WriteLine($"      {Dim}走勢{Reset} {Tui.Sparkline(_gpuHist, 100, 24)}");
            Tui.Kpi("記憶體", r.MemL, " %", 100, warnAbove: 85, critAbove: 95);
            Console.WriteLine($"      {Dim}走勢{Reset} {Tui.Sparkline(_memHist, 100, 24)}");
            Tui.Separator();
            Console.WriteLine($"  {Pad("CPU 時脈", 12)}{Bold}{(r.CpuClock is null ? "——" : $"{r.CpuClock:0.00}")}{Reset}{Dim} GHz{Reset}   "
                + $"{Pad("CPU 功率", 12)}{Bold}{(r.CpuPower is null ? "——" : $"{r.CpuPower:0.#}")}{Reset}{Dim} W{Reset}   "
                + $"{Pad("記憶體用量", 12)}{Bold}{(r.MemUsed is null ? "——" : $"{r.MemUsed:0.0}")}{Reset}{Dim} GB{Reset}");
        }
    }

    private static string Pad(string s, int w) => s + new string(' ', Math.Max(0, w - Tui.VisibleLength(s)));

    // ── 全螢幕儀表板：獨立螢幕、原地重繪、q 離開 ────────────────────────────

    private static void Dashboard()
    {
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器。{Reset}");
            return;
        }
        using (svc)
        {
            Tui.AltScreenEnter();
            try
            {
                while (true)
                {
                    var r = svc.Read();
                    PushHistory(r);
                    Console.Write("\x1b[H");
                    Console.WriteLine($"{Tui.Dim}┌─ {Tui.Bold}XSC 即時儀表板{Tui.Reset} {Tui.Dim}{new string('─', 44)}{DateTime.Now:HH:mm:ss} ─┐{Tui.Reset}");
                    Console.WriteLine();
                    DashRow("CPU 溫度", r.CpuT, "°C", 100, 75, 85, _cpuHist, ref _prevCpuT);
                    DashRow("CPU 負載", r.CpuL, "%", 100, null, null, null, ref _prevCpuL);
                    DashRow("GPU 溫度", r.GpuT, "°C", 100, 75, 85, _gpuHist, ref _prevGpuT);
                    DashRow("記憶體", r.MemL, "%", 100, 85, 95, _memHist, ref _prevMem);
                    DashRow("CPU 時脈", r.CpuClock, "GHz", 6, null, null, null, ref _prevClock);
                    DashRow("CPU 功率", r.CpuPower, "W", 253, 200, 240, null, ref _prevPower);
                    Console.WriteLine();
                    Console.WriteLine($"  {Tui.Dim}記憶體用量 {(r.MemUsed is null ? "——" : r.MemUsed.Value.ToString("0.0") + " GB")} ・ PCI：{(_pci?.Count.ToString() ?? "未掃（pci scan）")} 台 ・ Bridge：本程序未連線{Tui.Reset}");
                    Console.WriteLine();
                    Console.WriteLine($"{Tui.Dim}  q 離開 ・ 1 秒刷新{Tui.Reset}");
                    Console.Write($"{Tui.SwissRed}{Tui.Bold}xs{Tui.Reset}{Tui.SwissBlue}▸{Tui.Reset} dash");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1000)
                    {
                        if (Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true);
                            if (k.Key == ConsoleKey.Q || (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)))
                            {
                                Tui.AltScreenExit();
                                Console.WriteLine();
                                return;
                            }
                        }
                        Thread.Sleep(20);
                    }
                }
            }
            finally { Tui.AltScreenExit(); }
        }
    }

    // 漲落箭頭用：與上一拍比較（溫度升＝紅▲、降＝綠▼；負載升＝黃▲）
    private static double? _prevCpuT, _prevCpuL, _prevGpuT, _prevMem, _prevClock, _prevPower;

    private static void DashRow(string label, double? v, string unit, double max,
        double? warn, double? crit, List<double?>? hist, ref double? prev)
    {
        string vt = v is null ? "——" : $"{v:0.#}{unit}";
        string vc = v is null ? Tui.Dim
            : v >= (crit ?? double.MaxValue) ? Tui.SwissRed
            : v >= (warn ?? double.MaxValue) ? Tui.Amber
            : Tui.Bold;
        string bar = Tui.Bar(v, max, 24, warn, crit);
        string spark = hist is null ? "" : $"  {Tui.Dim}{Tui.Sparkline(hist, max, 18)}{Tui.Reset}";
        // 漲落：與上一拍比。溫度上升是壞事（紅▲）；下降是好事（綠▼）。
        string trend = v is null || prev is null ? $"{Tui.Dim}●{Reset}"
            : v > prev + 0.05 ? $"{Tui.SwissRed}▲{Reset}"
            : v < prev - 0.05 ? $"{Tui.SwissGreen}▼{Reset}"
            : $"{Tui.Dim}●{Reset}";
        prev = v;
        Console.WriteLine("  " + Tui.Pad(label + " " + trend, 12) + Tui.Pad(vc + vt, 14) + bar + spark);
    }

    private static void LiveTop(string secondsText)
    {
        double interval = double.TryParse(secondsText, out double s) && s >= 1 ? s : 2;
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器。{Reset}");
            return;
        }
        using (svc)
        {
            Console.WriteLine($"{Dim}Ctrl+C 停止。{Reset}");
            Console.WriteLine($"  {"時間",-10}{"CPU 溫度",10}{"CPU 負載",10}{"GPU 溫度",10}{"MEM",8}");
            while (true)
            {
                var (cpuT, gpuT, cpuL, memL, _, _, _) = svc.Read();
                Console.WriteLine($"  {DateTime.Now.ToString("HH:mm:ss"),-10}"
                    + $"{Heat(cpuT, 75, 85),10}{Fmt(cpuL),10}{Heat(gpuT, 75, 85),10}{Heat(memL, 85, 95),8}");
                Thread.Sleep((int)(interval * 1000));
            }
        }
    }

    /// <summary>超過警告／臨界門檻時把數字染黃／染紅（top 表用）。</summary>
    private static string Heat(double? v, double warn, double crit)
        => v is null ? "—"
         : v >= crit ? $"{Tui.SwissRed}{v:0.#}{Tui.Reset}"
         : v >= warn ? $"{Tui.Amber}{v:0.#}{Tui.Reset}"
         : v.Value.ToString("0.#");

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("0.#");

    /// <summary>LibreHardwareMonitor 的薄包裝；失敗回 null（缺驅動／非管理員是環境事實）。</summary>
    private sealed class LibreHardwareMonitorLibWrapperOrNull : IDisposable
    {
        private readonly LibreHardwareMonitor.Hardware.Computer? _computer;
        public LibreHardwareMonitorLibWrapperOrNull()
        {
            try
            {
                var c = new LibreHardwareMonitor.Hardware.Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = false,
                    IsPsuEnabled = false,
                };
                c.Open();
                _computer = c;
            }
            catch { _computer = null; }
        }
        public (double? CpuT, double? GpuT, double? CpuL, double? MemL, double? CpuClock, double? CpuPower, double? MemUsed) Read()
        {
            double? cpuT = null, gpuT = null, cpuL = null, memL = null;
            double? cpuClock = null, cpuPower = null, memUsed = null;
            if (_computer is null) return (null, null, null, null, null, null, null);
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu)
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not { } v) continue;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && cpuT is null) cpuT = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && cpuL is null && s.Name == "CPU Total") cpuL = v;
                        // 時脈取全核最大（MHz→GHz）；功率取 Package
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Clock && v > (cpuClock ?? 0) && s.Name.StartsWith("Core", StringComparison.Ordinal)) cpuClock = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) cpuPower = v;
                    }
                if (hw.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel)
                    foreach (var s in hw.Sensors)
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && gpuT is null && s.Value is { } v) gpuT = v;
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory)
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not { } v) continue;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && memL is null) memL = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Data && s.Name == "Used Memory") memUsed = v;
                    }
            }
            return (cpuT, gpuT, cpuL, memL, cpuClock is null ? null : cpuClock / 1000.0, cpuPower, memUsed);
        }
        public void Dispose() => _computer?.Close();
    }

    // ── Ring0／IPC／既有 CLI ──────────────────────────────────────────────

    private static void ReadMsr(string hexText)
    {
        string t = hexText.Replace("0x", "").Replace("0X", "");
        if (!uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out uint index))
        {
            Console.WriteLine($"{Red}索引格式不對（十六進位）。{Reset}");
            return;
        }
        var r = PowerConsoleRing0Service.ReadMsr(index);
        if (r.Error is { } err) { Console.WriteLine($"{Red}MSR 0x{index:X8}：{err}{Reset}"); return; }
        Console.WriteLine($"MSR {Bold}0x{r.Index:X8}{Reset} = {Bold}0x{r.Value:X16}{Reset}");
        Console.WriteLine($"{Dim}EDX 0x{r.Edx:X8} ・ EAX 0x{r.Eax:X8}{Reset}");
    }

    private static List<PciConsoleDevice>? _pci;
    private static void Pci(string rest)
    {
        if (rest is "scan" or "")
        {
            Tui.SpinnerStart("掃描 bus 0–255（唯讀）");
            var (devices, error) = PowerConsoleRing0Service.ScanPci();
            if (devices is null) { Tui.SpinnerStop("掃描失敗：" + error); return; }
            _pci = devices.OrderBy(d => d.Bus).ThenBy(d => d.Device).ThenBy(d => d.Function).ToList();
            Tui.SpinnerStop($"找到 {_pci.Count} 台裝置。");
            Tui.Table(
                ["#", "BDF", "VEN:DEV", "裝置"],
                _pci.Select((d, i) => new[]
                {
                    i.ToString(), $"{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}",
                    $"{d.VendorId:X4}:{d.DeviceId:X4}", Trim(d.Name, 46),
                }));
            return;
        }
        if (rest.StartsWith("dump", StringComparison.Ordinal))
        {
            string nText = rest["dump".Length..].Trim();
            if (_pci is null) { Console.WriteLine($"{Red}先 pci scan。{Reset}"); return; }
            if (!int.TryParse(nText, out int n) || n < 0 || n >= _pci.Count) { Console.WriteLine($"{Red}編號範圍 0–{(_pci?.Count ?? 1) - 1}。{Reset}"); return; }
            var d = _pci[n];
            Console.WriteLine($"{Bold}{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}{Reset}  {d.Name}");
            Console.WriteLine($"{Dim}VEN {d.VendorId:X4} ・ DEV {d.DeviceId:X4} ・ 類別 {d.BaseClass:X2}{d.SubClass:X2}{Reset}");
            for (int row = 0; row < 16; row++)
            {
                Console.Write($"  {row * 16:X2}  ");
                for (int col = 0; col < 16; col++)
                    Console.Write(((d.ConfigSpace[row] >> ((col % 4) * 8)) & 0xFF).ToString("X2") + " ");
                Console.WriteLine();
            }
            return;
        }
        Console.WriteLine($"{Red}用法：pci scan｜pci dump <n>{Reset}");
    }

    private static void Ipc(string rest)
    {
        using var engine = new BlueSquadronEngine();
        if (!engine.Start())
        {
            Console.WriteLine($"{Red}Bridge 啟動失敗：{engine.LastError}{Reset}");
            return;
        }
        var resp = engine.SendCmd(rest, 15000);
        Console.WriteLine(resp is { } json ? json.GetRawText() : $"{Red}無回應（逾時）。{Reset}");
    }

    private static int RunCli(string[] args)
        => CliService.Run(args, Collect, Console.Out, Console.Error);

    /// <summary>與主程式 CLI 同一條採集路徑（EvidenceLabService＋使用者模式事實）。</summary>
    private static IReadOnlyList<HardwareFact> Collect()
    {
        var svc = new EvidenceLabService();
        EvidenceCollection.ReloadInto(svc);
        EvidenceCollection.LoadUsermodeFacts(svc);
        return svc.AllFacts;
    }

    // ── 行編輯：↑↓ 歷史、Tab 補全 ─────────────────────────────────────────

    private static string? ReadLineWithHistory()
    {
        // 輸入被重導向（管線／指令稿）時沒有按鍵事件可拿：退回普通 ReadLine，
        // 讓 `echo "help" | xsc` 這種批次用法也能跑。歷史與 Tab 只在真 TTY 有。
        if (Console.IsInputRedirected) return Console.ReadLine();

        var buf = new StringBuilder();
        int hist = History.Count;
        int pos = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                int w;
                try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                Console.Write(Tui.Esc + "[" + w + "G" + Tui.BrandRed + "│" + Tui.Reset + Environment.NewLine);
                Console.WriteLine($"{Tui.BrandRed}╰{new string('─', w - 2)}╯{Tui.Reset}");   // 長方框底線（閉合）
                string status = "● 唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令書 · book 百科";
                int scol = Math.Max(1, w - status.Length - 1);
                Console.Write(Tui.Esc + "[" + scol + "G" + Tui.Dim + status + Tui.Reset + Environment.NewLine);
                return buf.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (pos > 0)
                {
                    buf.Remove(pos - 1, 1);
                    pos--;
                    Redraw(buf, pos);
                }
                continue;
            }
            if (key.Key == ConsoleKey.LeftArrow) { if (pos > 0) { pos--; Console.Write("\b"); } continue; }
            if (key.Key == ConsoleKey.RightArrow) { if (pos < buf.Length) { pos++; Console.Write("\x1b[1C"); } continue; }
            if (key.Key == ConsoleKey.Home) { pos = 0; Redraw(buf, pos); continue; }
            if (key.Key == ConsoleKey.End) { pos = buf.Length; Redraw(buf, pos); continue; }
            if (key.Key == ConsoleKey.UpArrow)
            {
                if (History.Count == 0) continue;
                hist = Math.Max(0, hist - 1);
                buf.Clear(); buf.Append(History[hist]); pos = buf.Length; Redraw(buf, pos);
                continue;
            }
            if (key.Key == ConsoleKey.DownArrow)
            {
                if (History.Count == 0) continue;
                hist = Math.Min(History.Count, hist + 1);
                buf.Clear();
                if (hist < History.Count) buf.Append(History[hist]);
                pos = buf.Length; Redraw(buf, pos);
                continue;
            }
            if (key.Key == ConsoleKey.Tab)
            {
                string prefix = buf.ToString(0, pos);
                var hits = Commands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1)
                {
                    string done = hits[0];
                    buf.Clear(); buf.Append(done); pos = done.Length;
                    Redraw(buf, pos);
                }
                else if (hits.Count > 1)
                {
                    Console.WriteLine();
                    Console.WriteLine($"{Dim}" + string.Join("  ", hits) + Reset);
                    Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} ");
                    Console.Write(buf.ToString());
                }
                continue;
            }
            if (key.KeyChar == '\0') continue;
            if (char.IsControl(key.KeyChar)) continue;
            buf.Insert(pos, key.KeyChar);
            pos++;
            Redraw(buf, pos);
        }
    }

    private static void Redraw(StringBuilder buf, int pos)
    {
        int w;
        try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
        Console.Write("\r\x1b[K");
        Console.Write($"{Tui.BrandRed}│{Tui.Reset} {Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
        Console.Write(Tui.Esc + "[" + w + "G" + Tui.BrandRed + "│" + Tui.Reset);
        Console.Write(Tui.Esc + "[" + (5 + pos) + "G");   // 游標回輸入位置
    }
}
