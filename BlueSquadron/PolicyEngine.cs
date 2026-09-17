namespace BlueSquadron;

/// <summary>
/// 防禦策略引擎：管理每條防線的模式（偵測/警告/阻擋）與威脅事件蒐集。
/// Phase 1 僅蒐集威脅事件；Phase 2 加入驅動回呼後才有阻擋能力。
/// </summary>
internal sealed class PolicyEngine
{
    private readonly object _lock = new();
    private readonly List<ThreatEventRecord> _threats = [];
    private readonly Dictionary<string, string> _modes = new()
    {
        ["dma"] = "detect",
        ["firmware"] = "detect",
        ["cpu"] = "detect",
        ["storage"] = "detect",
        ["drivers"] = "detect",
    };
    private readonly HashSet<string> _enabled = ["dma", "firmware", "cpu", "storage", "drivers"];

    public void SetMode(string line, string mode)
    {
        lock (_lock)
            if (_modes.ContainsKey(line))
                _modes[line] = mode;
    }

    public void Enable(string line)
    {
        lock (_lock) _enabled.Add(line);
    }

    public void Disable(string line)
    {
        lock (_lock) _enabled.Remove(line);
    }

    public bool IsEnabled(string line)
    {
        lock (_lock) return _enabled.Contains(line);
    }

    public string GetMode(string line)
    {
        lock (_lock) return _modes.GetValueOrDefault(line, "detect");
    }

    /// <summary>記錄一筆威脅事件。</summary>
    public void RecordThreat(string severity, string defenseLine, string title, string detail)
    {
        lock (_lock)
        {
            _threats.Add(new ThreatEventRecord(
                DateTimeOffset.UtcNow, severity, defenseLine, title, detail));
            // Keep only last 500
            if (_threats.Count > 500)
                _threats.RemoveRange(0, _threats.Count - 500);
        }
    }

    /// <summary>取得最近的威脅事件。</summary>
    public IReadOnlyList<ThreatEventRecord> RecentThreats(int max)
    {
        lock (_lock)
        {
            int start = Math.Max(0, _threats.Count - max);
            return _threats.GetRange(start, _threats.Count - start).AsReadOnly();
        }
    }
}

internal sealed record ThreatEventRecord(
    DateTimeOffset Timestamp,
    string Severity,     // "critical"/"warning"/"advisory"
    string DefenseLine,  // "dma"/"firmware"/"cpu"/"storage"/"drivers"
    string Title,
    string Detail
);
