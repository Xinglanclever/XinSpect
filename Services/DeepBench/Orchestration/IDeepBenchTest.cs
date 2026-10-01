namespace XinSpect;

public sealed record DeepBenchRunContext(
    Guid SessionId,
    DeepBenchRunProfile Profile,
    IProgress<DeepBenchProgress> Progress);

public sealed record DeepBenchProgress(
    string CurrentTestId,
    int CompletedCount,
    int TotalCount,
    double Fraction,
    string Phase);

public interface IDeepBenchTest
{
    string Id { get; }
    Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken);
}

public enum DeepBenchRunState { Running, Completed, CompletedWithFailures, Cancelled }

public sealed record DeepBenchRunRecord(
    Guid SessionId,
    DeepBenchRunProfile Profile,
    DateTime StartedUtc,
    DateTime EndedUtc,
    DeepBenchRunState State,
    IReadOnlyList<DeepBenchTestResult> Results,
    IReadOnlyList<DeepBenchInsight> Insights);

public sealed record DeepBenchInsight(string Title, string Text, IReadOnlyList<string> EvidenceTestIds);
