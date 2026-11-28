namespace XinSpect;

/// <summary>只保留使用者明確執行的一次、單裝置結果；不在啟動／CLI 同步採集路徑等待輸入。</summary>
internal sealed class RawMouseSampleStore
{
    internal static RawMouseSampleStore Shared { get; } = new();
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    private MousePollRateFactsService.PollSample? _latest;

    internal void Clear(string reason = "尚未取樣；請開啟滑鼠檢測並按「開始取樣」。")
        => Volatile.Write(ref _latest, new(0, 0, 0, false, 0)
        { Availability = FactAvailability.Unknown, Reason = reason });

    internal void Publish(RawMouseSnapshot snapshot, string? deviceId)
    {
        var device = snapshot.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        var availability = snapshot.Availability;
        string? reason = availability == FactAvailability.ReadError ? snapshot.Status : null;
        if (snapshot.IsRunning) { availability = FactAvailability.Unknown; reason = "Raw Input 取樣進行中；尚未發佈結果。"; }
        else if (reason == null && device == null)
        { availability = FactAvailability.Unknown; reason = "取樣窗沒有選定裝置的輸入；無法判定回報率，不是 0 Hz，也不證明沒有滑鼠。"; }
        else if (reason == null && device != null)
        {
            if (!device.IdentityReadable) { availability = FactAvailability.Unknown; reason = "裝置身份未確認（可能是遠端／虛擬輸入）；不作單一實體滑鼠量測。"; }
            else if (device.Removed) { availability = FactAvailability.Unknown; reason = "裝置在取樣期間移除；請重新開始，不把重用 handle 當成同一裝置。"; }
            else if (device.CapacityReached || snapshot.IgnoredDevices > 0)
            { availability = FactAvailability.Unknown; reason = "裝置／樣本超過有界容量；結果不完整，請重新取樣。"; }
            else if (device.InvalidTimestamps > 0)
            { availability = FactAvailability.Unknown; reason = "到達時間含非遞增樣本，無法可靠換算頻率。"; }
            else if (device.Samples < RawMouseSampling.MinimumIntervals)
            { availability = FactAvailability.Unknown; reason = $"有效間隔不足 {RawMouseSampling.MinimumIntervals} 筆；請持續移動後重新取樣。"; }
            else availability = FactAvailability.Present;
        }
        Volatile.Write(ref _latest, new(device?.Samples ?? 0, device?.MedianIntervalUs ?? 0,
            device?.ObservedHz ?? 0, device?.IdentityReadable ?? false, snapshot.WindowMs)
        {
            Availability = availability, Reason = reason, DeviceId = device?.DeviceId,
            P95IntervalUs = device?.P95IntervalUs, JitterMadUs = device?.JitterMadUs,
            MeasuredAtUtc = snapshot.MeasuredAtUtc == default ? null : snapshot.MeasuredAtUtc,
            StartedAtUtc = snapshot.StartedAtUtc == default ? null : snapshot.StartedAtUtc,
            Reports = device?.Reports, IdleGaps = device?.IdleGaps
        });
    }

    internal MousePollRateFactsService.PollSample Read(DateTimeOffset now)
    {
        var sample = Volatile.Read(ref _latest);
        if (sample is null) return new(0, 0, 0, false, 0)
        { Availability = FactAvailability.Unknown, Reason = "尚未取樣；請開啟滑鼠檢測並按「開始取樣」。" };
        if (sample.MeasuredAtUtc is { } measured && (now < measured || now - measured > MaximumAge))
            return sample with { Availability = FactAvailability.Unknown, Reason = "上次 Raw Input 結果已逾 5 分鐘或時鐘已變更；請重新取樣。" };
        return sample;
    }
}
