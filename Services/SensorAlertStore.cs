using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>Separate, bounded local configuration; malformed files are retained, never silently replaced.</summary>
public sealed class SensorAlertStore
{
    public const int MaxFileBytes = 524288;
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect", "sensor-alert-rules.json");
    public string FilePath { get; }
    private bool _loadFailed;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private sealed record Document
    {
        public required int Version { get; init; }
        public required List<SensorAlertRule> Rules { get; init; }
    }
    public SensorAlertStore(string? filePath = null) => FilePath = filePath ?? DefaultFilePath;

    public IReadOnlyList<SensorAlertRule> Load(out string? error)
    {
        error = null;
        _loadFailed = false;
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<SensorAlertRule>();
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes) throw new InvalidDataException("規則檔超出 512 KiB 上限。");
            var doc = JsonSerializer.Deserialize<Document>(stream, Json);
            if (doc is null || doc.Version != 1 || doc.Rules is null) throw new InvalidDataException("不支援的規則格式或版本。");
            Validate(doc.Rules);
            return doc.Rules.AsReadOnly();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            _loadFailed = true;
            error = $"無法載入感測器警示規則；原檔保留，禁止覆寫。請先備份並修正／移開檔案後重新啟動。{ex.Message}";
            return Array.Empty<SensorAlertRule>();
        }
    }

    public bool Save(IReadOnlyList<SensorAlertRule> rules, out string? error)
    {
        error = null;
        if (_loadFailed) { error = "規則檔載入失敗，已保留原檔；本次不覆寫。"; return false; }
        string? temporary = null;
        try
        {
            Validate(rules);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document { Version = 1, Rules = rules.ToList() }, Json);
            if (bytes.Length > MaxFileBytes) throw new InvalidDataException("規則檔超出 512 KiB 上限。");
            var full = Path.GetFullPath(FilePath);
            var directory = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".sensor-alert-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(full)) File.Replace(temporary, full, null);
            else File.Move(temporary, full);
            temporary = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { error = "規則未儲存：" + ex.Message; return false; }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch { /* Preserve original save failure. */ }
        }
    }

    private static void Validate(IReadOnlyList<SensorAlertRule> rules)
    {
        if (rules.Count > SensorAlertEngine.MaxRules) throw new InvalidDataException("最多支援 256 條規則。");
        if (rules.Any(r => r is null || r.Validate() is not null)) throw new InvalidDataException("規則包含無效 ID、單位、閾值或時間預算。");
        if (rules.Select(r => r.Id).Distinct().Count() != rules.Count) throw new InvalidDataException("規則 ID 重複。");
    }
}
