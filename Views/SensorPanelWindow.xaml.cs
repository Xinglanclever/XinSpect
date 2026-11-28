using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>Configurable desktop panel. Consumes existing observations; opens no hardware or game process.</summary>
public partial class SensorPanelWindow : Window
{
    private readonly Func<SensorFrame> _readFrame;
    private readonly SensorPanelLayoutStore _store;
    private readonly SensorPanelRuntime _runtime = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TileVisual> _visuals = new(StringComparer.Ordinal);
    private SensorPanelLayout _layout = new();
    private string? _selectedId;
    private string _catalogKey = "";
    private bool _loaded, _closed, _dirty;
    private SensorService? _live;

    private sealed record SensorChoice(SensorReading Reading)
    {
        public string Label => Reading.Label + (Reading.Unit.Length == 0 ? "" : " (" + Reading.Unit + ")");
        public string Identity => Reading.Id;
        public string Details => Reading.DeviceId + "\n" + Reading.Id + "\n" + Reading.Source
            + " · " + Reading.Availability + " · " + Reading.Trust;
    }
    private sealed class TileVisual
    {
        public required Border Border;
        public required Thumb Header;
        public required TextBlock Title, Value, Status, Details, Range;
        public required ProgressBar Gauge;
        public required Canvas Plot;
        public SensorPanelTileState? State;
    }

    public SensorPanelWindow(SensorService live) : this(() => live.CurrentSensorFrame)
    {
        ArgumentNullException.ThrowIfNull(live);
        _live = live;
        live.PropertyChanged += Live_Changed;
    }

    /// <summary>Injected frame/store entry point for hardware-free UI tests and offline observation previews.</summary>
    public SensorPanelWindow(Func<SensorFrame> readFrame, SensorPanelLayoutStore? store = null)
    {
        _readFrame = readFrame ?? throw new ArgumentNullException(nameof(readFrame));
        _store = store ?? new SensorPanelLayoutStore();
        InitializeComponent();
        // These slots combine already-localized UI text with raw identifiers, units or diagnostics.
        LanguageService.SetIsRawText(SensorPicker, true);
        LanguageService.SetIsRawText(SelectedLabel, true);
        LanguageService.SetIsRawText(CatalogStatus, true);
        LanguageService.SetIsRawText(StatusText, true);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshFrame();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        ThemeService.Changed += Theme_Changed;
        LanguageService.Changed += Language_Changed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try { _layout = _store.LoadLocal(); }
        catch (Exception ex) when (IsLayoutError(ex)) { SetStatus("無法讀取本機版面，暫用空白版面：", ex.Message); }
        RebuildTiles();
        RefreshFrame();
        _timer.Start();
        Language_Changed();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_loaded || !_dirty) return;
        try { _store.SaveLocal(_layout); _dirty = false; }
        catch (Exception ex) when (IsLayoutError(ex))
        {
            // Keep edits reviewable: a failed save must not silently close and lose the layout.
            var answer = MessageBox.Show(this, LanguageService.T("本機版面儲存失敗：") + ex.Message
                + "\n" + LanguageService.T("仍要關閉並放棄本次版面變更嗎？可取消後改用匯出。"),
                LanguageService.T("自訂感測面板"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        if (_live is not null) _live.PropertyChanged -= Live_Changed;
        ThemeService.Changed -= Theme_Changed;
        LanguageService.Changed -= Language_Changed;
    }
    private void Live_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SensorService.CurrentSensorFrame) || !_loaded || _closed) return;
        // Refresh uses the latest whole frame on the UI dispatcher; the timer also expires idle data.
        if (Dispatcher.CheckAccess()) RefreshFrame();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_closed) RefreshFrame(); }));
    }
    private void Theme_Changed()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Theme_Changed)); return; }
        foreach (var visual in _visuals.Values) DrawHistory(visual);
    }
    private void Language_Changed()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Language_Changed)); return; }
        if (_closed) return;
        LanguageService.ConvertVisualTree(this, LanguageService.IsSimplified);
        if (!_loaded) return;
        foreach (var visual in _visuals.Values)
            if (visual.State is { } state)
            {
                SetHeaderText(visual.Header, state.Tile.Kind);
                UpdateVisual(visual, state);
            }
        UpdateCatalog(_readFrame(), force: true);
        SelectTile(_selectedId);
        RefreshStatus();
    }

    private static bool IsLayoutError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException;
    private string _statusOriginal = "關閉時自動儲存本機版面。匯出只含版面與感測器身份，不含歷史讀數。";
    private string? _statusDiagnostic;
    private void SetStatus(string text)
    {
        _statusOriginal = text;
        _statusDiagnostic = null;
        RefreshStatus();
    }
    private void SetStatus(string prefix, string diagnostic)
    {
        _statusOriginal = prefix;
        _statusDiagnostic = diagnostic;
        RefreshStatus();
    }
    private void RefreshStatus() => StatusText.Text = KnownText(_statusOriginal)
        + (_statusDiagnostic is null ? "" : KnownText(_statusDiagnostic));
    // Unknown provider diagnostics are evidence, so only registered product sentences/templates translate.
    private static string KnownText(string text)
        => UiTextTranslation.KnownDisplayText(text, LanguageService.IsEnglish, LanguageService.IsSimplified);
    private static string StatusTextFor(SensorPanelTileState state)
    {
        string[] parts = state.Status.Split(" · ", 3);
        if (parts.Length < 3) return KnownText(state.Status);
        string reason = state.Reading?.Source.StartsWith("LibreHardwareMonitor ", StringComparison.Ordinal) == true
            ? ProductReasonText(parts[2]) : KnownText(parts[2]);
        return parts[0] + " · " + parts[1] + " · " + reason;
    }
    private static readonly string[] ProductReasonSuffixes =
    [
        " LHM CPU 功率缺少當拍讀取成功與來源更新時間證據；全域 RAPL 響應驗證未綁定此感測器。保留上報值供診斷，不納入統計／CSV 數值／告警。",
        " LHM PM table 可能保留前值；來源未提供成功更新時間，不能確認是目前控制器時脈。",
        " 來源識別碼不可讀；識別僅限本次工作階段。",
        " 來源識別碼重複；不能確定裝置，不供數值消費。",
    ];
    private static string ProductReasonText(string reason)
    {
        var translatedSuffixes = new Stack<string>();
        for (int layer = 0; layer < ProductReasonSuffixes.Length; layer++)
        {
            string? suffix = ProductReasonSuffixes.FirstOrDefault(part => reason.EndsWith(part, StringComparison.Ordinal));
            if (suffix is null) break;
            translatedSuffixes.Push(" " + LanguageService.T(suffix[1..]));
            reason = reason[..^suffix.Length];
        }
        return KnownText(reason) + string.Concat(translatedSuffixes);
    }
    private Brush Brush(string key) => (Brush)FindResource(key);
    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static string KindName(SensorPanelTileKind kind) => kind switch
    {
        SensorPanelTileKind.Gauge => LanguageService.T("儀表"),
        SensorPanelTileKind.History => LanguageService.T("歷史折線"), _ => LanguageService.T("數字"),
    };

    private void RefreshFrame()
    {
        if (_closed) return;
        var frame = _readFrame();
        var states = _runtime.Update(_layout, frame, Stopwatch.GetTimestamp());
        foreach (var state in states)
            if (_visuals.TryGetValue(state.Tile.TileId, out var visual)) UpdateVisual(visual, state);
        UpdateCatalog(frame);
    }

    private void UpdateCatalog(SensorFrame frame, bool force = false)
    {
        string key = string.Join("\n", frame.Readings.Select(r => r.Id + "|" + r.DeviceId + "|" + r.Name
            + "|" + r.Unit + "|" + r.DeviceName + "|" + r.Availability + "|" + r.Trust));
        if (!force && key == _catalogKey) return;
        _catalogKey = key;
        var selected = (SensorPicker.SelectedItem as SensorChoice)?.Reading;
        string query = SensorSearch.Text.Trim();
        // A duplicate stable ID is not safe to bind even when its translated labels differ.
        var choices = frame.Readings.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .Select(g => new SensorChoice(g.Single()))
            .Where(c => query.Length == 0 || c.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Identity.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Label, StringComparer.CurrentCulture).ToArray();
        SensorPicker.ItemsSource = choices;
        SensorPicker.SelectedItem = choices.FirstOrDefault(c => c.Reading.Id == selected?.Id
            && c.Reading.DeviceId == selected?.DeviceId) ?? choices.FirstOrDefault();
        CatalogStatus.Text = string.Format(CultureInfo.CurrentCulture,
            LanguageService.T("{0} 個項目 · 已加入 {1}/32"), choices.Length, _layout.Tiles.Count);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _closed) return;
        UpdateCatalog(_readFrame(), force: true);
    }

    private void RebuildTiles()
    {
        Board.Children.Clear();
        _visuals.Clear();
        Board.Width = _layout.CanvasWidth; Board.Height = _layout.CanvasHeight;
        Topmost = _layout.Topmost; TopmostOption.IsChecked = _layout.Topmost;
        foreach (var tile in _layout.Tiles)
        {
            var visual = CreateTile(tile);
            _visuals[tile.TileId] = visual;
            Board.Children.Add(visual.Border);
            ApplyGeometry(tile);
        }
        EmptyHint.Visibility = _layout.Tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        string? selected = _layout.Tiles.Any(t => t.TileId == _selectedId) ? _selectedId : null;
        _selectedId = null;
        SelectTile(selected);
    }

    private TextBlock Text(double size = 12, string resource = "SecondaryInkBrush")
    {
        var text = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        LanguageService.SetIsRawText(text, true);
        text.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return text;
    }
    private static void SetHeaderText(Thumb header, SensorPanelTileKind kind)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetValue(LanguageService.IsRawTextProperty, true);
        factory.SetValue(TextBlock.TextProperty, string.Format(CultureInfo.CurrentCulture,
            LanguageService.T("{0} · 移動 / 縮放"), KindName(kind)));
        factory.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryInkBrush");
        factory.SetValue(TextBlock.FontSizeProperty, 12d);
        factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        header.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
    }
    private TileVisual CreateTile(SensorPanelTile tile)
    {
        var border = new Border { Padding = new Thickness(10), BorderThickness = new Thickness(2) };
        border.SetResourceReference(Border.CornerRadiusProperty, "Radius.Card");
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        var grid = new Grid();
        foreach (var height in new[] { 28d, 36d, 34d }) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 30 });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(23) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        var header = new Thumb { Cursor = Cursors.SizeAll, Focusable = true,
            ToolTip = LanguageService.T("拖曳移動；方向鍵移動；Shift＋方向鍵縮放") };
        SetHeaderText(header, tile.Kind);
        header.GotKeyboardFocus += (_, _) => SelectTile(tile.TileId);
        header.PreviewMouseLeftButtonDown += (_, _) => SelectTile(tile.TileId);
        header.DragDelta += (_, e) => ChangeGeometry(tile.TileId, e.HorizontalChange, e.VerticalChange, 0, 0);
        header.PreviewKeyDown += (_, e) => Header_KeyDown(tile.TileId, e);
        AutomationProperties.SetName(header, string.Format(CultureInfo.CurrentCulture,
            LanguageService.T("{0}磁貼標頭；方向鍵移動，Shift 方向鍵縮放"), KindName(tile.Kind)));
        grid.Children.Add(header);
        var title = Text(12, "PrimaryInkBrush"); title.TextWrapping = TextWrapping.Wrap;
        title.MaxHeight = 36; Grid.SetRow(title, 1); grid.Children.Add(title);
        var value = Text(25, "PrimaryInkBrush"); value.FontWeight = FontWeights.SemiBold;
        Grid.SetRow(value, 2); grid.Children.Add(value);
        var chartGrid = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        Grid.SetRow(chartGrid, 3); grid.Children.Add(chartGrid);
        var gauge = new ProgressBar { Minimum = 0, Maximum = 1, Height = 12,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        gauge.SetResourceReference(ForegroundProperty, "AccentBrush");
        gauge.SetResourceReference(BackgroundProperty, "HairlineBrush"); chartGrid.Children.Add(gauge);
        var plot = new Canvas { ClipToBounds = true, Visibility = Visibility.Collapsed };
        chartGrid.Children.Add(plot);
        var range = Text(11); Grid.SetRow(range, 4); grid.Children.Add(range);
        var bottom = new Grid(); bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        Grid.SetRow(bottom, 5); grid.Children.Add(bottom);
        var status = Text(11); bottom.Children.Add(status);
        var resize = new Thumb { Width = 20, Height = 20, Cursor = Cursors.SizeNWSE, Focusable = false,
            ToolTip = LanguageService.T("拖曳縮放磁貼；亦可使用左側縮小／放大按鈕") };
        var resizeFactory = new FrameworkElementFactory(typeof(TextBlock));
        resizeFactory.SetValue(TextBlock.TextProperty, "↘");
        resizeFactory.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryInkBrush");
        resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = resizeFactory };
        resize.DragDelta += (_, e) => { SelectTile(tile.TileId); ChangeGeometry(tile.TileId, 0, 0, e.HorizontalChange, e.VerticalChange); };
        Grid.SetColumn(resize, 1); bottom.Children.Add(resize);
        border.Child = grid;
        border.PreviewMouseLeftButtonDown += (_, _) => SelectTile(tile.TileId);
        var details = Text(12); details.TextWrapping = TextWrapping.Wrap; details.MaxHeight = 52;
        chartGrid.Children.Add(details);
        var visual = new TileVisual { Border = border, Header = header, Title = title, Value = value,
            Status = status, Details = details, Range = range, Gauge = gauge, Plot = plot };
        plot.SizeChanged += (_, _) => DrawHistory(visual);
        return visual;
    }

    private void UpdateVisual(TileVisual visual, SensorPanelTileState state)
    {
        visual.State = state;
        var tile = state.Tile;
        var reading = state.Reading;
        visual.Title.Text = reading?.Label ?? tile.SensorId;
        visual.Title.ToolTip = tile.DeviceId + "\n" + tile.SensorId + "\n" + reading?.Label;
        visual.Value.Text = state.Value is double value ? Format(value) + " " + tile.Unit : "—";
        visual.Status.Text = StatusTextFor(state);
        visual.Status.ToolTip = visual.Status.Text;
        AutomationProperties.SetName(visual.Header, string.Format(CultureInfo.CurrentCulture,
            LanguageService.T("{0}磁貼標頭；方向鍵移動，Shift 方向鍵縮放"), KindName(tile.Kind)));
        AutomationProperties.SetName(visual.Border, visual.Title.Text + " · " + visual.Value.Text + " · " + visual.Status.Text);
        string provenance = reading is null ? LanguageService.T("尚未取得此感測器")
            : reading.Source + " · " + reading.Trust + "\n"
                + LanguageService.T("本程式讀取：") + reading.ReadAtUtc.ToLocalTime().ToString("HH:mm:ss") + "\n"
                + LanguageService.T("來源更新：")
                + (reading.SourceUpdatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? LanguageService.T("未知"));
        visual.Value.ToolTip = provenance;
        visual.Details.Text = provenance;
        visual.Details.Visibility = tile.Kind == SensorPanelTileKind.Numeric ? Visibility.Visible : Visibility.Collapsed;
        visual.Gauge.Visibility = tile.Kind == SensorPanelTileKind.Gauge && state.Value is not null ? Visibility.Visible : Visibility.Collapsed;
        visual.Gauge.Value = state.GaugeFraction ?? 0; // Hidden on unavailable data; zero never becomes a displayed observation.
        AutomationProperties.SetName(visual.Gauge, visual.Title.Text + " " + visual.Value.Text);
        visual.Plot.Visibility = tile.Kind == SensorPanelTileKind.History ? Visibility.Visible : Visibility.Collapsed;
        bool outside = state.Value is double current && (current < tile.ScaleMinimum || current > tile.ScaleMaximum);
        visual.Range.Text = tile.Kind == SensorPanelTileKind.Numeric ? LanguageService.T("來源詳情請指向數值")
            : Format(tile.ScaleMinimum) + " — " + Format(tile.ScaleMaximum) + " " + tile.Unit
                + LanguageService.T(outside ? " · 超出刻度" : tile.Kind == SensorPanelTileKind.History ? " · 缺值斷線" : "");
        visual.Range.ToolTip = tile.Kind == SensorPanelTileKind.History
            ? LanguageService.T("最多保留最近 120 筆本次工作階段資料；橫軸為採集順序，縱軸為所設刻度。缺值與超出刻度的樣本留空。") : provenance;
        DrawHistory(visual);
    }

    private void DrawHistory(TileVisual visual)
    {
        visual.Plot.Children.Clear();
        if (visual.State is not { Tile.Kind: SensorPanelTileKind.History } state
            || visual.Plot.ActualWidth < 2 || visual.Plot.ActualHeight < 2) return;
        double width = visual.Plot.ActualWidth, height = visual.Plot.ActualHeight;
        var points = state.History;
        var tile = state.Tile;
        for (int i = 0; i < 3; i++)
        {
            double y = 2 + (height - 4) * i / 2;
            visual.Plot.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = Brush("HairlineBrush"), StrokeThickness = 1 });
        }
        // Never connect across null/unusable samples, or flatten out-of-scale values into a false line.
        Polyline? segment = null;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Value is not double value || value < tile.ScaleMinimum || value > tile.ScaleMaximum)
            { segment = null; continue; }
            var point = new Point(points.Count < 2 ? width / 2 : 3 + (width - 6) * i / (points.Count - 1),
                2 + (height - 4) * (1 - (value - tile.ScaleMinimum) / (tile.ScaleMaximum - tile.ScaleMinimum)));
            if (segment is null)
            {
                segment = new Polyline { Stroke = Brush("AccentBrush"), StrokeThickness = 2 };
                visual.Plot.Children.Add(segment);
                var dot = new Ellipse { Width = 4, Height = 4, Fill = Brush("AccentBrush") };
                Canvas.SetLeft(dot, point.X - 2); Canvas.SetTop(dot, point.Y - 2); visual.Plot.Children.Add(dot);
            }
            segment.Points.Add(point);
        }
        AutomationProperties.SetName(visual.Plot, LanguageService.T($"{points.Count} 筆歷史樣本，缺值以斷線表示"));
    }

    private SensorPanelTile? Selected => _layout.Tiles.FirstOrDefault(t => t.TileId == _selectedId);
    private void SelectTile(string? id)
    {
        bool changed = _selectedId != id;
        _selectedId = id;
        foreach (var pair in _visuals)
            pair.Value.Border.SetResourceReference(Border.BorderBrushProperty, pair.Key == id ? "AccentBrush" : "HairlineBrush");
        var tile = Selected;
        TileEditor.IsEnabled = tile is not null;
        SelectedLabel.Text = tile is null ? LanguageService.T("先選取一個磁貼")
            : KindName(tile.Kind) + " · " + tile.SensorId;
        if (tile is not null && (changed || ScaleMin.Text.Length == 0))
        {
            ScaleMin.Text = tile.ScaleMinimum.ToString(CultureInfo.CurrentCulture);
            ScaleMax.Text = tile.ScaleMaximum.ToString(CultureInfo.CurrentCulture);
        }
    }
    private void ApplyGeometry(SensorPanelTile tile)
    {
        if (!_visuals.TryGetValue(tile.TileId, out var visual)) return;
        visual.Border.Width = tile.Width; visual.Border.Height = tile.Height;
        Canvas.SetLeft(visual.Border, tile.X); Canvas.SetTop(visual.Border, tile.Y);
    }
    private void ReplaceTile(SensorPanelTile tile)
    {
        _layout = _layout with { Tiles = _layout.Tiles.Select(t => t.TileId == tile.TileId ? tile : t).ToArray() };
        _dirty = true;
    }
    private void ChangeGeometry(string id, double dx, double dy, double dw, double dh)
    {
        var tile = _layout.Tiles.FirstOrDefault(t => t.TileId == id);
        if (tile is null) return;
        tile = SensorPanelLayoutService.Constrain(_layout, tile, tile.X + dx, tile.Y + dy, tile.Width + dw, tile.Height + dh);
        ReplaceTile(tile); ApplyGeometry(tile);
    }
    private void Header_KeyDown(string id, KeyEventArgs e)
    {
        double dx = e.Key == Key.Left ? -16 : e.Key == Key.Right ? 16 : 0;
        double dy = e.Key == Key.Up ? -16 : e.Key == Key.Down ? 16 : 0;
        if (dx == 0 && dy == 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ChangeGeometry(id, 0, 0, dx, dy);
        else ChangeGeometry(id, dx, dy, 0, 0);
        e.Handled = true;
    }
    private void Geometry_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } tile || sender is not Button { Tag: string action }) return;
        ChangeGeometry(tile.TileId, action == "left" ? -16 : action == "right" ? 16 : 0,
            action == "up" ? -16 : action == "down" ? 16 : 0,
            action == "smaller" ? -16 : action == "larger" ? 16 : 0,
            action == "smaller" ? -16 : action == "larger" ? 16 : 0);
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (SensorPicker.SelectedItem is not SensorChoice choice) { SetStatus("請先選取感測器。"); return; }
        var reading = choice.Reading;
        var kind = (SensorPanelTileKind)Math.Max(0, AddKind.SelectedIndex);
        if (_layout.Tiles.Count >= SensorPanelLayoutService.MaximumTiles) { SetStatus("每份版面最多 32 個磁貼。"); return; }
        if (_layout.Tiles.Any(t => t.SensorId == reading.Id && t.DeviceId == reading.DeviceId && t.Kind == kind))
        { SetStatus("此感測器的相同顯示類型已在面板上。"); return; }
        int index = _layout.Tiles.Count;
        int columns = Math.Max(1, (int)((_layout.CanvasWidth - 16) / 316));
        var tile = new SensorPanelTile { TileId = Guid.NewGuid().ToString("D"), SensorId = reading.Id,
            DeviceId = reading.DeviceId, Unit = reading.Unit, Kind = kind, X = 16 + index % columns * 316, Y = 16 + index / columns * 256,
            Width = 300, Height = 240, ScaleMinimum = 0, ScaleMaximum = 100 };
        var canvas = _layout with { CanvasHeight = Math.Min(SensorPanelLayoutService.MaximumCanvasHeight,
            Math.Max(_layout.CanvasHeight, tile.Y + tile.Height + 16)) };
        tile = SensorPanelLayoutService.Constrain(canvas, tile, tile.X, tile.Y, tile.Width, tile.Height);
        var candidate = canvas with { Tiles = _layout.Tiles.Append(tile).ToArray() };
        try { SensorPanelLayoutService.Validate(candidate); }
        catch (InvalidDataException ex) { SetStatus(ex.Message); return; }
        _layout = candidate; _selectedId = tile.TileId; _dirty = true;
        RebuildTiles(); SelectTile(tile.TileId); RefreshFrame(); UpdateCatalog(_readFrame(), true);
        _visuals[tile.TileId].Border.BringIntoView(); _visuals[tile.TileId].Header.Focus();
        SetStatus("已加入磁貼；儀表與歷史圖預設刻度為 0–100，請依單位調整。");
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } tile) return;
        _layout = _layout with { Tiles = _layout.Tiles.Where(t => t.TileId != tile.TileId).ToArray() };
        _dirty = true;
        _selectedId = null; RebuildTiles(); RefreshFrame(); UpdateCatalog(_readFrame(), true);
        SetStatus("已移除磁貼。");
    }
    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } tile) return;
        if (!double.TryParse(ScaleMin.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double min)
            || !double.TryParse(ScaleMax.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double max))
        { SetStatus("請輸入有效的刻度數字。"); return; }
        tile = tile with { ScaleMinimum = min, ScaleMaximum = max };
        var candidate = _layout with { Tiles = _layout.Tiles.Select(t => t.TileId == tile.TileId ? tile : t).ToArray() };
        try { SensorPanelLayoutService.Validate(candidate); }
        catch (InvalidDataException ex) { SetStatus(ex.Message); return; }
        _layout = candidate; _dirty = true; RefreshFrame(); SetStatus("已套用刻度。超出刻度的原始數值仍如實顯示。");
    }
    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostOption.IsChecked == true; _layout = _layout with { Topmost = Topmost }; _dirty = true;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { _store.SaveLocal(_layout); _dirty = false; SetStatus("本機版面已儲存。"); }
        catch (Exception ex) when (IsLayoutError(ex)) { SetStatus("儲存失敗：", ex.Message); }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = LanguageService.T("匯入感測面板版面"),
            Filter = "JSON (*.json)|*.json", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var candidate = SensorPanelLayoutStore.Read(dialog.FileName);
            _layout = candidate; _selectedId = null; _dirty = true; RebuildTiles(); RefreshFrame(); UpdateCatalog(_readFrame(), true);
            SetStatus("版面已匯入；不存在的感測器保留缺少狀態，關閉時儲存本機。");
        }
        catch (Exception ex) when (IsLayoutError(ex)) { SetStatus("匯入失敗，原版面保留：", ex.Message); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = LanguageService.T("匯出感測面板版面（含感測器身份）"),
            Filter = "JSON (*.json)|*.json", FileName = "sensor-panel.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { SensorPanelLayoutStore.Write(dialog.FileName, _layout); SetStatus("版面已匯出（僅版面與感測器身份，無讀數、命令或程式路徑）。"); }
        catch (Exception ex) when (IsLayoutError(ex)) { SetStatus("匯出失敗：", ex.Message); }
    }
}
