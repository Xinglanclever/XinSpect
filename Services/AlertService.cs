using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>單筆警示事件（時間＋訊息＋嚴重度色）。</summary>
public sealed class AlertEvent
{
    public AlertEvent(string time, string message, Severity sev, string? productPrefix = null,
        string? productMessage = null, string rawMessageSuffix = "",
        string? productConditionText = null, string? productReason = null)
    {
        Time = time; Message = message; Severity = sev; ProductPrefix = productPrefix;
        ProductMessage = productMessage; RawMessageSuffix = rawMessageSuffix;
        ProductConditionText = productConditionText; ProductReason = productReason;
    }
    public string Time { get; }
    /// <summary>Original event text for diagnostics and callers that need the source wording.</summary>
    public string Message { get; }
    public Severity Severity { get; }
    private string? ProductPrefix { get; }
    private string? ProductMessage { get; }
    private string RawMessageSuffix { get; }
    private string? ProductConditionText { get; }
    private string? ProductReason { get; }
    public string DisplayMessage => ProductMessage is not null
        ? (ProductPrefix ?? "") + LanguageService.Catify(ProductMessage,
            ProductTextProjection.LocalizeKnown(ProductMessage)) + RawMessageSuffix
        : ProductConditionText is not null && ProductReason is not null
            ? SensorAlertTextProjection.Triggered(Message, ProductPrefix ?? "", ProductConditionText, ProductReason)
        : LanguageService.Catify(Message, ProductTextProjection.LocalizeKnown(Message));
}

/// <summary>溫度／負載警示：每拍比對即時值與使用者閾值，超標時記錄事件並回呼系統匣通知。
/// 具遲滯（回落一段幅度才解除），避免臨界值抖動狂刷通知。</summary>
public sealed class AlertService : ObservableObject
{
    /// <summary>Explicit arbitrary-sensor rules; their notifications never invoke automatic AI diagnosis.</summary>
    public SensorAlertService SensorAlerts { get; }

    public AlertService(SensorAlertService? sensorAlerts = null)
    {
        SensorAlerts = sensorAlerts ?? new SensorAlertService();
        SensorAlerts.Transition += change =>
        {
            if (change.Kind != SensorAlertTransitionKind.Triggered) return;
            string? productPrefix = change.ProductMessage is null ? null : change.Rule.Name + "：";
            var alert = AddEvent(change.Message, Severity.Critical, productPrefix,
                change.ProductMessage, change.RawMessageSuffix, change.ProductConditionText, change.ProductReason);
            if (change.Notify)
                try { Balloon?.Invoke("曦覽 XinSpect ・ 感測器警示", alert.DisplayMessage); } catch { /* Optional local notification. */ }
        };
    }

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

    /// <summary>
    /// 警示觸發時的追加處理（項目名稱, 訊息），供「主動診斷」接上 AI。
    /// 同一項目在 <see cref="DiagnoseCooldown"/> 內只會回呼一次，避免臨界值反覆越線就連發請求。
    /// </summary>
    public Action<string, string>? Diagnose { get; set; }

    /// <summary>同一警示項目再次觸發主動診斷的最短間隔。</summary>
    public static readonly TimeSpan DiagnoseCooldown = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTime> _lastDiagnose = new(StringComparer.Ordinal);

    /// <summary>Poll failure delivery accepts only immutable failed evidence, never cache-backed legacy scalars.
    /// Existing legacy latches stay unresolved until a successful poll; this path cannot trigger diagnosis.</summary>
    public void CheckFailedFrame(SensorFrame failedFrame, bool alertsEnabled)
    {
        SensorAlerts.Check(failedFrame, alertsEnabled);
        if (!alertsEnabled)
        {
            _cpuTempActive = _gpuTempActive = _cpuLoadActive = _memLoadActive = false;
            BannerText = "";
            AnyActive = false;
            return;
        }
        var active = new List<string>();
        if (_cpuTempActive) active.Add("CPU 溫度（本拍失敗；尚未確認恢復）");
        if (_gpuTempActive) active.Add("GPU 溫度（本拍失敗；尚未確認恢復）");
        if (_cpuLoadActive) active.Add("CPU 負載（本拍失敗；尚未確認恢復）");
        if (_memLoadActive) active.Add("記憶體負載（本拍失敗；尚未確認恢復）");
        UpdateBanner(active);
    }

    /// <summary>由主計時器每拍呼叫。</summary>
    public void Check(SensorService live, SettingsService s)
    {
        // Snapshot exactly once; all arbitrary rules consume the same immutable whole poll.
        var frame = live.CurrentSensorFrame;
        SensorAlerts.Check(frame, s.AlertsEnabled);
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

        Evaluate(live.MemLoad, s.MemLoadThreshold, LoadMargin, ref _memLoadActive, "記憶體負載", $"{live.MemLoad:0}%", "%", active);

        UpdateBanner(active);
    }

    private void UpdateBanner(List<string> active)
    {
        active.AddRange(SensorAlerts.Statuses.Where(status => status.IsActive)
            .Select(status => $"{status.Name} {status.ValueText}（{status.PhaseText}）"));
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
            Raise(label, $"{label} {valueText} 已超過上限 {threshold:0}{unit}");
        }
        else if (!over && activeFlag && value < threshold - margin)
        {
            activeFlag = false;
        }
        if (activeFlag) activeSink.Add($"{label} {valueText}");
    }

    private void Raise(string label, string message)
    {
        var alert = AddEvent(message, Severity.Critical);
        try { Balloon?.Invoke("曦覽 XinSpect ・ 硬體警示", alert.DisplayMessage); } catch { /* 通知為附加 */ }

        if (Diagnose is null) return;
        var now = DateTime.UtcNow;
        if (_lastDiagnose.TryGetValue(label, out var last) && now - last < DiagnoseCooldown) return;
        _lastDiagnose[label] = now;
        try { Diagnose.Invoke(label, message); } catch { /* 主動診斷為附加 */ }
    }

    private AlertEvent AddEvent(string message, Severity severity, string? productPrefix = null,
        string? productMessage = null, string rawMessageSuffix = "",
        string? productConditionText = null, string? productReason = null)
    {
        var alert = new AlertEvent(DateTime.Now.ToString("HH:mm:ss"), message, severity,
            productPrefix, productMessage, rawMessageSuffix, productConditionText, productReason);
        Events.Insert(0, alert);
        while (Events.Count > 100) Events.RemoveAt(Events.Count - 1);
        OnPropertyChanged(nameof(HasEvents));
        return alert;
    }
}
