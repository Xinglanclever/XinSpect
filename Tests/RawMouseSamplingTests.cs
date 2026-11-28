using Xunit;

namespace XinSpect.Tests;

/// <summary>合成單調時間與原生 API 注入回歸；不模擬為真實 USB／硬體驗收。</summary>
public sealed class RawMouseSamplingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(125)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(4000)]
    [InlineData(8000)]
    public void KnownCadencesUseQpcIntervalsWithoutFrameRate(int hz)
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        for (int i = 0; i < 41; i++) sampler.Add(1, "mouse", "滑鼠", true, i * (1_000_000 / hz), 1, 0, 0);
        var device = Assert.Single(sampler.Snapshot(400_000).Devices);
        Assert.Equal(41, device.Reports);
        Assert.Equal(40, device.Samples);
        Assert.Equal(hz, device.ObservedHz, 6);
        Assert.Equal(1_000_000.0 / hz, device.P95IntervalUs, 6);
        Assert.Equal(0, device.JitterMadUs);
    }

    [Fact]
    public void InterleavedDevicesNeverCreateCrossDeviceIntervals()
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        for (int i = 0; i < 40; i++)
        {
            sampler.Add(1, "one", "一", true, i * 1000, 1, 0, 0);
            sampler.Add(2, "two", "二", true, i * 2000 + 100, 1, 0, 0);
        }
        var devices = sampler.Snapshot(100_000).Devices;
        Assert.Equal(2, devices.Count);
        Assert.Equal(1000, devices.Single(d => d.DeviceId == "one").ObservedHz);
        Assert.Equal(500, devices.Single(d => d.DeviceId == "two").ObservedHz);
    }

    [Fact]
    public void P95IsNearestRankAndJitterIsUnscaledMedianAbsoluteDeviation()
    {
        var result = RawMouseSampling.Distribution(Enumerable.Range(1, 20).Select(i => (double)i).ToArray());
        Assert.Equal(10.5, result.Median);
        Assert.Equal(19, result.P95);
        Assert.Equal(5, result.Mad);
    }

    [Fact]
    public void IdleAndNonIncreasingTimestampsAreExcludedAndDoNotMoveTimeBack()
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        foreach (long stamp in new long[] { 0, 1000, 1000, 500, 300_000, 301_000 })
            sampler.Add(1, "one", "一", true, stamp, 1, 0, 0);
        var device = Assert.Single(sampler.Snapshot(400_000).Devices);
        Assert.Equal(2, device.Samples);
        Assert.Equal(1, device.IdleGaps);
        Assert.Equal(2, device.InvalidTimestamps);
        Assert.Equal(1000, device.MedianIntervalUs);
    }

    [Fact]
    public void ButtonOnlyAndAbsolutePacketsDoNotPolluteMovementCadence()
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        sampler.Add(1, "one", "一", true, 0, 1, 0, 8);
        sampler.Add(1, "one", "一", true, 100, 0, 0, 0);
        sampler.Add(1, "one", "一", true, 200, 15, 20, 1);
        sampler.Add(1, "one", "一", true, 1000, -1, 0, 8);
        var device = Assert.Single(sampler.Snapshot(2000).Devices);
        Assert.Equal(4, device.Reports);
        Assert.Equal(2, device.MovementReports);
        Assert.Equal(2, device.ExcludedReports);
        Assert.Equal(2, device.NoCoalesceReports);
        Assert.Equal(1000, device.MedianIntervalUs);
    }

    [Fact]
    public void RemovalNeverBridgesAReusedHandle()
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        sampler.Add(1, "one", "一", true, 0, 1, 0, 0);
        sampler.Remove(1);
        sampler.Add(1, "replacement", "二", true, 1000, 1, 0, 0);
        var device = Assert.Single(sampler.Snapshot(2000).Devices);
        Assert.True(device.Removed);
        Assert.Equal(1, device.Reports);
        Assert.Equal(0, device.Samples);
    }

    [Fact]
    public void DeviceAndSampleBudgetsAreBoundedAndTruncationIsVisible()
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        for (int device = 1; device <= 100; device++) sampler.Add(device, device.ToString(), "滑鼠", true, 0, 1, 0, 0);
        for (int i = 1; i <= RawMouseSampling.MaximumIntervalsPerDevice + 10; i++)
            sampler.Add(1, "1", "滑鼠", true, i, 1, 0, 0);
        var snapshot = sampler.Snapshot(100_000);
        Assert.Equal(RawMouseSampling.MaximumDevices, snapshot.Devices.Count);
        Assert.Equal(RawMouseSampling.MaximumDevices, snapshot.IgnoredDevices);
        Assert.Equal(RawMouseSampling.MaximumIntervalsPerDevice, snapshot.Devices[0].Samples);
        Assert.True(snapshot.Devices[0].CapacityReached);
    }

    [Fact]
    public void DeadlineAndStopRejectLateReports()
    {
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        sampler.Add(1, "one", "一", true, 100, 1, 0, 0);
        sampler.Add(1, "one", "一", true, 10_000_000, 1, 0, 0);
        sampler.Add(1, "one", "一", true, 10_001_000, 1, 0, 0);
        var snapshot = sampler.Snapshot(20_000_000);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(10_000, snapshot.WindowMs);
        Assert.Equal(1, Assert.Single(snapshot.Devices).Reports);
        Assert.Equal(At.AddSeconds(10), snapshot.MeasuredAtUtc);
    }

    [Fact]
    public void NoInputIsUnknownRatherThanAbsentHardwareOrZeroHz()
    {
        var store = new RawMouseSampleStore();
        Assert.Equal(FactAvailability.Unknown, store.Read(At).Availability);
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        sampler.Stop(10_000_000);
        store.Publish(sampler.Snapshot(10_000_000), null);
        var fact = MousePollRateFactsService.Collect(At.AddSeconds(10), () => store.Read(At.AddSeconds(10)));
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("不證明沒有滑鼠", fact.UnavailableReason);
    }

    [Fact]
    public void StorePublishesSelectedDeviceAndOriginalTimeThenExpires()
    {
        var store = new RawMouseSampleStore();
        var sampler = new RawMouseSampling(1_000_000, 0, At);
        for (int i = 0; i <= 30; i++)
        {
            sampler.Add(1, "one", "一", true, i * 1000, 1, 0, 0);
            sampler.Add(2, "two", "二", true, i * 2000, 1, 0, 0);
        }
        sampler.Stop(100_000);
        store.Publish(sampler.Snapshot(100_000), "two");
        var fact = MousePollRateFactsService.Collect(At.AddSeconds(1), () => store.Read(At.AddSeconds(1)));
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(500, fact.NumericValue);
        Assert.Equal(At.AddMilliseconds(100), fact.MeasuredAtUtc);
        Assert.Contains("裝置 two", fact.Value);
        Assert.Contains("不是實體 USB 輪詢", fact.Value);
        var expired = MousePollRateFactsService.Collect(At.AddMinutes(6), () => store.Read(At.AddMinutes(6)));
        Assert.Equal(FactAvailability.Unknown, expired.Availability);
        Assert.Null(expired.NumericValue);
        Assert.Equal(fact.MeasuredAtUtc, expired.MeasuredAtUtc);
    }

    [Theory]
    [InlineData(false, false, false, 25)]
    [InlineData(true, true, false, 25)]
    [InlineData(true, false, true, 25)]
    [InlineData(true, false, false, 3)]
    public void IncompleteOrUnidentifiedResultsNeverBecomePresent(bool readable, bool removed, bool truncated, int samples)
    {
        var store = new RawMouseSampleStore();
        var device = new RawMouseDeviceSample("one", "一", readable, 30, 30, samples, 0, 0, 0, 0, 1000, 1000, 0, 1000, truncated, removed);
        store.Publish(new(false, FactAvailability.Present, "停止", At, At.AddSeconds(1), 1000, [device], 0, 0), "one");
        var fact = MousePollRateFactsService.Collect(At.AddSeconds(1), () => store.Read(At.AddSeconds(1)));
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
    }

    [Fact]
    public void StartingOrClearingCannotReusePriorSuccess()
    {
        var store = new RawMouseSampleStore();
        var device = new RawMouseDeviceSample("one", "一", true, 30, 30, 29, 0, 0, 0, 0, 1000, 1000, 0, 1000, false, false);
        var snapshot = new RawMouseSnapshot(false, FactAvailability.Present, "停止", At, At.AddSeconds(1), 1000, [device], 0, 0);
        store.Publish(snapshot, "one");
        Assert.Equal(FactAvailability.Present, store.Read(At.AddSeconds(1)).Availability);
        store.Publish(snapshot with { IsRunning = true }, "one");
        Assert.Equal(FactAvailability.Unknown, store.Read(At.AddSeconds(1)).Availability);
        store.Clear();
        Assert.Equal(0, store.Read(At.AddSeconds(1)).Samples);
    }

    [Fact]
    public void NonFiniteIntervalsAndLabelsNeverProducePresentNumbers()
    {
        var sample = MousePollRateFactsService.Summarize([double.NaN, double.PositiveInfinity, -1, 0, 1000], 500);
        Assert.Equal(1, sample.Samples);
        Assert.Equal(1000, sample.Hz);
        Assert.Equal("未量到", MousePollRateFactsService.NearestRateLabel(double.NaN));
        var fact = MousePollRateFactsService.Collect(At, () => new(20, 1000, double.PositiveInfinity, true, 500));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
    }
}
