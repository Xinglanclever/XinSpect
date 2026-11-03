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
        "help", "ver", "facts", "fact", "sys", "top", "msr", "pci", "ipc",
        "redfish", "compliance", "merkle", "clear", "exit",
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

        Banner();
        string? line;
        while (true)
        {
            Console.Write($"{Blue}xs{Reset}{Dim}▸{Reset} ");
            line = ReadLineWithHistory();
            if (line is null) break;              // Ctrl+Z／EOF
            line = line.Trim();
            if (line.Length == 0) continue;
            History.Add(line);
            if (!Execute(line)) break;
        }
        return 0;
    }

    private static void Banner()
    {
        Console.WriteLine($"{Bold}曦覽終端機 XinSpect Console{Reset} {Dim}· 唯讀 · 不寫硬體 · 不連網{Reset}");
        Console.WriteLine($"{Dim}輸入 help 看指令；Tab 補全、↑↓ 歷史、Ctrl+C 停止即時刷新、exit 離開。{Reset}");
        Console.WriteLine();
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
                case "help" or "?": Help(); return true;
                case "ver": Console.WriteLine(AppInfo.VersionText); return true;
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

    private static void Help()
    {
        Console.WriteLine($"{Bold}指令{Reset}");
        (string c, string d)[] rows =
        [
            ("facts", "採集一輪本機事實（之後的 fact 查詢走這份快取）"),
            ("fact <字>", "在事實快取裡搜鍵名或值"),
            ("sys", "即時一行：CPU/GPU 溫度與負載、記憶體"),
            ("top [秒]", "即時表格持續刷新（預設 2 秒一拍，Ctrl+C 停）"),
            ("msr <索引>", "讀一支 MSR（十六進位，如 msr 0x19C；唯讀）"),
            ("pci scan", "全匯流排 PCI 掃描（bus 0–255，唯讀）"),
            ("pci dump <n>", "看第 n 台裝置的 256B 設定空間 hex dump"),
            ("ipc <指令>", "送 BlueSquadronBridge（真 JSON 進出）"),
            ("redfish <目錄>", "讀離線 Redfish 目錄（不連網）"),
            ("compliance [格式]", "合規判定（json|sarif|junit|cef|otel）"),
            ("merkle", "穩定事實 Merkle 指紋"),
            ("ver / clear / exit", "版號／清屏／離開"),
        ];
        foreach (var (c, d) in rows)
            Console.WriteLine($"  {Green}{c,-18}{Reset}{Dim}{d}{Reset}");
    }

    // ── 事實採集與查詢 ────────────────────────────────────────────────────

    private static void EnsureFacts()
    {
        if (_cache is not null)
        {
            Console.WriteLine($"{Dim}（使用現有快取；重新採集請再跑一次 facts）{Reset}");
            return;
        }
        Console.Write($"{Dim}採集中（讀 SMBIOS／WMI／感測器，幾秒到幾十秒）…{Reset}");
        _cache = Collect();
        Console.Write("\r\x1b[K");
        Console.WriteLine($"{Green}{_cache.Count}{Reset} 筆事實已進快取。");
    }

    private static void SearchFacts(string query)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        IEnumerable<HardwareFact> rows = _cache;
        if (query.Length > 0)
            rows = rows.Where(f => f.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(40).ToList();
        foreach (var f in list)
        {
            string avail = f.Availability == FactAvailability.Present ? Green : Red;
            Console.WriteLine($"  {Blue}{f.Key,-34}{Reset} {avail}{f.Availability,-10}{Reset} {Trim(f.Value, 60)}");
        }
        int total = query.Length == 0 ? _cache.Count
            : _cache.Count(f => f.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{Dim}… {list.Count} / {total} 筆{Reset}");
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ── 即時遙測 ──────────────────────────────────────────────────────────

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
            var (cpuT, gpuT, cpuL, memL) = svc.Read();
            Console.WriteLine($"CPU {Bold}{Fmt(cpuT)}{Reset}°C {Dim}·{Reset} 負載 {Bold}{Fmt(cpuL)}{Reset}%   "
                + $"GPU {Bold}{Fmt(gpuT)}{Reset}°C   MEM {Bold}{Fmt(memL)}{Reset}%");
        }
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
                var (cpuT, gpuT, cpuL, memL) = svc.Read();
                Console.WriteLine($"  {DateTime.Now.ToString("HH:mm:ss"),-10}{Fmt(cpuT),10}{Fmt(cpuL),10}{Fmt(gpuT),10}{Fmt(memL),8}");
                Thread.Sleep((int)(interval * 1000));
            }
        }
    }

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
        public (double? CpuT, double? GpuT, double? CpuL, double? MemL) Read()
        {
            double? cpuT = null, gpuT = null, cpuL = null, memL = null;
            if (_computer is null) return (null, null, null, null);
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu)
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && cpuT is null && s.Value is { } v) cpuT = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && cpuL is null && s.Value is { } l) cpuL = l;
                    }
                if (hw.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel)
                    foreach (var s in hw.Sensors)
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && gpuT is null && s.Value is { } v) gpuT = v;
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory)
                    foreach (var s in hw.Sensors)
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && memL is null && s.Value is { } v) memL = v;
            }
            return (cpuT, gpuT, cpuL, memL);
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
            Console.Write($"{Dim}掃描 bus 0–255（唯讀）…{Reset}");
            var (devices, error) = PowerConsoleRing0Service.ScanPci();
            Console.Write("\r\x1b[K");
            if (devices is null) { Console.WriteLine($"{Red}掃描失敗：{error}{Reset}"); return; }
            _pci = devices.OrderBy(d => d.Bus).ThenBy(d => d.Device).ThenBy(d => d.Function).ToList();
            foreach (var (d, i) in _pci.Select((d, i) => (d, i)))
                Console.WriteLine($"  [{i,2}] {Blue}{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}{Reset} {d.VendorId:X4}:{d.DeviceId:X4}  {Trim(d.Name, 52)}");
            Console.WriteLine($"{Dim}{_pci.Count} 台。pci dump <n> 看 hex。{Reset}");
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
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buf.ToString(); }
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
                    Console.Write($"{Blue}xs{Reset}{Dim}▸{Reset} " + buf.ToString());
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
        Console.Write("\r\x1b[K");
        Console.Write($"{Blue}xs{Reset}{Dim}▸{Reset} " + buf.ToString());
        if (pos < buf.Length) Console.Write($"\x1b[{buf.Length - pos}D");
    }
}
