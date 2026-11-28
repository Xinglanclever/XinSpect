using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

public sealed class RawMouseCaptureTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(4, 40)]
    [InlineData(8, 48)]
    public void WindowsSdkMouseLayoutsDecodeSignedMovementAndDevice(int pointerBytes, int packetBytes)
    {
        // SDK: RAWINPUTHEADER = 16/24 bytes; RAWMOUSE lLastX/lLastY = +12/+16.
        byte[] bytes = new byte[packetBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)packetBytes);
        if (pointerBytes == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x12345678);
        else BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x12345678);
        int header = pointerBytes == 4 ? 16 : 24;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(header), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(header + 12), -27);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(header + 16), 400);
        Assert.True(RawMouseNativeApi.TryDecode(bytes, pointerBytes, out var packet));
        Assert.Equal((nint)0x12345678, packet.Device);
        Assert.Equal(-27, packet.X); Assert.Equal(400, packet.Y); Assert.Equal(8, packet.Flags);
    }

    [Theory]
    [InlineData(0, 48, 24)]
    [InlineData(0, 47, 48)]
    [InlineData(0, 48, 4096)]
    [InlineData(1, 48, 48)]
    [InlineData(2, 48, 48)]
    public void ShortMismatchedOrNonMousePacketsFailClosed(int type, int length, int declared)
    {
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)declared);
        Assert.False(RawMouseNativeApi.TryDecode(bytes, 8, out _));
    }

    [Fact]
    public void ExistingRegistrationIsNeverOverwritten()
    {
        var native = new FakeNative { Target = 99 };
        using var capture = Create(native);
        Assert.False(capture.Start(10));
        Assert.Equal(0, native.Registers);
        Assert.Equal(99, native.Target);
        Assert.Equal(FactAvailability.ReadError, capture.Snapshot().Availability);
    }

    [Fact]
    public void StartStopResetAndDisposeReleaseOnlyOwnedRegistrationExactlyOnce()
    {
        var native = new FakeNative();
        var capture = Create(native);
        Assert.True(capture.Start(10));
        Assert.True(capture.Start(10));
        capture.Stop(); capture.Stop();
        Assert.Equal(1, native.Registers); Assert.Equal(1, native.Removes);
        Assert.True(capture.Start(10));
        capture.Reset();
        Assert.Empty(capture.Snapshot().Devices);
        Assert.Equal(FactAvailability.Unknown, capture.Snapshot().Availability);
        capture.Dispose(); capture.Dispose();
        Assert.Equal(2, native.Removes); Assert.Equal(1, native.Disposes);
        Assert.Throws<ObjectDisposedException>(() => capture.Start(10));
    }

    [Fact]
    public void ReadFailureStopsCaptureAndReleasesRegistration()
    {
        var native = new FakeNative { ReadFails = true };
        using var capture = Create(native);
        Assert.True(capture.Start(10));
        capture.ProcessInput(1);
        Assert.False(capture.IsRunning);
        Assert.Equal(1, native.Removes);
        Assert.Equal(FactAvailability.ReadError, capture.Snapshot().Availability);
        Assert.Equal(1, capture.Snapshot().ReadErrors);
    }

    [Fact]
    public void ChangedRegistrationIsNotRemovedAndResultIsReadError()
    {
        var native = new FakeNative();
        using var capture = Create(native);
        Assert.True(capture.Start(10));
        native.Target = 99;
        capture.Stop();
        Assert.Equal(0, native.Removes);
        Assert.Equal(99, native.Target);
        Assert.Equal(FactAvailability.ReadError, capture.Snapshot().Availability);
    }

    [Fact]
    public void DeadlineRejectsInputBeforeCallingNativeAndCleansUp()
    {
        long stamp = 0;
        var native = new FakeNative();
        using var capture = new RawMouseCapture(native, () => stamp, () => At, 1_000_000);
        Assert.True(capture.Start(10));
        stamp = 10_000_000;
        capture.ProcessInput(1);
        Assert.Equal(0, native.Reads);
        Assert.Equal(1, native.Removes);
        Assert.False(capture.IsRunning);
    }

    [Fact]
    public void InjectedNativeProducerReachesPerDeviceSnapshotAndFact()
    {
        long stamp = 0;
        var native = new FakeNative();
        using var capture = new RawMouseCapture(native, () => stamp, () => At, 1_000_000);
        Assert.True(capture.Start(10));
        for (int i = 0; i <= 25; i++)
        { stamp = i * 1000; capture.ProcessInput(1); }
        stamp = 100_000; capture.Stop();
        var snapshot = capture.Snapshot();
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal(1, native.Identifications);
        Assert.Equal(25, device.Samples);
        var store = new RawMouseSampleStore();
        store.Publish(snapshot, device.DeviceId);
        var fact = MousePollRateFactsService.Collect(At.AddSeconds(1), () => store.Read(At.AddSeconds(1)));
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(1000, fact.NumericValue);
        Assert.Equal(FactTrustLevel.Measured, fact.Trust);
    }

    [Fact]
    public void RegistrationFailureAndFailedCleanupRemainVisible()
    {
        var native = new FakeNative { RegisterFails = true };
        using var capture = Create(native);
        Assert.False(capture.Start(10));
        Assert.Equal(FactAvailability.ReadError, capture.Snapshot().Availability);
        native.RegisterFails = false;
        Assert.True(capture.Start(10));
        native.RemoveFails = true;
        capture.Stop();
        Assert.Equal(FactAvailability.ReadError, capture.Snapshot().Availability);
        Assert.Contains("remove failed", capture.Snapshot().Status);
    }

    private static RawMouseCapture Create(FakeNative native) => new(native, () => 0, () => At, 1_000_000);

    private sealed class FakeNative : IRawMouseNativeApi
    {
        internal nint Target;
        internal bool ReadFails, RegisterFails, RemoveFails;
        internal int Registers, Removes, Reads, Identifications, Disposes;
        public bool TryGetMouseRegistration(out nint target, out string? error)
        { target = Target; error = null; return true; }
        public bool Register(nint target, bool remove, out string? error)
        {
            if (remove) Removes++; else Registers++;
            bool fail = remove ? RemoveFails : RegisterFails;
            error = fail ? (remove ? "remove failed" : "register failed") : null;
            if (!fail) Target = remove ? 0 : target;
            return !fail;
        }
        public bool TryRead(nint rawInput, out RawMousePacket packet, out string? error)
        { Reads++; packet = new(1, 1, 0, 0); error = ReadFails ? "read failed" : null; return !ReadFails; }
        public (string Id, bool Readable) Identify(nint device) { Identifications++; return ("injected-device", true); }
        public void Dispose() => Disposes++;
    }
}
