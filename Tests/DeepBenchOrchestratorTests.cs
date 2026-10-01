using System.Text.Json;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class DeepBenchOrchestratorTests
{
    private sealed class FakeTest(string id, Func<DeepBenchRunContext, CancellationToken, DeepBenchTestResult> body) : IDeepBenchTest
    {
        public string Id { get; } = id;
        public int Calls { get; private set; }
        public Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(body(context, cancellationToken));
        }
    }

    private static DeepBenchTestResult Result(string id, Guid session, double value = 1) => new(
        id, session, DeepBenchRunProfile.Quick, DateTime.UtcNow, DateTime.UtcNow, "test",
        [new DeepBenchMetric(id, id, "MiB/s", true, "fake", [value], [])],
        [], [], DeepBenchFailureKind.None, null);

    [Fact]
    public async Task 重複或未知ID在副作用前拒絕()
    {
        var good = new FakeTest("good", (context, _) => Result("good", context.SessionId));
        var dup1 = new FakeTest("same", (context, _) => Result("same", context.SessionId));
        var dup2 = new FakeTest("same", (context, _) => Result("same", context.SessionId));
        var orchestrator = new DeepBenchOrchestrator([good, dup1, dup2]);

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RunAsync(DeepBenchRunProfile.Quick, ["good", "missing"]));
        Assert.Equal(0, good.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RunAsync(DeepBenchRunProfile.Quick, ["same", "same"]));
        Assert.Equal(0, dup1.Calls);
    }

    [Fact]
    public async Task 單項例外轉結果並繼續下一項()
    {
        var failing = new FakeTest("fail", (_, _) => throw new InvalidOperationException("boom"));
        var next = new FakeTest("next", (context, _) => Result("next", context.SessionId));
        var record = await new DeepBenchOrchestrator([failing, next]).RunAsync(
            DeepBenchRunProfile.Quick, ["fail", "next"]);

        Assert.Equal(DeepBenchRunState.CompletedWithFailures, record.State);
        Assert.Equal(2, record.Results.Count);
        Assert.Equal(DeepBenchFailureKind.PlatformError, record.Results[0].FailureKind);
        Assert.Contains("boom", record.Results[0].Error, StringComparison.Ordinal);
        Assert.Equal(1, next.Calls);
        Assert.All(record.Results, result => Assert.Equal(record.SessionId, result.SessionId));
    }

    [Fact]
    public async Task 取消保存已完成結果()
    {
        var first = new FakeTest("first", (context, _) => Result("first", context.SessionId));
        var cancelled = new FakeTest("cancel", (_, token) => throw new OperationCanceledException(token));
        var never = new FakeTest("never", (context, _) => Result("never", context.SessionId));
        using var cts = new CancellationTokenSource();
        var record = await new DeepBenchOrchestrator([first, cancelled, never]).RunAsync(
            DeepBenchRunProfile.Quick, ["first", "cancel", "never"], null, cts.Token);

        Assert.Equal(DeepBenchRunState.Cancelled, record.State);
        Assert.Equal(2, record.Results.Count);
        Assert.Equal("first", record.Results[0].TestId);
        Assert.Equal(DeepBenchFailureKind.Cancelled, record.Results[1].FailureKind);
        Assert.Equal(0, never.Calls);
    }

    [Fact]
    public async Task 進度單調且最後到一()
    {
        var progress = new ProgressCollector();
        var test = new FakeTest("test", (context, _) => Result("test", context.SessionId));
        _ = await new DeepBenchOrchestrator([test]).RunAsync(
            DeepBenchRunProfile.Full, ["test"], progress, CancellationToken.None);

        Assert.Equal([0, 1], progress.Fractions);
    }

    [Fact]
    public async Task 歷史Json往返且損毀回空並明示()
    {
        string path = Path.Combine(Path.GetTempPath(), "xinspect-deepbench-test", $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new DeepBenchRunStore(path);
        var record = await new DeepBenchOrchestrator([new FakeTest("test", (context, _) => Result("test", context.SessionId))], store)
            .RunAsync(DeepBenchRunProfile.Quick, ["test"]);
        store.Save(record);
        Assert.Single(store.LoadRecent());
        Assert.Equal(record.SessionId, store.LoadRecent()[0].SessionId);

        await File.WriteAllTextAsync(path, "{corrupt");
        Assert.Empty(store.LoadRecent());
        Assert.Contains("JSON", store.LastLoadError, StringComparison.Ordinal);
        File.Delete(path);
    }

    [Fact]
    public void 聚合只引用同場證據且缺域明示不給總分()
    {
        Guid session = Guid.NewGuid();
        var record = new DeepBenchRunRecord(
            session, DeepBenchRunProfile.Quick, DateTime.UtcNow, DateTime.UtcNow,
            DeepBenchRunState.Completed,
            [
                Result("cpu.aes-sha", session),
                Result("memory.stream-bandwidth", session),
                Result("gpu.fp32-fp64-integer", Guid.NewGuid())
            ], []);
        var insights = DeepBenchCrossDomainSynthesis.Summarize(record);

        Assert.DoesNotContain(insights, insight => insight.Title.Contains("總分", StringComparison.Ordinal));
        Assert.Contains(insights, insight => insight.EvidenceTestIds.All(id => id != "gpu.fp32-fp64-integer"));
        Assert.Contains(insights, insight => insight.Title.Contains("缺", StringComparison.Ordinal) && insight.Text.Contains("GPU", StringComparison.Ordinal));
    }

    private sealed class ProgressCollector : IProgress<DeepBenchProgress>
    {
        public List<double> Fractions { get; } = [];
        public void Report(DeepBenchProgress value) => Fractions.Add(value.Fraction);
    }
}
