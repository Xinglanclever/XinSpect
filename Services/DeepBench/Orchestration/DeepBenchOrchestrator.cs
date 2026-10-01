namespace XinSpect;

/// <summary>單一 run session 的序列化執行器。Phase 1 保守序列化，不高負載並行。</summary>
public sealed class DeepBenchOrchestrator
{
    private readonly IReadOnlyDictionary<string, IDeepBenchTest> _tests;
    private readonly DeepBenchRunStore? _store;
    private readonly Func<DateTime> _utcNow;

    public DeepBenchOrchestrator(IEnumerable<IDeepBenchTest> tests, DeepBenchRunStore? store = null, Func<DateTime>? utcNow = null)
    {
        _tests = tests.GroupBy(test => test.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<DeepBenchRunRecord> RunAsync(
        DeepBenchRunProfile profile,
        IReadOnlyList<string> requestedIds,
        IProgress<DeepBenchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedIds);
        ValidateRequestedIds(requestedIds);

        Guid session = Guid.NewGuid();
        DateTime started = _utcNow();
        var selected = requestedIds.Select(id => _tests[id]).ToArray();
        IProgress<DeepBenchProgress> reporter = progress ?? new Progress<DeepBenchProgress>();
        var results = new List<DeepBenchTestResult>();
        bool cancelled = false;
        bool failed = false;

        reporter.Report(new DeepBenchProgress(selected.Length == 0 ? "none" : selected[0].Id, 0, selected.Length, 0, "準備"));
        try
        {
            foreach (IDeepBenchTest test in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = new DeepBenchRunContext(session, profile, reporter);
                DateTime testStarted = _utcNow();
                try
                {
                    DeepBenchTestResult result = await test.RunAsync(context, cancellationToken).ConfigureAwait(false);
                    results.Add(result.SessionId == session ? result : result with { SessionId = session });
                    if (result.FailureKind == DeepBenchFailureKind.Cancelled)
                    {
                        // 真實測項取消是「回傳 Cancelled 結果」而非丟例外（VM 的 cts 觸發服務內部攔截）；
                        // 這裡必須停止後續測項並把整場記為 Cancelled，已完成結果照常保留。
                        cancelled = true;
                        break;
                    }
                    if (result.FailureKind != DeepBenchFailureKind.None) failed = true;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    results.Add(CreateFailure(test.Id, session, profile, testStarted, DeepBenchFailureKind.Cancelled, "使用者取消；已完成結果已保留。"));
                    break;
                }
                catch (Exception exception)
                {
                    failed = true;
                    results.Add(CreateFailure(test.Id, session, profile, testStarted, DeepBenchFailureKind.PlatformError, exception.Message));
                }

                reporter.Report(new DeepBenchProgress(test.Id, results.Count, selected.Length, (double)results.Count / selected.Length, "完成一項"));
            }
        }
        finally
        {
            DateTime ended = _utcNow();
            var state = cancelled ? DeepBenchRunState.Cancelled
                : failed ? DeepBenchRunState.CompletedWithFailures
                : DeepBenchRunState.Completed;
            IReadOnlyList<DeepBenchInsight> insights = DeepBenchCrossDomainSynthesis.Summarize(session, results);
            var record = new DeepBenchRunRecord(session, profile, started, ended, state, results.ToArray(), insights);
            _store?.Save(record);
        }

        var finalRecord = BuildRecord(session, profile, started, results, cancelled, failed);
        return finalRecord;
    }

    private DeepBenchRunRecord BuildRecord(Guid session, DeepBenchRunProfile profile, DateTime started, List<DeepBenchTestResult> results, bool cancelled, bool failed)
    {
        var state = cancelled ? DeepBenchRunState.Cancelled
            : failed ? DeepBenchRunState.CompletedWithFailures
            : DeepBenchRunState.Completed;
        return new DeepBenchRunRecord(session, profile, started, _utcNow(), state, results.ToArray(), DeepBenchCrossDomainSynthesis.Summarize(session, results));
    }

    private void ValidateRequestedIds(IReadOnlyList<string> requestedIds)
    {
        if (requestedIds.Count == 0) throw new ArgumentException("未選擇任何測項。", nameof(requestedIds));
        var duplicates = requestedIds.GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null) throw new ArgumentException($"測項重複：{string.Join(", ", duplicates)}", nameof(requestedIds));

        string[] unknown = requestedIds.Where(id => !_tests.ContainsKey(id)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"未知測項：{string.Join(", ", unknown)}", nameof(requestedIds));
    }

    private DeepBenchTestResult CreateFailure(
        string testId, Guid session, DeepBenchRunProfile profile, DateTime started,
        DeepBenchFailureKind kind, string error) =>
        new(testId, session, profile, started, _utcNow(), "尚未完成", [], [], ["此項未產生可信量測。"], kind, error);
}


