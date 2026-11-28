using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

[Collection(HardwareOperationPolicyCollection.Name)]
public sealed class CpuPackagePowerDiagnosticTests
{
    [Fact]
    public async Task ClosedGateAndPreCancellationDoNotTouchTopologyOrAcquire()
    {
        var runtime = new FakeRuntime(); int acquisitions = 0;
        var service = new CpuPackagePowerDiagnosticService(() => false,
            () => { acquisitions++; throw new Exception("no acquisition"); }, runtime);
        var closed = await service.CaptureAsync();
        Assert.Equal(FactAvailability.InsufficientPrivilege, closed.Availability);
        Assert.True(closed.PolicyStopped);
        Assert.Equal(0, runtime.TopologyCalls); Assert.Equal(0, acquisitions);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = await service.CaptureAsync(cts.Token);
        Assert.True(cancelled.Cancelled); Assert.Equal(0, acquisitions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task UnknownOrMultiplePackagesRefuseBeforePinOrMsr(int count)
    {
        var runtime = new FakeRuntime { Packages = Enumerable.Range(0, count)
            .Select(i => (IReadOnlyList<ProcessorRef>)new[] { new ProcessorRef((ushort)i, 0) }).ToArray() };
        var service = new CpuPackagePowerDiagnosticService(() => true,
            () => throw new Exception("must not acquire"), runtime);
        Assert.Equal(FactAvailability.NotSupported, (await service.CaptureAsync()).Availability);
        Assert.Equal(0, runtime.PinCalls);
    }

    [Theory]
    [InlineData("AuthenticAMD", 6, 0x55, false, true)]
    [InlineData("GenuineIntel", 6, 0x5E, false, true)]
    [InlineData("GenuineIntel", 6, 0x55, true, true)]
    [InlineData("GenuineIntel", 6, 0x55, false, false)]
    public async Task UnknownCpuVmOrPackageIdentityDoesNotReadMsr(string vendor, int family, int model, bool vm, bool known)
    {
        var runtime = new FakeRuntime();
        runtime.Platform = runtime.Platform with { Vendor = vendor, Family = family, Model = model,
            Hypervisor = vm, CpuidPackageId = known ? 7u : null };
        var service = new CpuPackagePowerDiagnosticService(() => true,
            () => throw new Exception("must not acquire"), runtime);
        Assert.Equal(FactAvailability.NotSupported, (await service.CaptureAsync()).Availability);
        Assert.True(runtime.PinDisposed);
    }

    [Fact]
    public async Task FailedPinAndMissingExistingSessionAreExplicitAndDoNotRetry()
    {
        var runtime = new FakeRuntime { PinOk = false }; int acquisitions = 0;
        var service = new CpuPackagePowerDiagnosticService(() => true, () => { acquisitions++; return null; }, runtime);
        Assert.Equal(FactAvailability.ReadError, (await service.CaptureAsync()).Availability);
        Assert.Equal(0, acquisitions);
        runtime.PinOk = true;
        var unavailable = await service.CaptureAsync();
        Assert.Equal(FactAvailability.InsufficientPrivilege, unavailable.Availability);
        Assert.Contains("未初始化", unavailable.Reason); Assert.Equal(1, acquisitions);
    }

    [Fact]
    public async Task CaptureUsesPinnedGroupAndQpcDespiteLargeWallClockCorrections()
    {
        var runtime = new FakeRuntime { WallClockStepSeconds = 100 };
        var lease = new FakeLease(runtime); var service = Service(runtime, lease);
        var result = await service.CaptureAsync();
        Assert.Equal(FactAvailability.Unknown, result.Availability);
        Assert.Equal(8, result.Samples.Count); Assert.Equal(16, lease.Reads.Count);
        Assert.Equal(new ProcessorRef(2, 3), result.Platform!.Processor);
        Assert.Equal(7u, result.Platform.CpuidPackageId);
        Assert.Null(result.Samples[0].Interval);
        Assert.All(result.Samples.Skip(1), point =>
        {
            Assert.Equal(1.0, point.Interval!.UnverifiedWatts!.Value, 9);
            Assert.Equal(0.5, point.Interval.Seconds, 9);
            Assert.Equal(8192u, point.Interval.Modulo32Counts);
        });
        Assert.True(lease.Disposed); Assert.True(runtime.PinDisposed);
        Assert.All(lease.Reads, msr => Assert.Contains(msr, new uint[] { 0x606, 0x611 }));
        using var json = JsonDocument.Parse(result.ToJson());
        Assert.Equal("Unknown", json.RootElement.GetProperty("Availability").GetString());
        Assert.Contains("0x00000000000A0E03", result.ToJson());
        Assert.Contains("QpcFrequency", result.ToJson());
        Assert.Contains("未驗證", CpuPackagePowerDiagnosticWindow.Format(result));
    }

    [Fact]
    public async Task GateRevocationDuringWaitStopsBeforeNextReadAndDisposesOnlyAfterCapture()
    {
        bool allowed = true; var runtime = new FakeRuntime();
        var lease = new FakeLease(runtime);
        runtime.OnWait = () => { Assert.False(lease.Disposed); allowed = false; };
        var service = Service(runtime, lease, () => allowed);
        var result = await service.CaptureAsync();
        Assert.True(result.PolicyStopped); Assert.Single(result.Samples);
        Assert.Equal(2, lease.Reads.Count); Assert.True(lease.Disposed);
        Assert.Null(result.Samples[0].Interval);
    }

    [Fact]
    public async Task GateRevokedInsideEnergyReadPreservesRawButNeverPublishesItAsAnEndpoint()
    {
        bool allowed = true; var runtime = new FakeRuntime();
        var lease = new FakeLease(runtime) { OnRead = msr => { if (msr == 0x611) allowed = false; } };
        var result = await Service(runtime, lease, () => allowed).CaptureAsync();
        Assert.True(result.PolicyStopped); var point = Assert.Single(result.Samples);
        Assert.Equal(0UL, point.Energy!.Raw);
        Assert.Equal(FactAvailability.ReadError, point.Energy.Availability);
        Assert.Null(point.Interval); Assert.Equal(2, lease.Reads.Count); Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task CancellationDuringWaitReturnsPartialEvidenceAndNoFurtherRead()
    {
        var runtime = new FakeRuntime(); var lease = new FakeLease(runtime);
        using var cts = new CancellationTokenSource(); runtime.OnWait = cts.Cancel;
        var result = await Service(runtime, lease).CaptureAsync(cts.Token);
        Assert.True(result.Cancelled); Assert.Single(result.Samples);
        Assert.Equal(2, lease.Reads.Count); Assert.True(lease.Disposed); Assert.True(runtime.PinDisposed);
    }

    [Fact]
    public async Task ReadFailureAfterSuccessRetainsEvidenceAndDoesNotRepeatStaleWatts()
    {
        var runtime = new FakeRuntime(); var lease = new FakeLease(runtime) { FailEnergyNumber = 3 };
        var result = await Service(runtime, lease).CaptureAsync();
        Assert.Equal(FactAvailability.ReadError, result.Availability);
        Assert.Equal(3, result.Samples.Count);
        Assert.Equal(1.0, result.Samples[1].Interval!.UnverifiedWatts!.Value, 9);
        var failed = result.Samples[2]; Assert.Null(failed.Energy!.Raw); Assert.Null(failed.Interval);
        Assert.Equal(6, lease.Reads.Count); Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task ReaderExceptionAndAffinityLossStopWithoutFallbackOrStaleValue()
    {
        var runtime = new FakeRuntime();
        var lease = new FakeLease(runtime) { OnRead = _ => throw new InvalidOperationException("injected I/O failure") };
        var failed = await Service(runtime, lease).CaptureAsync();
        Assert.Equal(FactAvailability.ReadError, failed.Availability);
        Assert.Contains("injected", failed.Reason); Assert.Single(lease.Reads);
        Assert.True(lease.Disposed);
        runtime = new FakeRuntime(); lease = new FakeLease(runtime);
        runtime.OnWait = () => runtime.OnTarget = false;
        var migrated = await Service(runtime, lease).CaptureAsync();
        Assert.Equal(FactAvailability.ReadError, migrated.Availability);
        Assert.Equal(2, lease.Reads.Count); Assert.Contains("group", migrated.Reason);
    }

    [Fact]
    public async Task LongSynchronousReadKeepsRawAndRefusesPowerInsteadOfFakingCancellation()
    {
        var runtime = new FakeRuntime();
        var lease = new FakeLease(runtime) { OnRead = _ => runtime.Ticks += 251 };
        var result = await Service(runtime, lease).CaptureAsync();
        Assert.Equal(FactAvailability.ReadError, result.Availability);
        var sample = Assert.Single(result.Samples);
        Assert.NotNull(sample.Units.Raw); Assert.Null(sample.Interval); Assert.Null(sample.Energy);
        Assert.Equal(251, sample.Units.AfterQpc - sample.Units.BeforeQpc);
    }

    [Fact]
    public async Task ReservedEnergyBitsArePreservedWithoutPublishingWatts()
    {
        var runtime = new FakeRuntime();
        var lease = new FakeLease(runtime) { EnergyOverride = 0x100000001 };
        var result = await Service(runtime, lease).CaptureAsync();
        Assert.Equal(FactAvailability.Unknown, result.Availability);
        var point = Assert.Single(result.Samples);
        Assert.Equal(0x100000001UL, point.Energy!.Raw); Assert.Null(point.Interval);
        Assert.Contains("高 32 位", result.Reason); Assert.Equal(2, lease.Reads.Count);
    }

    [Fact]
    public void UnitChangeInvalidatesOnlyTheDiscontinuousInterval()
    {
        var a = Point(0, 0, 1000);
        var b = Point(1, 8192, 1500) with { Units = a.Units with { Raw = 0xA0F03 } };
        var invalid = CpuPackagePowerDiagnosticService.Derive(a, b, Math.ScaleB(1.0, -15), 1000);
        Assert.Null(invalid.UnverifiedWatts); Assert.Null(invalid.OneCountResolutionWatts);
        var c = Point(2, 16384, 2000) with { Units = b.Units };
        var valid = CpuPackagePowerDiagnosticService.Derive(b, c, Math.ScaleB(1.0, -15), 1000);
        Assert.Equal(0.5, valid.UnverifiedWatts!.Value, 9);
    }

    [Fact]
    public async Task ConcurrentCaptureIsRejectedAndLeaseOutlivesBlockedSynchronousIo()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var runtime = new FakeRuntime(); using var cts = new CancellationTokenSource();
        var lease = new FakeLease(runtime) { OnRead = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        var service = Service(runtime, lease);
        Task<CpuPackagePowerDiagnosticResult> pending = service.CaptureAsync(cts.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());
            cts.Cancel(); Assert.False(lease.Disposed); Assert.False(pending.IsCompleted);
        }
        finally { release.Set(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Cancelled); Assert.True(lease.Disposed); Assert.True(runtime.PinDisposed);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x100000UL)]
    [InlineData(0xA2E03UL)]
    public void InvalidOrReservedUnitEncodingIsNotGuessed(ulong raw)
        => Assert.False(CpuPackagePowerDiagnosticService.TryEnergyUnit(raw, out _));

    [Fact]
    public void EnergyUnit31RemainsPositiveAndDoesNotUseSignedShift()
    {
        Assert.True(CpuPackagePowerDiagnosticService.TryEnergyUnit(0xA1F03, out double unit));
        Assert.Equal(Math.ScaleB(1.0, -31), unit);
    }

    [Fact]
    public void ModuloWrapIsPreservedButResetAmbiguityDoesNotBecomeWatts()
    {
        var interval = CpuPackagePowerDiagnosticService.Derive(Point(0, 0xFFFFFFF0, 1000), Point(1, 0x10, 1500),
            Math.ScaleB(1.0, -14), 1000);
        Assert.Equal(32u, interval.Modulo32Counts); Assert.True(interval.WrapOrReset);
        Assert.Null(interval.UnverifiedWatts); Assert.Contains("重設", interval.Limitation);
    }

    [Fact]
    public void ZeroAdvanceAndLongOrReversedQpcNeverMeanZeroWatts()
    {
        foreach (var (end, at) in new[] { (100UL, 1500L), (200UL, 999L), (200UL, 4000L) })
            Assert.Null(CpuPackagePowerDiagnosticService.Derive(Point(0, 100, 1000), Point(1, end, at),
                Math.ScaleB(1.0, -14), 1000).UnverifiedWatts);
    }

    [Fact]
    public void PackageTopologyParsesGroupsAsMembersRatherThanSeparatePackagesAndRejectsOverlap()
    {
        byte[] one = Package((0x8, 2), (0x1, 3));
        var package = Assert.Single(CpuPackagePowerDiagnosticRuntime.ParsePackages(one));
        Assert.Equal(new[] { new ProcessorRef(2, 3), new ProcessorRef(3, 0) }, package);
        byte[] overlap = one.Concat(Package((0x8, 2))).ToArray();
        Assert.Empty(CpuPackagePowerDiagnosticRuntime.ParsePackages(overlap));
        Assert.Empty(CpuPackagePowerDiagnosticRuntime.ParsePackages(one[..^1]));
    }

    [Fact]
    public void ExistingLeaseDoesNotInitializeAndModeRevocationKeepsBackendAliveUntilRelease()
    {
        var settings = new SettingsService(loadFromDisk: false) { AdvancedDiagnosticsEnabled = true };
        HardwareOperationGate.Bind(settings);
        int creations = 0; var backend = new FakeBackend();
        KernelBackendHost.SetFactoriesForTests(() => { creations++; return (backend, ""); },
            () => PawnIoBackend.Create(true, () => (null, "test unavailable")));
        try
        {
            KernelBackendHost.ApplyMode(KernelBackendMode.UniversalAllowed);
            Assert.Null(KernelBackendHost.TryAcquireExistingPowerReadLease(out _));
            Assert.Equal(0, creations);
            using (var opened = KernelBackendHost.RouteUse(KernelCapability.MsrRead))
                Assert.Same(backend, opened.Route.Backend); // Test factory only, no actual driver.
            var lease = KernelBackendHost.TryAcquireExistingPowerReadLease(out _);
            Assert.NotNull(lease); Assert.Equal(1, creations);
            Assert.Throws<ArgumentOutOfRangeException>(() => lease.Read(0x198));
            Assert.Equal(0xA0E03UL, lease.Read(0x606));
            KernelBackendHost.ApplyMode(KernelBackendMode.Auto);
            Assert.False(lease.IsCurrent); Assert.Equal(0, backend.Closes);
            Assert.Throws<OperationCanceledException>(() => lease.Read(0x606));
            lease.Dispose(); lease.Dispose(); Assert.Equal(1, backend.Closes);
        }
        finally
        {
            KernelBackendHost.ApplyMode(KernelBackendMode.Auto);
            KernelBackendHost.SetFactoriesForTests(null); HardwareOperationGate.Bind(null);
        }
    }

    [Fact]
    public async Task ExistingLeaseKeepsBlockedReadAliveWithoutBlockingModeRetirement()
    {
        var settings = new SettingsService(loadFromDisk: false) { AdvancedDiagnosticsEnabled = true };
        HardwareOperationGate.Bind(settings);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new FakeBackend
        {
            OnRead = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("Fake read was not released.");
            }
        };
        ICpuPackagePowerDiagnosticReadLease? lease = null;
        Task<ulong?>? read = null;
        Task? modeChange = null;
        KernelBackendHost.SetFactoriesForTests(() => (backend, ""),
            () => PawnIoBackend.Create(true, () => (null, "test unavailable")));
        try
        {
            KernelBackendHost.ApplyMode(KernelBackendMode.UniversalAllowed);
            using (var opened = KernelBackendHost.RouteUse(KernelCapability.MsrRead))
                Assert.Same(backend, opened.Route.Backend); // Injected factory only.
            lease = KernelBackendHost.TryAcquireExistingPowerReadLease(out _);
            Assert.NotNull(lease);
            read = Task.Run(() => lease.Read(0x606));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Fake read did not start.");

            modeChange = Task.Run(() => KernelBackendHost.ApplyMode(KernelBackendMode.Auto));
            Assert.Same(modeChange, await Task.WhenAny(modeChange, Task.Delay(TimeSpan.FromSeconds(2))));
            await modeChange;
            Assert.False(lease.IsCurrent);
            Assert.Equal(0, backend.Closes);
            lease.Dispose();
            Assert.Equal(0, backend.Closes); // Disposal cannot release an in-flight native read.

            release.Set();
            Assert.Equal(0xA0E03UL, await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, backend.Closes);
            Assert.Throws<OperationCanceledException>(() => lease.Read(0x606));
            lease.Dispose();
            Assert.Equal(1, backend.Closes);
        }
        finally
        {
            release.Set();
            if (read is not null)
                try { await read.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (modeChange is not null)
                try { await modeChange.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            lease?.Dispose();
            KernelBackendHost.ApplyMode(KernelBackendMode.Auto);
            KernelBackendHost.SetFactoriesForTests(null);
            HardwareOperationGate.Bind(null);
        }
    }

    [Fact]
    public void ProductionWiringHasNoImplicitLoadOrTrustPromotion()
    {
        string root = FindRoot();
        string service = File.ReadAllText(Path.Combine(root, "Services", "CpuPackagePowerDiagnosticService.cs"));
        string runtime = File.ReadAllText(Path.Combine(root, "Services", "CpuPackagePowerDiagnosticRuntime.cs"));
        string window = File.ReadAllText(Path.Combine(root, "Views", "CpuPackagePowerDiagnosticWindow.xaml.cs"));
        foreach (string forbidden in new[] { "RaplEnergyGate.", "WinRing0Bridge.Create(", "KernelBackendHost.Route(", "KernelBackendHost.RouteUse(", "KernelBackendHost.Snapshot(", ".Open(", "WriteMsr(" })
        {
            Assert.DoesNotContain(forbidden, service); Assert.DoesNotContain(forbidden, runtime);
        }
        Assert.Contains("TryAcquireExistingPowerReadLease", service);
        Assert.DoesNotContain("Loaded=", File.ReadAllText(Path.Combine(root, "Views", "CpuPackagePowerDiagnosticWindow.xaml")));
        Assert.Contains("CaptureAsync(cancellation.Token)", window);
    }

    private static CpuPackagePowerDiagnosticService Service(FakeRuntime runtime, FakeLease lease, Func<bool>? allowed = null)
        => new(allowed ?? (() => true), () => lease, runtime);

    private static CpuPackagePowerDiagnosticSample Point(int index, ulong energy, long at) => new(index,
        DateTimeOffset.UnixEpoch, new(0x606, 0xA0E03, at, at, FactAvailability.Present, "test"),
        new(0x611, energy, at, at, FactAvailability.Present, "test"), null, "test");

    private static byte[] Package(params (ulong Mask, ushort Group)[] groups)
    {
        var bytes = new byte[32 + groups.Length * 16];
        BitConverter.GetBytes(3).CopyTo(bytes, 0); BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)groups.Length).CopyTo(bytes, 30);
        for (int i = 0; i < groups.Length; i++)
        {
            BitConverter.GetBytes(groups[i].Mask).CopyTo(bytes, 32 + i * 16);
            BitConverter.GetBytes(groups[i].Group).CopyTo(bytes, 40 + i * 16);
        }
        return bytes;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XinSpect.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }

    private sealed class FakeRuntime : ICpuPackagePowerDiagnosticRuntime
    {
        public long Ticks = 1000;
        public long Timestamp => Ticks;
        public long Frequency => 1000;
        private int _utcReads;
        public int WallClockStepSeconds;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddSeconds(++_utcReads * WallClockStepSeconds);
        public int TopologyCalls, PinCalls;
        public bool PinOk = true, PinDisposed, OnTarget = true;
        public Action? OnWait;
        public IReadOnlyList<IReadOnlyList<ProcessorRef>> Packages = [new[] { new ProcessorRef(2, 3) }];
        public CpuPackagePowerDiagnosticPlatform Platform = new("GenuineIntel", 6, 0x55, 4, false, 7, 1, new(2, 3), "test topology");
        public IReadOnlyList<IReadOnlyList<ProcessorRef>> ReadPackages() { TopologyCalls++; return Packages; }
        public ICpuPackagePowerDiagnosticPin Pin(ProcessorRef processor) { PinCalls++; return new FakePin(this); }
        public CpuPackagePowerDiagnosticPlatform IdentifyPinned(ProcessorRef processor, int count) => Platform with { Processor = processor, WindowsPackageCount = count };
        public bool IsOnProcessor(ProcessorRef processor) => OnTarget;
        public void Wait(int milliseconds, CancellationToken token) { Ticks += milliseconds; OnWait?.Invoke(); token.ThrowIfCancellationRequested(); }
    }
    private sealed class FakePin(FakeRuntime runtime) : ICpuPackagePowerDiagnosticPin
    {
        public bool Ok => runtime.PinOk;
        public void Dispose() => runtime.PinDisposed = true;
    }
    private sealed class FakeLease(FakeRuntime runtime) : ICpuPackagePowerDiagnosticReadLease
    {
        public CpuPackagePowerDiagnosticSource Source => new("fake", "fake existing", "Sandboxed", "SandboxOnly", "test-lease", "test");
        public bool IsCurrent => !Disposed;
        public bool Disposed;
        public List<uint> Reads { get; } = [];
        public Action<uint>? OnRead;
        private int _energyReads;
        public int FailEnergyNumber;
        public ulong? EnergyOverride;
        public ulong? Read(uint msr)
        {
            Assert.False(Disposed); Assert.False(runtime.PinDisposed); Reads.Add(msr); OnRead?.Invoke(msr);
            if (msr == 0x606) return 0xA0E03;
            _energyReads++;
            return _energyReads == FailEnergyNumber ? null : EnergyOverride ?? (ulong)((_energyReads - 1) * 8192);
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeBackend : IKernelBackend
    {
        public string Id => "winring0";
        public string DisplayName => "test existing backend";
        public KernelTrustTier Tier => KernelTrustTier.Universal;
        public KernelCapability Capabilities => KernelCapability.MsrRead;
        public string? UnavailableReason => null;
        public int Closes;
        public Action? OnRead;
        public ulong? ReadMsr(uint index) { OnRead?.Invoke(); return 0xA0E03; }
        public KernelWriteOutcome WriteMsr(uint index, ulong value, string caller) => throw new Exception("writes forbidden");
        public uint? ReadPciConfig(byte bus, byte device, byte function, uint register) => throw new Exception("PCI forbidden");
        public KernelWriteOutcome WritePciConfig(byte bus, byte device, byte function, uint register, uint value, string caller) => throw new Exception("writes forbidden");
        public byte? ReadIoPortByte(uint port) => throw new Exception("ports forbidden");
        public KernelWriteOutcome WriteIoPortByte(uint port, byte value, string caller) => throw new Exception("writes forbidden");
        public byte[]? ReadMemoryBlock(ulong address, int length) => throw new Exception("MMIO forbidden");
        public void Dispose() => Interlocked.Increment(ref Closes);
    }
}
