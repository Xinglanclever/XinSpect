using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace XinSpect;

/// <summary>
/// GPU 燒機測試：以公認的第三方負載產生器 FurMark 2 對顯示卡施加滿載，
/// 配 XinSpect 自身誠實的即時監測（核心／熱點溫度、功耗、核心＋記憶體時脈、風扇、使用率，
/// 含最高／平均與降頻偵測）。FurMark 為 Geeks3D 專有免費軟體，其授權不允許轉散布，
/// 故本程式不內含其執行檔：首次使用時以 Windows 內建 winget 從官方來源安裝
/// （套件 ID Geeks3D.FurMark.2），或由使用者手動指定既有路徑（記入設定插槽）。
/// 智能安全：即時溫度觸及使用者設定的上限時自動結束燒機，保護硬體優先於測試時長。
/// 監測採獨立、僅啟用 GPU 的 LHM Computer，與主監控互不干擾；僅在本頁顯示時輪詢。
/// </summary>
public sealed class GpuStressService : ObservableObject, IDisposable
{
    public const string WingetId = "Geeks3D.FurMark.2";
    private const string SlotName = "FurMark2";

    private readonly SettingsService _settings = new();

    // ── FurMark 偵測 ─────────────────────────────────────
    private string? _furPath;
    public string? FurMarkPath
    {
        get => _furPath;
        private set { if (SetProperty(ref _furPath, value)) { OnPropertyChanged(nameof(Available)); OnPropertyChanged(nameof(NotAvailable)); OnPropertyChanged(nameof(PathText)); OnPropertyChanged(nameof(CanStart)); } }
    }
    public bool Available => !string.IsNullOrEmpty(_furPath) && File.Exists(_furPath);
    public bool NotAvailable => !Available;
    public string PathText => Available ? _furPath! : "尚未偵測到 FurMark（可一鍵安裝或手動指定）";

    // ── winget 可用性與安裝 ──────────────────────────────
    private bool _wingetOk;
    public bool WingetAvailable { get => _wingetOk; private set { if (SetProperty(ref _wingetOk, value)) OnPropertyChanged(nameof(CanInstall)); } }

    private bool _installing;
    public bool IsInstalling { get => _installing; private set { if (SetProperty(ref _installing, value)) { OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(Busy)); } } }
    public bool CanInstall => _wingetOk && !_installing;

    // ── 燒機執行狀態 ─────────────────────────────────────
    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) { OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(Busy)); OnPropertyChanged(nameof(StartStopText)); } } }
    public bool Busy => _installing || _running;
    public bool CanStart => Available && !_running && !_installing;
    public string StartStopText => _running ? "停止燒機" : "開始燒機";

    // ── 燒機設定（智能預設：本機原生解析度、10 分鐘、視窗、OpenGL、開啟熱保護）──
    private int _width = Math.Max(640, (int)SystemParameters.PrimaryScreenWidth);
    public int Width { get => _width; set => SetProperty(ref _width, Math.Clamp(value, 320, 16384)); }

    private int _height = Math.Max(480, (int)SystemParameters.PrimaryScreenHeight);
    public int Height { get => _height; set => SetProperty(ref _height, Math.Clamp(value, 240, 16384)); }

    // 0＝持續（手動停止）；其餘為分鐘
    private int _durationMin = 10;
    public int DurationMinutes { get => _durationMin; set { if (SetProperty(ref _durationMin, Math.Clamp(value, 0, 720))) OnPropertyChanged(nameof(DurationText)); } }
    public string DurationText => _durationMin <= 0 ? "持續（手動停止）" : $"{_durationMin} 分鐘";

    private bool _fullscreen;
    public bool Fullscreen { get => _fullscreen; set => SetProperty(ref _fullscreen, value); }

    private bool _useVulkan;   // false＝OpenGL（furmark-gl）、true＝Vulkan（furmark-vk）
    public bool UseVulkan { get => _useVulkan; set => SetProperty(ref _useVulkan, value); }

    // ── 智能熱保護 ───────────────────────────────────────
    private bool _autoProtect = true;
    public bool AutoProtect { get => _autoProtect; set => SetProperty(ref _autoProtect, value); }

    private double _ceiling = 90;
    public double ThermalCeilingC { get => _ceiling; set { if (SetProperty(ref _ceiling, Math.Clamp(value, 60, 105))) OnPropertyChanged(nameof(CeilingText)); } }
    public string CeilingText => $"{_ceiling:0} °C";

    private bool _tripped;
    public bool ThermalTripped { get => _tripped; private set => SetProperty(ref _tripped, value); }

    private string _status = "";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    // ── 即時監測（獨立 GPU-only LHM）────────────────────
    private Computer? _mon;
    private IHardware? _gpuHw;
    private ISensor? _sTemp, _sHot, _sLoad, _sCoreClk, _sMemClk, _sFanCtrl, _sFanRpm, _sPower;
    private DispatcherTimer? _timer;
    private bool _polling;

    public GpuRow? Gpu { get; private set; }
    public bool HasGpu => Gpu is not null;

    private double? _hotSpot;
    public double? HotSpotC { get => _hotSpot; private set { if (SetProperty(ref _hotSpot, value)) OnPropertyChanged(nameof(HotSpotText)); } }
    public string HotSpotText => _hotSpot is double t ? $"{t:0} °C" : "—";

    private double _fanRpm;
    public double FanRpm { get => _fanRpm; private set { if (SetProperty(ref _fanRpm, value)) OnPropertyChanged(nameof(FanRpmText)); } }
    public string FanRpmText => _fanRpm > 0 ? $"{_fanRpm:0} RPM" : "—";

    // ── 燒機期間累計極值 ─────────────────────────────────
    private double? _maxTemp, _maxHot; private double _tempSum; private int _tempN;
    private double _maxPower, _maxLoad, _maxClk, _minClk;
    private bool _throttled;
    public bool Throttled { get => _throttled; private set { if (SetProperty(ref _throttled, value)) OnPropertyChanged(nameof(VerdictText)); } }

    public double? MaxTempC { get => _maxTemp; private set { if (SetProperty(ref _maxTemp, value)) OnPropertyChanged(nameof(MaxTempText)); } }
    public string MaxTempText => _maxTemp is double t ? $"{t:0} °C" : "—";
    public double? MaxHotSpotC { get => _maxHot; private set { if (SetProperty(ref _maxHot, value)) OnPropertyChanged(nameof(MaxHotText)); } }
    public string MaxHotText => _maxHot is double t ? $"{t:0} °C" : "—";
    public string AvgTempText => _tempN > 0 ? $"{_tempSum / _tempN:0} °C" : "—";
    public double MaxPowerW { get => _maxPower; private set { if (SetProperty(ref _maxPower, value)) OnPropertyChanged(nameof(MaxPowerText)); } }
    public string MaxPowerText => _maxPower > 0 ? $"{_maxPower:0.#} W" : "—";
    public double MaxLoadPercent { get => _maxLoad; private set { if (SetProperty(ref _maxLoad, value)) OnPropertyChanged(nameof(MaxLoadText)); } }
    public string MaxLoadText => _maxLoad > 0 ? $"{_maxLoad:0} %" : "—";

    private readonly Stopwatch _sw = new();
    private string _elapsed = "00:00";
    public string ElapsedText { get => _elapsed; private set => SetProperty(ref _elapsed, value); }

    private string _verdict = "";
    public string VerdictText { get => _verdict; private set => SetProperty(ref _verdict, value); }
    public Severity VerdictSeverity => _tripped ? Severity.Critical
        : _throttled ? Severity.Warning
        : (_maxHot ?? _maxTemp) is double t && t >= 90 ? Severity.Serious : Severity.Good;

    public GpuStressService()
    {
        Status = "正在偵測 FurMark 與 winget…";
        _ = InitAsync();
    }

    /// <summary>重新偵測 FurMark 路徑與 winget 可用性。</summary>
    public void Redetect() => _ = InitAsync();

    /// <summary>將解析度填為本機主螢幕原生大小。</summary>
    public void UseNativeResolution()
    {
        Width = Math.Max(640, (int)SystemParameters.PrimaryScreenWidth);
        Height = Math.Max(480, (int)SystemParameters.PrimaryScreenHeight);
    }

    private async Task InitAsync()
    {
        // 先讀設定插槽中的既有路徑，否則掃描常見安裝位置
        string? slot = _settings.ToolSlots.TryGetValue(SlotName, out var p) ? p : null;
        string? found = await Task.Run(() => (slot is not null && File.Exists(slot)) ? slot : LocateFurMark());
        bool winget = await Task.Run(DetectWinget);
        FurMarkPath = found;
        WingetAvailable = winget;
        Status = Available
            ? "FurMark 已就緒。設定完成後按「開始燒機」，或用「智能燒機」一鍵啟動。"
            : winget ? "尚未安裝 FurMark。按「一鍵安裝 FurMark」由官方 winget 來源下載安裝，或手動指定路徑。"
                     : "尚未安裝 FurMark，且未偵測到 winget。請至官方頁下載 FurMark，或手動指定既有路徑。";
    }

    /// <summary>掃描 Geeks3D 常見安裝目錄尋找 furmark.exe（有限深度，避免整碟遞迴）。</summary>
    private static string? LocateFurMark()
    {
        var roots = new List<string>();
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var b = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(b)) roots.Add(Path.Combine(b, "Geeks3D"));
        }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
                // 優先命令列工具 furmark.exe；找不到再退而求其次用 GUI
                var exe = Directory.EnumerateFiles(root, "furmark.exe", opt).FirstOrDefault()
                       ?? Directory.EnumerateFiles(root, "FurMark_GUI.exe", opt).FirstOrDefault();
                if (exe is not null) return exe;
            }
            catch { /* 個別目錄存取失敗略過 */ }
        }
        return null;
    }

    private static bool DetectWinget()
    {
        try
        {
            var psi = new ProcessStartInfo("winget", "--version")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(8000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>由 winget 官方來源安裝 FurMark 2；完成後重新偵測路徑。</summary>
    public async Task InstallAsync()
    {
        if (!CanInstall) return;
        IsInstalling = true;
        Status = "正在透過 winget 從官方來源下載並安裝 FurMark 2…（首次安裝需數分鐘）";
        int code = await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("winget",
                    $"install --id {WingetId} -e --accept-package-agreements --accept-source-agreements --disable-interactivity")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
                using var pr = Process.Start(psi);
                if (pr is null) return -1;
                pr.WaitForExit();
                return pr.ExitCode;
            }
            catch { return -1; }
        });

        var found = await Task.Run(LocateFurMark);
        FurMarkPath = found;
        IsInstalling = false;
        Status = Available
            ? "FurMark 安裝完成並已偵測到。可以開始燒機了。"
            : code == 0 ? "winget 回報安裝完成，但未能自動找到 furmark.exe，請用「手動指定」選擇其安裝路徑。"
                        : $"安裝未完成（winget 結束碼 {code}）。可重試，或至官方頁手動下載後以「手動指定」指向 furmark.exe。";
    }

    /// <summary>使用者手動指定既有的 furmark.exe / FurMark_GUI.exe，並記入設定插槽以便下次直接使用。</summary>
    public bool SetManualPath(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) { Status = "指定的檔案不存在。"; return false; }
        FurMarkPath = exe;
        try { _settings.SetToolSlot(SlotName, exe); } catch { /* 持久化失敗不影響本次使用 */ }
        Status = "已指定 FurMark 路徑並記住，可以開始燒機了。";
        return true;
    }

    /// <summary>智能燒機：未安裝則先自動安裝，再以目前設定啟動；已安裝則直接啟動。</summary>
    public async Task SmartStartAsync()
    {
        if (_running) { Stop(); return; }
        if (!Available)
        {
            if (!CanInstall) { Status = "FurMark 尚未就緒，且無法自動安裝，請手動指定路徑。"; return; }
            await InstallAsync();
            if (!Available) return;   // 安裝失敗，InstallAsync 已說明
        }
        Start();
    }

    private Process? _proc;

    /// <summary>以目前設定啟動 FurMark 燒機（命令列直接進入 demo，無需在其介面再點選）。</summary>
    public void Start()
    {
        if (!Available || _running) return;

        // 重置本輪累計極值與判定
        _maxTemp = _maxHot = null; _tempSum = 0; _tempN = 0;
        MaxPowerW = MaxLoadPercent = 0; _maxClk = _minClk = 0;
        Throttled = false; ThermalTripped = false; VerdictText = "";
        OnPropertyChanged(nameof(MaxTempText)); OnPropertyChanged(nameof(MaxHotText)); OnPropertyChanged(nameof(AvgTempText));

        string demo = _useVulkan ? "furmark-vk" : "furmark-gl";
        var sb = new StringBuilder($"--demo {demo} --width {_width} --height {_height}");
        if (_durationMin > 0) sb.Append($" --max-time {_durationMin * 60}");
        if (_fullscreen) sb.Append(" --fullscreen");

        try
        {
            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(_furPath!, sb.ToString())
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_furPath!) ?? "" },
                EnableRaisingEvents = true,
            };
            _proc.Exited += (_, _) => OnUi(() => Finish());
            if (!_proc.Start()) { Status = "無法啟動 FurMark。"; _proc = null; return; }
        }
        catch (Exception ex) { Status = "啟動 FurMark 失敗：" + ex.Message; _proc = null; return; }

        _sw.Restart();
        ElapsedText = "00:00";
        IsRunning = true;
        StartMonitor();   // 確保監測輪詢啟動以累計極值與執行熱保護
        Status = _durationMin > 0
            ? $"燒機中：{demo} ・ {_width}×{_height} ・ {_durationMin} 分鐘。請留意溫度；達 {CeilingText} 將自動停止。"
            : $"持續燒機中：{demo} ・ {_width}×{_height}。完成後按「停止燒機」；達 {CeilingText} 將自動停止。";
    }

    /// <summary>停止燒機（結束 FurMark 行程樹）。</summary>
    public void Stop()
    {
        var p = _proc;
        if (p is null) { if (_running) Finish(); return; }
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* 已結束或無權限；狀態仍由 Exited/Finish 收斂 */ }
    }

    /// <summary>開啟 FurMark 圖形介面（命令列參數不適用時的備援；由使用者自行操作）。</summary>
    public void OpenGui()
    {
        if (!Available) return;
        try
        {
            var dir = Path.GetDirectoryName(_furPath!) ?? "";
            var gui = Path.Combine(dir, "FurMark_GUI.exe");
            var target = File.Exists(gui) ? gui : _furPath!;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch (Exception ex) { Status = "開啟 FurMark 介面失敗：" + ex.Message; }
    }

    /// <summary>開啟官方下載頁（winget 不可用時取得 FurMark）。</summary>
    public void OpenOfficialPage()
    {
        try { Process.Start(new ProcessStartInfo("https://geeks3d.com/furmark/downloads/") { UseShellExecute = true }); }
        catch (Exception ex) { Status = "開啟官方頁失敗：" + ex.Message; }
    }

    private void Finish()
    {
        _sw.Stop();
        IsRunning = false;
        try { _proc?.Dispose(); } catch { }
        _proc = null;

        string peak = _maxHot is not null ? $"熱點最高 {MaxHotText}" : $"最高溫 {MaxTempText}";
        VerdictText = _tripped
            ? $"已因高溫自動停止（達 {CeilingText}）・ 歷時 {ElapsedText} ・ {peak} ・ 平均 {AvgTempText}"
            : _throttled
                ? $"完成 ・ 歷時 {ElapsedText} ・ {peak} ・ 平均 {AvgTempText} ・ 期間偵測到降頻（散熱或功耗牆）"
                : $"完成 ・ 歷時 {ElapsedText} ・ {peak} ・ 平均 {AvgTempText} ・ 溫度與時脈穩定";
        OnPropertyChanged(nameof(VerdictSeverity));
        Status = VerdictText;
    }

    // ── 監測輪詢 ─────────────────────────────────────────
    public void StartMonitor()
    {
        if (_mon is null) BuildMonitor();
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => await TickAsync();
        }
        _timer.Start();
    }

    public void StopMonitor()
    {
        if (_running) return;   // 燒機進行中不停止監測，以持續累計極值並執行熱保護
        _timer?.Stop();
    }

    private void BuildMonitor()
    {
        try
        {
            _mon = new Computer { IsGpuEnabled = true };
            _mon.Open();
            _gpuHw = _mon.Hardware.FirstOrDefault(h =>
                h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
            if (_gpuHw is null) { HotSpotC = null; return; }
            _gpuHw.Update();
            _sTemp = Pick(_gpuHw, SensorType.Temperature, "GPU Core", "GPU");
            _sHot = Pick(_gpuHw, SensorType.Temperature, "GPU Hot Spot", "Hot Spot", "Junction");
            _sLoad = Pick(_gpuHw, SensorType.Load, "GPU Core", "GPU", "D3D 3D");
            _sCoreClk = Pick(_gpuHw, SensorType.Clock, "GPU Core");
            _sMemClk = Pick(_gpuHw, SensorType.Clock, "GPU Memory");
            _sFanCtrl = Pick(_gpuHw, SensorType.Control, "GPU Fan", "Fan");
            _sFanRpm = Pick(_gpuHw, SensorType.Fan, "GPU", "Fan");
            _sPower = Pick(_gpuHw, SensorType.Power, "GPU Package", "GPU Power", "GPU");
            Gpu = new GpuRow(_gpuHw.Name) { VendorText = _gpuHw.HardwareType.ToString() };
            OnPropertyChanged(nameof(Gpu));
            OnPropertyChanged(nameof(HasGpu));
        }
        catch { _mon = null; _gpuHw = null; }
    }

    private static ISensor? Pick(IHardware hw, SensorType t, params string[] names)
    {
        foreach (var n in names)
        {
            var s = hw.Sensors.FirstOrDefault(x => x.SensorType == t && x.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (s is not null) return s;
        }
        return null;
    }

    private async Task TickAsync()
    {
        if (_polling || _gpuHw is null) return;
        _polling = true;
        try
        {
            try { await Task.Run(() => _gpuHw.Update()); } catch { /* 單次讀取失敗略過 */ }
            Publish();
        }
        finally { _polling = false; }
    }

    /// <summary>將即時感測值寫入繫結列，並於燒機期間累計極值、偵測降頻、執行熱保護。</summary>
    private void Publish()
    {
        var g = Gpu;
        if (g is null) return;

        double? core = Val(_sTemp), hot = Val(_sHot);
        double load = Val(_sLoad) ?? 0, coreClk = Val(_sCoreClk) ?? 0;
        double memClk = Val(_sMemClk) ?? 0, power = Val(_sPower) ?? 0;
        double fanPct = Val(_sFanCtrl) ?? 0, fanRpm = Val(_sFanRpm) ?? 0;

        g.TempC = core;
        g.LoadPercent = load;
        g.CoreClockMHz = coreClk;
        g.MemClockMHz = memClk;
        g.FanPercent = fanPct;
        g.PowerW = power;
        HotSpotC = hot;
        FanRpm = fanRpm;

        if (!_running) return;

        double secs = _sw.Elapsed.TotalSeconds;
        ElapsedText = TimeSpan.FromSeconds(secs).ToString(secs >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");

        double? eff = hot ?? core;   // 有熱點溫度時以熱點為準（更貼近晶片最熱處）
        if (eff is double e)
        {
            if (!_maxTemp.HasValue || (core ?? e) > _maxTemp) MaxTempC = core ?? e;
            if (hot is double h && (!_maxHot.HasValue || h > _maxHot)) MaxHotSpotC = h;
            _tempSum += e; _tempN++; OnPropertyChanged(nameof(AvgTempText));

            // 熱保護：達上限即自動停止（僅觸發一次）
            if (_autoProtect && !_tripped && e >= _ceiling)
            {
                ThermalTripped = true;
                Status = $"溫度達 {e:0} °C（上限 {CeilingText}），已自動停止燒機以保護硬體。";
                Stop();
                return;
            }
        }
        if (power > _maxPower) MaxPowerW = power;
        if (load > _maxLoad) MaxLoadPercent = load;

        // 降頻偵測：高負載下核心時脈自峰值明顯下滑
        if (load > 50 && coreClk > 0)
        {
            if (coreClk > _maxClk) _maxClk = coreClk;
            if (_minClk <= 0 || coreClk < _minClk) _minClk = coreClk;
            if (secs > 8 && _maxClk > 0 && _minClk < _maxClk * 0.90) Throttled = true;
        }
    }

    private static double? Val(ISensor? s) => s?.Value is float f && !float.IsNaN(f) ? f : (double?)null;

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public void Dispose()
    {
        try { _timer?.Stop(); } catch { }
        try { Stop(); } catch { }
        try { _mon?.Close(); } catch { }
        _mon = null; _gpuHw = null;
    }
}
