using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace XinSpect;

/// <summary>
/// 環境自檢的單一項目：某項功能所需的執行階段／驅動／服務是否就緒。
/// <see cref="Severity"/> 決定顏色（Good 綠＝就緒、Warning 黃＝缺少但可補、Neutral 灰＝硬體不適用、Critical 紅＝核心必要項缺失）。
/// </summary>
public sealed class EnvCheckItem : ObservableObject
{
    public required string Name { get; init; }

    private string _status = "檢測中…";
    public string StatusText { get => _status; set => SetProperty(ref _status, value); }

    private string _detail = "";
    public string Detail { get => _detail; set { if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => _detail.Length > 0;

    private Severity _severity = Severity.Neutral;
    public Severity Severity { get => _severity; set => SetProperty(ref _severity, value); }

    private string? _url;
    /// <summary>缺少時的官方取得連結；null 表示無需動作。</summary>
    public string? DownloadUrl { get => _url; set { if (SetProperty(ref _url, value)) OnPropertyChanged(nameof(HasLink)); } }
    public bool HasLink => !string.IsNullOrEmpty(_url);

    private string _linkText = "前往取得";
    public string LinkText { get => _linkText; set => SetProperty(ref _linkText, value); }
}

/// <summary>
/// 環境自檢：偵測各分頁功能所需的執行階段／驅動／服務是否就緒（.NET、系統管理員、感測器核心驅動、
/// WebView2、winget、NVIDIA 驅動、Intel XTU），缺少者標示黃／灰並附官方下載連結。
/// 全部為真實偵測（實際探測執行階段與現有服務狀態），不做任何模擬。可於設定頁重複執行。
/// </summary>
public sealed class EnvCheckService : ObservableObject
{
    public ObservableCollection<EnvCheckItem> Items { get; } = new();

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanRun)); } }
    public bool CanRun => !_running;

    private string _summary = "尚未檢測。按「開始檢測」偵測各功能所需環境。";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private bool _hasRun;
    public bool HasRun { get => _hasRun; private set => SetProperty(ref _hasRun, value); }

    /// <summary>
    /// 執行完整環境自檢。核心必要項（.NET、系統管理員）直接判定；感測器／顯示卡超頻／CPU 超頻沿用
    /// 對應服務的實際就緒狀態；WebView2 與 winget 另行即時探測。UI 執行緒呼叫，探測工作交背景。
    /// </summary>
    public async Task RunAsync(MainViewModel vm)
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            Items.Clear();

            // ── 1. .NET 10 桌面執行階段（本程式正在其上執行，必為就緒；顯示實際版本）──
            Add(".NET 10 桌面執行階段", Severity.Good, "已就緒",
                RuntimeInformation.FrameworkDescription);

            // ── 2. 系統管理員權限（讀取溫度／感測器、超頻寫入皆需要）──
            bool admin = IsAdministrator();
            Add("系統管理員權限", admin ? Severity.Good : Severity.Critical,
                admin ? "已提升" : "未提升",
                admin ? "以系統管理員身分執行，可讀取底層感測器並進行超頻。"
                      : "未以系統管理員執行，溫度／電壓讀取與超頻可能失效。請以系統管理員重新開啟。");

            // ── 3. 感測器核心驅動（LibreHardwareMonitor 內建，隨程式自動安裝）──
            bool sensorOk = vm.Live?.CpuTemp is > 0;
            Add("感測器核心驅動", sensorOk ? Severity.Good : Severity.Warning,
                sensorOk ? "已載入" : "未讀到溫度",
                sensorOk ? "已讀到 CPU 溫度，溫度／電壓／風扇監控正常。"
                         : "尚未讀到 CPU 溫度。若已開啟「核心隔離／記憶體完整性」可能阻擋底層驅動；可於設定頁「一鍵初始化」重試，或於系統設定中關閉後重開。");

            // ── 背景探測 WebView2 與 winget（皆可能阻塞）──
            string? webView2Ver = null;
            bool wingetOk = false;
            await Task.Run(() =>
            {
                try { webView2Ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch { webView2Ver = null; }
                wingetOk = ProbeWinget();
            });

            // ── 4. WebView2 執行階段（內建瀏覽器）──
            bool wv2 = !string.IsNullOrWhiteSpace(webView2Ver);
            Add("WebView2 執行階段", wv2 ? Severity.Good : Severity.Warning,
                wv2 ? "已安裝" : "未安裝",
                wv2 ? $"內建瀏覽器可用（版本 {webView2Ver}）。"
                    : "未安裝 Microsoft Edge WebView2 執行階段，內建瀏覽器分頁無法顯示網頁（其餘分頁不受影響）。",
                wv2 ? null : "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "下載 WebView2");

            // ── 5. winget（一鍵裝機 / App Installer）──
            Add("winget 套件管理器", wingetOk ? Severity.Good : Severity.Warning,
                wingetOk ? "已就緒" : "未安裝",
                wingetOk ? "一鍵裝機可用，可批次安裝常用軟體。"
                         : "未偵測到 winget（App Installer），一鍵裝機無法使用。請於 Microsoft Store 安裝「應用程式安裝程式」。",
                wingetOk ? null : "https://apps.microsoft.com/detail/9NBLGGH4NNS1",
                "取得 App Installer");

            // ── 6. NVIDIA 驅動（顯示卡超頻，N 卡專屬）──
            bool nv = vm.GpuOc.Available;
            Add("NVIDIA 驅動（顯示卡超頻）",
                nv ? Severity.Good : Severity.Neutral,
                nv ? "已就緒" : "不適用",
                nv ? $"偵測到 {(string.IsNullOrWhiteSpace(vm.GpuOc.GpuName) ? "NVIDIA 顯示卡" : vm.GpuOc.GpuName)}，可調整功耗／風扇／時脈。"
                   : "未偵測到 NVIDIA 顯示卡或驅動（NVML/NVAPI），顯示卡超頻不適用。若為 N 卡請安裝官方驅動。",
                nv ? null : "https://www.nvidia.com/download/index.aspx",
                "下載 NVIDIA 驅動");

            // ── 7. Intel XTU（CPU 超頻）──
            bool xtu = vm.Overclock.EngineReady;
            Add("Intel XTU（CPU 超頻）",
                xtu ? Severity.Good : Severity.Neutral,
                xtu ? "已就緒" : "未就緒",
                xtu ? "超頻引擎就緒，可調整倍頻／外頻／電壓。"
                    : ("CPU 超頻需安裝 Intel Extreme Tuning Utility（XTU）並支援本平台。目前狀態：" + vm.Overclock.EngineStatusText),
                xtu ? null : "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html",
                "下載 Intel XTU");

            int ok = Items.Count(i => i.Severity == Severity.Good);
            int warn = Items.Count(i => i.Severity is Severity.Warning or Severity.Critical);
            int na = Items.Count(i => i.Severity == Severity.Neutral);
            Summary = $"共 {Items.Count} 項：就緒 {ok}、需注意 {warn}、不適用 {na}。缺少項目點右側連結即可前往官方取得。";
            HasRun = true;
        }
        finally { IsRunning = false; }
    }

    private void Add(string name, Severity sev, string status, string detail, string? url = null, string linkText = "前往取得")
        => Items.Add(new EnvCheckItem { Name = name, Severity = sev, StatusText = status, Detail = detail, DownloadUrl = url, LinkText = linkText });

    // 目前行程是否具備系統管理員（本機 Administrators 群組）權限。
    private static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // 探測 winget 是否可用（執行 winget --version，結束碼 0 視為就緒）。
    private static bool ProbeWinget()
    {
        try
        {
            var psi = new ProcessStartInfo("winget", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(8000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }
}
