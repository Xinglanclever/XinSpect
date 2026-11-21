using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace XinSpect;

/// <summary>
/// 使用者從「關於」頁明確點擊百科後才下載並顯示。HTML 先經 Release SHA-256 對帳，
/// 再以 WebView2 虛擬主機映射快取資料夾，不使用 file://。
/// </summary>
public partial class EncyclopediaWindow : Window
{
    private const string VirtualHost = "xinspect.docs";

    private bool _ready;
    private bool _loading;
    private bool _closed;
    private string? _localPath;
    private string? _docUrl;
    private CancellationTokenSource? _loadCts;

    public EncyclopediaWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await LoadAndShowAsync();

    private async Task LoadAndShowAsync()
    {
        if (_loading) return;
        _loading = true;
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        RetryBtn.IsEnabled = false;
        Fallback.Visibility = Visibility.Visible;
        FallbackMsg.Text = LanguageService.T("正在從 XinSpect Release 下載並校驗百科…");
        StatusText.Text = LanguageService.T("正在下載百科…");
        try
        {
            EncyclopediaCatalog.EncyclopediaReport report = await EncyclopediaCatalog.LoadAsync(_loadCts.Token);
            if (_closed || _loadCts.IsCancellationRequested) return;
            if (!report.Available || string.IsNullOrEmpty(report.Text))
            {
                ShowFallback(LanguageService.TComposite(report.UnavailableReason ?? EncyclopediaCatalog.MissingReason));
                StatusText.Text = LanguageService.T("百科下載失敗。");
                return;
            }

            _localPath = Path.Combine(EncyclopediaCatalog.CacheDirectory, EncyclopediaCatalog.FileName);
            if (!File.Exists(_localPath))
                throw new FileNotFoundException("校驗完成後找不到百科快取。", _localPath);
            string folder = Path.GetDirectoryName(_localPath) ?? AppContext.BaseDirectory;

            // 多開時第二份實例不能共用同一個 WebView2 使用者資料夾（WebView2 會鎖住它）。
            string baseFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XinSpect", "WebView2-docs");
            bool another = Process.GetProcessesByName("XinSpect").Any(p => p.Id != Environment.ProcessId);
            string dataFolder = another ? Path.Combine(baseFolder, "inst-" + Environment.ProcessId) : baseFolder;
            Directory.CreateDirectory(dataFolder);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Web.EnsureCoreWebView2Async(env);
            if (_closed || _loadCts.IsCancellationRequested) return;

            var core = Web.CoreWebView2
                       ?? throw new InvalidOperationException("WebView2 初始化後沒有 CoreWebView2");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            try { core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; }
            catch (Exception) { /* 舊版 WebView2 執行階段沒有這個屬性：維持跟隨系統，不影響閱讀 */ }
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;

            _docUrl = "https://" + VirtualHost + "/" + Uri.EscapeDataString(EncyclopediaCatalog.FileName);
            _ready = true;
            core.Navigate(_docUrl);
            Fallback.Visibility = Visibility.Collapsed;
            StatusText.Text = LanguageService.T(report.UsedOfflineCache
                ? "使用已驗證的本機百科快取 ・ 目前無法連線至 Release"
                : "百科已下載並完成 SHA-256 校驗 ・ 目錄可點 ・ 可搜尋");
        }
        catch (OperationCanceledException) when (_loadCts?.IsCancellationRequested == true)
        {
            // 視窗關閉時取消下載，不顯示錯誤，也不寫入不完整快取。
        }
        catch (Exception ex)
        {
            ShowFallback(LanguageService.T("百科下載或顯示失敗，請檢查網路後重試。")
                         + " (" + ex.GetType().Name + ": " + ex.Message + ")");
            StatusText.Text = LanguageService.T("百科暫時無法開啟。");
        }
        finally
        {
            _loading = false;
            if (_loadCts is { IsCancellationRequested: false }) RetryBtn.IsEnabled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        try { _loadCts?.Cancel(); } catch { }
        _loadCts?.Dispose();
        try { Web.Dispose(); } catch { /* 關窗時釋放失敗不影響任何資料 */ }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
        => _ = LoadAndShowAsync();

    /// <summary>只允許網頁（http／https）。</summary>
    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (BrowserPolicy.IsNavigationAllowed(Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null,
                                              allowSelfDocument: false))
            return;
        e.Cancel = true;
        StatusText.Text = "已擋下不是網頁的連結（" + (uri?.Scheme ?? "未知") + "）。";
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_ready || string.IsNullOrWhiteSpace(e.Uri)) return;
        if (BrowserPolicy.IsNavigationAllowed(Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null))
        {
            if (Web.CoreWebView2 is not null) Web.CoreWebView2.Navigate(uri!.ToString());
            return;
        }
        StatusText.Text = "已擋下一個不是網頁的新視窗連結（" + (uri?.Scheme ?? "未知") + "）。";
    }

    private void Toc_Click(object sender, RoutedEventArgs e)
    {
        var core = Web.CoreWebView2;
        if (core is null || _docUrl is null) return;
        string? current = Web.Source?.ToString();
        if (!string.Equals(current, _docUrl, StringComparison.OrdinalIgnoreCase))
        {
            core.Navigate(_docUrl);
            return;
        }
        _ = core.ExecuteScriptAsync("window.scrollTo({top:0,behavior:'instant'})");
    }

    private void FindBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        FindNext_Click(sender, e);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        var core = Web.CoreWebView2;
        if (core is null) return;
        string term = FindBox.Text ?? "";
        if (!BrowserFind.IsUsableTerm(term))
        {
            StatusText.Text = "請先輸入要找的字（最多 " + BrowserFind.MaxTermLength + " 字）。";
            return;
        }
        _ = core.ExecuteScriptAsync(BrowserFind.Script(term));
        StatusText.Text = "尋找：" + term;
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_localPath) || !File.Exists(_localPath))
        {
            StatusText.Text = LanguageService.T("尚無已驗證的百科快取；請先下載成功再用瀏覽器開啟。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_localPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "開不了預設瀏覽器（" + ex.GetType().Name + "）";
        }
    }

    private void ShowFallback(string message)
    {
        FallbackMsg.Text = message;
        FallbackBrowserBtn.Visibility = !string.IsNullOrWhiteSpace(_localPath) && File.Exists(_localPath)
            ? Visibility.Visible : Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
    }
}
