using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>記憶體整理分頁：自持 MemoryService，即時顯示真實記憶體分佈與可釋放的待命快取，並執行各項清理。</summary>
public partial class MemoryCleanView : UserControl
{
    private readonly MemoryService _svc = new();
    private readonly DispatcherTimer _timer;
    private MemStats _stats = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private bool _busy;

    public MemoryCleanView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => { if (!_busy) Refresh(); };
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        BarHost.SizeChanged += (_, _) => UpdateBar();
    }

    private void Refresh()
    {
        _stats = _svc.ReadStats();
        UsedText.Text = $"{_stats.UsedGB:0.00} GB";
        StandbyText.Text = $"{_stats.StandbyGB:0.00} GB";
        AvailText.Text = $"{_stats.AvailGB:0.00} GB";
        TotalText.Text = $"{_stats.TotalGB:0.00} GB";
        LoadText.Text = $"負載 {_stats.LoadPercent}%";
        CommitText.Text = $"認可 {_stats.CommitUsedGB:0.0} / {_stats.CommitLimitGB:0.0} GB";
        UpdateBar();
    }

    // 依真實比例排出「使用中｜待命｜可用」三段（可用＝透明，露出底色軌道）。
    private void UpdateBar()
    {
        double w = BarHost.ActualWidth;
        if (w <= 0 || _stats.TotalGB <= 0) return;
        double used = _stats.UsedGB, standby = Math.Min(_stats.StandbyGB, used);
        double usedNoStandby = Math.Max(0, used - standby);
        UsedSeg.Width = w * usedNoStandby / _stats.TotalGB;
        StandbySeg.Width = w * standby / _stats.TotalGB;
        FreeSeg.Width = Math.Max(0, w - UsedSeg.Width - StandbySeg.Width);
    }

    private async void RunOp(MemOp op)
    {
        if (_busy) return;
        _busy = true;
        ResultText.Text = "整理中…";
        try
        {
            var (_, msg) = await Task.Run(() => _svc.Run(op));
            ResultText.Text = msg;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void Deep_Click(object sender, RoutedEventArgs e) => RunOp(MemOp.DeepClean);
    private void PurgeStandby_Click(object sender, RoutedEventArgs e) => RunOp(MemOp.PurgeStandby);
    private void EmptyWs_Click(object sender, RoutedEventArgs e) => RunOp(MemOp.EmptyWorkingSets);
    private void PurgeLow_Click(object sender, RoutedEventArgs e) => RunOp(MemOp.PurgeLowPriorityStandby);
    private void Flush_Click(object sender, RoutedEventArgs e) => RunOp(MemOp.FlushModified);
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
