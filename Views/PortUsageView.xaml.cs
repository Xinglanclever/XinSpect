using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 連接埠占用分頁：自持一個 PortUsageService（與主 ViewModel 無關），
/// 首次載入時抓取連線表，並提供搜尋／只顯示監聽／自動更新／結束占用行程。
/// </summary>
public partial class PortUsageView : UserControl
{
    private readonly PortUsageService _svc = new();
    private ICollectionView? _view;
    private readonly DispatcherTimer _timer;
    private bool _loaded;
    private bool _refreshing;
    private PortRow? _pendingKill;          // 兩段式確認：待確認的目標列
    private DispatcherTimer? _pendingKillTimer;  // 5 秒未再點即解除
    private Button? _pendingKillButton;     // 〔修復 v3.20〕待確認中的按鈕（解除時要還原文字）
    private object? _pendingKillContent;    // 〔修復 v3.20〕按鈕的原 Content（Content 是 object）

    public PortUsageView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await ReloadAsync();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            _loaded = true;
            _view = CollectionViewSource.GetDefaultView(_svc.Rows);
            _view.Filter = RowFilter;
            Grid.ItemsSource = _view;
            _svc.PropertyChanged += Svc_PropertyChanged;
            _ = ReloadAsync();
        }
        // 重入時若「自動更新」仍勾選，重啟被 Unloaded 停掉的計時器——否則 UI 顯示「開」但實際已死。
        if (AutoRefresh.IsChecked == true) _timer.Start();
    }

    private void Svc_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PortUsageService.Status))
            Dispatcher.Invoke(() => StatusText.Text = _svc.Status);
    }

    // 背景採集、回 UI 套用：Rows 是 ObservableCollection，Apply 必須落在 UI 執行緒。
    // _refreshing 防重入：採集慢於 3 秒拍時，丟棄該拍而不是排隊疊加（原同步版天然串行，async 版需自守）。
    private async Task ReloadAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var rows = await Task.Run(_svc.Collect);
            _svc.Apply(rows);
            _view?.Refresh();
        }
        catch (Exception ex)
        {
            _svc.Apply([]);
            _svc.ReportError(ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // 搜尋（埠／行程／位址）＋ 只顯示監聽埠。
    private bool RowFilter(object o)
    {
        if (o is not PortRow r) return false;
        if (ListenOnly.IsChecked == true && r.State != "監聽中") return false;
        string q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return r.LocalPort.ToString().Contains(q)
            || r.RemotePort.ToString().Contains(q)
            || r.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.LocalAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.RemoteAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Protocol.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();
    private void Filter_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    // 結束占用連接埠的行程；兩段式確認避免誤殺（零彈窗：不用 XMsg 對話框）。
    // Kill＋WaitForExit(3000) 在背景執行——殺頑固行程最長 3 秒的等待不凍結視窗。
    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PortRow row }) return;
        if (row.Pid <= 0)
        {
            _svc.ReportStatus("此連線由系統核心持有，無法結束。");
            return;
        }

        // 第一次點：進入待確認態；再點同一列：執行。點別列：換目標重新計時。
        if (_pendingKill is null || _pendingKill.Pid != row.Pid)
        {
            ArmKillConfirm(row, (Button)sender);
            return;
        }
        DisarmKillConfirm();

        if (sender is Button trigger) trigger.IsEnabled = false;
        try
        {
            var (success, message) = await Task.Run(() => _svc.KillProcess(row.Pid));
            _svc.ReportStatus(message);            // 結果走狀態列（零彈窗）
            if (success) await ReloadAsync();
        }
        finally
        {
            if (sender is Button trigger2) trigger2.IsEnabled = true;
        }
    }

    private void ArmKillConfirm(PortRow row, Button button)
    {
        // 〔修復 v3.20〕先還原上一顆待確認按鈕的文字再武裝新的：自動更新關閉時沒有 Reload 會重建列，
        // 舊版換目標只改不還原，上一顆會永遠停在「再點一次確認結束」。
        if (_pendingKillButton is not null) _pendingKillButton.Content = _pendingKillContent;
        _pendingKill = row;
        _pendingKillButton = button;
        _pendingKillContent = button.Content;
        button.Content = "再點一次確認結束";
        _pendingKillTimer ??= CreatePendingKillTimer();
        _pendingKillTimer.Stop();
        _pendingKillTimer.Start();
    }

    private DispatcherTimer CreatePendingKillTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) => DisarmKillConfirm();
        return t;
    }

    private void DisarmKillConfirm()
    {
        _pendingKillTimer?.Stop();
        _pendingKill = null;
        // 〔修復 v3.20〕逐按鈕回寫原文字：原註解假設「下一次 ReloadAsync／Apply 重建列會自然還原」，
        // 但自動更新關閉時沒有下一次 Reload——5 秒逾時解除後按鈕會永遠停在「再點一次確認結束」。
        if (_pendingKillButton is not null) _pendingKillButton.Content = _pendingKillContent;
        _pendingKillButton = null;
        _pendingKillContent = null;
    }
}
