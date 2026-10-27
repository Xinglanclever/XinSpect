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
        PreviewKeyDown += BrowserView_PreviewKeyDown;   // 焦點在本頁的 WPF 控制項時（例如網址列）也能叫出尋找列
    }

    private async void BrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;
        _initStarted = true;

        try
        {
            var baseFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XinSpect", "WebView2");
            // 多開時第二份實例不能共用同一個 WebView2 使用者資料夾——WebView2 會鎖住它，
            // 第二份 CreateAsync 直接失敗、瀏覽器頁誤報「未安裝執行階段」。偵測到已有另一份
            // XinSpect 在跑就改用帶 PID 的獨立資料夾（該實例 Cookie／快取不持久，但至少能用）；
            // 單一實例時仍用共用資料夾以保留登入狀態。
            bool another = Process.GetProcessesByName("XinSpect").Any(p => p.Id != Environment.ProcessId);
            var dataFolder = another
                ? System.IO.Path.Combine(baseFolder, "inst-" + Environment.ProcessId)
                : baseFolder;
            System.IO.Directory.CreateDirectory(dataFolder);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            // 預設以 Bing 為搜尋引擎；保留右鍵與狀態列。
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            // DevTools 是一個能對頁面執行任意腳本、檢視 Cookie 的完整介面，而這個行程是管理員權杖。
            // 只有在明確設了環境變數時才開（理由與變數名寫在 BrowserPolicy）——預設擺著等於常駐一個提權介面。
            core.Settings.AreDevToolsEnabled =
                BrowserPolicy.DevToolsAllowed(Environment.GetEnvironmentVariable(BrowserPolicy.DevToolsEnvVar));
            // 縮放由本頁自己管（按鈕與 Ctrl＋滾輪，範圍夾在 50%–200%）；
            // 留著 WebView2 內建的縮放鍵會變成兩套規則同時改同一個值。
            core.Settings.IsZoomControlEnabled = false;
            ApplyZoom(1.0);

            core.SourceChanged += (_, _) => SyncAddress();
            core.HistoryChanged += (_, _) => SyncNavButtons();
            core.NewWindowRequested += Core_NewWindowRequested;
            // 導覽政策、下載落地位置、裝置權限：三件事都走有測試守著的 BrowserPolicy，
            // 不在事件處理常式裡現寫規則（現寫的規則沒有人驗，也沒有第二個人知道它存在）。
            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.DownloadStarting += Core_DownloadStarting;
            core.PermissionRequested += Core_PermissionRequested;

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
            // 多半是未安裝 WebView2 執行階段（Evergreen Runtime），也可能是資料夾一時被鎖。
            // 重設 _initStarted，讓下次重新進頁能再試一次，而不是永久卡在後備提示。
            _initStarted = false;
            _ready = false;
            Fallback.Visibility = Visibility.Visible;
            FallbackMsg.Text = LanguageService.T("找不到可用的 Microsoft Edge WebView2 執行階段，或其初始化失敗。\n"
                             + "安裝執行階段後即可使用內建瀏覽器。\n\n（")
                             + ex.GetType().Name + "：" + ex.Message + "）";
        }
    }

    // 網頁要求開新視窗（target=_blank 等）：一律導回本檢視內開啟，不另彈系統視窗。
    // 這裡也要過導覽政策：新視窗請求同樣可能是 file:// 或 javascript:。
    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_ready || string.IsNullOrWhiteSpace(e.Uri)) return;

        if (!BrowserPolicy.IsNavigationAllowed(Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u : null))
        {
            string scheme = u?.Scheme ?? "未知";
            ShowNotice("已擋下一個不是網頁的新視窗連結（" + scheme + "）。\n" + BrowserPolicy.DescribeBlocked(scheme));
            return;
        }
        Web.Source = u!;
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

    // 網址列的解析規則住在 BrowserPolicy（有測試釘住）：只有 http／https 能載入，
    // 本機路徑、javascript:、data:、自訂協定一律擋下並說明原因。
    private void Navigate()
    {
        if (!_ready) return;
        var input = BrowserPolicy.Resolve(AddressBar.Text);
        switch (input.Kind)
        {
            case BrowserInputKind.Home:
                GoHome();
                break;
            case BrowserInputKind.Url when input.Target is not null:
                try { Web.Source = input.Target; }
                catch { ShowNotice("無法開啟這個網址。"); }
                break;
            case BrowserInputKind.Search:
                Web.Source = new Uri(BrowserPolicy.SearchBase + Uri.EscapeDataString(input.Query));
                break;
            default:
                ShowNotice(input.Reason);
                break;
        }
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
        // 內部呼叫端也一樣要過政策：今天只有自家頁面在呼叫，明天不會記得這條規矩。
        if (!BrowserPolicy.IsNavigationAllowed(Uri.TryCreate(url, UriKind.Absolute, out var target) ? target : null))
        {
            ShowNotice("已擋下不是網頁的跳轉（" + url + "）。");
            return;
        }
        if (!_ready) { _pendingNavUrl = url; return; }
        try { Web.Source = target; AddressBar.Text = url; }
        catch { ShowNotice("無法開啟這個網址。"); }
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
        ReloadBtn.ToolTip = on ? LanguageService.T("停止載入") : LanguageService.T("重新整理");
    }

    // ── 頁內尋找 ──────────────────────────────────────────────────────────

    private void BrowserView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ShowFind();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            HideFind();
            e.Handled = true;
        }
    }

    private void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindHint.Text = "";
    }

    private void FindToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FindBar.Visibility == Visibility.Visible) HideFind();
        else ShowFind();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => HideFind();

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunFind(backwards: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFind();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => RunFind(backwards: false);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => RunFind(backwards: true);

    /// <summary>
    /// 叫頁面自己找：<c>window.find</c> 回傳 true／false。字串一律走 JSON 編碼
    /// （見 <see cref="BrowserFind"/>）——這一頁跑在管理員權杖的分頁裡。
    /// </summary>
    private async void RunFind(bool backwards)
    {
        string term = FindBox.Text;
        if (!BrowserFind.IsUsableTerm(term))
        {
            FindHint.Text = "請輸入要尋找的字（最多 " + BrowserFind.MaxTermLength + " 個字元）。";
            return;
        }

        var core = Web.CoreWebView2;
        if (core is null)
        {
            FindHint.Text = "瀏覽器尚未就緒。";
            return;
        }

        try
        {
            string result = await core.ExecuteScriptAsync(BrowserFind.Script(term, backwards));
            FindHint.Text = result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                ? ""
                : "找不到「" + term + "」";
        }
        catch (Exception ex)
        {
            FindHint.Text = "尋找失敗：" + ex.Message;
        }
    }

    // ── 縮放（按鈕與 Ctrl＋滾輪共用同一套夾範圍規則）────────────────────────

    private double _zoom = 1.0;

    private void ApplyZoom(double value)
    {
        _zoom = BrowserPolicy.ClampZoom(value);
        try { if (Web.CoreWebView2 is not null) Web.ZoomFactor = _zoom; }
        catch { /* 尚未就緒時設定會在初始化後重套 */ }
        if (ZoomResetBtn is not null)
            ZoomResetBtn.Content = (_zoom * 100).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyZoom(BrowserPolicy.NextZoom(_zoom, +1));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(BrowserPolicy.NextZoom(_zoom, -1));
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ApplyZoom(1.0);

    private void Web_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ApplyZoom(BrowserPolicy.NextZoom(_zoom, e.Delta > 0 ? +1 : -1));
        e.Handled = true;   // 不要讓縮放同時捲動頁面
    }

    // ── 導覽政策、下載、權限 ──────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _noticeTimer;

    /// <summary>在網頁區左下角顯示一則提示（自動消失）。被擋下的導覽、下載位置、權限拒絕都走這裡。</summary>
    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        Notice.Visibility = Visibility.Visible;
        _noticeTimer?.Stop();
        _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _noticeTimer.Tick += (_, _) =>
        {
            Notice.Visibility = Visibility.Collapsed;
            _noticeTimer?.Stop();
        };
        _noticeTimer.Start();
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Uri? target = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u : null;
        if (!BrowserPolicy.IsNavigationAllowed(target))
        {
            // 網頁裡的連結也一樣：只要不是 http／https 就不載入，並說出被擋的是什麼。
            e.Cancel = true;
            string scheme = target?.Scheme ?? "未知";
            ShowNotice("已擋下一個不是網頁的連結（" + scheme + "）。\n"
                       + BrowserPolicy.DescribeBlocked(scheme));
            return;
        }
        HideNavError();
        SetLoading(true);
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        SetLoading(false);
        if (e.IsSuccess) { HideNavError(); return; }
        ShowNavError(DescribeWebError(e.WebErrorStatus));
    }

    /// <summary>WebView2 的錯誤碼翻成「這裡發生什麼事、可以怎麼做」，不要只丟一個數字。</summary>
    private static string DescribeWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "找不到這個網域（DNS 查不到）。檢查網址是否拼錯，或這台機器目前的 DNS 是否正常。",
        CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.ConnectionAborted
            or CoreWebView2WebErrorStatus.ConnectionReset => "連不上伺服器（連線被拒絕或中斷）。網站可能暫時不可用，或本機網路／防火牆擋住了。",
        CoreWebView2WebErrorStatus.Timeout => "連線逾時。網站沒有回應，稍後再試或檢查網路。",
        CoreWebView2WebErrorStatus.Disconnected => "網路已中斷。",
        CoreWebView2WebErrorStatus.CannotConnect => "無法建立連線（可能是 TLS／憑證問題，或網站拒絕了這個連線）。",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid
            => "網站的 TLS 憑證有問題（名稱不符、過期或已撤銷）。內建瀏覽器不會略過憑證警告。",
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "伺服器回應了無效的內容。",
        CoreWebView2WebErrorStatus.OperationCanceled => "載入已取消。",
        CoreWebView2WebErrorStatus.UnexpectedError => "未預期的錯誤（WebView2 沒有給更具體的原因）。",
        _ => "載入失敗：" + status,
    };

    private void ShowNavError(string message)
    {
        NavErrorMsg.Text = message;
        NavError.Visibility = Visibility.Visible;
    }

    private void HideNavError() => NavError.Visibility = Visibility.Collapsed;

    private void NavErrorRetry_Click(object sender, RoutedEventArgs e)
    {
        HideNavError();
        try { Web.CoreWebView2?.Reload(); } catch { /* 尚未就緒時無事可做 */ }
    }

    private void NavErrorHome_Click(object sender, RoutedEventArgs e)
    {
        HideNavError();
        GoHome();
    }

    /// <summary>
    /// 下載一律落到程式自己的資料夾（<c>%LOCALAPPDATA%\XinSpect\Downloads</c>），
    /// 一律不覆蓋既有檔案、也一律告訴使用者存到哪裡。原本走 WebView2 的預設行為：
    /// 跳出系統另存對話框，使用者按取消或存到別處都不會在畫面上留下任何痕跡。
    /// </summary>
    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var op = e.DownloadOperation;
        try
        {
            System.IO.Directory.CreateDirectory(BrowserPolicy.DownloadFolder);
            string suggested = System.IO.Path.GetFileName(e.ResultFilePath ?? "");
            string path = BrowserPolicy.UniquePath(BrowserPolicy.DownloadFolder, suggested, System.IO.File.Exists);

            e.ResultFilePath = path;    // 指定路徑
            e.Handled = true;           // 且不再跳系統的「另存新檔」對話框
            ShowNotice("開始下載：" + System.IO.Path.GetFileName(path) + "\n儲存於 " + BrowserPolicy.DownloadFolder);

            op.StateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    string size = op.TotalBytesToReceive is > 0
                        ? $"，{op.TotalBytesToReceive:N0} bytes" : "";
                    ShowNotice("下載完成：" + System.IO.Path.GetFileName(op.ResultFilePath) + size
                               + "\n檔案在 " + BrowserPolicy.DownloadFolder);
                }
                else if (op.State == CoreWebView2DownloadState.Interrupted)
                {
                    ShowNotice("下載中斷（" + op.InterruptReason + "）："
                               + System.IO.Path.GetFileName(op.ResultFilePath));
                }
            });
        }
        catch (Exception ex)
        {
            ShowNotice("無法決定下載位置，這次下載已取消：" + ex.Message);
            e.Cancel = true;
        }
    }

    /// <summary>
    /// 裝置權限一律拒絕。硬體檢測工具沒有任何理由把麥克風／攝影機／位置權限交給網頁；
    /// 而且「默默允許」是最糟的一種——使用者不會知道剛剛同意了什麼。
    /// </summary>
    private void Core_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        string host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : e.Uri;
        ShowNotice("已拒絕「" + PermissionName(e.PermissionKind) + "」權限要求（" + host + "）。\n"
                   + "內建瀏覽器一律不授予裝置權限；需要的話請用系統瀏覽器開這個網站。");
    }

    private static string PermissionName(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "麥克風",
        CoreWebView2PermissionKind.Camera => "攝影機",
        CoreWebView2PermissionKind.Geolocation => "位置",
        CoreWebView2PermissionKind.Notifications => "通知",
        CoreWebView2PermissionKind.ClipboardRead => "剪貼簿讀取",
        CoreWebView2PermissionKind.OtherSensors => "感測器",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "連續自動下載",
        CoreWebView2PermissionKind.Autoplay => "自動播放",
        CoreWebView2PermissionKind.WindowManagement => "視窗管理",
        _ => kind.ToString(),
    };

    /// <summary>清除 Cookie、快取與本機儲存。清除後會回首頁——停在原頁只會看起來「還在登入」。</summary>
    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        var core = Web.CoreWebView2;
        if (core is null) { ShowNotice("瀏覽器尚未就緒。"); return; }
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            GoHome();
            ShowNotice("已清除 Cookie、快取與本機儲存（各網站會視為未登入）。");
        }
        catch (Exception ex)
        {
            ShowNotice("清除失敗：" + ex.Message);
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = Web.Source?.ToString();
        if (string.IsNullOrWhiteSpace(url)) url = AddressBar.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        // 交給系統瀏覽器之前先確認是網頁——那條路徑沒有我們的導覽政策保護。
        var input = BrowserPolicy.Resolve(url);
        if (input.Kind == BrowserInputKind.Url && input.Target is { } target)
        {
            try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); }
            catch { ShowNotice("找不到可用的系統瀏覽器。"); }
        }
        else
        {
            ShowNotice("這一頁不是網頁，沒有可交給系統瀏覽器開啟的位址。");
        }
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(BrowserPolicy.DownloadFolder);
            Process.Start(new ProcessStartInfo(BrowserPolicy.DownloadFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowNotice("無法開啟下載資料夾：" + ex.Message); }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }
}
