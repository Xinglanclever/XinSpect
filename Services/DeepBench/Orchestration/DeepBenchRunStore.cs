using System.IO;
using System.Text;
using System.Text.Json;

namespace XinSpect;

public sealed class DeepBenchRunStore
{
    private const int MaximumRecords = 100;
    // Explicit resource policy, not a claim that all historical files fit this budget.
    internal const int MaximumHistoryBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Func<string, string> _readText;
    private readonly int _maximumBytes;

    public string Path => _path;
    public string? LastLoadError { get; private set; }

    public DeepBenchRunStore(string? path = null)
        : this(path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XinSpect", "deepbench-history.json"),
            file => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1), MaximumHistoryBytes) { }

    // Retain the R357 fault seam; production reads use the bounded stream constructor.
    internal DeepBenchRunStore(string path, Func<string, string> readText)
    {
        _path = path;
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _maximumBytes = MaximumHistoryBytes;
    }

    internal DeepBenchRunStore(string path, Func<string, Stream> openRead, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(openRead);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _path = path;
        _maximumBytes = maximumBytes;
        _readText = file => ReadBoundedText(file, openRead, maximumBytes);
    }

    public void Save(DeepBenchRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        LastLoadError = null;
        List<DeepBenchRunRecord> records;
        try { records = [.. ReadRecords()]; }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            LastLoadError = DescribeReadFailure(exception);
            throw new IOException("Deep Bench 歷史讀取失敗；未寫入或覆寫既有歷史。", exception);
        }
        ValidateStructure(record);
        records.RemoveAll(item => item.SessionId == record.SessionId);
        records.Insert(0, record);
        if (records.Count > MaximumRecords) records.RemoveRange(MaximumRecords, records.Count - MaximumRecords);
        string json = JsonSerializer.Serialize(records, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > _maximumBytes)
            throw new InvalidDataException($"Deep Bench history output exceeds the {_maximumBytes}-byte UTF-8 budget; existing history was not written.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        AtomicWrite.AllText(_path, json);
    }

    public IReadOnlyList<DeepBenchRunRecord> LoadRecent(int maximum = 20)
    {
        LastLoadError = null;
        try { return ReadRecords().Take(Math.Max(0, maximum)).ToArray(); }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            LastLoadError = DescribeReadFailure(exception);
            return [];
        }
    }

    private IReadOnlyList<DeepBenchRunRecord> ReadRecords()
    {
        string json;
        // Only actual missing-file/directory reads mean first use. File.Exists also returns false
        // for some access failures, which must never authorize replacement of unread history.
        try { json = _readText(_path); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Deep Bench history root must be an array.");
        // Save has always retained at most100 records. Refuse an over-budget source rather than
        // silently discarding an unread tail or allocating an unbounded deserialized record list.
        if (document.RootElement.GetArrayLength() > MaximumRecords)
            throw new InvalidDataException("Deep Bench history exceeds the100-record retention budget.");
        var records = new List<DeepBenchRunRecord>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            DeepBenchRunRecord record = element.Deserialize<DeepBenchRunRecord>(JsonOptions)
                ?? throw new JsonException("Deep Bench history contains a null record.");
            ValidateStructure(record);
            records.Add(record);
        }
        return records;
    }

    private static string ReadBoundedText(string path, Func<string, Stream> openRead, int maximumBytes)
    {
        using Stream source = openRead(path);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[Math.Min(64 * 1024, maximumBytes)];
        while (bytes.Length < maximumBytes)
        {
            int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - bytes.Length));
            if (count == 0) break;
            bytes.Write(buffer, 0, count);
        }
        // Probe at most one extra actual byte, including a growing/non-seekable source.
        if (bytes.Length == maximumBytes && source.ReadByte() != -1)
            throw new InvalidDataException($"Deep Bench history exceeds the {maximumBytes}-byte input budget; existing history was preserved.");
        bytes.Position = 0;
        // Match File.ReadAllText's default UTF-8 and BOM detection for UTF-8/16/32.
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Structural null checks follow the model's non-null members and its consumers.
    // Empty collections/strings and unknown numeric enums remain accepted. This does not
    // validate units, values, chronology, session correspondence or measurement authenticity.
    private static void ValidateStructure(DeepBenchRunRecord record)
    {
        Required(record.Results, "results");
        Required(record.Insights, "insights");
        foreach (DeepBenchTestResult result in record.Results)
        {
            Required(result, "results[]");
            Required(result.TestId, "results[].testId");
            Required(result.Configuration, "results[].configuration");
            Required(result.Metrics, "results[].metrics");
            RequiredStrings(result.Conditions, "results[].conditions");
            RequiredStrings(result.Limitations, "results[].limitations");
            foreach (DeepBenchMetric metric in result.Metrics)
            {
                Required(metric, "results[].metrics[]");
                Required(metric.Id, "metrics[].id");
                Required(metric.Title, "metrics[].title");
                Required(metric.Unit, "metrics[].unit");
                Required(metric.Configuration, "metrics[].configuration");
                Required(metric.Samples, "metrics[].samples");
                Required(metric.Points, "metrics[].points");
                foreach (DeepBenchMetricPoint point in metric.Points)
                {
                    Required(point, "metrics[].points[]");
                    Required(point.Axes, "points[].axes");
                    Required(point.Samples, "points[].samples");
                    foreach (KeyValuePair<string, string> axis in point.Axes)
                        Required(axis.Value, "points[].axes value");
                }
            }
        }
        foreach (DeepBenchInsight insight in record.Insights)
        {
            Required(insight, "insights[]");
            Required(insight.Title, "insights[].title");
            Required(insight.Text, "insights[].text");
            RequiredStrings(insight.EvidenceTestIds, "insights[].evidenceTestIds");
        }
    }

    private static void Required(object? value, string member)
    {
        if (value is null) throw new JsonException($"Deep Bench history requires non-null {member}.");
    }

    private static void RequiredStrings(IReadOnlyList<string>? values, string member)
    {
        Required(values, member);
        foreach (string? value in values!) Required(value, member + "[]");
    }

    private static bool IsReadFailure(Exception exception)
        => exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string DescribeReadFailure(Exception exception)
        => $"Deep Bench 歷史 JSON 無法解析或讀取（{exception.Message}）；本次回報為空，不推算舊結果。";
}
