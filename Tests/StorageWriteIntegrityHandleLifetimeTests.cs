using System.IO;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace XinSpect.Tests;

public class StorageWriteIntegrityHandleLifetimeTests
{
    [Fact]
    public async Task SuccessKeepsSameHandleUntilAwaitedWorkloadEnds()
    {
        var owner = new FakeOwner();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StorageWriteIntegrityService.WithOwnedHandleAsync(owner, owner.Handle, handle =>
        {
            Assert.Same(owner.Handle, handle);
            Assert.False(handle.IsClosed);
            return completion.Task;
        }, CancellationToken.None);
        Assert.False(running.IsCompleted);
        Assert.False(owner.Disposed);
        completion.SetResult(42);
        Assert.Equal(42, await running);
        Assert.True(owner.Disposed);
        Assert.True(owner.Handle.IsClosed);
    }

    [Fact]
    public async Task CancellationRequestDoesNotDisposeUntilPendingIoActuallyCompletes()
    {
        var owner = new FakeOwner();
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StorageWriteIntegrityService.WithOwnedHandleAsync(owner, owner.Handle,
            _ => completion.Task, cancel.Token);
        cancel.Cancel();
        Assert.False(running.IsCompleted);
        Assert.False(owner.Disposed);
        Assert.False(owner.Handle.IsClosed);
        completion.SetCanceled(cancel.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task LateSuccessAfterCancellationIsDrainedThenRejected()
    {
        var owner = new FakeOwner();
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StorageWriteIntegrityService.WithOwnedHandleAsync(owner, owner.Handle,
            _ => completion.Task, cancel.Token);
        cancel.Cancel();
        Assert.False(owner.Disposed);
        Assert.False(running.IsCompleted);
        completion.SetResult(42);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task LateIoFailureIsPreservedBeforeOwnerCloses()
    {
        var owner = new FakeOwner();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StorageWriteIntegrityService.WithOwnedHandleAsync(owner, owner.Handle,
            _ => completion.Task, CancellationToken.None);
        Assert.False(owner.Disposed);
        var failure = new IOException("injected late I/O failure");
        completion.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => running));
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task CancellationThenLateIoFailurePreservesOriginalExceptionUntilCompletion()
    {
        var owner = new FakeOwner();
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = StorageWriteIntegrityService.WithOwnedHandleAsync(owner, owner.Handle,
            _ => completion.Task, cancel.Token);
        cancel.Cancel();
        Assert.False(running.IsCompleted);
        Assert.False(owner.Disposed);
        Assert.False(owner.Handle.IsClosed);
        var failure = new IOException("injected I/O failure after cancellation request");
        completion.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => running));
        Assert.True(owner.Disposed);
        Assert.True(owner.Handle.IsClosed);
    }

    [Fact]
    public async Task PrecancelledOwnedScopeClosesLeaseWithoutIssuingWorkload()
    {
        var owner = new FakeOwner();
        bool issued = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StorageWriteIntegrityService.WithOwnedHandleAsync(
            owner, owner.Handle, _ => { issued = true; return Task.FromResult(1); }, new CancellationToken(true)));
        Assert.False(issued);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task SynchronousWorkloadFailureAlsoDisposesLease()
    {
        var owner = new FakeOwner();
        var failure = new IOException("injected synchronous failure");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => StorageWriteIntegrityService.WithOwnedHandleAsync<int>(
            owner, owner.Handle, _ => throw failure, CancellationToken.None)));
        Assert.True(owner.Disposed);
    }

    private sealed class FakeOwner : IDisposable
    {
        internal SafeFileHandle Handle { get; } = new(new IntPtr(12345), ownsHandle: false);
        internal bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; Handle.Dispose(); }
    }
}
