using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 運算穩定性壓測橋接：以公認的第三方工具 <b>y-cruncher</b> 對 CPU／記憶體施加運算負載，
/// 抓出原生壓測到不了的「靜默運算錯誤」（算 Pi 並自我校驗，結果錯了就代表硬體在壓力下算錯）。
/// y-cruncher 為免費但以 zip 散布的可攜工具，本程式<b>不內含其執行檔</b>：偵測已安裝的路徑
/// （設定插槽＋常見位置），或由使用者手動指定；啟動後擷取其輸出交給 <see cref="YCruncherParser"/> 判定。
/// </summary>
public sealed class StressBridgeService : ObservableObject, IDisposable
{
    private const string SlotName = "ycruncher";
    private readonly SettingsService _settings = new();

    // ── y-cruncher 偵測 ──────────────────────────────────
    private string? _path;
    public string? YCruncherPath
    {
        get => _path;
        private set { if (SetProperty(ref _path, value)) { OnPropertyChanged(nameof(Available)); OnPropertyChanged(nameof(NotAvailable)); OnPropertyChanged(nameof(PathText)); OnPropertyChanged(nameof(CanStart)); } }
    }
    public bool Available => !string.IsNullOrEmpty(_path) && File.Exists(_path);
    public bool NotAvailable => !Available;
    public string PathText => Available ? _path! : "尚未偵測到 y-cruncher（可手動指定，或至官方頁下載可攜版）";

    // ── 執行狀態 ─────────────────────────────────────────
    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) { OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(StartStopText)); } } }
    public bool CanStart => Available && !_running;
    public string StartStopText => _running ? "停止壓測" : "開始壓測";

    private int _durationMin = 10;
    public int DurationMinutes { get => _durationMin; set { if (SetProperty(ref _durationMin, Math.Clamp(value, 1, 720))) OnPropertyChanged(nameof(DurationText)); } }
    public string DurationText => $"{_durationMin} 分鐘";

    private string _status = "正在偵測 y-cruncher…";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private readonly Stopwatch _sw = new();
    private string _elapsed = "00:00";
    public string ElapsedText { get => _elapsed; private set => SetProperty(ref _elapsed, value); }

    // ── 結果（帶原始日誌尾巴供人核對）──────────────────────
    private StressResult? _result;
    public StressResult? Result { get => _result; private set { if (SetProperty(ref _result, value)) { OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(ResultSummary)); OnPropertyChanged(nameof(ResultLog)); OnPropertyChanged(nameof(ResultSeverity)); } } }
    public bool HasResult => _result is not null;
    public string ResultSummary => _result?.Summary ?? "";
    public string ResultLog => _result?.LogTail ?? "";
    public Severity ResultSeverity => _result?.Outcome switch
    {
        StressOutcome.ErrorDetected => Severity.Critical,
        StressOutcome.Incomplete => Severity.Warning,
        StressOutcome.Passed => Severity.Good,
        _ => Severity.Neutral,
    };

    private Process? _proc;
    private readonly StringBuilder _out = new();
    private DispatcherTimer? _tick;

    public StressBridgeService() => _ = InitAsync();

    /// <summary>重新偵測 y-cruncher 路徑。</summary>
    public void Redetect() => _ = InitAsync();

    private async Task InitAsync()
    {
        string? slot = _settings.ToolSlots.TryGetValue(SlotName, out var p) ? p : null;
        string? found = await Task.Run(() => (slot is not null && File.Exists(slot)) ? slot : Locate());
        YCruncherPath = found;
        Status = Available
            ? "y-cruncher 已就緒。設定時長後按「開始壓測」；它會算 Pi 並自我校驗，抓壓力下的運算錯誤。"
            : "尚未偵測到 y-cruncher。它是免費可攜工具（zip），請至官方頁下載解壓後「手動指定」其 y-cruncher.exe。";
    }

    /// <summary>掃描常見位置尋找 y-cruncher.exe（有限深度，避免整碟遞迴）。可攜工具無固定安裝路徑，故掃使用者目錄。</summary>
    private static string? Locate()
    {
        var roots = new List<string>();
        foreach (var f in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
                                   Environment.SpecialFolder.DesktopDirectory })
        {
            var b = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(b)) roots.Add(b);
        }
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var b = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(b)) roots.Add(b);
        }
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var exe = Directory.EnumerateFiles(root, "y-cruncher.exe", opt).FirstOrDefault();
                if (exe is not null) return exe;
            }
            catch { /* 個別目錄存取失敗略過 */ }
        }
        return null;
    }

    /// <summary>使用者手動指定 y-cruncher.exe，並記入設定插槽以便下次直接使用。</summary>
    public bool SetManualPath(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) { Status = "指定的檔案不存在。"; return false; }
        YCruncherPath = exe;
        try { _settings.SetToolSlot(SlotName, exe); } catch { /* 持久化失敗不影響本次使用 */ }
        Status = "已指定 y-cruncher 路徑並記住，可以開始壓測了。";
        return true;
    }

    /// <summary>開啟 y-cruncher 官方下載頁。</summary>
    public void OpenOfficialPage()
    {
        try { Process.Start(new ProcessStartInfo("http://numberworld.org/y-cruncher/") { UseShellExecute = true }); }
        catch (Exception ex) { Status = "開啟官方頁失敗：" + ex.Message; }
    }

    /// <summary>開始／停止切換。</summary>
    public void Toggle() { if (_running) Stop(); else Start(); }

    /// <summary>以目前時長啟動 y-cruncher 壓測（skip-warnings stress -TL:秒），擷取輸出待判定。</summary>
    public void Start()
    {
        if (!Available || _running) return;
        _out.Clear();
        Result = null;
        int totalSec = _durationMin * 60;
        try
        {
            var psi = new ProcessStartInfo(_path!, $"skip-warnings stress -TL:{totalSec}")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_path!) ?? "",
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_out) _out.AppendLine(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_out) _out.AppendLine(e.Data); };
            _proc.Exited += (_, _) => OnUi(Finish);
            if (!_proc.Start()) { Status = "無法啟動 y-cruncher。"; _proc = null; return; }
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
        catch (Exception ex) { Status = "啟動 y-cruncher 失敗：" + ex.Message; _proc = null; return; }

        _sw.Restart();
        ElapsedText = "00:00";
        IsRunning = true;
        _tick ??= MakeTimer();
        _tick.Start();
        Status = $"壓測中：y-cruncher 算 Pi 並自我校驗，時限 {_durationMin} 分鐘。請留意散熱；完成後給出判定。";
    }
    /// <summary>停止壓測（結束 y-cruncher 行程樹）；結果由 Exited→Finish 收斂。</summary>
    public void Stop()
    {
        var p = _proc;
        if (p is null) { if (_running) Finish(); return; }
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* 已結束或無權限 */ }
    }

    private void Finish()
    {
        if (!_running && _proc is null) return;
        _sw.Stop();
        _tick?.Stop();
        IsRunning = false;
        int exit = -1;
        try { if (_proc is { HasExited: true }) exit = _proc.ExitCode; } catch { }
        string stdout; lock (_out) stdout = _out.ToString();
        try { _proc?.Dispose(); } catch { }
        _proc = null;

        Result = YCruncherParser.Parse(exit, _sw.Elapsed.TotalSeconds, _durationMin * 60.0, stdout);
        Status = Result.Summary;
    }

    private DispatcherTimer MakeTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            double s = _sw.Elapsed.TotalSeconds;
            ElapsedText = TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
        };
        return t;
    }

    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
    public void Dispose()
    {
        try { _tick?.Stop(); } catch { }
        try { Stop(); } catch { }
    }
}
