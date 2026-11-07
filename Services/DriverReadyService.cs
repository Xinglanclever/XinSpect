// ⚠ 未編譯——需作者機驗證（dotnet build 命令見 patch-25.md）
//
// DriverReadyService.cs — 完整替換檔（patch-25，P0-14：BlueSquadron 驅動進就緒狀態卡）
// 覆蓋目標：Services/DriverReadyService.cs（原 155 行）
// 配套：patch-24-BlueSquadronModule.cs（init 後查 caps 交給 UpdateBlueSquadronDriver）、
//       patch-24-Program.cs（Bridge 的 caps 指令）。
//
// 修復內容（對照原檔）：
//   新增第三列「BlueSquadron 核心防護」狀態（BsSeverity／BsStatus／BsDetail），
//   資料源是 Bridge caps 指令回的能力帳單（FeatureMask＋逐步 NTSTATUS＋MSR 快照活體
//   健康）。四種如實狀態：
//     全量（4/4 回呼＋MSR 健康）→ Good；
//     部分就緒（驅動載入成功但 K/4 回呼）→ Warning，逐項列哪些沒註冊、原始 NTSTATUS；
//     全未註冊 → Serious（防護等於零，不因「載入成功」假裝在跑）；
//     查不到（Bridge 沒連上／驅動未載入／版本不符）→ Neutral／Advisory，附原因。
//   既有 WinRing0／LHM 兩列逐行不動。Bs* 進 SettingsView 的 XAML 繫結屬後續批次
//   （見 patch-25.md 未覆蓋清單）——本檔先讓資料可用，Summary 亦會帶一句核心防護狀態。

using System.Security.Principal;
using System.Text.Json;
using System.Windows;

namespace XinSpect;

/// <summary>
/// 驅動就緒器：把「底層驅動能不能用、為什麼不能、要怎麼修」集中在一張卡上講清楚。
/// <list type="bullet">
/// <item>WinRing0（LHM 0.9.4 內嵌的簽章驅動）——MSR 讀寫（頻率真相、MCA、免疫位元…）與 SMBus／SPD 直讀的基礎。</item>
/// <item>LHM 感測引擎（LibreHardwareMonitorLib 0.9.6）——溫度／負載／電壓／風扇等即時讀值。</item>
/// <item>BlueSquadron 核心防護（BlueSquadron.sys）——四個核心回呼＋MSR 快照；能力以
/// 驅動 GET_CAPS 的 FeatureMask 為準，「部分就緒」如實顯示（P0-14）。</item>
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

    // ── P0-14：BlueSquadron 核心防護列 ──────────────────────────────────

    private Severity _bsSeverity = Severity.Neutral;
    public Severity BsSeverity { get => _bsSeverity; private set => SetProperty(ref _bsSeverity, value); }

    private string _bsStatus = "尚未檢測";
    public string BsStatus { get => LanguageService.T(_bsStatus); private set { _bsStatus = value; OnPropertyChanged(); } }

    private string _bsDetail = "";
    public string BsDetail { get => LanguageService.T(_bsDetail); private set { _bsDetail = value; OnPropertyChanged(); } }
    public bool HasBsDetail => _bsDetail.Length > 0;

    private bool _probing;
    public bool IsProbing { get => _probing; private set { if (SetProperty(ref _probing, value)) OnPropertyChanged(nameof(CanProbe)); } }
    public bool CanProbe => !_probing;

    private string _summary = "按「檢測驅動」以實際載入一次驅動並回報真實狀態。";
    public string Summary { get => LanguageService.T(_summary); private set { _summary = value; OnPropertyChanged(); } }

    public DriverReadyService()
    {
        // 語言切換時重新通知顯示字串；屬性 getter 會以新語言即時轉換。
        LanguageService.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WinRing0Status));
        OnPropertyChanged(nameof(WinRing0Detail));
        OnPropertyChanged(nameof(LhmStatus));
        OnPropertyChanged(nameof(LhmDetail));
        OnPropertyChanged(nameof(BsStatus));
        OnPropertyChanged(nameof(BsDetail));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// 把 Bridge「caps」指令的能力帳單寫進狀態卡（由 BlueSquadronModule 在 Bridge
    /// init 成功後呼叫）。ok=false／缺欄位＝如實標「查不到」＋原因，不猜。
    /// </summary>
    public void UpdateBlueSquadronDriver(JsonElement capsResp)
    {
        try
        {
            if (capsResp.ValueKind != JsonValueKind.Object
                || !capsResp.TryGetProperty("ok", out var okEl)
                || okEl.ValueKind != JsonValueKind.True)
            {
                string err = capsResp.ValueKind == JsonValueKind.Object
                    && capsResp.TryGetProperty("error", out var eEl)
                    && eEl.ValueKind == JsonValueKind.String
                        ? eEl.GetString() ?? "" : "回應格式不符";
                BsSeverity = Severity.Neutral;
                BsStatus = "無法查詢";
                BsDetail = $"核心驅動能力帳單查不到：{err}。" +
                           "user-mode 偵測（ETW）仍在運行；核心防護是否在跑無法確認——如實標示，不假設。";
                OnPropertyChanged(nameof(HasBsDetail));
                return;
            }

            int featureMask = capsResp.TryGetProperty("featureMask", out var fmEl)
                && fmEl.TryGetInt32(out int fm) ? fm : -1;
            if (featureMask < 0)
            {
                // 〔修復建置 2026-11-07〕Severity 沒有 Advisory 成員（見 Converters.cs 註解：
                // 有 Advisory 的是 SecuritySeverity）。「回應不完整」屬查不到的變體，歸 Neutral。
                BsSeverity = Severity.Neutral;
                BsStatus = "回應不完整";
                BsDetail = "caps 回應缺 featureMask 欄——Bridge 與驅動版本可能不對應，如實標示不猜。";
                OnPropertyChanged(nameof(HasBsDetail));
                return;
            }

            bool loadImage = (featureMask & 0x01) != 0;
            bool process = (featureMask & 0x02) != 0;
            bool registry = (featureMask & 0x04) != 0;
            bool obProtect = (featureMask & 0x08) != 0;
            bool msrOk = capsResp.TryGetProperty("msrSnapshotOk", out var msrEl)
                && msrEl.ValueKind == JsonValueKind.True;
            int registered = (loadImage ? 1 : 0) + (process ? 1 : 0) + (registry ? 1 : 0) + (obProtect ? 1 : 0);

            // 逐項失敗清單（含原始 NTSTATUS——「為什麼沒註冊上」要能回答）
            var failures = new List<string>();
            void Check(string name, bool ok, string statusKey)
            {
                if (ok) return;
                string hex = capsResp.TryGetProperty("statuses", out var sts)
                    && sts.ValueKind == JsonValueKind.Object
                    && sts.TryGetProperty(statusKey, out var st)
                    && st.ValueKind == JsonValueKind.String
                        ? st.GetString() ?? "?" : "?";
                failures.Add($"{name}（NTSTATUS {hex}）");
            }
            Check("驅動/DLL 載入回呼", loadImage, "loadImage");
            Check("進程建立回呼", process, "process");
            Check("登錄檔回呼", registry, "registry");
            Check("物件存取保護（LSASS）", obProtect, "obProtect");

            string msrText = capsResp.TryGetProperty("msrActiveCpus", out var acEl)
                && acEl.TryGetInt32(out int cpus)
                    ? $"MSR 快照覆蓋 {cpus} 核" : "MSR 快照";
            if (!msrOk) msrText += "（未就緒——基線未建立或 init 失敗，竄改比對不在跑）";

            if (registered == 4 && msrOk)
            {
                BsSeverity = Severity.Good;
                BsStatus = "已就緒（核心防護全量）";
                BsDetail = $"四個核心回呼全數註冊成功；{msrText}。" +
                           "注意：這是偵測能力就緒，不代表有攔截——Phase 1 所有防線只偵測不阻擋。";
            }
            else if (registered == 0)
            {
                BsSeverity = Severity.Serious;
                BsStatus = "核心防護未生效";
                BsDetail = "核心驅動已載入，但四個回呼全部註冊失敗：核心防護等於零——" +
                           "只剩 user-mode 偵測（ETW）。常見原因：HVCI／記憶體完整性或資安軟體封鎖未經微軟簽署的驅動。" +
                           (failures.Count > 0 ? "失敗明細：" + string.Join("、", failures) : "");
            }
            else
            {
                BsSeverity = Severity.Warning;
                BsStatus = $"部分就緒（{registered}/4 回呼）";
                BsDetail = $"{msrText}；未註冊：{string.Join("、", failures)}。" +
                           "這些防護沒有在跑——不會因為驅動整體載入成功就假裝在跑。" +
                           "常見原因：HVCI／資安軟體對 ObRegisterCallbacks 的限制（0xC0000024）、權限（0xC0000022）。";
            }
            OnPropertyChanged(nameof(HasBsDetail));
        }
        catch (Exception ex)
        {
            BsSeverity = Severity.Neutral;   // 解析失敗＝查不到，同上歸 Neutral
            BsStatus = "狀態解析失敗";
            BsDetail = $"caps 回應解析例外：{ex.Message}——如實標示，不假設就緒。";
            OnPropertyChanged(nameof(HasBsDetail));
        }
    }

    /// <summary>以實際載入一次 WinRing0 的方式偵測（經 KernelBackendHost 路由；宿主持有會話至行程結束或模式切換，暴露窗口語義變更見 patch-21.md 第 3 節）。</summary>
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

            // WinRing0／後端鏈：改走 KernelBackendHost——模式（Auto／SandboxOnly／UniversalAllowed）
            // 先裁決信任層級，再決定要不要載驅動。Route 觸發宿主懶初始化；Snapshot 把「為什麼不在鏈上」
            // 帶回來（PawnIO 缺席、驅動建立失敗、被模式排除，各有各的原因字串）。
            // 注意：宿主持有的會話不在此 Dispose——橋接本身是引用計數，探測不再是「測完即釋」，
            // 暴露窗口的語義變更見方法頭註與 patch-21.md 第 3 節。
            var probe = await Task.Run(() =>
            {
                var route = KernelBackendHost.Route(KernelCapability.MsrRead);
                string? rowReason = null;
                foreach (var r in KernelBackendHost.Snapshot())
                    if (r.Desc.Id == "winring0") { rowReason = r.Reason; break; }
                return (Available: route.Backend is not null,
                        Error: rowReason ?? route.Explanation,
                        Backend: route.Backend is { } b ? b.DisplayName + "・" + b.Tier : "無");
            });
            var result = (Available: probe.Available, Error: probe.Error);
            if (result.Available)
            {
                WinRing0Severity = Severity.Good;
                WinRing0Status = "已就緒";
                WinRing0Detail = "驅動已成功載入並完成一次 MSR 讀取驗證。頻率真相、MCA、SPD 直讀等底層功能可用。服務後端：" + probe.Backend + "。";
                if (!IsAdmin)
                {
                    _winRing0Detail += "（注意：本次以一般權限就載入成功，部分平台仍需管理員權限才能讀到完整 MSR。）";
                    OnPropertyChanged(nameof(WinRing0Detail));
                    OnPropertyChanged(nameof(HasWinRing0Detail));
                }
            }
            else if (vm.Settings.KernelBackendMode == 1)
            {
                // SandboxOnly 是使用者刻意的選擇，不是故障：狀態用中性色、原因講清楚，
                // 不套「載入失敗」的紅色與 HVCI 排障文案——那是給「想載載不了」的人看的。
                // 〔修復 v3.20〕原文案指向不存在的「設定頁切換入口」——SettingsView 對
                // KernelBackendMode 沒有任何 UI，照著文案找是死路。如實說明：本版須手動改設定檔。
                WinRing0Severity = Severity.Neutral;
                WinRing0Status = "依模式未載入";
                WinRing0Detail = "設定為「僅沙箱」：WinRing0 萬能鑰匙刻意不載入。MSR／MMIO 將整片三態。" +
                                 "目前版本尚未提供設定頁的後端切換入口；請手動編輯設定檔中的 " +
                                 "KernelBackendMode（0=自動／1=SandboxOnly／2=允許萬能鑰匙）後重啟。";
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
            // P0-14：Summary 帶上核心防護列現況（Bs* 若尚未查過，如實說「未查」）
            string bsText = _bsStatus == "尚未檢測"
                ? "核心防護：尚未查詢（守護進程未連線或尚未回報）"
                : $"核心防護：{_bsStatus}";
            Summary = (allGood
                ? "兩項驅動都已就緒，底層功能可用。"
                : "有項目未就緒——上方各列已標明原因與下一步。") + $" {bsText}";
        }
        catch (Exception ex)
        {
            // 〔修復 v3.20〕呼叫端是 async void（設定頁按鈕）——原碼只有 try/finally 沒有 catch，
            // 例外會沿 async void 直接終結行程。如實寫進總結；各列維持當下值（可能只更新到一半，
            // 那也是本次真正的進度），不假裝「檢測完成」。IsProbing 由既有的 finally 復原。
            Summary = "驅動就緒檢測失敗：" + ex.Message + "（各列維持檢測中斷前的狀態，請重試）";
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
