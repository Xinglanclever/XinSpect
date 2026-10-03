using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>一次掃描的遙測：只有數字與時間——<b>不記任何事實內容</b>（匿名硬性要求）。</summary>
public sealed record TelemetryScan(
    DateTimeOffset AtUtc,
    double DurationMs,
    int FactCount,
    int UnavailableCount,
    int ExceptionCount);

/// <summary>彙總：掃描次數、平均耗時、讀取失敗率（三態筆數／總筆數）、例外次數。</summary>
public sealed record TelemetrySummary(int Scans, double AvgDurationMs, double UnavailableRate, int ExceptionCount);

/// <summary>
/// 自家可觀測性（V7 WP43／A54）：掃描耗時／讀取失敗率／例外數。
/// 硬性界線：<b>預設關閉</b>——使用者明確 <see cref="Enable"/> 才收集；只記本機（不上傳）；
/// 記錄內容只有數字與時間戳，沒有事實值／序號／任何硬體材料。
/// </summary>
public sealed class SelfTelemetry
{
    /// <summary>整個行程共用的會話（預設關閉）。</summary>
    public static SelfTelemetry Session { get; } = new();

    private readonly object _gate = new();
    private readonly List<TelemetryScan> _scans = [];
    private const int MaxScans = 500; // 環形上限：遙測不該無限成長

    public bool Enabled { get; private set; }

    public void Enable() { lock (_gate) Enabled = true; }
    public void Disable() { lock (_gate) Enabled = false; }

    /// <summary>記一次掃描。未啟用時是無聲的 no-op（預設關閉的意義）。</summary>
    public void RecordScan(TimeSpan duration, int factCount, int unavailableCount, int exceptionCount)
    {
        lock (_gate)
        {
            if (!Enabled) return;
            _scans.Add(new TelemetryScan(DateTimeOffset.UtcNow, duration.TotalMilliseconds, factCount, unavailableCount, exceptionCount));
            if (_scans.Count > MaxScans) _scans.RemoveAt(0);
        }
    }

    public IReadOnlyList<TelemetryScan> Scans
    {
        get { lock (_gate) return _scans.ToArray(); }
    }

    /// <summary>彙總；沒有記錄回 null（如實說「沒有資料」，不給 0 冒充）。</summary>
    public TelemetrySummary? Summary()
    {
        lock (_gate)
        {
            if (_scans.Count == 0) return null;
            double avg = _scans.Average(s => s.DurationMs);
            int totalFacts = _scans.Sum(s => s.FactCount);
            int unavailable = _scans.Sum(s => s.UnavailableCount);
            int exceptions = _scans.Sum(s => s.ExceptionCount);
            return new TelemetrySummary(_scans.Count, avg, totalFacts == 0 ? 0 : (double)unavailable / totalFacts, exceptions);
        }
    }

    // ── 本機存取（不上傳；檔案只含數字與時間戳）──

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void SaveToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(Scans, FileOptions));
    }

    public void LoadFromFile(string path)
    {
        var scans = JsonSerializer.Deserialize<List<TelemetryScan>>(File.ReadAllText(path), FileOptions) ?? [];
        lock (_gate) _scans.AddRange(scans);
    }
}
