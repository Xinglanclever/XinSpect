using System.IO;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace XinSpect.Tests;

public class DiskBenchOwnershipTests
{
    [Fact]
    public void PathsUseIndependentNonfixedIdentityWithinSelectedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "fake-root");
        string first = DiskBenchService.CreateTempPath(root);
        string second = DiskBenchService.CreateTempPath(root);
        Assert.Equal(root, Path.GetDirectoryName(first));
        Assert.NotEqual(first, second);
        string name = Path.GetFileName(first);
        Assert.StartsWith("XinSpectDiskBench.", name);
        Assert.EndsWith(".tmp", name);
        Assert.True(Guid.TryParseExact(name["XinSpectDiskBench.".Length..^4], "N", out _));
    }

    [Fact]
    public void CreationCollisionNeverRunsWorkloadOrClosesUnownedHandle()
    {
        using var existing = FakeHandle();
        var collision = new IOException("CreateNew collision");
        bool ran = false;
        var actual = Assert.Throws<IOException>(() => DiskBenchService.WithOwnedFile<int>("fake",
            _ => throw collision, _ => { ran = true; return 1; }));
        Assert.Same(collision, actual);
        Assert.False(ran);
        Assert.False(existing.IsClosed);
    }

    [Fact]
    public void SuccessUsesSameOwnedHandleAndClosesOnlyAfterWorkload()
    {
        var owned = FakeHandle();
        int creates = 0;
        int result = DiskBenchService.WithOwnedFile("fake", path =>
        {
            Assert.Equal("fake", path);
            creates++;
            return owned;
        }, handle =>
        {
            Assert.Same(owned, handle);
            Assert.False(handle.IsClosed);
            return 42;
        });
        Assert.Equal(42, result);
        Assert.Equal(1, creates);
        Assert.True(owned.IsClosed);
    }

    [Fact]
    public void WorkloadFailurePreservesExceptionAndClosesOwnedHandle()
    {
        var owned = FakeHandle();
        var failure = new IOException("injected workload failure");
        Assert.Same(failure, Assert.Throws<IOException>(() => DiskBenchService.WithOwnedFile<int>("fake",
            _ => owned, _ => throw failure)));
        Assert.True(owned.IsClosed);
    }

    [Fact]
    public void PrecancelledAttemptDoesNotCreateFile()
    {
        bool created = false;
        Assert.ThrowsAny<OperationCanceledException>(() => DiskBenchService.WithOwnedFile("fake",
            _ => { created = true; return FakeHandle(); }, _ => 1, new CancellationToken(true)));
        Assert.False(created);
    }

    [Fact]
    public void CancellationDuringCreateClosesOwnedHandleBeforeWorkload()
    {
        using var cancel = new CancellationTokenSource();
        var owned = FakeHandle();
        bool ran = false;
        Assert.ThrowsAny<OperationCanceledException>(() => DiskBenchService.WithOwnedFile("fake",
            _ => { cancel.Cancel(); return owned; }, _ => { ran = true; return 1; }, cancel.Token));
        Assert.False(ran);
        Assert.True(owned.IsClosed);
    }

    [Fact]
    public void WorkloadCancellationClosesOwnedHandle()
    {
        using var cancel = new CancellationTokenSource();
        var owned = FakeHandle();
        Assert.ThrowsAny<OperationCanceledException>(() => DiskBenchService.WithOwnedFile<int>("fake",
            _ => owned, _ => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); return 1; }, cancel.Token));
        Assert.True(owned.IsClosed);
    }

    // No native ownership/release or I/O occurs: ownsHandle=false makes Dispose managed-only.
    private static SafeFileHandle FakeHandle() => new(new IntPtr(12345), ownsHandle: false);
}
