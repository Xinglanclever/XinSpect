using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 核心熱區圖：一顆實體核心一格。底色＝該核溫度（沿用 <see cref="HeatScale"/>，與逐核液柱同一份色階），
/// 格內大字是溫度、角落是核編號、底部一條是使用率；下方是統計列與色階圖例。
/// </summary>
/// <remarks>
/// <para>
/// <b>格數由實體核心數決定，不是由「感測器回了幾筆」決定。</b>這張圖要回答的是
/// 「這台機器的每一顆核心現在幾度」，所以 <see cref="PhysicalCores"/> 決定了總共幾格：
/// 感測器少回幾顆時，缺的位置照樣佔一格並顯示「—」，因為把缺的格子拿掉看起來會像
/// 「這顆處理器只有 8 核」。反過來，讀值比拓樸多時<b>只補不裁</b>——寧可多畫幾格並在統計列
/// 說明白，也不把真實讀值吃掉。
/// </para>
/// <para>
/// <b>為什麼樣板在 XAML、數量在程式碼：</b>樣板只認得 <see cref="CoreHeatCell"/> 這個契約，
/// 它不可能知道這台機器有幾顆實體核心。舊版把兩件事都交給樣板，於是「有資料時才套用的樣板」
/// 從來沒被驗過，繫結路徑寫錯也一路綠燈（正確的路徑只有兩條：<c>Cores</c> 集合與
/// <see cref="CoreHeatCell"/> 的成員，兩者都由測試釘住）。
/// </para>
/// <para>
/// 顏色一律由 <see cref="HeatScale"/> 決定：同一個溫度在熱區圖、逐核液柱與圖例一定是同一個顏色，
/// 不隨主題或強調色改——色溫對照是資料語意，換了主題就失效的圖例等於沒有圖例。
/// </para>
/// </remarks>
public partial class CoreHeatmap : UserControl
{
    /// <summary>量不到寬度時的預設格寬（px）。</summary>
    private const double DefaultTile = 68;

    /// <summary>格寬上下限：太小讀不到字，太大變成稀疏的色塊牆。</summary>
    private const double MinTile = 42;
    private const double MaxTile = 78;

    /// <summary>低於這個格寬就進精簡模式（收掉角落編號、縮小溫度字）。</summary>
    private const double CompactTile = 50;

    /// <summary>基板內距加外框的大約用量，算可用寬度時先扣掉。</summary>
    private const double PlateInset = 44;

    public static readonly DependencyProperty CoresProperty = DependencyProperty.Register(
        nameof(Cores), typeof(IEnumerable), typeof(CoreHeatmap),
        new PropertyMetadata(null, OnCoresChanged));

    /// <summary>逐核資料來源；項目的形狀必須是 <see cref="ICoreHeatCell"/>（<see cref="CoreRow"/>／<see cref="CoreTempEntry"/> 都是）。</summary>
    public IEnumerable? Cores
    {
        get => (IEnumerable?)GetValue(CoresProperty);
        set => SetValue(CoresProperty, value);
    }

    public static readonly DependencyProperty PhysicalCoresProperty = DependencyProperty.Register(
        nameof(PhysicalCores), typeof(int), typeof(CoreHeatmap),
        new PropertyMetadata(0, OnCoresChanged));

    /// <summary>這台機器的實體核心數（0＝未知，就只畫來源給的格數）。補格子的依據。</summary>
    public int PhysicalCores
    {
        get => (int)GetValue(PhysicalCoresProperty);
        set => SetValue(PhysicalCoresProperty, value);
    }

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CoreHeatmap),
        new PropertyMetadata(1, null, CoerceColumns));

    /// <summary>一列幾格（由核心數推導，見 <see cref="CoreHeatLayout.Columns"/>）。</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        private set => SetValue(ColumnsProperty, value);
    }

    private static object CoerceColumns(DependencyObject d, object value)
        => Math.Max(1, (int)value);

    public static readonly DependencyProperty BoardWidthProperty = DependencyProperty.Register(
        nameof(BoardWidth), typeof(double), typeof(CoreHeatmap), new PropertyMetadata(0d));

    /// <summary>基板上核區的寬（＝欄數 × 格寬）。</summary>
    public double BoardWidth
    {
        get => (double)GetValue(BoardWidthProperty);
        private set => SetValue(BoardWidthProperty, value);
    }

    public static readonly DependencyProperty BoardHeightProperty = DependencyProperty.Register(
        nameof(BoardHeight), typeof(double), typeof(CoreHeatmap), new PropertyMetadata(0d));

    /// <summary>基板上核區的高（＝列數 × 格寬，力求方格）。</summary>
    public double BoardHeight
    {
        get => (double)GetValue(BoardHeightProperty);
        private set => SetValue(BoardHeightProperty, value);
    }

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(CoreHeatmap), new PropertyMetadata(false));

    /// <summary>格子變小到放不下角落編號時為 true（樣板據此收掉次要文字）。</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        private set => SetValue(CompactProperty, value);
    }

    public static readonly DependencyProperty SummaryTextProperty = DependencyProperty.Register(
        nameof(SummaryText), typeof(string), typeof(CoreHeatmap), new PropertyMetadata(""));

    /// <summary>統計列：幾顆核心、最熱／最冷／平均、幾格有讀值。</summary>
    public string SummaryText
    {
        get => (string)GetValue(SummaryTextProperty);
        private set => SetValue(SummaryTextProperty, value);
    }

    public static readonly DependencyProperty HasCellsProperty = DependencyProperty.Register(
        nameof(HasCells), typeof(bool), typeof(CoreHeatmap), new PropertyMetadata(false));

    /// <summary>有格子可畫嗎（沒有時顯示「這個平台沒有逐核感測器」而不是一張空板）。</summary>
    public bool HasCells
    {
        get => (bool)GetValue(HasCellsProperty);
        private set => SetValue(HasCellsProperty, value);
    }

    /// <summary>畫面上的格子（每個位置一格，含沒有讀值的位置）。</summary>
    public ObservableCollection<CoreHeatCell> Cells { get; } = new();

    public CoreHeatmap()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RefreshGeometry();
    }

    private static void OnCoresChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (CoreHeatmap)d;
        if (e.Property == CoresProperty)
        {
            map.DetachSource(e.OldValue as IEnumerable);
            map.AttachSource(e.NewValue as IEnumerable);
        }
        map.Rebuild();
    }

    private void AttachSource(IEnumerable? source)
    {
        if (source is INotifyCollectionChanged n) n.CollectionChanged += OnSourceCollectionChanged;
    }

    private void DetachSource(IEnumerable? source)
    {
        if (source is INotifyCollectionChanged n) n.CollectionChanged -= OnSourceCollectionChanged;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>重建格子（來源集合換了／拓樸核心數變了才會走這裡；每秒的溫度更新走通知，不重建）。</summary>
    private void Rebuild()
    {
        foreach (var cell in Cells)
        {
            cell.PropertyChanged -= OnCellChanged;
            cell.Dispose();   // 解掉對來源項目的訂閱，否則重複重建會累積一群沒人看的接線
        }
        Cells.Clear();

        var items = new List<object?>();
        if (Cores is not null)
            foreach (var item in Cores) items.Add(item);

        // 只補不裁：格數取「讀值筆數」與「拓樸實體核心數」的較大者。
        int count = Math.Max(items.Count, PhysicalCores);
        for (int i = 0; i < count; i++)
        {
            var cell = new CoreHeatCell(i, i < items.Count ? items[i] as ICoreHeatCell : null);
            cell.PropertyChanged += OnCellChanged;
            Cells.Add(cell);
        }

        Columns = CoreHeatLayout.Columns(Math.Max(count, 1));
        HasCells = count > 0;
        UpdateSummary();
        RefreshGeometry();
    }

    private void OnCellChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只有溫度會改變統計列（最熱／最冷／平均），其餘通知不必重算整條
        if (e.PropertyName == nameof(CoreHeatCell.TempC)) UpdateSummary();
    }

    /// <summary>依可用寬度決定格寬，讓每一格盡量接近正方形，基板因此維持「一塊晶片」的形狀。</summary>
    private void RefreshGeometry()
    {
        if (Cells.Count == 0 || Columns <= 0)
        {
            BoardWidth = 0;
            BoardHeight = 0;
            Compact = false;
            return;
        }

        int rows = Math.Max(1, CoreHeatLayout.Rows(Cells.Count, Columns));
        double available = ActualWidth > 0 ? ActualWidth : Columns * DefaultTile + PlateInset;
        double tile = Math.Clamp((available - PlateInset) / Columns, MinTile, MaxTile);

        BoardWidth = Columns * tile;
        BoardHeight = rows * tile;
        Compact = tile < CompactTile;
    }

    /// <summary>
    /// 統計列：把「這張圖畫了幾格、其中幾格有讀值」講清楚。
    /// 沒有讀值時如實說沒有，不寫「0 °C」——0 度是合法溫度，不能拿來當「不知道」。
    /// </summary>
    /// <remarks>
    /// 分段用「 ・ 」拼、整串走 <see cref="LanguageService.TComposite"/>：這是程式拼出來的句子，
    /// 整串查翻譯表一定查不到；分段之後每段的前綴（「最熱核心」「讀值」…）查得到，
    /// 後面的數字原樣接回——否則英語模式下這一行會整句中文。
    /// </remarks>
    private void UpdateSummary()
    {
        int total = Cells.Count;
        if (total == 0)
        {
            SummaryText = "";
            return;
        }

        var readings = Cells.Where(c => c.TempC.HasValue).ToList();
        var parts = new List<string>
        {
            PhysicalCores > 0 ? $"實體核心 ×{PhysicalCores}" : $"核心 ×{total}",
        };

        if (readings.Count == 0)
        {
            parts.Add("沒有任何逐核溫度讀值（平台沒有逐核感測器，或感測器還沒回報）");
        }
        else
        {
            var hottest = readings.MaxBy(c => c.TempC!.Value)!;
            var coldest = readings.MinBy(c => c.TempC!.Value)!;
            parts.Add($"最熱核心 {hottest.ShortName} {hottest.TempC:0} °C");
            parts.Add($"最冷核心 {coldest.ShortName} {coldest.TempC:0} °C");
            parts.Add($"平均 {readings.Average(c => c.TempC!.Value):0} °C");
            parts.Add($"讀值 {readings.Count}/{total} 格");
        }

        // 讀值比拓樸多：多畫的格子要說明白是怎麼回事，不能讓它看起來像「這台有 36 顆實體核心」
        if (PhysicalCores > 0 && total > PhysicalCores)
            parts.Add("讀值格數多於拓樸實體核心數（逐核讀值可能以邏輯處理器計）");

        SummaryText = LanguageService.TComposite(string.Join(" ・ ", parts));
    }
}
