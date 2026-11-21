using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

public class DiskSurfaceBackingDeviceTests
{
    [Fact]
    public void NativeBufferAddressUsesReportedAlignmentAndFreeInvalidatesIt()
    {
        var buffer = new DiskSurfaceBackingDevice.AlignedBuffer(512, 4096);
        Assert.Equal(0, buffer.Address.ToInt64() % 4096);
        Assert.Equal(512, buffer.Memory.Length);
        ((IDisposable)buffer).Dispose();
        Assert.Equal(IntPtr.Zero, buffer.Address);
        Assert.Throws<ObjectDisposedException>(() => buffer.GetSpan());
    }

    [Fact]
    public async Task AlignedPendingReadSurvivesCancellationUntilCompletion()
    {
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Memory<byte> pending = default;
        var read = DiskSurfaceScanService.ReadAlignedAsync(0, new byte[512],
            new DiskSurfaceBackingDevice.Alignment(512, 4096), (_, memory, _) =>
            { pending = memory; return new ValueTask<int>(completion.Task); }, cancel.Token).AsTask();
        cancel.Cancel();
        Assert.False(read.IsCompleted);
        pending.Span.Fill(0x42);
        completion.SetResult(512);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Throws<ObjectDisposedException>(() => { _ = pending.Span.Length; });
    }

    [Fact]
    public async Task MisalignedShortReadStopsBeforeAnotherRead()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DiskSurfaceScanService.ReadAlignedAsync(0,
            new byte[1024], new DiskSurfaceBackingDevice.Alignment(512, 4096), (_, memory, _) =>
            { calls++; Assert.Equal(1024, memory.Length); return ValueTask.FromResult(513); }, CancellationToken.None).AsTask());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidParameterIsConfigurationFailureWithoutMediaRetry()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DiskSurfaceScanService.ReadBlocksAsync("fake", 512, 512, 512,
            (offset, memory, ct) => DiskSurfaceScanService.ReadAlignedAsync(offset, memory,
                new DiskSurfaceBackingDevice.Alignment(512, 4096), (_, _, _) =>
                { calls++; return ValueTask.FromException<int>(new System.IO.IOException("invalid parameter", unchecked((int)0x80070057))); }, ct)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StorageAlignmentRequiresReportedLogicalPhysicalGeometry()
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 4096);
        Assert.Equal(new DiskSurfaceBackingDevice.Alignment(512, 4096), DiskSurfaceBackingDevice.ParseAlignment(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 512);
        Assert.Null(DiskSurfaceBackingDevice.ParseAlignment(bytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0);
        Assert.Null(DiskSurfaceBackingDevice.ParseAlignment(bytes));
    }
    private static byte[] Extent(uint count = 1)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 1024);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 4096);
        return bytes;
    }

    [Fact]
    public void ExtentRequiresCompleteSingleDiskRecord()
    {
        Assert.Equal(new DiskSurfaceBackingDevice.Extent(4, 1024, 4096), DiskSurfaceBackingDevice.ParseExtent(Extent()));
        Assert.Null(DiskSurfaceBackingDevice.ParseExtent(Extent(0)));
        Assert.Null(DiskSurfaceBackingDevice.ParseExtent(Extent(2)));
        Assert.Null(DiskSurfaceBackingDevice.ParseExtent(Extent().AsSpan(0, 31)));
        var overflow = Extent();
        BinaryPrimitives.WriteInt64LittleEndian(overflow.AsSpan(16), long.MaxValue);
        Assert.Null(DiskSurfaceBackingDevice.ParseExtent(overflow));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(11, true)]
    [InlineData(17, false)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    public void OnlyExplicitAtaSataDirectAccessDescriptorAllowed(uint bus, bool allowed)
    {
        var bytes = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), bus);
        Assert.Equal(allowed, DiskSurfaceBackingDevice.IsAllowedDescriptor(bytes));
        bytes[8] = 5;
        Assert.False(DiskSurfaceBackingDevice.IsAllowedDescriptor(bytes));
    }

    [Fact]
    public void PhysicalDiskNumberMustMatchExtentAndNotBeAPartition()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue);
        Assert.True(DiskSurfaceBackingDevice.MatchesDeviceNumber(bytes, 4));
        Assert.False(DiskSurfaceBackingDevice.MatchesDeviceNumber(bytes, 5));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        Assert.False(DiskSurfaceBackingDevice.MatchesDeviceNumber(bytes, 4));
    }

    [Fact]
    public void FakeMetadataMustDescribeSameVolumeExtentAndPhysicalDevice()
    {
        var number = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(number, 7);
        BinaryPrimitives.WriteUInt32LittleEndian(number.AsSpan(4), 4);
        var descriptor = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(28), 11);
        Assert.True(DiskSurfaceBackingDevice.VerifyTopology(Extent(), Extent(), number, descriptor, descriptor));
        var changedExtent = Extent();
        BinaryPrimitives.WriteInt64LittleEndian(changedExtent.AsSpan(16), 2048);
        Assert.False(DiskSurfaceBackingDevice.VerifyTopology(Extent(), changedExtent, number, descriptor, descriptor));
        var changedDescriptor = descriptor.ToArray();
        changedDescriptor[35] = 1;
        Assert.False(DiskSurfaceBackingDevice.VerifyTopology(Extent(), Extent(), number, changedDescriptor, descriptor));
        BinaryPrimitives.WriteUInt32LittleEndian(number.AsSpan(4), 8);
        Assert.False(DiskSurfaceBackingDevice.VerifyTopology(Extent(), Extent(), number, descriptor, descriptor));
    }
}
