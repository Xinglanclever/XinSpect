using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 感測器儀表板頁（U10-VZ-0057，追蹤鍵 <c>ui.dashboard.sensors</c>）：既有感測事實的即時監看。
/// </summary>
/// <remarks>
/// <para>
/// <b>本頁不做任何新的讀取。</b>四個區塊全部搬既有資料：逐核溫度搬
/// <see cref="CoreTempMapService"/> 每秒已在更新的格點、電壓搬 <see cref="SensorService"/>
/// 已發佈的感測列、控制圖搬 <see cref="HistoryStore"/> 已記錄的歷史、日曆搬
/// <see cref="EventsService"/> 已收集的事件。計時器只在頁面可見時跑（IPageLifecycle），
/// 而且只做「搬運＋重畫」，不觸發任何硬體查詢。
/// </para>
/// </remarks>
public partial class SensorDashboardView : UserControl, IPageLifecycle
{
    /// <summary>核×時間熱圖保留的時間欄數（近 N 拍；每 5 秒一拍＝近 5 分鐘）。</summary>
    public const int CoreColumns = 60;

    private const string VoltageTypeText = "電壓";   // 與 SensorService 對 SensorType.Voltage 的標示同源

    private DispatcherTimer? _timer;
    private readonly SensorDashboardService.CoreHeatBuffer _coreHeat = new(CoreColumns);

    public SensorDashboardView()
    {
        InitializeComponent();
        Calendar.Month = DateTime.Today;   // 初值：本月（資料仍在 OnActivated 彙整）
        // 初選放在 InitializeComponent 之後：寫在 XAML 的 IsSelected 會在 Spc／History 等
        // 具名控制項還沒建立時就觸發 SelectionChanged，於是 OnActivated 以外的那條路就 NRE。
        MetricCombo.SelectedIndex = 0;
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    public void OnActivated()
    {
        RefreshAll();
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _timer.Tick += (_, _) => RefreshAll();
        }
        _timer.Start();
    }

    public void OnDeactivated() => _timer?.Stop();

    private void MetricCombo_Changed(object sender, SelectionChangedEventArgs e) => RefreshSpc();

    // ── 彙整：全部是搬運，沒有讀取 ──────────────────────────────────────────

    private void RefreshAll()
    {
        var vm = Vm;
        if (vm is null) return;

        RefreshCoreHeat(vm);
        RefreshRails(vm);
        RefreshSpc();
        RefreshCalendar(vm);

        StatusLine.Text =
            "資料來源：逐核溫度←核心溫度地圖、電壓←感測總表、控制圖←歷史倉、日曆←事件收集"
            + " ・ 本頁不新增讀取，收起時不占取樣成本";
    }

    private void RefreshCoreHeat(MainViewModel vm)
    {
        var cores = vm.CoreTempMap?.Cores;
        if (cores is not { Count: > 0 }) return;      // 核心溫度地圖尚未就緒：熱圖保持「沒有樣本」
        _coreHeat.Push(cores.Select(c => (c.Label, c.TempC)));
        var rows = _coreHeat.Rows
            .Select(r => new CoreTimeHeatmapMath.CoreTimeHeatmapRow(r.Label, r.Values))
            .ToArray();
        CoreHeat.Rows = new CoreTimeHeatmapChart.HeatRows(rows);
    }

    private void RefreshRails(MainViewModel vm)
    {
        var rows = (vm.Live?.AllSensors ?? [])
            .Where(s => s.TypeText == VoltageTypeText)
            .Select(s => (s.Name, SensorDashboardService.ParseVolts(s.ValueText)));
        var rails = SensorDashboardService.CollectRails(rows);

        Rails.ItemsSource = rails.Select(r => new RailRow(r.Name, r.Volts, r.Nominal)).ToArray();
        RailsEmpty.Visibility = rails.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshSpc()
    {
        var vm = Vm;
        // Spc 可能是 null：XAML 事件在建構期間（具名欄位尚未全部指派）就可能被觸發。
        // 這個防護不是裝飾——2026-10-26 真的因此在正式版裡整頁建不起來。
        if (vm is null || Spc is null || MetricCombo?.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag || !int.TryParse(tag, out int metric)) return;

        // 歷史倉的既有記錄：最近 6 小時、分鐘級。指標整段沒有讀值（本機無此感測器）時
        // MetricPoints 回空陣列，控制圖畫「沒有樣本」——不把 0 串成量測。
        var nowUtc = DateTime.UtcNow;
        var series = vm.History.Query(nowUtc - TimeSpan.FromHours(6), nowUtc);
        var points = SensorDashboardService.MetricPoints(series, metric);

        Spc.SeriesTitle = HistoryMetrics.Titles[metric];
        Spc.Points = points.Length > 0 ? new ControlChart.SpcSeries(points) : null;
    }

    private void RefreshCalendar(MainViewModel vm)
    {
        var today = DateTime.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        var last = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        var events = vm.Events.All.Select(ev => (ev.Time, ev.Severity));
        var counts = SensorDashboardService.DailyAnomalies(events, first, last);

        Calendar.Month = today;
        Calendar.Days = new CalendarHeatmapChart.CalendarDays(
            counts.Select(c => new SensorDashboardService.DailyCount(c.Day, c.Count)).ToArray());
    }

    /// <summary>子彈圖一列的繫結列（把服務的 RailReading 包成帶 DP 形狀的列）。</summary>
    public sealed class RailRow
    {
        public RailRow(string name, double? volts, double nominal)
        {
            Name = name;
            Volts = volts;
            Nominal = nominal;
            Bands = new BulletChart.BulletBands(SensorDashboardService.AtxBands(nominal));
        }

        public string Name { get; }
        public double? Volts { get; }
        public double Nominal { get; }
        public BulletChart.BulletBands Bands { get; }
    }
}
