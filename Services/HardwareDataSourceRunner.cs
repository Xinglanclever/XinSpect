using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>Runs one declared source without merging it into results from other sources.</summary>
public static class HardwareDataSourceRunner
{
    public const int MaxFactsPerSource = 500;
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

    private static readonly Regex StableId = new(
        @"\A[a-z][a-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex StableVersion = new(
        @"\A[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex StableFactKey = new(
        @"\A[a-z0-9][a-z0-9._-]{0,159}\z", RegexOptions.CultureInvariant);

    public static async Task<HardwareDataSourceRunResult> RunAsync(
        IHardwareDataSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        HardwareDataSourceDescriptor descriptor = source.Descriptor
            ?? throw new ArgumentException("資料來源未提供描述。", nameof(source));
        ValidateDescriptor(descriptor);
        descriptor = descriptor with
        {
            SupportedFacts = Array.AsReadOnly(descriptor.SupportedFacts.ToArray()),
        };

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        using var timeoutCancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCancellation.Token);
        timeoutCancellation.CancelAfter(descriptor.Timeout);

        Task<IReadOnlyList<HardwareFact>>? collection;
        try
        {
            CancellationToken sourceToken = descriptor.SupportsCancellation
                ? linkedCancellation.Token : CancellationToken.None;
            var invocation = Task.Factory.StartNew(
                () => source.CollectAsync(startedAt, sourceToken), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            collection = invocation.Unwrap();
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            return ReadError(descriptor, startedAt, DateTimeOffset.UtcNow, ex.GetType().Name);
        }

        if (collection is null)
            return ReadError(descriptor, startedAt, DateTimeOffset.UtcNow, "NullTask");

        try
        {
            IReadOnlyList<HardwareFact>? reported = await collection
                .WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            if (reported is null)
                return ReadError(descriptor, startedAt, completedAt, "NullResult");
            if (reported.Count > MaxFactsPerSource)
                return ReadError(descriptor, startedAt, completedAt, "TooManyFacts");

            return Normalize(descriptor, startedAt, completedAt, reported);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateFault(collection);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            ObserveLateFault(collection);
            return TimedOut(descriptor, startedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return ReadError(descriptor, startedAt, DateTimeOffset.UtcNow, ex.GetType().Name);
        }
    }

    private static HardwareDataSourceRunResult Normalize(
        HardwareDataSourceDescriptor descriptor, DateTimeOffset startedAt, DateTimeOffset completedAt,
        IReadOnlyList<HardwareFact> reported)
    {
        var declared = descriptor.SupportedFacts.ToDictionary(static f => f.Key, StringComparer.Ordinal);
        var byKey = new Dictionary<string, HardwareFact>(StringComparer.Ordinal);
        foreach (HardwareFact? fact in reported)
        {
            if (fact is null || fact.Key is null || !declared.ContainsKey(fact.Key)
                || !IsValidReportedFact(fact) || !byKey.TryAdd(fact.Key, fact))
                return ReadError(descriptor, startedAt, completedAt, "SourceContractViolation");
        }

        var facts = descriptor.SupportedFacts.Select(item => byKey.TryGetValue(item.Key, out var fact)
            ? fact with
            {
                Category = item.Category,
                Name = item.Name,
                Unit = item.Unit,
                Source = SourceName(descriptor),
                Sensitive = item.Sensitive || fact.Sensitive,
            }
            : new HardwareFact(item.Key, item.Category, item.Name, "未回報", item.Unit,
                SourceName(descriptor), FactTrustLevel.Unknown, item.Sensitive, completedAt, null,
                FactAvailability.Unknown, "來源未回報宣告支援的事實鍵；不推導為零或不支援。")).ToArray();

        return new(descriptor, startedAt, completedAt, HardwareDataSourceRunStatus.Completed,
            Array.AsReadOnly(facts), null);
    }

    private static bool IsValidReportedFact(HardwareFact fact)
    {
        if (fact.Value is null || fact.Value.Length > 16_384
            || fact.MeasuredAtUtc == default
            || !Enum.IsDefined(fact.Availability) || !Enum.IsDefined(fact.Trust)
            || fact.NumericValue is { } numeric
                && (!double.IsFinite(numeric) || fact.Availability != FactAvailability.Present))
            return false;

        return fact.Availability switch
        {
            FactAvailability.Present => !string.IsNullOrWhiteSpace(fact.Value)
                && string.IsNullOrWhiteSpace(fact.UnavailableReason),
            FactAvailability.Unknown => !string.IsNullOrWhiteSpace(fact.Value)
                && !string.IsNullOrWhiteSpace(fact.UnavailableReason)
                && fact.UnavailableReason.Length <= 512,
            _ => (fact.UnavailableReason?.Length ?? 0) is > 0 and <= 512
                && !string.IsNullOrWhiteSpace(fact.UnavailableReason),
        };
    }

    private static HardwareDataSourceRunResult TimedOut(
        HardwareDataSourceDescriptor descriptor, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        var facts = descriptor.SupportedFacts.Select(item => new HardwareFact(
            item.Key, item.Category, item.Name, "", item.Unit, SourceName(descriptor),
            FactTrustLevel.Unknown, item.Sensitive, completedAt, null, FactAvailability.ReadError,
            "來源逾時；本次未取得資料。")).ToArray();
        return new(descriptor, startedAt, completedAt, HardwareDataSourceRunStatus.TimedOut,
            Array.AsReadOnly(facts), "Timeout");
    }

    private static HardwareDataSourceRunResult ReadError(
        HardwareDataSourceDescriptor descriptor, DateTimeOffset startedAt, DateTimeOffset completedAt,
        string failureCode)
    {
        var facts = descriptor.SupportedFacts.Select(item => new HardwareFact(
            item.Key, item.Category, item.Name, "", item.Unit, SourceName(descriptor),
            FactTrustLevel.Unknown, item.Sensitive, completedAt, null, FactAvailability.ReadError,
            $"來源讀取失敗（{failureCode}）；未複製原始錯誤內容。")).ToArray();
        return new(descriptor, startedAt, completedAt, HardwareDataSourceRunStatus.ReadError,
            Array.AsReadOnly(facts), failureCode);
    }

    private static string SourceName(HardwareDataSourceDescriptor descriptor)
        => $"{descriptor.SourceId}@{descriptor.Version}";

    private static void ValidateDescriptor(HardwareDataSourceDescriptor descriptor)
    {
        if (!StableId.IsMatch(descriptor.SourceId ?? ""))
            throw new ArgumentException("資料來源 ID 必須是穩定的小寫識別碼。", nameof(descriptor));
        if (!StableVersion.IsMatch(descriptor.Version ?? ""))
            throw new ArgumentException("資料來源版本格式無效。", nameof(descriptor));
        if (!StableId.IsMatch(descriptor.DataDomain ?? ""))
            throw new ArgumentException("資料域必須是穩定的小寫識別碼。", nameof(descriptor));
        if (descriptor.Timeout <= TimeSpan.Zero || descriptor.Timeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(descriptor),
                $"來源逾時必須大於零且不超過 {MaxTimeout.TotalMinutes} 分鐘。");
        if (!Enum.IsDefined(descriptor.RequiredPermission))
            throw new ArgumentException("資料來源權限需求無效。", nameof(descriptor));
        if (descriptor.SupportedFacts is null
            || descriptor.SupportedFacts.Count is < 1 or > MaxFactsPerSource)
            throw new ArgumentException($"來源必須宣告 1 至 {MaxFactsPerSource} 個事實鍵。", nameof(descriptor));

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in descriptor.SupportedFacts)
        {
            if (fact is null)
                throw new ArgumentException("資料來源描述含無效或重複的事實鍵。", nameof(descriptor));
            string key = fact.Key;
            if (!StableFactKey.IsMatch(key)
                || string.IsNullOrWhiteSpace(fact.Category) || fact.Category.Length > 80
                || string.IsNullOrWhiteSpace(fact.Name) || fact.Name.Length > 256
                || fact.Unit is null || fact.Unit.Length > 32 || !keys.Add(key))
                throw new ArgumentException("資料來源描述含無效或重複的事實鍵。", nameof(descriptor));
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
