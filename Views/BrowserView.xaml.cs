using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Web.WebView2.Core;

namespace XinSpect;

/// <summary>
/// 內建瀏覽器分頁：以 WebView2（Chromium 核心）承載完整網頁，可正常播放影片與串流。
/// 預設搜尋引擎為 Bing；網址列輸入非網址字串時，自動改以 Bing 搜尋。
/// 使用者資料（Cookie／快取）獨立存於 %LOCALAPPDATA%\XinSpect\WebView2，不干擾系統瀏覽器。
/// 未安裝 WebView2 執行階段時，顯示後備提示與下載連結，不影響其餘分頁。
/// </summary>
public partial class BrowserView : UserControl
{
    private bool _initStarted;
    private bool _ready;
    private bool _isLoading;
    private string? _pendingNavUrl;   // 於瀏覽器就緒前收到的導覽請求（如自網速頁跳轉），就緒後補跳
    private string? _returnUtilityKey;   // 自他頁跳轉而來（如網速測試）時的返回目標子工具鍵；非空時顯示返回鈕

    public BrowserView()
    {
        InitializeComponent();
        AddressBar.Text = "";
        Loaded += BrowserView_Loaded;
    }

    private async void BrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;
        _initStarted = true;

        try
        {
            var dataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XinSpect", "WebView2");
            System.IO.Directory.CreateDirectory(dataFolder);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            // 預設以 Bing 為搜尋引擎；關閉不必要的開發者干擾，保留右鍵與狀態列。
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.AreDevToolsEnabled = true;

            core.SourceChanged += (_, _) => SyncAddress();
            core.HistoryChanged += (_, _) => SyncNavButtons();
            core.NewWindowRequested += Core_NewWindowRequested;
            core.NavigationStarting += (_, _) => SetLoading(true);
            core.NavigationCompleted += (_, _) => SetLoading(false);

            _ready = true;
            SyncNavButtons();
            if (_pendingNavUrl is { } pending)   // 有待處理的跳轉（如自網速頁）優先，否則載入起始頁
            {
                _pendingNavUrl = null;
                NavigateTo(pending);
            }
            else GoHome();   // 首頁改為內建的離線「硬體導航」起始頁（A）
        }
        catch (Exception ex)
        {
            // 多半是未安裝 WebView2 執行階段（Evergreen Runtime）。誠實提示，附下載連結。
            _ready = false;
            Fallback.Visibility = Visibility.Visible;
            FallbackMsg.Text = "找不到可用的 Microsoft Edge WebView2 執行階段，或其初始化失敗。\n"
                             + "安裝執行階段後即可使用內建瀏覽器。\n\n（"
                             + ex.GetType().Name + "：" + ex.Message + "）";
        }
    }

    // 網頁要求開新視窗（target=_blank 等）：一律導回本檢視內開啟，不另彈系統視窗。
    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_ready && !string.IsNullOrWhiteSpace(e.Uri))
            Web.Source = new Uri(e.Uri);
    }

    private void SyncAddress()
    {
        if (AddressBar.IsKeyboardFocused) return;
        var src = Web.Source?.ToString();
        // 起始頁以 NavigateToString 載入，Source 為 about:blank／空 → 網址列留空
        AddressBar.Text = (string.IsNullOrEmpty(src) || src == "about:blank") ? "" : src;
    }

    private void SyncNavButtons()
    {
        var core = Web.CoreWebView2;
        BackBtn.IsEnabled = core?.CanGoBack ?? false;
        FwdBtn.IsEnabled = core?.CanGoForward ?? false;
    }

    // 將使用者輸入解析為「直接前往的網址」或「以 Bing 搜尋的關鍵字」；空字串回 null（表示回起始頁）。
    private static Uri? ResolveInput(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return null;

        // 已含協定 → 直接前往
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            if (Uri.TryCreate(text, UriKind.Absolute, out var abs)) return abs;

        // 看起來像網域（含點、不含空白）→ 補上 https://
        bool looksLikeHost = !text.Contains(' ') && text.Contains('.');
        if (looksLikeHost && Uri.TryCreate("https://" + text, UriKind.Absolute, out var host))
            return host;

        // 其餘一律以 Bing 搜尋
        return new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(text));
    }

    private void Navigate()
    {
        if (!_ready) return;
        var target = ResolveInput(AddressBar.Text);
        try { if (target is null) GoHome(); else Web.Source = target; }
        catch { /* 無效輸入時不動作 */ }
    }

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Navigate(); e.Handled = true; }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate();
    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward(); }
    // 載入中時作為「停止」鍵，否則為「重新整理」（B）
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) Web.CoreWebView2?.Stop();
        else Web.CoreWebView2?.Reload();
    }
    private void Home_Click(object sender, RoutedEventArgs e) => GoHome();

    // 供其他分頁（如網速測試的官方測速頁節點）跳轉至指定網址。
    // 若瀏覽器尚未就緒（首次載入的非同步初始化未完成），先暫存，待就緒後自動補跳。
    public void NavigateTo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!_ready) { _pendingNavUrl = url; return; }
        try { Web.Source = new Uri(url); AddressBar.Text = url; }
        catch { /* 無效網址時不動作 */ }
    }

    /// <summary>設定「回到測速頁面」一類的返回鈕：帶子工具鍵即顯示，傳 null 隱藏。</summary>
    public void SetReturnUtility(string? utilityKey)
    {
        _returnUtilityKey = utilityKey;
        ReturnUtilityBtn.Visibility = string.IsNullOrEmpty(utilityKey) ? Visibility.Collapsed : Visibility.Visible;
    }

    // 返回鈕：切回跳轉來源頁（目前僅網速測試）。點過即收，避免按鈕留在一般瀏覽狀態。
    private void ReturnUtility_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_returnUtilityKey)) return;
        var key = _returnUtilityKey;
        SetReturnUtility(null);
        Shell.Main?.NavigateToUtility(key);
    }

    // 回到內建離線起始頁（硬體導航）。以 NavigateToString 載入，不需外部檔案。
    private void GoHome()
    {
        if (!_ready) return;
        AddressBar.Text = "";
        try { Web.NavigateToString(BrowserHome.Html); } catch { }
    }

    // 切換載入狀態：顯示／隱藏頂端進度條，並讓重新整理鍵在「停止」與「重新整理」間切換。
    private void SetLoading(bool on)
    {
        _isLoading = on;
        LoadBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ReloadBtn.Content = on ? "✕" : "⟳";
        ReloadBtn.ToolTip = on ? "停止載入" : "重新整理";
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = Web.Source?.ToString();
        if (string.IsNullOrWhiteSpace(url)) url = AddressBar.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 無可用系統瀏覽器時靜默略過 */ }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }
}
