using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
namespace XinSpect;

/// <summary>Explicit, on-demand analysis; construction never touches a user directory.</summary>
public partial class DiskSpaceWindow : Window
{
    private readonly DiskSpaceScanService _scanner = new();
    private CancellationTokenSource? _scanCancellation;
    private DiskSpaceScanResult? _result;
    private DiskSpaceNode? _mapNode;
    private DiskSpaceNode? _detailNode;
    private Func<string>? _statusText;
    private bool _closingAfterScan;
    private bool _exporting;
    private bool _closed;
    private static readonly Color[] TileColors = [Color.FromRgb(45, 102, 153), Color.FromRgb(33, 126, 141), Color.FromRgb(76, 105, 153), Color.FromRgb(75, 122, 109), Color.FromRgb(100, 91, 143)];

    public DiskSpaceWindow() : this(null) { }
    public DiskSpaceWindow(string? initialDirectory)
    {
        InitializeComponent();
        DirectoryBox.Text = initialDirectory ?? "";
        // These display projections are separate from the stable, raw report fields.
        ((DataGridTextColumn)TypesGrid.Columns[0]).Binding = new Binding(nameof(DiskSpaceTypeRow.UiExtension));
        ((DataGridTextColumn)TypesGrid.Columns[3]).Binding = new Binding(nameof(DiskSpaceTypeRow.UiAllocatedDisplay));
        ((DataGridTextColumn)IssuesGrid.Columns[2]).Binding = new Binding("UiReason");
        // Detail contains raw paths/system payloads. Its product-owned parts are localized before composition.
        LanguageService.SetIsRawText(DetailBox, true);
        MemoryBox.ToolTip = LanguageService.T(DiskSpaceScanResult.MemoryAccounting);
        LanguageService.Changed += RefreshLanguage;
        Closed += (_, _) => { _closed = true; LanguageService.Changed -= RefreshLanguage; };
        if (LanguageService.IsSimplified) LanguageService.ConvertVisualTree(this, true);
    }
    private void SetStatus(Func<string> text) { _statusText = text; StatusText.Text = text(); }
    private void SetDetail(DiskSpaceNode node) { _detailNode = node; DetailBox.Text = node.Detail; }
    private void RefreshLanguage()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshLanguage)); return; }
        MemoryBox.ToolTip = LanguageService.T(DiskSpaceScanResult.MemoryAccounting);
        if (_statusText is not null) StatusText.Text = _statusText();
        if (_detailNode is not null) DetailBox.Text = _detailNode.Detail;
        TypesGrid.Items.Refresh(); BigFilesGrid.Items.Refresh();
        RefreshTreeDisplay(FolderTree);
        RefreshIssues(); DrawMap();
    }
    private static void RefreshTreeDisplay(DependencyObject target)
    {
        // Re-evaluate only realized display bindings; keep selection, expansion and raw node data intact.
        if (target is TextBlock text) text.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        if (target is FrameworkElement element) element.GetBindingExpression(FrameworkElement.ToolTipProperty)?.UpdateTarget();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(target); i++) RefreshTreeDisplay(VisualTreeHelper.GetChild(target, i));
    }
    private void RefreshIssues()
    {
        if (_result is not { } result) return;
        IssuesGrid.ItemsSource = result.Issues.Select(issue => new
        { issue.Path, Kind = DiskSpaceUiText.Kind(issue.Kind), UiReason = DiskSpaceUiText.Reason(issue.Reason), issue.NativeError }).ToArray();
        IssuesNoteText.Text = DiskSpaceUiText.Format("共 {0:N0} 條，清單保留 {1:N0} 條。\n", result.TotalIssues, result.Issues.Count) + LanguageService.T(DiskSpaceScanResult.Limits);
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = LanguageService.T("選擇要分析的目錄"), Multiselect = false };
        if (dialog.ShowDialog(this) == true) DirectoryBox.Text = dialog.FolderName;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null || _exporting) return;
        if (!int.TryParse(EntriesBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int entries) || entries is < 1 or > 1_000_000 ||
            !int.TryParse(DepthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int depth) || depth is < 0 or > 256 ||
            !int.TryParse(SecondsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int seconds) || seconds is < 1 or > 600 ||
            !int.TryParse(MemoryBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int memoryMiB) || memoryMiB is < 1 or > 512)
        { SetStatus(() => LanguageService.T("預算無效：項目 1–1,000,000；深度 0–256；時間 1–600 秒；模型估算 1–512 MiB。")); return; }
        string root = DirectoryBox.Text.Trim();
        var cancellation = new CancellationTokenSource(); _scanCancellation = cancellation;
        SetBusy(true); _result = null; _mapNode = null; _detailNode = null;
        FolderTree.ItemsSource = null; TypesGrid.ItemsSource = null; BigFilesGrid.ItemsSource = null; IssuesGrid.ItemsSource = null; MapCanvas.Children.Clear();
        SetStatus(() => LanguageService.T("開始唯讀分析…")); DetailBox.Text = ""; CurrentPathText.Text = "";
        IssuesNoteText.Text = LanguageService.T("尚無結果。清單最多保留 2,000 條，總數另列；未遍歷的項目數無法推算。");
        try
        {
            var progress = new Progress<DiskSpaceProgress>(p =>
            {
                if (_scanCancellation != cancellation) return;
                SetStatus(() => DiskSpaceUiText.Format("已讀 {0:N0} 項 · {1:N0} 個檔案 · {2} · {3:F1} 秒 · 模型估算 {4}",
                    p.EntriesVisited, p.Files, DiskSpaceFormatting.Bytes(p.LogicalBytes), p.Elapsed.TotalSeconds, DiskSpaceFormatting.Bytes(p.EstimatedRetainedMetadataBytes)));
                CurrentPathText.Text = p.CurrentPath;
            });
            var result = await _scanner.ScanAsync(root, new() { MaxEntries = entries, MaxDepth = depth, MaxDuration = TimeSpan.FromSeconds(seconds), MaxRetainedMetadataBytes = memoryMiB * 1024L * 1024 }, progress, cancellation.Token);
            _result = result; SetStatus(() => result.Summary);
            FolderTree.ItemsSource = new[] { result.Root };
            TypesGrid.ItemsSource = result.FileTypes; BigFilesGrid.ItemsSource = result.BiggestFiles; RefreshIssues();
            _mapNode = result.Root; SetDetail(_mapNode); DrawMap();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { SetStatus(() => LanguageService.T("分析未完成：") + DiskSpaceUiText.ExceptionMessage(ex)); }
        finally
        {
            _scanCancellation = null; cancellation.Dispose(); SetBusy(false);
            if (_closingAfterScan) Close();
        }
    }
    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = BrowseButton.IsEnabled = DirectoryBox.IsEnabled = BudgetPanel.IsEnabled = !busy;
        CancelButton.IsEnabled = busy; ScanProgress.IsIndeterminate = busy; ExportButton.IsEnabled = !busy && _result is not null;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel(); CancelButton.IsEnabled = false;
        SetStatus(() => LanguageService.T("已要求取消；等待目前檔案系統呼叫返回後保留部分結果。"));
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_scanCancellation is null) return;
        // Keep the worker and cancellation source owned until the real I/O finishes.
        e.Cancel = true; _closingAfterScan = true; _scanCancellation.Cancel();
        SetStatus(() => LanguageService.T("正在停止分析；檔案系統呼叫返回後關閉。"));
    }
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (e.NewValue is DiskSpaceNode node) { _mapNode = node; SetDetail(node); DrawMap(); } }
    private void BigFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (BigFilesGrid.SelectedItem is DiskSpaceNode node) SetDetail(node); }
    private void Up_Click(object sender, RoutedEventArgs e)
    { if (_mapNode?.Parent is { } parent) { _mapNode = parent; SetDetail(parent); DrawMap(); } }
    private void Basis_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) DrawMap(); }
    private void Map_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMap();
    private void DrawMap()
    {
        if (MapCanvas is null || _mapNode is null) return;
        MapCanvas.Children.Clear(); MapPathText.Text = _mapNode.Path;
        bool allocated = BasisBox.SelectedIndex == 1;
        long Size(DiskSpaceNode n) => allocated ? n.UniqueAllocatedBytes : n.LogicalBytes;
        var nodes = (_mapNode.IsDirectory ? _mapNode.Children : new List<DiskSpaceNode> { _mapNode }).Where(n => Size(n) > 0).OrderByDescending(Size).ToList();
        int allCount = nodes.Count;
        if (nodes.Count > 80)
        {
            int otherCount = nodes.Count - 79;
            var other = new DiskSpaceNode($"其他 {otherCount:N0} 項（合併顯示）", false)
            { UiLabel = () => DiskSpaceUiText.Format("其他 {0:N0} 項（合併顯示）", otherCount) };
            foreach (var n in nodes.Skip(79))
            {
                other.LogicalBytes = Saturate(other.LogicalBytes, n.LogicalBytes);
                other.UniqueAllocatedBytes = Saturate(other.UniqueAllocatedBytes, n.UniqueAllocatedBytes);
                other.UnknownAllocationFiles += n.UnknownAllocationFiles; other.FileCount += n.FileCount;
            }
            other.Note = "此矩形合併顯示其餘子項；完整清單見左側目錄樹。";
            nodes = nodes.Take(79).Append(other).ToList();
        }
        var tiles = DiskSpaceTreemap.Layout(nodes, MapCanvas.ActualWidth, MapCanvas.ActualHeight, allocated);
        int i = 0;
        foreach (var tile in tiles)
        {
            var node = tile.Node;
            var button = new Button { Width = Math.Max(0, tile.Width - 2), Height = Math.Max(0, tile.Height - 2), MinWidth = 0, MinHeight = 0,
                Background = new SolidColorBrush(TileColors[i++ % TileColors.Length]), Foreground = Brushes.White, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = node.Detail, ClipToBounds = true, Cursor = Cursors.Hand, Padding = new Thickness(5) };
            var label = new TextBlock { Text = node.UiName + "\n" + DiskSpaceFormatting.Bytes(Size(node)), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            LanguageService.SetIsRawText(label, true); button.Content = label;
            System.Windows.Automation.AutomationProperties.SetName(button, node.UiName + "，" + DiskSpaceFormatting.Bytes(Size(node)) + LanguageService.T(node.IsDirectory ? "，開啟子目錄空間圖" : "，檢視明細"));
            button.Click += (_, _) => { SetDetail(node); if (node.IsDirectory) { _mapNode = node; DrawMap(); } };
            Canvas.SetLeft(button, tile.X + 1); Canvas.SetTop(button, tile.Y + 1); MapCanvas.Children.Add(button);
        }
        MapNoteText.Text = DiskSpaceUiText.Format("{0:N0} 個有大小的子項，最多 80 個矩形；面積依{1}。", allCount,
            LanguageService.T(allocated ? "已確認唯一配置" : "檔名邏輯大小")) +
            (_mapNode.IsPartial ? LanguageService.T(" 此範圍不完整；圖中只含已讀到的部分。") : "") + LanguageService.T(tiles.Count == 0 ? " 沒有可繪製的已知正值。" : " 點選目錄可深入，上層可返回。");
    }
    private static long Saturate(long a, long b) => long.MaxValue - a < b ? long.MaxValue : a + b;
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var result = _result; if (result is null || _scanCancellation is not null || _exporting) return;
        var dialog = new SaveFileDialog { Title = LanguageService.T("匯出磁碟空間報告（包含所選目錄的檔案路徑）"), Filter = "JSON report (*.json)|*.json", FileName = "XinSpect-disk-space.json", DefaultExt = ".json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        _exporting = true; StartButton.IsEnabled = ExportButton.IsEnabled = false;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, DiskSpaceScanService.CreateJsonReport(result), new UTF8Encoding(false));
            SetStatus(() => result.Summary + "\n" + LanguageService.T("報告已儲存至使用者指定位置。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { SetStatus(() => LanguageService.T("報告匯出失敗：") + ex.Message); }
        finally { _exporting = false; StartButton.IsEnabled = _scanCancellation is null; ExportButton.IsEnabled = _scanCancellation is null && _result is not null; }
    }
}
