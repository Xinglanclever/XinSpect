using System.Collections.ObjectModel;
using System.Diagnostics;

namespace XinSpect;

/// <summary>UI-thread service around immutable sampling and bounded local rules/history.</summary>
public sealed class SensorAlertService : ObservableObject
{
    private readonly SensorAlertStore _store;
    private readonly SensorAlertEngine _engine = new();
    private readonly ObservableCollection<SensorAlertRule> _rules = new();
    private readonly ObservableCollection<SensorAlertStatus> _statuses = new();
    public ReadOnlyObservableCollection<SensorAlertRule> Rules { get; }
    public ReadOnlyObservableCollection<SensorAlertStatus> Statuses { get; }
    public ObservableCollection<SensorAlertHistoryEntry> History { get; } = new();
    public string FilePath => _store.FilePath;
    private string _configurationProblem = "";
    public string ConfigurationProblem
    {
        get => _configurationProblem;
        private set
        {
            if (SetProperty(ref _configurationProblem, value)) OnPropertyChanged(nameof(DisplayConfigurationProblem));
        }
    }
    public string DisplayConfigurationProblem => ProductTextProjection.LocalizeKnown(_configurationProblem);
    public bool AnyActive => _statuses.Any(s => s.IsActive);
    public event Action<SensorAlertTransition>? Transition;
    public SensorAlertService(SensorAlertStore? store = null)
    {
        _store = store ?? new SensorAlertStore();
        Rules = new(_rules);
        Statuses = new(_statuses);
        var loadedRules = _store.Load(out var error);
        foreach (var rule in loadedRules) _rules.Add(rule);
        ConfigurationProblem = error ?? "";
        RefreshConfiguredStatuses();
    }

    public bool SaveRule(SensorAlertRule rule, out string? error)
    {
        error = rule.Validate();
        if (error is not null) return false;
        var updated = _rules.ToList();
        int i = updated.FindIndex(r => r.Id == rule.Id);
        if (i < 0) updated.Add(rule); else updated[i] = rule;
        if (!_store.Save(updated, out error)) { ConfigurationProblem = error ?? "規則未儲存。"; return false; }
        if (i < 0) _rules.Add(rule); else _rules[i] = rule;
        RefreshConfiguredStatuses();
        ConfigurationProblem = "";
        return true;
    }

    public bool DeleteRule(Guid id, out string? error)
    {
        error = null;
        var rule = _rules.FirstOrDefault(r => r.Id == id);
        if (rule is null) { error = "找不到規則。"; return false; }
        var updated = _rules.Where(r => r.Id != id).ToList();
        if (!_store.Save(updated, out error)) { ConfigurationProblem = error ?? "規則未刪除。"; return false; }
        _rules.Remove(rule);
        RefreshConfiguredStatuses();
        ConfigurationProblem = "";
        return true;
    }

    public void Check(SensorFrame frame, bool enabled) => Check(frame, enabled, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
    internal void Check(SensorFrame frame, bool enabled, long nowTimestamp, DateTimeOffset nowUtc)
    {
        var result = _engine.Evaluate(_rules.ToArray(), frame, enabled, nowTimestamp, nowUtc);
        _statuses.Clear();
        foreach (var status in result.Statuses) _statuses.Add(status);
        OnPropertyChanged(nameof(AnyActive));
        foreach (var change in result.Transitions)
        {
            History.Insert(0, new(change.AtUtc, change.Rule.Id, change.Rule.Name, change.Kind, change.Message,
                change.ProductMessage, change.RawMessageSuffix, change.ProductConditionText, change.ProductReason));
            while (History.Count > 200) History.RemoveAt(History.Count - 1);
            Transition?.Invoke(change);
        }
    }

    private void RefreshConfiguredStatuses()
    {
        var old = _statuses.ToDictionary(s => s.Id);
        _statuses.Clear();
        foreach (var rule in _rules)
            _statuses.Add(old.TryGetValue(rule.Id, out var status) && status.Rule == rule ? status
                : new SensorAlertStatus(rule, rule.Enabled ? SensorAlertPhase.Unknown : SensorAlertPhase.Disabled,
                    false, null, 0, "等待新的感測器採樣。", null, ""));
        OnPropertyChanged(nameof(AnyActive));
    }
}
