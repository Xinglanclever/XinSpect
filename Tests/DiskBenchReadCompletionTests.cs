using System.IO;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace XinSpect.Tests;

// Managed injected reads only; fake handles do not own or release a native resource.
public sealed class DiskBenchReadCompletionTests
{
    [Theory]
    [InlineData(4096, 0)]
    [InlineData(4096, 512)]
    [InlineData(4096, 4095)]
    [InlineData(1048576, 0)]
    [InlineData(1048576, 4096)]
    [InlineData(1048576, 1048575)]
    public void ShortOrEmptyBlockRejectsResultBeforeCompletedOperationAndClosesOwnedHandle(int requested, int returned)
    {
        var handle = FakeHandle();
        long completedBytes = 0;
        int completedOperations = 0, samples = 0, readCalls = 0;
        var exception = Assert.Throws<IOException>(() => DiskBenchService.WithOwnedFile("injected", _ => handle, owned =>
        {
            byte[] block = new byte[requested];
            DiskBenchService.ReadFullBlock(owned, block, 8192, (actualHandle, buffer, offset) =>
            {
                Assert.Same(handle, actualHandle);
                Assert.False(actualHandle.IsClosed);
                Assert.Equal(requested, buffer.Length);
                Assert.Equal(8192L, offset);
                readCalls++;
                return returned;
            });
            // Same ordering as RunAll: only a full block may advance bytes/ops/latency.
            completedBytes += requested;
            completedOperations++;
            samples++;
            return completedBytes;
        }));
        Assert.Contains($"預期 {requested} bytes，實際 {returned} bytes", exception.Message);
        Assert.Equal(1, readCalls); // No unsafe unaligned partial-read retry.
        Assert.Equal(0L, completedBytes);
        Assert.Equal(0, completedOperations);
        Assert.Equal(0, samples);
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(1048576)]
    public void FullBlockCompletesOnceOnTheBorrowedOwnedHandle(int requested)
    {
        var handle = FakeHandle();
        int reads = 0;
        var bytes = DiskBenchService.WithOwnedFile("injected", _ => handle, owned =>
        {
            byte[] block = new byte[requested];
            DiskBenchService.ReadFullBlock(owned, block, 0, (actualHandle, buffer, offset) =>
            {
                Assert.Same(handle, actualHandle);
                Assert.False(actualHandle.IsClosed);
                reads++;
                return buffer.Length;
            });
            return requested;
        });
        Assert.Equal(requested, bytes);
        Assert.Equal(1, reads);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void ReadFailurePreservesCauseAndClosesTheSameOwnedHandle()
    {
        var handle = FakeHandle();
        var failure = new IOException("injected read failure");
        var actual = Assert.Throws<IOException>(() => DiskBenchService.WithOwnedFile<int>("injected", _ => handle, owned =>
        {
            DiskBenchService.ReadFullBlock(owned, new byte[4096], 0, (_, _, _) => throw failure);
            return 1;
        }));
        Assert.Same(failure, actual);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void VisibleAlignmentLimitationDoesNotClaimDeviceGeometryWasProbed()
    {
        var service = new DiskBenchService();
        Assert.Contains(DiskBenchService.AlignmentLimitation, service.StatusLine);
        Assert.Contains("未查證", service.StatusLine);
        Assert.Contains("邏輯／實體磁區", service.StatusLine);
    }

    private static SafeFileHandle FakeHandle() => new(new IntPtr(12345), ownsHandle: false);
}
