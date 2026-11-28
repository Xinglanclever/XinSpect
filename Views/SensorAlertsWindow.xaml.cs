using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class SensorAlertsWindow : Window
{
    private readonly SensorService _live;
    private readonly SensorAlertEditor _editor;
    public SensorAlertsWindow(SensorService live, AlertService alerts)
    {
        _live = live;
        _editor = new SensorAlertEditor(alerts.SensorAlerts);
        InitializeComponent();
        DataContext = _editor;
        _editor.RefreshSensors(live.CurrentSensorFrame);
        live.PropertyChanged += LiveChanged;
        alerts.SensorAlerts.PropertyChanged += AlertsChanged;
        Closed += (_, _) =>
        {
            live.PropertyChanged -= LiveChanged;
            alerts.SensorAlerts.PropertyChanged -= AlertsChanged;
        };
        if (LanguageService.IsSimplified) LanguageService.ConvertVisualTree(this, true);
    }

    private void LiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorService.CurrentSensorFrame) or null or "")
            _editor.RefreshSensors(_live.CurrentSensorFrame);
    }
    private void AlertsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SensorAlertService.AnyActive) && _editor.EditingId is Guid id)
            RulesGrid.SelectedItem = _editor.Service.Statuses.FirstOrDefault(s => s.Id == id);
    }
    private void RuleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RulesGrid.SelectedItem is SensorAlertStatus s && s.Id != _editor.EditingId)
            _editor.Edit(s.Rule);
    }
    private void NewRule(object sender, RoutedEventArgs e)
    { RulesGrid.SelectedItem = null; _editor.New(); NameBox.Focus(); }
    private void SaveRule(object sender, RoutedEventArgs e)
    { if (!_editor.Save()) ErrorSummary.Focus(); }
    private void ToggleRule(object sender, RoutedEventArgs e)
    { if (!_editor.Toggle()) ErrorSummary.Focus(); }
    private void DeleteRule(object sender, RoutedEventArgs e)
    { if (!_editor.Delete()) ErrorSummary.Focus(); }
}

internal sealed record SensorAlertSensorChoice(string Id, string Unit, string Label)
{
    public string Display => $"{Label} [{Unit}] — {Id}";
}

/// <summary>Text-entry validation is bound through INotifyDataErrorInfo, with a focusable summary.</summary>
internal sealed class SensorAlertEditor : ObservableObject, INotifyDataErrorInfo
{
    public SensorAlertService Service { get; }
    public ObservableCollection<SensorAlertSensorChoice> Sensors { get; } = new();
    private readonly Dictionary<string, string> _errors = new();
    public bool HasErrors => _errors.Count != 0;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) => propertyName is not null && _errors.TryGetValue(propertyName, out var error)
        ? new[] { error } : Array.Empty<string>();
    public Guid? EditingId { get; private set; }
    private string _ruleName = "", _threshold = "", _recovery = "", _duration = "5", _cooldown = "60", _errorSummary = "";
    private int _directionIndex;
    private bool _ruleEnabled = true;
    private SensorAlertSensorChoice? _selectedSensor;
    public string RuleName { get => _ruleName; set { if (SetProperty(ref _ruleName, value)) ClearError(nameof(RuleName)); } }
    public string Threshold { get => _threshold; set { if (SetProperty(ref _threshold, value)) ClearError(nameof(Threshold)); } }
    public string Recovery { get => _recovery; set { if (SetProperty(ref _recovery, value)) ClearError(nameof(Recovery)); } }
    public string Duration { get => _duration; set { if (SetProperty(ref _duration, value)) ClearError(nameof(Duration)); } }
    public string Cooldown { get => _cooldown; set { if (SetProperty(ref _cooldown, value)) ClearError(nameof(Cooldown)); } }
    public int DirectionIndex { get => _directionIndex; set { if (SetProperty(ref _directionIndex, value)) ClearError(nameof(Recovery)); } }
    public bool RuleEnabled { get => _ruleEnabled; set => SetProperty(ref _ruleEnabled, value); }
    public SensorAlertSensorChoice? SelectedSensor
    {
        get => _selectedSensor;
        set
        {
            if (!SetProperty(ref _selectedSensor, value)) return;
            ClearError(nameof(SelectedSensor)); OnPropertyChanged(nameof(SensorDetail));
            if (EditingId is null && string.IsNullOrWhiteSpace(RuleName) && value is not null) RuleName = value.Label;
        }
    }
    public string SensorDetail => SelectedSensor is { } sensor ? $"原始單位：{(sensor.Unit.Length == 0 ? "無單位" : sensor.Unit)}；{sensor.Id}" : "請選擇感測器；不以名稱自動改配。";
    public string ErrorSummary
    {
        get => _errorSummary;
        private set
        {
            if (SetProperty(ref _errorSummary, value)) OnPropertyChanged(nameof(DisplayErrorSummary));
        }
    }
    public string DisplayErrorSummary
    {
        get
        {
            const string editingPrefix = "正在編輯：";
            if (_errorSummary.StartsWith(editingPrefix, StringComparison.Ordinal))
                return ProductTextProjection.LocalizeKnown(editingPrefix) + _errorSummary[editingPrefix.Length..];
            return ProductTextProjection.LocalizeKnown(_errorSummary);
        }
    }
    public SensorAlertEditor(SensorAlertService service) => Service = service;

    public void RefreshSensors(SensorFrame frame)
    {
        var choices = frame.Readings.GroupBy(r => (r.Id, r.Unit)).Select(g => g.First())
            .Select(r => new SensorAlertSensorChoice(r.Id, r.Unit, r.Label))
            .OrderBy(r => r.Label, StringComparer.CurrentCulture).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var selected = SelectedSensor;
        if (selected is not null && !choices.Any(r => r.Id == selected.Id && r.Unit == selected.Unit))
            choices.Add(selected); // Keep an explicit unavailable binding; never select a different sensor automatically.
        if (choices.SequenceEqual(Sensors)) return;
        Sensors.Clear();
        foreach (var choice in choices) Sensors.Add(choice);
        SelectedSensor = selected is null ? null : Sensors.FirstOrDefault(r => r.Id == selected.Id && r.Unit == selected.Unit);
    }

    public void Edit(SensorAlertRule rule)
    {
        EditingId = rule.Id;
        RuleName = rule.Name; Threshold = Format(rule.Threshold); Recovery = Format(rule.RecoveryThreshold);
        Duration = Format(rule.DurationSeconds); Cooldown = Format(rule.CooldownSeconds);
        DirectionIndex = rule.Direction == SensorAlertDirection.Above ? 0 : 1; RuleEnabled = rule.Enabled;
        var sensor = Sensors.FirstOrDefault(s => s.Id == rule.SensorId && s.Unit == rule.Unit);
        if (sensor is null) { sensor = new(rule.SensorId, rule.Unit, "尚未取得／已缺讀"); Sensors.Add(sensor); }
        SelectedSensor = sensor; ClearErrors(); ErrorSummary = "正在編輯：" + rule.Name;
    }
    public void New()
    {
        EditingId = null; RuleName = Threshold = Recovery = ""; Duration = "5"; Cooldown = "60";
        DirectionIndex = 0; RuleEnabled = true; SelectedSensor = null; ClearErrors(); ErrorSummary = "請輸入新規則，再按儲存。";
    }
    public bool Save()
    {
        ClearErrors();
        if (string.IsNullOrWhiteSpace(RuleName) || RuleName.Length > 100 || RuleName.Any(char.IsControl))
            SetError(nameof(RuleName), "名稱須為 1–100 個字元，不得含控制字元。");
        if (SelectedSensor is null) SetError(nameof(SelectedSensor), "請選擇感測器。");
        bool t = Parse(Threshold, out double threshold), r = Parse(Recovery, out double recovery);
        if (!t) SetError(nameof(Threshold), "觸發閾值須為有限數字。");
        if (!r) SetError(nameof(Recovery), "恢復閾值須為有限數字。");
        if (t && r && (DirectionIndex == 0 ? recovery >= threshold : recovery <= threshold))
            SetError(nameof(Recovery), DirectionIndex == 0 ? "恢復閾值須低於上限。" : "恢復閾值須高於下限。");
        if (!Parse(Duration, out double duration) || duration < 0 || duration > 86400)
            SetError(nameof(Duration), "持續時間須為 0–86400 秒。");
        if (!Parse(Cooldown, out double cooldown) || cooldown < 0 || cooldown > 604800)
            SetError(nameof(Cooldown), "冷卻時間須為 0–604800 秒。");
        if (HasErrors) { ErrorSummary = string.Join(" ", _errors.Values); return false; }
        var rule = new SensorAlertRule
        {
            Id = EditingId ?? Guid.NewGuid(), Name = RuleName.Trim(), SensorId = SelectedSensor!.Id, Unit = SelectedSensor.Unit,
            Direction = DirectionIndex == 0 ? SensorAlertDirection.Above : SensorAlertDirection.Below,
            Threshold = threshold, RecoveryThreshold = recovery, DurationSeconds = duration, CooldownSeconds = cooldown, Enabled = RuleEnabled
        };
        if (!Service.SaveRule(rule, out var error)) { ErrorSummary = error ?? "規則未儲存。"; return false; }
        EditingId = rule.Id; ErrorSummary = "已儲存規則；下一筆新採樣開始判定。"; return true;
    }
    public bool Toggle()
    {
        var rule = Service.Rules.FirstOrDefault(r => r.Id == EditingId);
        if (rule is null) { ErrorSummary = "請先選擇已儲存的規則。"; return false; }
        if (!Service.SaveRule(rule with { Enabled = !rule.Enabled }, out var error)) { ErrorSummary = error ?? "未儲存。"; return false; }
        RuleEnabled = !rule.Enabled; ErrorSummary = RuleEnabled ? "規則已啟用，等待新採樣。" : "規則已停用。"; return true;
    }
    public bool Delete()
    {
        if (EditingId is not Guid id) { ErrorSummary = "請先選擇已儲存的規則。"; return false; }
        if (!Service.DeleteRule(id, out var error)) { ErrorSummary = error ?? "未刪除。"; return false; }
        New(); ErrorSummary = "規則已刪除。"; return true;
    }
    private static string Format(double value) => value.ToString("G", CultureInfo.CurrentCulture);
    private static bool Parse(string text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    private void SetError(string property, string error) { _errors[property] = error; ErrorsChanged?.Invoke(this, new(property)); }
    private void ClearError(string property) { if (_errors.Remove(property)) ErrorsChanged?.Invoke(this, new(property)); }
    private void ClearErrors() { foreach (var key in _errors.Keys.ToArray()) ClearError(key); }
}
