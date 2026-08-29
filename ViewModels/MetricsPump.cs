using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 每秒脈動：唯一的即時資料心跳。輪詢感測器 → 發佈繫結 → 推入走勢 → 交付記錄／警示／健康／超頻。
/// </summary>
/// <remarks>
/// 設計要點：
/// <list type="bullet">
/// <item>感測輪詢（<see cref="SensorService.Poll"/>，會觸碰 Ring0 驅動、可能耗時）丟到執行緒集區，
/// <see cref="SensorService.Publish"/> 則留在 UI 執行緒上發出屬性變更，繫結才安全。</item>
/// <item><c>_ticking</c> 重入旗標：若某一拍因驅動阻塞而超過間隔，下一拍直接跳過而非堆積。</item>
/// <item>間隔取自設定並即時反應變更（使用者於設定頁調整後不需重啟）。</item>
/// <item>整個拍體以 try/catch 包覆：任一模組拋出都不得中斷心跳。</item>
/// </list>
/// </remarks>
internal sealed class MetricsPump
{
    private readonly MainViewModel _vm;
    private DispatcherTimer? _timer;
    private bool _ticking;          // 重入防護：上一拍未結束則跳過本拍
    private long _tick;             // 拍數（部分較慢的工作每 N 拍才做一次）
    private bool _rankGpuDone;      // 天梯榜顯示卡高亮只需標一次

    public MetricsPump(MainViewModel vm) => _vm = vm;

    /// <summary>建立並啟動計時器，並掛上設定變更以即時套用新間隔。</summary>
    public void Start()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = IntervalFromSettings() };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // 更新間隔於設定頁變更後立即套用
        _vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.UpdateIntervalSec) && _timer is not null)
                _timer.Interval = IntervalFromSettings();
        };
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private TimeSpan IntervalFromSettings()
        => TimeSpan.FromSeconds(Math.Clamp(_vm.Settings.UpdateIntervalSec, 1, 10));

    // 一拍：感測 → 發佈 → 走勢 → 各模組取樣。
    private async Task TickAsync()
    {
        if (_ticking) return;       // 上一拍尚未結束（驅動阻塞），跳過以免堆積
        _ticking = true;
        try
        {
            var live = _vm.Live;
            if (live is not null)
            {
                // 輪詢可能觸碰 Ring0 驅動而較慢：置於背景；發佈屬性變更回到 UI 執行緒
                await Task.Run(live.Poll);
                live.Publish();

                _vm.CoreLoads.Refresh();
                PushHistories(live);

                if (_vm.Stress.IsRunning) _vm.Stress.Sample(live.CpuTemp, live.CpuClock, live.CpuLoad);

                // 跑分期間一併記下實機條件：一個分數若不附帶當時的溫度與頻率，就無從判斷它是在冷機
                // 還是在溫度牆下量到的——這也是「同一台機器兩次跑分不一樣」最常見的原因
                if (_vm.Bench.IsRunning) _vm.Bench.Conditions.Sample(live.CpuTemp, live.CpuClock);
                if (_vm.Chess.IsRunning) _vm.Chess.Conditions.Sample(live.CpuTemp, live.CpuClock);
                if (_vm.SuperPi.IsRunning) _vm.SuperPi.Conditions.Sample(live.CpuTemp, live.CpuClock);

                // 天梯榜：顯示卡名稱須待感測引擎列出裝置後才有，故於首拍補標一次
                if (!_rankGpuDone)
                {
                    var gpuName = live.PrimaryGpu?.Name;
                    if (!string.IsNullOrWhiteSpace(gpuName))
                    {
                        _rankGpuDone = true;
                        try { _vm.Ranking.Highlight(null, gpuName); } catch { /* 天梯高亮為附加功能 */ }
                    }
                }

                try { _vm.SensorLog.Sample(live, _vm.Settings); } catch { /* 記錄失敗不影響心跳 */ }
                try { _vm.Alerts.Check(live, _vm.Settings); } catch { /* 警示失敗不影響心跳 */ }
                try { _vm.History.Sample(live); } catch { /* 歷史取樣失敗不影響心跳 */ }
                try { _vm.Events.Check(live); } catch { /* 事件偵測失敗不影響心跳 */ }
                try { _vm.FanCurves.Tick(live); } catch { /* 風扇曲線寫入失敗不影響心跳 */ }
            }

            _vm.Net?.Refresh();

            // 磁碟容量變動慢：每 5 拍刷新一次即足夠
            if (_tick % 5 == 0) _vm.Volumes.Refresh();

            _vm.Health.Update(live, _vm.Volumes);

            if (live is not null)
            {
                await _vm.Overclock.TickAsync(live);
                _vm.GpuOc.Tick();
            }

            _tick++;
            _vm.UpdateClock();
        }
        catch { /* 任一模組異常都不得中斷心跳，下一拍再試 */ }
        finally { _ticking = false; }
    }

    // 推入七條即時走勢緩衝（供 HistoryGraph 繪製）。
    // 沒讀到的項目傳 null，不以 0 充數——否則沒有溫度感測器或無獨顯的機器會看到「0 °C」的假讀值。
    private void PushHistories(SensorService live)
    {
        var g = live.PrimaryGpu;
        _vm.CpuLoadHist.Push(live.CpuLoad);
        _vm.CpuTempHist.Push(live.CpuTemp);
        _vm.MemHist.Push(live.MemLoad);
        _vm.GpuHist.Push(g?.LoadPercent);
        _vm.GpuTempHist.Push(g?.TempC);
        _vm.GpuVramHist.Push(g?.VramUsedMB);
        _vm.CpuClockHist.Push(live.CpuClock);
    }
}
