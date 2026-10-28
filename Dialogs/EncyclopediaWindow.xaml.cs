using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace XinSpect;

/// <summary>
/// 程式內的百科翻閱視窗：把內嵌的 <c>百科.html</c>（由 README.md 產生、自足、含目錄與錨點）
/// 交給 WebView2 顯示。定位靠三件事：左側可摺疊目錄、每個標題的 ¶ 錨點、上方尋找（window.find）。
///
/// <para>
/// 載入方式刻意不是 <c>NavigateToString</c>（它有 2MB 上限，百科超過），也不是 <c>file://</c>
/// （本專案的政策是「只認 http／https」，而 file:// 能讀本機任意檔案）。做法是先把內嵌內容
/// 寫到 <c>%LOCALAPPDATA%\XinSpect\docs\</c>，再用 WebView2 的虛擬主機對應
/// （<c>SetVirtualHostNameToFolderMapping</c>）以 <c>https://xinspect.docs/百科.html</c> 開啟——
/// 只映射那一個資料夾，不開 file:// 這條路。
/// </para>
/// </summary>
public partial class EncyclopediaWindow : Window
{
    private const string VirtualHost = "xinspect.docs";

    private bool _ready;
    private string? _localPath;
    private string? _docUrl;

    public EncyclopediaWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var report = EncyclopediaCatalog.Load();
        if (!report.Available || string.IsNullOrEmpty(report.Text))
        {
            ShowFallback(report.UnavailableReason ?? EncyclopediaCatalog.MissingReason
                         + "\n（單檔發佈版應內嵌百科；開發機請確認倉庫根有 百科.html）");
            return;
        }

        try
        {
            _localPath = EncyclopediaCatalog.EnsureLocalCopy(report.Text);
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

            var core = Web.CoreWebView2
                       ?? throw new InvalidOperationException("WebView2 初始化後沒有 CoreWebView2");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;

            _docUrl = "https://" + VirtualHost + "/" + Uri.EscapeDataString(EncyclopediaCatalog.FileName);
            _ready = true;
            core.Navigate(_docUrl);
            StatusText.Text = "離線自足文件 ・ 左側目錄可點 ・ 上方尋找或 Ctrl＋F";
        }
        catch (Exception ex)
        {
            ShowFallback("找不到可用的 Microsoft Edge WebView2 執行階段，或其初始化失敗。\n"
                         + "仍可用「用瀏覽器開啟」閱讀同一份離線 HTML。\n\n（"
                         + ex.GetType().Name + "：" + ex.Message + "）");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { Web.Dispose(); } catch { /* 關窗時釋放失敗不影響任何資料 */ }
    }

    /// <summary>只允許網頁（http／https）。文件內的 GitHub 連結會在視窗內開啟；
    /// 其他協定（file:、javascript:、data:）一律擋下並寫出原因。</summary>
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

    // ── 工具列 ──────────────────────────────────────────────────────────

    private void Toc_Click(object sender, RoutedEventArgs e)
    {
        var core = Web.CoreWebView2;
        if (core is null || _docUrl is null) return;
        string? current = Web.Source?.ToString();
        if (!string.Equals(current, _docUrl, StringComparison.OrdinalIgnoreCase))
        {
            core.Navigate(_docUrl);                     // 從外部連結回到百科首頁
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
        if (_localPath is null || !File.Exists(_localPath))
        {
            var report = EncyclopediaCatalog.Load();
            if (!report.Available || string.IsNullOrEmpty(report.Text))
            {
                ShowFallback(report.UnavailableReason ?? EncyclopediaCatalog.MissingReason);
                return;
            }
            _localPath = EncyclopediaCatalog.EnsureLocalCopy(report.Text);
        }
        try
        {
            Process.Start(new ProcessStartInfo(_localPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "開不了預設瀏覽器（" + ex.GetType().Name + "）：" + _localPath;
        }
    }

    private void ShowFallback(string message)
    {
        FallbackMsg.Text = message;
        Fallback.Visibility = Visibility.Visible;
    }
}
