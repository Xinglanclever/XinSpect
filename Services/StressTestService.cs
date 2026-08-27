using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// 烤機（穩定度壓力測試）：以全部邏輯執行緒持續進行高強度浮點 / 整數運算，將 CPU 推至滿載，
/// 用於觀察長時間高負載下的溫度、頻率與穩定度。可設定時間或持續烤機，隨時停止。
/// 溫度 / 頻率 / 使用率的即時值由 MainViewModel 每秒以 <see cref="Sample"/> 餵入，
/// 服務據此累計最高溫、最高 / 最低頻率並偵測降頻（throttle）。
/// 工作執行緒採一般優先權，確保長時間烤機時介面仍可操作。
/// </summary>
public sealed class StressTestService : ObservableObject
{
    private static double _sink;   // 防止 JIT 將運算最佳化消除

    private CancellationTokenSource? _cts;
    private readonly Stopwatch _sw = new();

    public int Threads => Environment.ProcessorCount;

    // 測試時間：0 代表持續烤機直到手動停止；預設 5 分鐘
    private int _duration = 300;
    public int DurationSeconds { get => _duration; set { if (SetProperty(ref _duration, value)) OnPropertyChanged(nameof(DurationText)); } }
    public string DurationText => _duration <= 0 ? "持續（手動停止）" : $"{_duration} 秒";

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未烤機";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _elapsed = "00:00";
    public string ElapsedText { get => _elapsed; private set => SetProperty(ref _elapsed, value); }

    private double? _maxTemp;
    public double? MaxTempC { get => _maxTemp; private set { if (SetProperty(ref _maxTemp, value)) OnPropertyChanged(nameof(MaxTempText)); } }
    public string MaxTempText => _maxTemp is double t ? $"{t:0} °C" : "—";

    private double _maxClock;
    public double MaxClockMHz { get => _maxClock; private set { if (SetProperty(ref _maxClock, value)) OnPropertyChanged(nameof(MaxClockText)); } }
    public string MaxClockText => _maxClock > 0 ? $"{_maxClock:0} MHz" : "—";

    private double _minClock;
    public double MinClockMHz { get => _minClock; private set { if (SetProperty(ref _minClock, value)) OnPropertyChanged(nameof(MinClockText)); } }
    public string MinClockText => _minClock > 0 ? $"{_minClock:0} MHz" : "—";

    // 降頻偵測：滿載下最低頻率較最高頻率下滑超過門檻，視為出現降頻（散熱 / 功耗牆）
    private bool _throttled;
    public bool Throttled { get => _throttled; private set { if (SetProperty(ref _throttled, value)) { OnPropertyChanged(nameof(StabilityText)); OnPropertyChanged(nameof(StabilitySeverity)); } } }

    // 熱保護：即時溫度觸及安全上限（近 TjMax）時自動中止烤機，避免長時間逼近極限造成硬體風險
    public const double ThermalLimitC = 100;
    private bool _thermalTripped;

    private double _maxLoad;
    public double MaxLoadPercent { get => _maxLoad; private set => SetProperty(ref _maxLoad, value); }

    public string StabilityText => !_running && _sw.Elapsed.TotalSeconds < 1 ? "—"
        : _throttled ? "偵測到降頻（散熱或功耗牆）" : "頻率穩定，未偵測到降頻";
    public Severity StabilitySeverity => _throttled ? Severity.Warning : (_maxTemp is double t && t >= 90 ? Severity.Serious : Severity.Good);

    private string _status = "選擇烤機時間後按「開始烤機」。將以全部邏輯執行緒滿載運算。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    public void SetDuration(int s) => DurationSeconds = s;

    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>由 UI 執行緒每秒餵入即時感測值，累計極值並偵測降頻與更新經過時間 / 進度。</summary>
    public void Sample(double? tempC, double clockMHz, double loadPercent)
    {
        if (!_running) return;

        double secs = _sw.Elapsed.TotalSeconds;
        ElapsedText = TimeSpan.FromSeconds(secs).ToString(secs >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
        if (_duration > 0) ProgressFraction = Math.Clamp(secs / _duration, 0, 1);

        if (tempC is double t && (!_maxTemp.HasValue || t > _maxTemp)) MaxTempC = t;
        if (loadPercent > _maxLoad) MaxLoadPercent = loadPercent;

        // 熱保護：即時溫度觸及安全上限時立即自動中止（僅觸發一次），保護硬體優先於測試完整性
        if (tempC is double tc && tc >= ThermalLimitC && _running && !_thermalTripped)
        {
            _thermalTripped = true;
            StatusLine = $"溫度達 {tc:0} °C（安全上限 {ThermalLimitC:0} °C），已自動中止烤機以保護硬體。";
            Cancel();
            return;
        }

        // 僅在確實高負載（>60%）時採計頻率極值，避免暖機 / 收尾階段的低載頻率汙染判讀
        if (loadPercent > 60 && clockMHz > 0)
        {
            if (clockMHz > _maxClock) MaxClockMHz = clockMHz;
            if (_minClock <= 0 || clockMHz < _minClock) MinClockMHz = clockMHz;
            // 最低較最高下滑逾 12% 視為降頻（先觀察數秒累積穩定極值後才判定）
            if (secs > 8 && _maxClock > 0 && _minClock > 0 && _minClock < _maxClock * 0.88)
                Throttled = true;
        }
    }

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int threads = Threads;

        IsRunning = true;
        Phase = "烤機中";
        ProgressFraction = 0;
        _sw.Restart();
        MaxTempC = null;
        MaxClockMHz = MinClockMHz = 0;
        MaxLoadPercent = 0;
        Throttled = false;
        _thermalTripped = false;
        ElapsedText = "00:00";
        StatusLine = _duration > 0
            ? $"烤機進行中（{threads} 執行緒滿載，{_duration} 秒）… 請留意溫度曲線。"
            : $"持續烤機中（{threads} 執行緒滿載）… 完成後請按「停止」。";

        try
        {
            await Task.Run(() => RunLoad(threads, _duration, ct), ct);

            Phase = "完成";
            ProgressFraction = 1;
            double secs = _sw.Elapsed.TotalSeconds;
            StatusLine = $"烤機結束 ・ 歷時 {ElapsedText} ・ 最高溫 {MaxTempText}"
                       + (_throttled ? " ・ 期間出現降頻" : " ・ 頻率穩定");
        }
        catch (OperationCanceledException)
        {
            Phase = _thermalTripped ? "熱保護中止" : "已停止";
            if (!_thermalTripped)
                StatusLine = $"烤機已停止 ・ 歷時 {ElapsedText} ・ 最高溫 {MaxTempText}"
                           + (_throttled ? " ・ 期間出現降頻" : " ・ 頻率穩定");
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "烤機失敗：" + ex.Message;
        }
        finally
        {
            _sw.Stop();
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>以 <paramref name="threads"/> 條執行緒持續滿載運算，直到逾時或取消。</summary>
    private static void RunLoad(int threads, int durationSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var workers = new Thread[threads];

        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                double sink = 0;
                while (!ct.IsCancellationRequested)
                {
                    if (durationSeconds > 0 && sw.Elapsed.TotalSeconds >= durationSeconds) break;
                    sink += HeatKernel(2_000_000);
                }
                Volatile.Write(ref _sink, sink);
            })
            {
                IsBackground = true,
                // 一般優先權：長時間烤機仍幾近滿載，但不至於餓死 UI 執行緒
                Priority = ThreadPriority.Normal,
                Name = $"XinStress#{t}",
            };
        }

        foreach (var w in workers) w.Start();

        // 逾時或取消時通知各執行緒收尾（迴圈內自行檢查），主緒等待其結束
        foreach (var w in workers) w.Join();

        if (ct.IsCancellationRequested && durationSeconds <= 0)
            ct.ThrowIfCancellationRequested();   // 持續模式下由停止觸發取消
        else
            ct.ThrowIfCancellationRequested();   // 定時模式下若中途取消亦視為停止
    }

    /// <summary>高強度混合運算核心（浮點乘加 + 週期性超越函數以充分加熱 FPU）。</summary>
    private static double HeatKernel(int iters)
    {
        double a = 1.0000001, acc = 0;
        for (int i = 0; i < iters; i++)
        {
            acc += a * a - a + 1.0 / (a + 1.0);
            a += 1e-7;
            if ((i & 4095) == 0) acc += Math.Sqrt(a) + Math.Sin(a);
        }
        return acc;
    }
}
