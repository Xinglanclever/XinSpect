using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>單筆警示事件（時間＋訊息＋嚴重度色）。</summary>
public sealed class AlertEvent
{
    public AlertEvent(string time, string message, Severity sev) { Time = time; Message = message; Severity = sev; }
    public string Time { get; }
    public string Message { get; }
    public Severity Severity { get; }
}

/// <summary>溫度／負載警示：每拍比對即時值與使用者閾值，超標時記錄事件並回呼系統匣通知。
/// 具遲滯（回落一段幅度才解除），避免臨界值抖動狂刷通知。</summary>
public sealed class AlertService : ObservableObject
{
    private const double TempMargin = 3;   // 溫度回落遲滯（°C）
    private const double LoadMargin = 5;    // 負載回落遲滯（%）

    private bool _cpuTempActive, _gpuTempActive, _cpuLoadActive, _memLoadActive;

    /// <summary>近期警示事件（最新在前，上限 100 筆）。</summary>
    public ObservableCollection<AlertEvent> Events { get; } = new();

    private bool _anyActive;
    public bool AnyActive { get => _anyActive; private set { if (SetProperty(ref _anyActive, value)) OnPropertyChanged(nameof(HasEvents)); } }

    public bool HasEvents => Events.Count > 0;

    private string _bannerText = "";
    public string BannerText { get => _bannerText; private set => SetProperty(ref _bannerText, value); }

    /// <summary>由主視窗接上系統匣氣泡通知（標題, 內文）。</summary>
    public Action<string, string>? Balloon { get; set; }

    /// <summary>由主計時器每拍呼叫。</summary>
    public void Check(SensorService live, SettingsService s)
    {
        if (!s.AlertsEnabled)
        {
            if (_anyActive || _cpuTempActive || _gpuTempActive || _cpuLoadActive || _memLoadActive)
            {
                _cpuTempActive = _gpuTempActive = _cpuLoadActive = _memLoadActive = false;
                BannerText = "";
                AnyActive = false;
            }
            return;
        }

        var active = new List<string>();

        if (live.CpuTemp is double ct)
            Evaluate(ct, s.CpuTempThreshold, TempMargin, ref _cpuTempActive, "CPU 溫度", $"{ct:0}°C", "°C", active);

        var g = live.PrimaryGpu;
        if (g?.TempC is double gt)
            Evaluate(gt, s.GpuTempThreshold, TempMargin, ref _gpuTempActive, "GPU 溫度", $"{gt:0}°C", "°C", active);

        Evaluate(live.CpuLoad, s.CpuLoadThreshold, LoadMargin, ref _cpuLoadActive, "CPU 負載", $"{live.CpuLoad:0}%", "%", active);
        Evaluate(live.MemLoad, s.MemLoadThreshold, LoadMargin, ref _memLoadActive, "記憶體負載", $"{live.MemLoad:0}%", "%", active);

        AnyActive = active.Count > 0;
        BannerText = active.Count > 0 ? "⚠ 警示 ・ " + string.Join(" ・ ", active) : "";
    }

    private void Evaluate(double value, double threshold, double margin, ref bool activeFlag,
                          string label, string valueText, string unit, List<string> activeSink)
    {
        bool over = value >= threshold;
        if (over && !activeFlag)
        {
            activeFlag = true;
            Raise($"{label} {valueText} 已超過上限 {threshold:0}{unit}");
        }
        else if (!over && activeFlag && value < threshold - margin)
        {
            activeFlag = false;
        }
        if (activeFlag) activeSink.Add($"{label} {valueText}");
    }

    private void Raise(string message)
    {
        var ev = new AlertEvent(DateTime.Now.ToString("HH:mm:ss"), message, Severity.Critical);
        Events.Insert(0, ev);
        while (Events.Count > 100) Events.RemoveAt(Events.Count - 1);
        OnPropertyChanged(nameof(HasEvents));
        try { Balloon?.Invoke("曦覽 XinSpect ・ 硬體警示", message); } catch { /* 通知為附加 */ }
    }
}
