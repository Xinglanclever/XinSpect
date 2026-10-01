using System.Security.Principal;
using System.Windows;

namespace XinSpect;

/// <summary>
/// 驅動就緒器：把「底層驅動能不能用、為什麼不能、要怎麼修」集中在一張卡上講清楚。
/// <list type="bullet">
/// <item>WinRing0（LHM 0.9.4 內嵌的簽章驅動）——MSR 讀寫（頻率真相、MCA、免疫位元…）與 SMBus／SPD 直讀的基礎。</item>
/// <item>LHM 感測引擎（LibreHardwareMonitorLib 0.9.6）——溫度／負載／電壓／風扇等即時讀值。</item>
/// </list>
/// 偵測結果全部如實呈現：就緒、缺管理員、被資安軟體或核心隔離擋下、缺檔，各附下一步。
/// 安裝（載入驅動）只在使用者按下按鈕時才做，絕不偷偷執行。
/// </summary>
public sealed class DriverReadyService : ObservableObject
{
    /// <summary>目前行程是否以系統管理員執行。</summary>
    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public bool IsNotAdmin => !IsAdmin;

    private Severity _winring0Severity = Severity.Neutral;
    public Severity WinRing0Severity { get => _winring0Severity; private set => SetProperty(ref _winring0Severity, value); }

    private string _winRing0Status = "尚未檢測";
    // 欄位永遠存繁體原文，屬性讀取時即時轉換；語言切換時只需重新通知。
    public string WinRing0Status { get => LanguageService.T(_winRing0Status); private set { _winRing0Status = value; OnPropertyChanged(); } }

    private string _winRing0Detail = "";
    public string WinRing0Detail { get => LanguageService.T(_winRing0Detail); private set { _winRing0Detail = value; OnPropertyChanged(); } }
    public bool HasWinRing0Detail => _winRing0Detail.Length > 0;

    private Severity _lhmSeverity = Severity.Neutral;
    public Severity LhmSeverity { get => _lhmSeverity; private set => SetProperty(ref _lhmSeverity, value); }

    private string _lhmStatus = "尚未檢測";
    public string LhmStatus { get => LanguageService.T(_lhmStatus); private set { _lhmStatus = value; OnPropertyChanged(); } }

    private string _lhmDetail = "";
    public string LhmDetail { get => LanguageService.T(_lhmDetail); private set { _lhmDetail = value; OnPropertyChanged(); } }
    public bool HasLhmDetail => _lhmDetail.Length > 0;

    private bool _probing;
    public bool IsProbing { get => _probing; private set { if (SetProperty(ref _probing, value)) OnPropertyChanged(nameof(CanProbe)); } }
    public bool CanProbe => !_probing;

    private string _summary = "按「檢測驅動」以實際載入一次驅動並回報真實狀態。";
    public string Summary { get => LanguageService.T(_summary); private set { _summary = value; OnPropertyChanged(); } }

    public DriverReadyService()
    {
        // 語言切換時重新通知五個顯示字串；屬性 getter 會以新語言即時轉換。
        LanguageService.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WinRing0Status));
        OnPropertyChanged(nameof(WinRing0Detail));
        OnPropertyChanged(nameof(LhmStatus));
        OnPropertyChanged(nameof(LhmDetail));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>以實際載入一次 WinRing0 的方式偵測（測完即釋放引用；驅動服務本身留到重開機）。</summary>
    public async Task ProbeAsync(MainViewModel vm)
    {
        if (IsProbing) return;
        IsProbing = true;
        Summary = "檢測中…";
        try
        {
            // LHM 感測引擎：看主感測服務現在拿不拿得到真實讀值
            var live = vm.Live;
            bool lhmOk = live is { } lv && (lv.CpuLoad > 0 || lv.CpuTemp is > -273 || lv.MemLoad > 0);
            LhmSeverity = lhmOk ? Severity.Good : Severity.Warning;
            LhmStatus = lhmOk ? "已就緒" : "未取得讀值";
            LhmDetail = lhmOk
                ? "感測器引擎已回報即時讀值（溫度／負載）。"
                : "感測器引擎尚未回報任何讀值。可能剛啟動還在初始化、或此環境（虛擬機／遠端桌面）讀不到硬體感測器；若總覽頁也全是「—」，請重啟程式或以系統管理員執行再試。";

            // WinRing0：實際載入一次
            var result = await Task.Run(() =>
            {
                using var b = WinRing0Bridge.Create();
                return (b.Available, b.Error);
            });
            if (result.Available)
            {
                WinRing0Severity = Severity.Good;
                WinRing0Status = "已就緒";
                WinRing0Detail = "驅動已成功載入並完成一次 MSR 讀取驗證。頻率真相、MCA、SPD 直讀等底層功能可用。";
                if (!IsAdmin)
                {
                    _winRing0Detail += "（注意：本次以一般權限就載入成功，部分平台仍需管理員權限才能讀到完整 MSR。）";
                    OnPropertyChanged(nameof(WinRing0Detail));
                    OnPropertyChanged(nameof(HasWinRing0Detail));
                }
            }
            else if (!IsAdmin)
            {
                WinRing0Severity = Severity.Warning;
                WinRing0Status = "需要系統管理員";
                WinRing0Detail = "驅動無法在一般權限下建立服務。按「以系統管理員重新啟動」提權後再檢測一次。原因：" + result.Error;
            }
            else if (result.Error.Contains("找不到"))
            {
                WinRing0Severity = Severity.Serious;
                WinRing0Status = "缺檔";
                WinRing0Detail = result.Error + " WinRing0 來自內嵌的 LibreHardwareMonitorLib 0.9.4 套件；單檔發佈版不應出現此狀況，若持續出現請回報。";
            }
            else
            {
                WinRing0Severity = Severity.Critical;
                WinRing0Status = "載入失敗";
                WinRing0Detail = result.Error + " 常見原因：資安軟體攔截 WinRing0x64.sys（加入信任清單）、或 Windows 核心隔離／記憶體完整性（HVCI）封鎖未經微軟簽署的驅動——可在「Windows 安全性 › 裝置安全性 › 核心隔離」確認。";
            }

            bool allGood = result.Available && lhmOk;
            Summary = allGood
                ? "兩項驅動都已就緒，底層功能可用。"
                : "有項目未就緒——上方各列已標明原因與下一步。";
        }
        finally
        {
            IsProbing = false;
        }
    }

    /// <summary>以系統管理員身分重新啟動本程式（使用者明確按下才會觸發 UAC）。</summary>
    public void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) { Summary = "無法取得執行檔路徑，提權失敗。"; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current?.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Summary = "提權被取消或被拒，未重新啟動。";
        }
        catch (Exception ex)
        {
            Summary = "提權失敗：" + ex.Message;
        }
    }
}
