using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class SlcTemporaryOwnershipTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PredispatchRejectionNeverTouchesExistingFixedPath(int cause)
    {
        var files = new SentinelFiles { Free = cause == 1 ? 9 * GiB : 24 * GiB };
        var engine = new ForbiddenEngine();
        var progress = new RecordingProgress(cause == 2);
        var profile = cause == 3 ? (DeepBenchRunProfile)999 : DeepBenchRunProfile.Quick;
        var service = new SlcSustainedWriteAdapter("C:\\injected-slc", cause == 0 ? 1 : 128 * 1024 * 1024,
            files, engine);
        var result = await service.RunAsync(new(Guid.NewGuid(), profile, progress), CancellationToken.None);
        Assert.Equal(cause < 2 ? DeepBenchFailureKind.NotRun : DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Equal(0, engine.Calls);
        Assert.Equal(0, files.PathOperations);
        Assert.True(files.SentinelExists);
        Assert.Contains(progress.Messages, message => message.Contains("未啟動量測引擎"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndFailureDoNotReleaseOwnedResourceBeforeActualWorkerCompletion(bool fail)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new FakeOwned();
        int created = 0;
        var failure = new IOException("injected flush failure");
        var writing = Task.Run(() => SlcCacheBenchService.RunOwned(() =>
        {
            Interlocked.Increment(ref created);
            return owned;
        }, handle =>
        {
            Assert.Same(owned, handle);
            entered.TrySetResult(true);
            if (!release.Wait(Budget)) throw new TimeoutException("Injected write was not released.");
            Assert.Equal(0, owned.DisposeCount);
            if (fail) throw failure;
            return new List<SlcSample> { new(1, 1) };
        }, cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            Assert.False(writing.IsCompleted);
            Assert.Equal(0, owned.DisposeCount);
            release.Set();
            if (fail)
            {
                var actual = await Assert.ThrowsAsync<IOException>(() => writing.WaitAsync(Budget));
                Assert.Same(failure, actual);
            }
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(Budget));
            Assert.Equal(1, created);
            Assert.Equal(1, owned.DisposeCount);
        }
        finally
        {
            release.Set();
            try { await writing.WaitAsync(Budget); } catch { }
        }
    }

    [Fact]
    public void CreateFailureDoesNotDispatchWriteOrAttemptCleanupOrRetry()
    {
        int createCalls = 0, writes = 0;
        var collision = new IOException("injected CreateNew collision");
        var actual = Assert.Throws<IOException>(() => SlcCacheBenchService.RunOwned<FakeOwned>(() =>
        {
            createCalls++;
            throw collision;
        }, _ => { writes++; return []; }, CancellationToken.None));
        Assert.Same(collision, actual);
        Assert.Equal(1, createCalls);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void CancellationBeforeCreateDoesNotAcquireAnyResource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int creates = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => SlcCacheBenchService.RunOwned(() =>
        {
            creates++;
            return new FakeOwned();
        }, _ => [], cancellation.Token));
        Assert.Equal(0, creates);
    }

    private sealed class FakeOwned : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
    private sealed class SentinelFiles : IDiskIoFileSystem
    {
        public long Free;
        public int PathOperations;
        public bool SentinelExists = true;
        public long GetAvailableFreeSpace(string root) => Free;
        public bool Exists(string path) { PathOperations++; return SentinelExists; }
        public void Create(string path) { PathOperations++; throw new InvalidOperationException("Forbidden path create"); }
        public void Delete(string path) { PathOperations++; SentinelExists = false; }
    }
    private sealed class ForbiddenEngine : ISlcSustainedWriteEngine
    {
        public int Calls;
        public Task<SlcSustainedWriteMeasurement> MeasureAsync(SlcSustainedWriteContext context, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Forbidden engine dispatch");
        }
    }
    private sealed class RecordingProgress(bool throwFirst) : IProgress<DeepBenchProgress>
    {
        public List<string> Messages { get; } = [];
        private bool _thrown;
        public void Report(DeepBenchProgress progress)
        {
            Messages.Add(progress.Phase);
            if (throwFirst && !_thrown) { _thrown = true; throw new InvalidOperationException("injected pre-dispatch progress failure"); }
        }
    }
}
