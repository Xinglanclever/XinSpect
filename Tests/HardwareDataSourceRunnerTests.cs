using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class HardwareDataSourceRunnerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private static HardwareDataSourceDescriptor Descriptor(
        params HardwareSourceFactDescriptor[] facts) => new(
        "test.synthetic", "1.0.0", "test.synthetic", facts,
        HardwareDataSourcePermission.StandardUser, TimeSpan.FromSeconds(1), true);

    private static HardwareFact Fact(string key, string value, double? numeric = null) => new(
        key, "Test", key, value, "", "test.synthetic@1.0.0", FactTrustLevel.Measured,
        false, At, numeric);

    [Fact]
    public async Task SuccessfulSourcePreservesFactsAndDescriptor()
    {
        var source = new FakeSource(Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count")),
            (_, _) => Task.FromResult<IReadOnlyList<HardwareFact>>([Fact("cpu.cores", "16", 16)]));

        HardwareDataSourceRunResult result = await HardwareDataSourceRunner.RunAsync(source);

        Assert.Equal(HardwareDataSourceRunStatus.Completed, result.Status);
        Assert.Equal("test.synthetic", result.Source.SourceId);
        Assert.Equal("1.0.0", result.Source.Version);
        Assert.Equal("test.synthetic", result.Source.DataDomain);
        var fact = Assert.Single(result.Facts);
        Assert.Equal("cpu.cores", fact.Key);
        Assert.Equal(16, fact.NumericValue);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task MissingDeclaredFactBecomesUnknownRatherThanZero()
    {
        var source = new FakeSource(Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count")),
            (_, _) => Task.FromResult<IReadOnlyList<HardwareFact>>([]));

        HardwareDataSourceRunResult result = await HardwareDataSourceRunner.RunAsync(source);

        var fact = Assert.Single(result.Facts);
        Assert.Equal(HardwareDataSourceRunStatus.Completed, result.Status);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Equal("未回報", fact.Value);
        Assert.Contains("未回報", fact.UnavailableReason, StringComparison.Ordinal);

        HardwareSnapshot snapshot = HardwareSnapshotService.Create("test", [fact], includeSensitive: false);
        var snapshotFact = Assert.Single(snapshot.Facts);
        Assert.Equal(FactAvailability.Unknown, snapshotFact.Availability);
        Assert.Equal("未回報", snapshotFact.Value);
    }

    [Fact]
    public async Task AdapterExceptionMapsToReadErrorWithoutLeakingMessage()
    {
        var source = new FakeSource(Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count")),
            (_, _) => Task.FromException<IReadOnlyList<HardwareFact>>(
                new IOException("private device path")));

        HardwareDataSourceRunResult result = await HardwareDataSourceRunner.RunAsync(source);

        Assert.Equal(HardwareDataSourceRunStatus.ReadError, result.Status);
        Assert.Equal("IOException", result.FailureCode);
        var fact = Assert.Single(result.Facts);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.DoesNotContain("private device path", fact.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutBoundsSynchronousAdapterStartupAndReturnsPerFactReadError()
    {
        var descriptor = Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count"))
            with { Timeout = TimeSpan.FromMilliseconds(30), SupportsCancellation = false };
        var source = new FakeSource(descriptor, (_, _) =>
        {
            Thread.Sleep(250);
            return Task.FromResult<IReadOnlyList<HardwareFact>>([]);
        });
        var timer = Stopwatch.StartNew();

        HardwareDataSourceRunResult result = await HardwareDataSourceRunner.RunAsync(source);

        Assert.Equal(HardwareDataSourceRunStatus.TimedOut, result.Status);
        Assert.Equal("Timeout", result.FailureCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.All(result.Facts, fact => Assert.Equal(FactAvailability.ReadError, fact.Availability));
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBecomingReadError()
    {
        var source = new FakeSource(Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count"))
            with { Timeout = TimeSpan.FromMinutes(1) },
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Array.Empty<HardwareFact>();
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await HardwareDataSourceRunner.RunAsync(source, cancellation.Token));
    }

    [Fact]
    public async Task CallerCancellationInterruptsWaitEvenWhenSourceCannotCancel()
    {
        var source = new FakeSource(Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count"))
            with { Timeout = TimeSpan.FromMilliseconds(500), SupportsCancellation = false },
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                return Array.Empty<HardwareFact>();
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await HardwareDataSourceRunner.RunAsync(source, cancellation.Token));
    }

    [Fact]
    public async Task DescriptorMetadataOverridesSourceMetadataAndCannotDowngradeSensitivity()
    {
        var source = new FakeSource(Descriptor(
            new HardwareSourceFactDescriptor("device.serial", "Device", "Serial number", "", Sensitive: true)),
            (_, _) => Task.FromResult<IReadOnlyList<HardwareFact>>([
                new HardwareFact("device.serial", "Wrong category", "Wrong name", "serial-123", "wrong unit",
                    "untrusted-source", FactTrustLevel.Reported, false, At)
            ]));

        HardwareDataSourceRunResult result = await HardwareDataSourceRunner.RunAsync(source);

        var fact = Assert.Single(result.Facts);
        Assert.Equal("Device", fact.Category);
        Assert.Equal("Serial number", fact.Name);
        Assert.Equal("", fact.Unit);
        Assert.Equal("test.synthetic@1.0.0", fact.Source);
        Assert.True(fact.Sensitive);
        Assert.Equal("serial-123", fact.Value);
    }

    [Fact]
    public async Task ConflictingValuesRemainSeparatePerSource()
    {
        var factDescriptor = new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores", "count");
        var left = new FakeSource(Descriptor(factDescriptor),
            (_, _) => Task.FromResult<IReadOnlyList<HardwareFact>>([Fact("cpu.cores", "8", 8)]));
        var right = new FakeSource(Descriptor(factDescriptor) with { SourceId = "test.other", Version = "2.0.0" },
            (_, _) => Task.FromResult<IReadOnlyList<HardwareFact>>([Fact("cpu.cores", "16", 16)]));

        HardwareDataSourceRunResult[] results = await Task.WhenAll(
            HardwareDataSourceRunner.RunAsync(left), HardwareDataSourceRunner.RunAsync(right));

        Assert.Equal(2, results.Length);
        Assert.Equal(new[] { "8", "16" }, results.Select(result => Assert.Single(result.Facts).Value));
        Assert.Equal(new[] { "test.synthetic", "test.other" }, results.Select(result => result.Source.SourceId));
    }

    [Fact]
    public async Task DuplicateDescriptorKeysAreRejectedBeforeCollection()
    {
        bool called = false;
        var duplicate = Descriptor(
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores"),
            new HardwareSourceFactDescriptor("cpu.cores", "CPU", "Logical cores again"));
        var source = new FakeSource(duplicate, (_, _) =>
        {
            called = true;
            return Task.FromResult<IReadOnlyList<HardwareFact>>([]);
        });

        await Assert.ThrowsAsync<ArgumentException>(() => HardwareDataSourceRunner.RunAsync(source));
        Assert.False(called);
    }

    private sealed class FakeSource(
        HardwareDataSourceDescriptor descriptor,
        Func<DateTimeOffset, CancellationToken, Task<IReadOnlyList<HardwareFact>>> collect)
        : IHardwareDataSource
    {
        public HardwareDataSourceDescriptor Descriptor { get; } = descriptor;

        public Task<IReadOnlyList<HardwareFact>> CollectAsync(
            DateTimeOffset measuredAtUtc, CancellationToken cancellationToken)
            => collect(measuredAtUtc, cancellationToken);
    }
}
