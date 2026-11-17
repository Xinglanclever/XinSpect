using System.Buffers.Binary;
using Xunit;

namespace XinSpect.Tests;

/// <summary>All dependencies are injected; no native API, native allocation, worker execution or load is used.</summary>
public sealed class DeepBenchProcessorPowerScopeTests
{
    [Theory]
    [InlineData(0, 4, 15UL, 64)]
    [InlineData(2, 4, 15UL, 64)]
    [InlineData(1, 0, 0UL, 64)]
    [InlineData(1, 65, ulong.MaxValue, 64)]
    [InlineData(1, 33, 8589934591UL, 32)]
    [InlineData(1, 1, 4294967296UL, 32)]
    [InlineData(1, 4, 7UL, 64)]
    [InlineData(1, 4, 31UL, 64)]
    [InlineData(1, 4, 15UL, 16)]
    [InlineData(1, -1, 15UL, 64)]
    public void InvalidOrUnprovedNativeScopeRejects(int groups, int count, ulong mask, int bits) =>
        Assert.Throws<ProcessorPowerUnsupportedException>(() => ProcessorPowerScope.Create((ushort)groups, unchecked((uint)count), mask, bits));

    [Fact]
    public void SingleGroupAcquisitionChecksBothSidesOfGetSystemInfo()
    {
        List<string> calls = [];
        int groups = 0;
        Assert.Throws<ProcessorPowerUnsupportedException>(() => WindowsProcessorPowerSampler.CaptureSingleGroupScope(
            () => { calls.Add("group"); return (ushort)(++groups == 1 ? 1 : 2); },
            () => { calls.Add("info"); return (4U, 15UL, 64); }));
        Assert.Equal(new[] { "group", "info", "group" }, calls);
        calls.Clear();
        Assert.Throws<ProcessorPowerUnsupportedException>(() => WindowsProcessorPowerSampler.CaptureSingleGroupScope(
            () => { calls.Add("group"); return 0; },
            () => throw new Exception("must not call GetSystemInfo for unknown groups")));
        Assert.Equal(new[] { "group" }, calls);
    }

    [Fact]
    public void SparseExactIdSetAndHighestMaskBitAcceptWithoutDenseIndexAssumption()
    {
        ProcessorPowerScope scope = ProcessorPowerScope.Create(1, 3, (1UL << 63) | 9UL, 64);
        ProcessorPowerRange range = WindowsProcessorPowerSampler.Parse(scope, Records(63, 0, 3), true);
        Assert.Equal(72, scope.BufferBytes);
        Assert.Equal(2000, range.CurrentMin);
        Assert.Equal(2063, range.CurrentMax);
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(24)]
    public void ParserRejectsShortOversizedAndMisalignedCapacity(int delta)
    {
        var scope = Scope4();
        byte[] records = new byte[scope.BufferBytes + delta];
        Assert.Throws<ProcessorPowerUnsupportedException>(() => WindowsProcessorPowerSampler.Parse(scope, records, true));
    }

    [Theory]
    [InlineData(0, 1, 2, 2)]
    [InlineData(0, 1, 2, 4)]
    [InlineData(0, 1, 2, 64)]
    public void DuplicateMissingUnexpectedIdsReject(int a, int b, int c, int d) =>
        Assert.Throws<ProcessorPowerUnsupportedException>(() => WindowsProcessorPowerSampler.Parse(
            Scope4(), Records((uint)a, (uint)b, (uint)c, (uint)d), true));

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void RequiredFieldsRejectZero(int offset)
    {
        byte[] bytes = Records(0, 1, 2, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), 0);
        Assert.Throws<ProcessorPowerUnsupportedException>(() => WindowsProcessorPowerSampler.Parse(Scope4(), bytes, true));
        if (offset == 12)
            Assert.Equal(0, WindowsProcessorPowerSampler.Parse(Scope4(), bytes, false).LimitMin);
    }

    [Fact]
    public void NativeCapacityComesFromNAndCannotBeExpandedByPlausibleTail()
    {
        var scope = Scope4();
        byte[] storage = Records(0, 1, 2, 3, 4, 5, 6, 7);
        uint requested = 0;
        var sampler = new WindowsProcessorPowerSampler(() => scope,
            (_, bytes) => { requested = bytes; return 0; }, (_, _) => { },
            (_, bytes) => storage[..bytes]);
        var range = sampler.Sample(scope, 123, true, CancellationToken.None);
        Assert.Equal(96U, requested);
        Assert.Equal(2003, range.CurrentMax);
        Assert.Throws<ProcessorPowerUnsupportedException>(() => WindowsProcessorPowerSampler.Parse(scope, storage, true));
        var scope8 = ProcessorPowerScope.Create(1, 8, 255, 64);
        var sampler8 = new WindowsProcessorPowerSampler(() => scope8,
            (_, bytes) => { requested = bytes; return 0; }, (_, _) => { }, (_, bytes) => storage[..bytes]);
        _ = sampler8.Sample(scope8, 123, true, CancellationToken.None);
        Assert.Equal(192U, requested);
    }

    [Fact]
    public void SuccessfulPrefixWriteCannotReusePlausiblePreviousTail()
    {
        var scope = Scope4();
        byte[] storage = Records(0, 1, 2, 3);
        int clears = 0;
        var sampler = new WindowsProcessorPowerSampler(() => scope,
            (_, _) => { Records(0, 1).CopyTo(storage, 0); return 0; },
            (_, bytes) => { Assert.Equal(96, bytes); Array.Clear(storage); clears++; },
            (_, _) => storage.ToArray());
        Assert.Throws<ProcessorPowerUnsupportedException>(() => sampler.Sample(scope, 123, true, CancellationToken.None));
        Assert.Equal(1, clears);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeMaskChangeWithSameCountRejectsBeforeOrAfterQuery(bool afterQuery)
    {
        var scope = Scope4();
        var changed = ProcessorPowerScope.Create(1, 4, 30, 64);
        int reads = 0, queries = 0, copies = 0;
        var sampler = new WindowsProcessorPowerSampler(
            () => ++reads == 1 && afterQuery ? scope : changed,
            (_, _) => { queries++; return 0; }, (_, _) => { },
            (_, _) => { copies++; return Records(0, 1, 2, 3); });
        Assert.Throws<ProcessorPowerUnsupportedException>(() => sampler.Sample(scope, 123, true, CancellationToken.None));
        Assert.Equal(afterQuery ? 1 : 0, queries);
        Assert.Equal(0, copies);
    }

    [Fact]
    public void QueryFailureDoesNotReadBufferAndCancellationWinsOverStatus()
    {
        var scope = Scope4();
        using var cancellation = new CancellationTokenSource();
        bool cancel = false;
        var sampler = new WindowsProcessorPowerSampler(() => scope,
            (_, _) => { if (cancel) cancellation.Cancel(); return -1; }, (_, _) => { },
            (_, _) => throw new Exception("must not parse failed query"));
        Assert.Throws<ProcessorPowerUnsupportedException>(() => sampler.Sample(scope, 123, true, CancellationToken.None));
        cancel = true;
        Assert.ThrowsAny<OperationCanceledException>(() => sampler.Sample(scope, 123, true, cancellation.Token));
    }

    [Theory]
    [InlineData(false, 8, 4)]
    [InlineData(true, 8, 4)]
    [InlineData(false, 4, 8)]
    [InlineData(true, 4, 8)]
    [InlineData(false, 0, 4)]
    [InlineData(true, 0, 4)]
    public async Task EnginesRejectWNotEqualNBeforeAllocationOrAnyWorker(bool boost, int workers, int native)
    {
        var scope = ProcessorPowerScope.Create(1, (uint)native, (1UL << native) - 1, 64);
        var sampler = new WindowsProcessorPowerSampler(() => scope,
            (_, _) => throw new Exception("must not query on mismatch"), (_, _) => throw new Exception("must not clear"),
            (_, _) => throw new Exception("must not copy"));
        Func<int, nint> allocate = _ => throw new Exception("must not allocate on mismatch");
        Action<nint> free = _ => throw new Exception("nothing owned");
        await RejectEngine(boost, sampler, workers, allocate, free, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, "query")]
    [InlineData(true, "query")]
    [InlineData(false, "ids")]
    [InlineData(true, "ids")]
    [InlineData(false, "scope")]
    [InlineData(true, "scope")]
    [InlineData(false, "groups")]
    [InlineData(true, "groups")]
    public async Task EnginesPreflightRefusalFreesExactlyOnceAndNeverReachesLoad(bool boost, string failure)
    {
        var scope = Scope4();
        int reads = 0, allocations = 0, frees = 0, queries = 0;
        var sampler = new WindowsProcessorPowerSampler(
            () =>
            {
                if (failure == "groups") return ProcessorPowerScope.Create(2, 4, 15, 64);
                return failure == "scope" && ++reads > 1 ? ProcessorPowerScope.Create(1, 4, 30, 64) : scope;
            },
            (_, bytes) => { Assert.Equal(96U, bytes); queries++; return failure == "query" ? -1 : 0; },
            (_, bytes) => Assert.Equal(96, bytes),
            (_, _) => failure == "ids" ? Records(0, 1, 2, 2) : Records(0, 1, 2, 3));
        Func<int, nint> allocate = bytes => { Assert.Equal(96, bytes); allocations++; return 123; };
        Action<nint> free = pointer => { Assert.Equal((nint)123, pointer); frees++; };
        await RejectEngine(boost, sampler, 4, allocate, free, CancellationToken.None);
        Assert.Equal(failure == "groups" ? 0 : 1, allocations);
        Assert.Equal(allocations, frees);
        Assert.Equal(failure is "groups" or "scope" ? 0 : 1, queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledEngineDoesNotReadScopeOrAllocate(bool boost)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sampler = new WindowsProcessorPowerSampler(() => throw new Exception("must not read scope"),
            (_, _) => throw new Exception("must not query"), (_, _) => { }, (_, _) => []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngine(boost, sampler, 4,
            _ => throw new Exception("must not allocate"), _ => { }, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedPrepareFaultOrCancellationOwnsBufferAndLeavesLockReusable(bool cancel)
    {
        using var runLock = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var failure = new ProcessorPowerUnsupportedException("injected prepare failure");
        int frees = 0, gates = 0, workers = 0, measured = 0;
        Func<Task<int>> run = () => DeepBenchWorkerLifetime.RunAsync<object, int>(runLock, 1,
            () => 123, buffer => { Assert.Equal((nint)123, buffer); frees++; },
            () => { gates++; return new object(); }, _ => { }, null,
            (_, _) => { workers++; throw new Exception("unexpected worker factory"); },
            (_, _) => { measured++; return Task.FromResult(1); }, cancellation.Token,
            prepare: buffer =>
            {
                Assert.Equal((nint)123, buffer);
                if (cancel) { cancellation.Cancel(); return; }
                throw failure;
            });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<ProcessorPowerUnsupportedException>(run));
            Assert.Same(failure, await Assert.ThrowsAsync<ProcessorPowerUnsupportedException>(run));
        }
        Assert.Equal(cancel ? 1 : 2, frees);
        Assert.Equal(0, gates);
        Assert.Equal(0, workers);
        Assert.Equal(0, measured);
        Assert.Equal(1, runLock.CurrentCount);
        Assert.True(runLock.Wait(0));
        runLock.Release();
    }

    [Fact]
    public async Task PrepareSuccessRunsAfterOwnershipBeforeGateAndZeroWorkerMeasurement()
    {
        using var runLock = new SemaphoreSlim(1, 1);
        List<string> order = [];
        int result = await DeepBenchWorkerLifetime.RunAsync<object, int>(runLock, 0,
            () => { order.Add("allocate"); return 123; }, _ => order.Add("free"),
            () => { order.Add("gate"); return new object(); }, _ => order.Add("stop"), null,
            (_, _) => throw new Exception("no workers requested"),
            (_, _) => { order.Add("measure"); return Task.FromResult(42); }, CancellationToken.None,
            prepare: _ => order.Add("prepare"));
        Assert.Equal(42, result);
        Assert.Equal(new[] { "allocate", "prepare", "gate", "measure", "stop", "free" }, order);
        Assert.Equal(1, runLock.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineCancellationDuringPreflightStillFreesOwnedBuffer(bool boost)
    {
        using var cancellation = new CancellationTokenSource();
        int frees = 0;
        var scope = Scope4();
        var sampler = new WindowsProcessorPowerSampler(() => scope,
            (_, _) => { cancellation.Cancel(); return -1; }, (_, _) => { },
            (_, _) => throw new Exception("cannot parse cancelled query"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEngine(boost, sampler, 4,
            _ => 123, _ => frees++, cancellation.Token));
        Assert.Equal(1, frees);
    }

    [Fact]
    public async Task PrepareRechecksScopeAfterWaitingForRunLock()
    {
        using var runLock = new SemaphoreSlim(0, 1);
        var scope = Scope4();
        bool changed = false;
        int allocations = 0, frees = 0;
        var sampler = new WindowsProcessorPowerSampler(
            () => changed ? ProcessorPowerScope.Create(1, 4, 30, 64) : scope,
            (_, _) => throw new Exception("changed scope must prevent query"), (_, _) => { }, (_, _) => []);
        Task<int> pending = DeepBenchWorkerLifetime.RunAsync<object, int>(runLock, 1,
            () => { allocations++; return 123; }, _ => frees++,
            () => throw new Exception("scope refusal must prevent gate"), _ => { }, null,
            (_, _) => throw new Exception("scope refusal must prevent worker"),
            (_, _) => throw new Exception("scope refusal must prevent measure"), CancellationToken.None,
            prepare: buffer => _ = sampler.Sample(scope, buffer, true, CancellationToken.None));
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, allocations);
        changed = true;
        runLock.Release();
        await Assert.ThrowsAsync<ProcessorPowerUnsupportedException>(() => pending);
        Assert.Equal(1, allocations);
        Assert.Equal(1, frees);
        Assert.Equal(1, runLock.CurrentCount);
    }

    private static ProcessorPowerScope Scope4() => ProcessorPowerScope.Create(1, 4, 15, 64);

    private static byte[] Records(params uint[] ids)
    {
        byte[] records = new byte[ids.Length * 24];
        for (int index = 0; index < ids.Length; index++)
        {
            Span<byte> record = records.AsSpan(index * 24, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(record, ids[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 4000);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], 2000 + ids[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], 3500);
        }
        return records;
    }

    private static async Task RejectEngine(bool boost, WindowsProcessorPowerSampler sampler, int workers,
        Func<int, nint> allocate, Action<nint> free, CancellationToken token)
    {
        if (boost) await Assert.ThrowsAsync<BoostRecoveryUnsupportedException>(() => RunEngine(boost, sampler, workers, allocate, free, token));
        else await Assert.ThrowsAsync<ThroughputDegradationUnsupportedException>(() => RunEngine(boost, sampler, workers, allocate, free, token));
    }

    private static Task RunEngine(bool boost, WindowsProcessorPowerSampler sampler, int workers,
        Func<int, nint> allocate, Action<nint> free, CancellationToken token) => boost
        ? new WindowsBoostRecoveryEngine(sampler, () => workers, allocate, free, _ => throw new Exception("worker startup forbidden in pure regression")).MeasureAsync(new(1, 1, 1, 1, 1), token)
        : new WindowsThroughputDegradationEngine(sampler, () => workers, allocate, free, _ => throw new Exception("worker startup forbidden in pure regression")).MeasureAsync(new(1, 1, 1, 1), token);
}
