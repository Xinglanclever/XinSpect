using System.Text.Json;

namespace XinSpect;

public sealed class DeepBenchRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;

    public string Path => _path;
    public string? LastLoadError { get; private set; }

    public DeepBenchRunStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XinSpect", "deepbench-history.json");
    }

    public void Save(DeepBenchRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<DeepBenchRunRecord> records = [.. LoadRecent(100)];
        records.RemoveAll(item => item.SessionId == record.SessionId);
        records.Insert(0, record);
        if (records.Count > 100) records.RemoveRange(100, records.Count - 100);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        AtomicWrite.AllText(_path, JsonSerializer.Serialize(records, JsonOptions));
    }

    public IReadOnlyList<DeepBenchRunRecord> LoadRecent(int maximum = 20)
    {
        LastLoadError = null;
        if (!System.IO.File.Exists(_path)) return [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(_path));
            return document.RootElement.EnumerateArray()
                .Take(Math.Max(0, maximum))
                .Select(element => element.Deserialize<DeepBenchRunRecord>(JsonOptions))
                .Where(record => record is not null)
                .Cast<DeepBenchRunRecord>()
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or System.IO.IOException)
        {
            LastLoadError = $"Deep Bench 歷史 JSON 無法解析（{exception.Message}）；本次回報為空，不推算舊結果。";
            return [];
        }
    }
}
