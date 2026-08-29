using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 歷史回放分頁：把 <see cref="HistoryViewModel"/> 的時間窗與查詢結果交給 <see cref="TimelineChart"/> 繪製。
/// </summary>
/// <remarks>
/// 本頁只在顯示時工作：<see cref="OnActivated"/> 起一支兩秒計時器推進自動跟隨並重查，
/// <see cref="OnDeactivated"/> 立刻停掉——歷史查詢會掃描數萬筆記錄，看不見時不該持續消耗。
/// 縮放／平移／雙擊還原由圖回報事件，一律轉交檢視模型調整時間窗，再由 <c>Changed</c> 回頭重畫。
/// </remarks>
public partial class HistoryView : UserControl, IPageLifecycle
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private HistoryViewModel? _vm;
    private DispatcherTimer? _timer;
    private bool _hooked;

    public HistoryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Chart.ZoomRequested += (factor, anchor) => _vm?.Zoom(factor, anchor);
        Chart.PanRequested += fraction => _vm?.Pan(fraction);
        Chart.ResetRequested += () => _vm?.Reset();
    }

    // 掛上檢視模型（DataContext 於子 Grid，故取自圖表的繼承 DataContext）
    private void Attach()
    {
        if (_hooked) return;
        _vm = Chart.DataContext as HistoryViewModel;
        if (_vm is null) return;

        _hooked = true;
        _vm.Changed += Redraw;
        _vm.Reload();
    }

    private void Redraw()
    {
        if (_vm is null) return;
        Chart.Series = _vm.Series;
        Chart.Active = _vm.Active;
        Chart.Markers = _vm.Markers;
        Chart.FromUtc = _vm.FromUtc;
        Chart.ToUtc = _vm.ToUtc;
        Chart.Render();

        // 滿刻度只有繪製後才確定（取自實際區間最大值），繪完回填圖例
        foreach (var m in _vm.Metrics) m.ScaleText = m.IsOn ? Chart.ScaleText(m.Index) : "";
    }

    public void OnActivated()
    {
        Attach();
        Redraw();
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => _vm?.Tick();
        _timer.Start();
    }

    public void OnDeactivated()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void All_Click(object sender, RoutedEventArgs e) => _vm?.SetAllMetrics(true);
    private void None_Click(object sender, RoutedEventArgs e) => _vm?.SetAllMetrics(false);
    private void Export_Click(object sender, RoutedEventArgs e) => _vm?.ExportCsv();

    /// <summary>由事件時間軸呼叫：跳到某一事件的時刻並重畫。</summary>
    public void JumpTo(DateTime utc)
    {
        Attach();
        _vm?.JumpTo(utc);
    }
}
