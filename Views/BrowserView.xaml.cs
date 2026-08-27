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
    private const string HomeUrl = "https://www.bing.com/";
    private bool _initStarted;
    private bool _ready;

    public BrowserView()
    {
        InitializeComponent();
        AddressBar.Text = HomeUrl;
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

            _ready = true;
            SyncNavButtons();
            Web.Source = new Uri(HomeUrl);
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
        var src = Web.Source?.ToString();
        if (!string.IsNullOrEmpty(src) && !AddressBar.IsKeyboardFocused)
            AddressBar.Text = src;
    }

    private void SyncNavButtons()
    {
        var core = Web.CoreWebView2;
        BackBtn.IsEnabled = core?.CanGoBack ?? false;
        FwdBtn.IsEnabled = core?.CanGoForward ?? false;
    }

    // 將使用者輸入解析為「直接前往的網址」或「以 Bing 搜尋的關鍵字」。
    private static Uri ResolveInput(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return new Uri(HomeUrl);

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
        try { Web.Source = ResolveInput(AddressBar.Text); }
        catch { /* 無效輸入時不動作 */ }
    }

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Navigate(); e.Handled = true; }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate();
    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) { if (_ready) Web.Source = new Uri(HomeUrl); }

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
