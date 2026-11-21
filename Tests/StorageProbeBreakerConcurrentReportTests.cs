using Xunit;

namespace XinSpect.Tests;

/// <summary>Deterministic completion schedules; no native probe is started.</summary>
public sealed class StorageProbeBreakerConcurrentReportTests
{
    [Fact]
    public void EarlierAdmittedSuccessMustNotEraseConcurrentFailure()
    {
        const int disk = 8791011;
        const string operation = "audit-concurrent-success";
        const long now = 1000;
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, operation, now));
        StorageProbeBreaker.Trip(disk, operation, now + 1);
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, operation, now + 2));

        // A's success contains no evidence that another admitted probe B's fault recovered.
        Assert.True(StorageProbeBreaker.Report(disk, operation, read: true, nowMs: now + 3));
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, operation, now + 4));
    }

    [Fact]
    public void FailedReportStillTripsOnlyItsDiskAndOperation()
    {
        const int disk = 8791012;
        const string operation = "audit-report-failure";
        Assert.False(StorageProbeBreaker.Report(disk, operation, read: false, nowMs: 1000));
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, operation, 1001));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, operation + "-other", 1001));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk + 100, operation, 1001));
    }

    [Fact]
    public void SuccessfulUntrippedReadStillReturnsTrueWithoutIntroducingFault()
    {
        const int disk = 8791013;
        const string operation = "audit-report-success";
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, operation, 1000));
        Assert.True(StorageProbeBreaker.Report(disk, operation, read: true, nowMs: 1001));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, operation, 1002));
    }

    [Fact]
    public void ExpiredFaultStillAllowsSuccessfulRetryAtTheProductionAdmissionBoundary()
    {
        const int disk = 8791014;
        const string operation = "audit-report-expired";
        const long first = 1000;
        long expiry = first + StorageProbeBreaker.CooldownMs;
        StorageProbeBreaker.Trip(disk, operation, first);
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, operation, expiry - 1));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, operation, expiry));
        Assert.True(StorageProbeBreaker.Report(disk, operation, read: true, nowMs: expiry + 1));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, operation, expiry + 2));
    }
}