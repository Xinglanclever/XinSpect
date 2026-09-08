using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>
/// 資料來源無關的硬體證據倉：每筆樣本獨立 append 到版本化 JSONL，查詢時再做篩選、桶化與事件推導。
/// </summary>
/// <remarks>
/// 尾端半行會被忽略；完整但損壞的中間行也不阻斷其後紀錄。寫入在同一檔案鎖內一次完成並 flush，
/// 不重寫既有資料。這層只保存與比較客觀讀值，不估算剩餘壽命，也不把限制或能力當實際 P-state。
/// </remarks>
public sealed class EvidenceTimelineService
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultFileName = "evidence-timeline.v1.jsonl";

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Mutex FileMutex = new(false, @"Local\XinSpect-EvidenceTimeline-v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
    };
    private static readonly JsonSerializerOptions ReadOptions = new(JsonOptions)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    public string FilePath { get; }

    public EvidenceTimelineService(string? folder = null, string? fileName = null)
    {
        string root = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        FilePath = Path.Combine(root, fileName ?? DefaultFileName);
        ValidateExistingFile();
    }

    /// <summary>新增一筆樣本。每次只附加一行，不會修改或刪除既有證據。</summary>
    public void Ingest(EvidenceSample sample) => Ingest([sample]);

    public void Ingest(IEnumerable<EvidenceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var envelopes = samples.Select(sample =>
        {
            Validate(sample);
            return new EvidenceEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                RecordType = "sample",
                Sample = Normalize(sample),
            };
        }).ToArray();
        if (envelopes.Length == 0) return;

        byte[] payload = Utf8NoBom.GetBytes(string.Concat(
            envelopes.Select(x => JsonSerializer.Serialize(x, JsonOptions) + "\n")));
        AppendPayload(payload);
    }

    private void AppendPayload(byte[] payload)
    {
        lock (_gate)
        {
            bool ownsMutex = false;
            try
            {
                try { ownsMutex = FileMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) throw new IOException("另一個曦覽實例正在寫入證據時間軸，請稍後再試。");
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                RepairInterruptedTail();
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read,
                    bufferSize: 4096, options: FileOptions.WriteThrough);
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            finally { if (ownsMutex) FileMutex.ReleaseMutex(); }
        }
    }

    private void RepairInterruptedTail()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0) return;
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n') return;
        stream.Seek(0, SeekOrigin.End);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    /// <summary>查詢 UTC 閉區間；結果依時間與檔案順序排列。</summary>
    public IReadOnlyList<EvidenceSample> Query(EvidenceQuery? query = null)
    {
        query ??= new EvidenceQuery();
        if (query.FromUtc is { } from && query.ToUtc is { } to && to < from) return [];

        List<EvidenceSample> result;
        lock (_gate)
        {
            result = ReadValidSamples()
                .Where(s => Matches(s, query))
                .OrderBy(s => s.TimeUtc)
                .ToList();
        }

        return query.MaxPoints is { } max && max > 0 && result.Count > max
            ? SelectRepresentativeSamples(result, max)
            : result;
    }

    /// <summary>將指定序列分為至多 maxBuckets 個等數量桶；保留每桶包絡與首末值。</summary>
    public static IReadOnlyList<EvidenceBucket> Downsample(
        IReadOnlyList<EvidenceSample> samples, int maxBuckets)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (maxBuckets <= 0) throw new ArgumentOutOfRangeException(nameof(maxBuckets));
        if (samples.Count == 0) return [];

        var ordered = samples.OrderBy(s => s.TimeUtc).ToArray();
        int bucketCount = Math.Min(maxBuckets, ordered.Length);
        var result = new List<EvidenceBucket>(bucketCount);
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            int start = (int)((long)bucket * ordered.Length / bucketCount);
            int end = (int)((long)(bucket + 1) * ordered.Length / bucketCount);
            decimal min = decimal.MaxValue, max = decimal.MinValue, sum = 0;
            for (int i = start; i < end; i++)
            {
                decimal value = ordered[i].Value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
            }
            result.Add(new EvidenceBucket
            {
                FromUtc = ordered[start].TimeUtc,
                ToUtc = ordered[end - 1].TimeUtc,
                SampleCount = end - start,
                First = ordered[start].Value,
                Last = ordered[end - 1].Value,
                Min = min,
                Max = max,
                Average = sum / (end - start),
            });
        }
        return result;
    }

    /// <summary>比較首末並辨識 reset、wrap 與時間缺口；任何中斷都不回傳猜測的 delta。</summary>
    public static EvidenceDelta? AnalyzeDelta(
        IReadOnlyList<EvidenceSample> samples, TimeSpan? expectedInterval = null,
        decimal gapMultiplier = 3m)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (gapMultiplier <= 0) throw new ArgumentOutOfRangeException(nameof(gapMultiplier));
        if (expectedInterval is { } expected && expected <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expectedInterval));
        if (samples.Count == 0) return null;

        var ordered = samples.OrderBy(s => s.TimeUtc).ToArray();
        EnsureOneSeries(ordered);
        var discontinuities = new List<EvidenceDiscontinuity>();
        for (int i = 1; i < ordered.Length; i++)
        {
            EvidenceSample previous = ordered[i - 1], current = ordered[i];
            if (expectedInterval is { } cadence
                && (decimal)(current.TimeUtc - previous.TimeUtc).Ticks > cadence.Ticks * gapMultiplier)
            {
                discontinuities.Add(new EvidenceDiscontinuity
                {
                    Kind = EvidenceDiscontinuityKind.Gap,
                    FromUtc = previous.TimeUtc,
                    ToUtc = current.TimeUtc,
                    PreviousValue = previous.Value,
                    CurrentValue = current.Value,
                    ExpectedInterval = cadence,
                });
            }

            if (previous.Semantics == EvidenceMetricSemantics.MonotonicCounter
                && current.Value < previous.Value)
            {
                discontinuities.Add(new EvidenceDiscontinuity
                {
                    Kind = IsPlausibleWrap(previous, current)
                        ? EvidenceDiscontinuityKind.CounterWrap
                        : EvidenceDiscontinuityKind.CounterReset,
                    FromUtc = previous.TimeUtc,
                    ToUtc = current.TimeUtc,
                    PreviousValue = previous.Value,
                    CurrentValue = current.Value,
                });
            }
        }

        return new EvidenceDelta
        {
            First = ordered[0],
            Last = ordered[^1],
            Delta = discontinuities.Count == 0 ? ordered[^1].Value - ordered[0].Value : null,
            Discontinuities = discontinuities,
        };
    }

    /// <summary>
    /// 產生可由數字直接證明的事件。只看同一序列相鄰點，跨 gap/reset/wrap 不推算淨增量。
    /// </summary>
    public static IReadOnlyList<EvidenceEvent> GenerateEvents(
        IReadOnlyList<EvidenceSample> samples, TimeSpan? expectedInterval = null,
        decimal gapMultiplier = 3m)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var result = new List<EvidenceEvent>();
        foreach (var group in samples.GroupBy(SeriesKey))
        {
            var ordered = group.OrderBy(s => s.TimeUtc).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                EvidenceSample previous = ordered[i - 1], current = ordered[i];
                if (!CanCompare(previous, current, expectedInterval, gapMultiplier)) continue;
                if (TryClassifyEvent(previous, current, out EvidenceEventKind kind, out decimal? delta))
                    result.Add(NewEvent(kind, previous, current, delta));
            }
        }
        return result.OrderBy(e => e.TimeUtc).ToList();
    }

    /// <summary>APERF/MPERF 的區間比率；分母為零或任一計數器回退時回 null。</summary>
    public static decimal? ComputeEffectiveRatio(
        decimal aperfStart, decimal aperfEnd, decimal mperfStart, decimal mperfEnd)
    {
        decimal aperfDelta = aperfEnd - aperfStart;
        decimal mperfDelta = mperfEnd - mperfStart;
        return aperfDelta >= 0 && mperfDelta > 0 ? aperfDelta / mperfDelta : null;
    }

    private static bool TryClassifyEvent(
        EvidenceSample previous, EvidenceSample current,
        out EvidenceEventKind kind, out decimal? delta)
    {
        delta = current.Value - previous.Value;
        switch (current.Semantics)
        {
            case EvidenceMetricSemantics.LinkWidth:
            case EvidenceMetricSemantics.LinkSpeed:
                if (current.Role != EvidenceValueRole.Observed || delta >= 0) break;
                kind = EvidenceEventKind.LinkValueDecreased;
                return true;
            case EvidenceMetricSemantics.MonotonicCounter:
                if (delta <= 0) break;
                kind = EvidenceEventKind.CounterIncreased;
                return true;
            case EvidenceMetricSemantics.Ratio:
                if (current.Role != EvidenceValueRole.DerivedMeasurement) break;
                kind = EvidenceEventKind.EffectiveRatioObserved;
                return true;
            case EvidenceMetricSemantics.ResidencyPercent:
                if (current.Role != EvidenceValueRole.Observed) break;
                kind = EvidenceEventKind.ResidencyObserved;
                return true;
            case EvidenceMetricSemantics.Gauge:
                if (IsPercentageUsed(current) && delta > 0)
                {
                    kind = EvidenceEventKind.PercentageIncreased;
                    return true;
                }
                break;
        }
        kind = default;
        delta = null;
        return false;
    }

    private void ValidateExistingFile()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int first = stream.ReadByte();
            if (first == 0xEF)
            {
                int second = stream.ReadByte(), third = stream.ReadByte();
                if (second == 0xBB && third == 0xBF) first = stream.ReadByte();
            }
            while (first is ' ' or '\t' or '\r' or '\n') first = stream.ReadByte();
            if (first >= 0 && first != '{')
                throw new InvalidDataException("Evidence timeline is not a compatible JSONL file.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private List<EvidenceSample> ReadValidSamples()
    {
        if (!File.Exists(FilePath)) return [];
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var result = new List<EvidenceSample>();
        while (start < bytes.Length)
        {
            int newline = Array.IndexOf(bytes, (byte)'\n', start);
            if (newline < 0) break; // 未以 LF 提交的尾端資料可能是中斷寫入，絕不嘗試採信。
            int length = newline - start;
            if (length > 0 && bytes[newline - 1] == '\r') length--;
            if (length > 0)
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<EvidenceEnvelope>(
                        bytes.AsSpan(start, length), ReadOptions);
                    if (envelope is { RecordType: "sample", Sample: not null }
                        && envelope.SchemaVersion is > 0 and <= CurrentSchemaVersion)
                    {
                        Validate(envelope.Sample);
                        result.Add(Normalize(envelope.Sample));
                    }
                }
                catch (JsonException) { /* 一筆壞資料不能遮住後面的完整證據。 */ }
                catch (ArgumentException) { }
            }
            start = newline + 1;
        }
        return result;
    }

    private static bool Matches(EvidenceSample sample, EvidenceQuery query)
        => (query.FromUtc is null || sample.TimeUtc >= query.FromUtc)
        && (query.ToUtc is null || sample.TimeUtc <= query.ToUtc)
        && Match(sample.Category, query.Category)
        && Match(sample.DeviceKey, query.DeviceKey)
        && Match(sample.Metric, query.Metric);

    private static bool Match(string value, string? filter)
        => string.IsNullOrWhiteSpace(filter)
        || string.Equals(value, filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<EvidenceSample> SelectRepresentativeSamples(
        IReadOnlyList<EvidenceSample> samples, int maxPoints)
    {
        if (maxPoints == 1) return [samples[^1]];
        var selected = new List<EvidenceSample>(maxPoints);
        for (int i = 0; i < maxPoints; i++)
        {
            int index = (int)((long)i * (samples.Count - 1) / (maxPoints - 1));
            selected.Add(samples[index]);
        }
        return selected;
    }

    private static bool CanCompare(
        EvidenceSample previous, EvidenceSample current,
        TimeSpan? expectedInterval, decimal gapMultiplier)
    {
        if (previous.Semantics != current.Semantics || previous.Role != current.Role) return false;
        if (expectedInterval is { } cadence
            && (decimal)(current.TimeUtc - previous.TimeUtc).Ticks > cadence.Ticks * gapMultiplier)
            return false;
        if (current.Semantics == EvidenceMetricSemantics.MonotonicCounter
            && current.Value < previous.Value)
            return false;
        return true;
    }

    private static bool IsPlausibleWrap(EvidenceSample previous, EvidenceSample current)
    {
        int? bits = current.CounterBits ?? previous.CounterBits;
        if (bits is null or < 2 or > 96) return false;
        decimal maximum;
        try { maximum = Pow2(bits.Value) - 1; }
        catch (OverflowException) { return false; }
        // 沒有接近上限與低位區這兩項證據，就保守視為 reset。
        return previous.Value >= maximum * 0.90m && current.Value <= maximum * 0.10m;
    }

    private static decimal Pow2(int exponent)
    {
        decimal value = 1;
        for (int i = 0; i < exponent; i++) value *= 2;
        return value;
    }

    private static bool IsPercentageUsed(EvidenceSample sample)
        => sample.Metric.Equals("nvme.percentage_used", StringComparison.OrdinalIgnoreCase)
        || sample.Metric.Equals("percentage_used", StringComparison.OrdinalIgnoreCase);

    private static string SeriesKey(EvidenceSample sample)
        => string.Join('', sample.Category.ToUpperInvariant(),
            sample.DeviceKey.ToUpperInvariant(), sample.Metric.ToUpperInvariant(),
            sample.Unit.ToUpperInvariant());

    private static EvidenceEvent NewEvent(
        EvidenceEventKind kind, EvidenceSample previous, EvidenceSample current, decimal? delta)
        => new()
        {
            Kind = kind,
            Category = current.Category,
            DeviceKey = current.DeviceKey,
            Metric = current.Metric,
            TimeUtc = current.TimeUtc,
            PreviousValue = previous.Value,
            CurrentValue = current.Value,
            Delta = delta,
            Unit = current.Unit,
            Source = current.Source,
            Trust = current.Trust,
        };

    private static void EnsureOneSeries(IReadOnlyList<EvidenceSample> samples)
    {
        string key = SeriesKey(samples[0]);
        if (samples.Skip(1).Any(sample => SeriesKey(sample) != key))
            throw new ArgumentException("Delta analysis requires one category/device/metric/unit series.", nameof(samples));
    }

    private static EvidenceSample Normalize(EvidenceSample sample)
    {
        var context = sample.Context is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(sample.Context, StringComparer.Ordinal);
        return sample with
        {
            Category = sample.Category.Trim(),
            DeviceKey = sample.DeviceKey.Trim(),
            Metric = sample.Metric.Trim(),
            Unit = sample.Unit.Trim(),
            Source = sample.Source.Trim(),
            TimeUtc = sample.TimeUtc.ToUniversalTime(),
            Context = context,
        };
    }

    private static void Validate(EvidenceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Require(sample.Category, nameof(sample.Category));
        Require(sample.DeviceKey, nameof(sample.DeviceKey));
        Require(sample.Metric, nameof(sample.Metric));
        Require(sample.Unit, nameof(sample.Unit), allowEmpty: true);
        Require(sample.Source, nameof(sample.Source));
        if (sample.TimeUtc == default) throw new ArgumentException("TimeUtc is required.", nameof(sample));
        if (sample.CounterBits is < 2 or > 128)
            throw new ArgumentOutOfRangeException(nameof(sample), "CounterBits must be between 2 and 128.");
        if (sample.Semantics != EvidenceMetricSemantics.MonotonicCounter && sample.CounterBits is not null)
            throw new ArgumentException("CounterBits is valid only for monotonic counters.", nameof(sample));
        if (sample.Context is not null && sample.Context.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("Context keys must be non-empty and values non-null.", nameof(sample));
    }

    private static void Require(string? value, string name, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException($"{name} is required.", name);
    }

    private sealed record EvidenceEnvelope
    {
        public int SchemaVersion { get; init; }
        public string RecordType { get; init; } = "sample";
        public EvidenceSample? Sample { get; init; }
    }
}
