using System.IO;
using Xunit;

namespace XinSpect.Tests;

// Real service with4KiB fixture files and fake engines only; no GPU/native workload.
public sealed class StorageGpuPipelineCancellationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("factory")]
    [InlineData("free")]
    [InlineData("write-progress")]
    [InlineData("read-progress")]
    [InlineData("gpu-progress")]
    public async Task CancellationAtDispatchBoundariesNeverStartsLaterGpuWork(string boundary)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        int freeCalls = 0, gpuCalls = 0;
        var fileSystem = new ProbeFileSystem(() =>
        {
            freeCalls++;
            if (boundary == "free") cancellation.Cancel();
            return long.MaxValue;
        });
        var engine = new FakeEngine(context => { gpuCalls++; return Task.FromResult(Honest(context)); });
        var service = new StorageGpuPipelineService(fixture.Root, 8192, fileSystem, engine)
        {
            WorkloadFactory = _ =>
            {
                if (boundary == "factory") cancellation.Cancel();
                return new GpuPipelineWorkload(4096);
            },
        };
        var progress = new InlineProgress(value =>
        {
            if ((boundary == "write-progress" && value.Phase == "寫入暫存檔並 FlushToDisk")
                || (boundary == "read-progress" && value.Phase == "磁碟讀取")
                || (boundary == "gpu-progress" && value.Phase == "上傳到 GPU 並執行")) cancellation.Cancel();
        });
        var result = await service.RunAsync(Context(progress), cancellation.Token);
        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Equal(0, gpuCalls);
        Assert.Equal(boundary == "factory" ? 0 : 1, freeCalls);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task LateEngineSuccessIsRejectedOnlyAfterActualCompletionAndOwnedFileRemainsUntilThen()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<GpuPipelineContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<GpuPipelineGpuResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(fixture, new FakeEngine(context => { entered.TrySetResult(context); return finish.Task; }));
        Task<DeepBenchTestResult> running = service.RunAsync(Context(), cancellation.Token);
        GpuPipelineContext? dispatched = null;
        try
        {
            dispatched = await entered.Task.WaitAsync(Budget);
            Assert.Equal(4096, dispatched.Data.Length);
            Assert.Equal(cancellation.Token, dispatched.CancellationToken);
            string path = Assert.Single(Directory.GetFiles(fixture.Root));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(File.Exists(path));
            // FileShare.None proves the owned lease is still open while the engine is pending.
            Assert.Throws<IOException>(() => { using var ignored = File.OpenRead(path); });
            finish.TrySetResult(Honest(dispatched));
            DeepBenchTestResult result = await running.WaitAsync(Budget);
            Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
            Assert.Empty(result.Metrics);
            Assert.Empty(Directory.GetFiles(fixture.Root));
        }
        finally
        {
            if (dispatched is not null) finish.TrySetResult(Honest(dispatched));
            else finish.TrySetException(new IOException("fixture release"));
            // Drain the actual task before Fixture.Dispose removes its directory.
            await running;
        }
    }

    [Theory]
    [InlineData("io", DeepBenchFailureKind.PlatformError)]
    [InlineData("foreign-oce", DeepBenchFailureKind.PlatformError)]
    [InlineData("validation", DeepBenchFailureKind.Unstable)]
    [InlineData("unsupported", DeepBenchFailureKind.Unsupported)]
    [InlineData("matching-oce", DeepBenchFailureKind.Cancelled)]
    public async Task CallerCancellationDoesNotMaskGenuineEngineFault(string failure, DeepBenchFailureKind expected)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var engine = new FakeEngine(context =>
        {
            cancellation.Cancel();
            Exception exception = failure switch
            {
                "io" => new IOException("injected real I/O failure"),
                "foreign-oce" => new OperationCanceledException("foreign operation canceled", CancellationToken.None),
                "validation" => new GpuPipelineValidationException("injected integrity failure"),
                "unsupported" => new GpuUnsupportedException("injected unsupported device"),
                _ => new OperationCanceledException(context.CancellationToken),
            };
            return Task.FromException<GpuPipelineGpuResult>(exception);
        });
        var result = await Service(fixture, engine).RunAsync(Context(), cancellation.Token);
        Assert.Equal(expected, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Empty(Directory.GetFiles(fixture.Root));
        if (failure != "matching-oce") Assert.Contains(failure == "foreign-oce" ? "foreign" : "injected", result.Error!);
    }

    [Fact]
    public async Task UnrequestedOperationCancellationIsAPlatformFault()
    {
        using var fixture = new Fixture();
        var service = Service(fixture, new FakeEngine(_ => Task.FromException<GpuPipelineGpuResult>(
            new OperationCanceledException("unrequested engine cancellation"))));
        var result = await service.RunAsync(Context(), CancellationToken.None);
        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("unrequested", result.Error!);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task InvalidLatePayloadRemainsValidationFailureWhenCallerAlsoCancelled()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var service = Service(fixture, new FakeEngine(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(new GpuPipelineGpuResult(1, 1, 1, [1]));
        }));
        var result = await service.RunAsync(Context(), cancellation.Token);
        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    private static StorageGpuPipelineService Service(Fixture fixture, IGpuPipelineEngine engine)
        => new(fixture.Root, 8192, new ProbeFileSystem(() => long.MaxValue), engine)
        { WorkloadFactory = _ => new GpuPipelineWorkload(4096) };
    private static DeepBenchRunContext Context(IProgress<DeepBenchProgress>? progress = null)
        => new(Guid.NewGuid(), DeepBenchRunProfile.Quick, progress ?? new InlineProgress(_ => { }));
    private static GpuPipelineGpuResult Honest(GpuPipelineContext context)
        => new(1, 1, 1, Enumerable.Range(0, context.Data.Length / 4)
            .Select(index => StorageGpuPipelineService.HashElement(context.Data, index)).ToArray());
    private sealed class FakeEngine(Func<GpuPipelineContext, Task<GpuPipelineGpuResult>> process) : IGpuPipelineEngine
    { public Task<GpuPipelineGpuResult> ProcessAsync(GpuPipelineContext context) => process(context); }
    private sealed class InlineProgress(Action<DeepBenchProgress> report) : IProgress<DeepBenchProgress>
    { public void Report(DeepBenchProgress value) => report(value); }
    private sealed class ProbeFileSystem(Func<long> free) : IDiskIoFileSystem
    {
        public long GetAvailableFreeSpace(string root) => free();
        public bool Exists(string path) => throw new InvalidOperationException("Legacy path API must not be used.");
        public void Create(string path) => throw new InvalidOperationException("Legacy path API must not be used.");
        public void Delete(string path) => throw new InvalidOperationException("Legacy path API must not be used.");
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "XinSpect.pipeline-cancellation." + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
