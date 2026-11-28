using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace XinSpect;

public sealed class DriverStoreSelectionRow : ObservableObject
{
    public DriverStorePackage Package { get; }
    private bool _selected;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value && Package.CanExport); }
    public DriverStoreSelectionRow(DriverStorePackage package) => Package = package;
    internal void RefreshLanguage() => OnPropertyChanged(nameof(Package));
}

/// <summary>Explicit, export-only interaction. Constructing or opening the window never inventories or exports.</summary>
public partial class DriverStoreWindow : Window
{
    private sealed class StatusPresentation : ObservableObject
    {
        private string _value = "";
        public string Value { get => _value; set => SetProperty(ref _value, value); }
    }
    private readonly DriverStoreService _service;
    private readonly StatusPresentation _statusPresentation = new();
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _closed;
    private Func<string> _renderStatus = () => LanguageService.T("尚未盤點。");
    private Func<string> _renderDetails = () => LanguageService.T("按「重新盤點」讀取；不會自動匯出套件。");
    public ObservableCollection<DriverStoreSelectionRow> Rows { get; } = [];
    public DriverStoreWindow() : this(new DriverStoreService()) { }
    public DriverStoreWindow(DriverStoreService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent(); DataContext = this;
        CollectionViewSource.GetDefaultView(Rows).Filter = Matches;
        // This mixed diagnostic display is explicitly localized below; raw error/path arguments stay verbatim.
        StatusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(StatusPresentation.Value))
            { Source = _statusPresentation, Mode = BindingMode.OneWay });
        LanguageService.Changed += RefreshLanguage;
        Closed += Window_Closed;
        RefreshLanguage();
    }

    private static string TranslateReason(string text)
    {
        const string refreshFailure = "匯出前重新盤點失敗：";
        const string manifestFailure = " manifest 寫入失敗：";
        const string bindingFailure = "WMI 裝置綁定失敗：";
        if (text.StartsWith(refreshFailure, StringComparison.Ordinal))
            return LanguageService.T(refreshFailure) + TranslateReason(text[refreshFailure.Length..]);
        if (text.StartsWith(bindingFailure, StringComparison.Ordinal))
            return LanguageService.T(bindingFailure) + UiTextTranslation.KnownDisplayText(
                text[bindingFailure.Length..], LanguageService.IsEnglish, LanguageService.IsSimplified);
        int separator = text.IndexOf(manifestFailure, StringComparison.Ordinal);
        if (separator >= 0)
            return TranslateReason(text[..separator]) + LanguageService.T(manifestFailure) + text[(separator + manifestFailure.Length)..];
        return UiTextTranslation.KnownDisplayText(text, LanguageService.IsEnglish, LanguageService.IsSimplified);
    }
    private void SetStatus(Func<string> render) { _renderStatus = render; _statusPresentation.Value = render(); }
    private void SetDetails(Func<string> render) { _renderDetails = render; DetailsBox.Text = render(); }
    private void RefreshLanguage()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshLanguage)); return; }
        LanguageService.ConvertVisualTree(this, LanguageService.IsSimplified);
        foreach (var row in Rows) row.RefreshLanguage();
        _statusPresentation.Value = _renderStatus();
        DetailsBox.Text = _renderDetails();
        UpdateExport();
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        LanguageService.Changed -= RefreshLanguage;
        foreach (var row in Rows) row.PropertyChanged -= Row_Changed;
    }

    private bool Matches(object value)
    {
        if (value is not DriverStoreSelectionRow row) return false;
        string filter = FilterBox?.Text.Trim() ?? "";
        var p = row.Package;
        return filter.Length == 0 || new[] { p.PublishedName, p.Provider, p.ClassName, p.Version, p.OriginalFileName }
            .Any(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (PackagesGrid != null) CollectionViewSource.GetDefaultView(Rows).Refresh();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; SetBusy(true);
        foreach (var row in Rows) row.PropertyChanged -= Row_Changed;
        Rows.Clear(); SetDetails(() => LanguageService.T("盤點中；取消會等待目前 DISM 原生呼叫返回後停止。"));
        SetStatus(() => LanguageService.T("正在讀取 DISM 清單與 WMI 綁定…"));
        try
        {
            var result = await _service.ReadInventoryAsync(cancellation.Token);
            if (result.Availability == FactAvailability.Present && !result.Cancelled)
            {
                foreach (var p in result.Packages)
                {
                    var row = new DriverStoreSelectionRow(p); row.PropertyChanged += Row_Changed; Rows.Add(row);
                }
                SetStatus(() => LanguageService.T($"{Rows.Count} 個 Store 套件；{Rows.Count(r => !r.Package.InBox)} 個第三方。綁定狀態：{result.BindingAvailability}。讀取 {result.ReadAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}。"));
                SetDetails(() => TranslateReason(result.Reason) + "\n" + TranslateReason(result.BindingReason)
                    + LanguageService.T("\n綁定來源更新時間未知；讀取時間不代表來源更新時間。"));
            }
            else SetStatus(() => $"{result.Availability}：{TranslateReason(result.Reason)}");
        }
        catch (OperationCanceledException) { SetStatus(() => LanguageService.T("盤點已取消。")); }
        catch (Exception ex) { SetStatus(() => LanguageService.T("盤點失敗：") + ex.Message); }
        finally { _cancellation = null; SetBusy(false); }
    }
    private void Row_Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(DriverStoreSelectionRow.IsSelected)) UpdateExport(); }
    private void UpdateExport()
    {
        if (ExportButton == null) return;
        int count = Rows.Count(r => r.IsSelected); ExportButton.IsEnabled = !_busy && count > 0;
        ExportButton.Content = LanguageService.T(count == 0 ? "匯出勾選套件" : $"匯出 {count} 個勾選套件");
    }
    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (DriverStoreSelectionRow row in CollectionViewSource.GetDefaultView(Rows)) row.IsSelected = row.Package.CanExport;
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var row in Rows) row.IsSelected = false; }
    private void Package_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetailsBox == null || PackagesGrid.SelectedItem is not DriverStoreSelectionRow row) return;
        var p = row.Package;
        SetDetails(() => string.Format(LanguageService.T("{0} | {1} / {2} | {3}\n原始 INF：{4}\nCAT：{5}；Class GUID：{6}；BootCritical：{7}\n{8}；{9}\n{10}：{11}\n"),
            p.PublishedName, p.Availability, p.Trust, p.Source, p.OriginalFileName, p.CatalogFile, p.ClassGuid,
            p.BootCritical, p.SignatureText, p.BindingText, p.BindingSource, p.BindingReasonText)
            + string.Join("\n", p.Bindings.Select(b => $"{b.DeviceName} | {b.DeviceId}")));
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = LanguageService.T("選擇 Driver Store 備份目的地（本機現有資料夾）") };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Package).ToArray();
        if (selected.Length == 0) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; SetBusy(true);
        try
        {
            var progress = new Progress<DriverStoreExportProgress>(p => SetStatus(() => $"{p.Completed}/{p.Total} — {TranslateReason(p.Message)}"));
            var result = await _service.ExportAsync(selected, DestinationBox.Text, progress, cancellation.Token);
            SetStatus(() => $"{result.Status} / {result.Availability}：{TranslateReason(result.Detail)}");
            SetDetails(() => TranslateReason(result.Detail) + LanguageService.T("\n輸出目錄：")
                + (result.Directory ?? LanguageService.T("未建立")) + "\nManifest："
                + (result.ManifestPath ?? LanguageService.T("未建立／寫入失敗")));
        }
        catch (OperationCanceledException) { SetStatus(() => LanguageService.T("已取消，未啟動匯出。")); }
        catch (Exception ex) { SetStatus(() => LanguageService.T("匯出失敗：") + ex.Message); }
        finally { _cancellation = null; SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; RefreshButton.IsEnabled = BrowseButton.IsEnabled = DestinationBox.IsEnabled = !busy;
        SelectButton.IsEnabled = ClearButton.IsEnabled = PackagesGrid.IsEnabled = !busy;
        CancelButton.IsEnabled = busy; UpdateExport();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel(); CancelButton.IsEnabled = false;
        SetStatus(() => LanguageService.T("已要求取消；等待目前 DISM 呼叫／匯出程序確實結束後記錄結果。"));
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; Cancel_Click(sender ?? this, new RoutedEventArgs());
    }
}
