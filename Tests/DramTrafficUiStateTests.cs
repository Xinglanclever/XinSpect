using Xunit;

namespace XinSpect.Tests;

public class DramTrafficUiStateTests
{
    [Fact]
    public void InitialStateDisclosesNativeUnsupportedWithoutCounts()
    {
        var service = new DramTrafficService();
        Assert.Contains("原生量測未支援", service.Status);
        Assert.Contains("所有權尚未驗證", service.Status);
        Assert.False(service.IsBusy);
        AssertEmpty(service);
    }

    [Fact]
    public void BeginningCapabilityCheckClearsEarlierReadingsWithoutClaimingWorkload()
    {
        var service = WithEarlierReadings();
        service.BeginMeasurement();
        Assert.True(service.IsBusy);
        Assert.Contains("正在檢查", service.Status);
        Assert.Contains("尚未執行硬體編程或負載", service.Status);
        AssertEmpty(service);
    }

    [Fact]
    public void FaultedTaskPublishesReasonClearsValuesAndEndsBusyState()
    {
        var service = WithEarlierReadings();
        service.BeginMeasurement();
        service.CompleteMeasurement(Task.FromException<DramTrafficService.Result>(
            new InvalidOperationException("injected capability failure")));
        Assert.Contains("失敗", service.Status);
        Assert.Contains("InvalidOperationException", service.Status);
        Assert.Contains("injected capability failure", service.Status);
        Assert.False(service.IsBusy);
        AssertEmpty(service);
    }

    [Fact]
    public void CanceledTaskPublishesCancellationClearsValuesAndEndsBusyState()
    {
        var service = WithEarlierReadings();
        service.BeginMeasurement();
        service.CompleteMeasurement(Task.FromCanceled<DramTrafficService.Result>(new CancellationToken(true)));
        Assert.Contains("已取消", service.Status);
        Assert.False(service.IsBusy);
        AssertEmpty(service);
    }

    [Fact]
    public void CompletedUnsupportedTaskPublishesItsReasonAndEndsBusyState()
    {
        var service = WithEarlierReadings();
        service.BeginMeasurement();
        service.CompleteMeasurement(Task.FromResult(new DramTrafficService.Result(
            "原生量測未支援：injected ownership unavailable", "沒有量到計數", Severity.Neutral,
            "—", "—", "—", "—")));
        Assert.Contains("injected ownership unavailable", service.Status);
        Assert.False(service.IsBusy);
        AssertEmpty(service);
    }

    private static DramTrafficService WithEarlierReadings()
    {
        var service = new DramTrafficService();
        service.CompleteMeasurement(Task.FromResult(new DramTrafficService.Result(
            "earlier synthetic result", "synthetic only", Severity.Neutral, "10", "20", "50%", "64")));
        Assert.Equal("10", service.MissText);
        return service;
    }

    private static void AssertEmpty(DramTrafficService service)
    {
        Assert.Equal("—", service.MissText);
        Assert.Equal("—", service.ReferenceText);
        Assert.Equal("—", service.HitText);
        Assert.Equal("—", service.TrafficText);
        Assert.Equal(Severity.Neutral, service.Severity);
    }
}
