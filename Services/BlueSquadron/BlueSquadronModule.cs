// ✓ 已隨 v3.20／v3.21 建置與全套測試驗證（2026-11-08 REPORT-修復建置；原補丁自述的「未編譯」狀態解除）
//
// BlueSquadronModule.cs — 完整替換檔（patch-24，P0-12/13/14 主程式側接線）
// 覆蓋目標：Services/BlueSquadron/BlueSquadronModule.cs（原 497 行）
// 依賴：patch-24-Program.cs（Bridge 端 init 的 blocklistPath／status 的 modeImplemented／
//       caps 指令）、patch-25-DriverReadyService.cs（UpdateBlueSquadronDriver 狀態卡）。
//
// 修復內容（對照原檔）：
//   P0-13：init 帶 blocklistPath＝主程式自己的 Rules\byovd-blocklist.xml（Bridge 從
//   %LOCALAPPDATA% 解壓目錄跑，看不到主程式目錄；主程式已有 ByovdCompareService 的
//   預設路徑解析，直接複用）。回應的 byovdBlocklist 三態進狀態列——降級模式不沉默。
//   P0-12：讀回 blockingImplemented／逐防線 modeImplemented；有防線被設過「block」
//   而未實施時，狀態列明確標出（GUI 本頁沒有「阻擋」按鈕——誠實聲明文字另見
//   patch-24-BlueSquadronView.xaml）。
//   P0-14：init 成功後查 caps，交 DriverReadyService.UpdateBlueSquadronDriver 進
//   驅動就緒狀態卡——「部分就緒」如實顯示，不再被「驅動載入成功」掩蓋。

using System.Collections.ObjectModel;
using System.Globalization;   // 〔修復 v3.21〕審計 P2-3：時間解析不吃當地文化
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 藍色中隊核心模組總管——單一進入點，由 MainViewModel 持有、由 StartupSequence 觸發。
/// 內部持有安全態勢服務與威脅時間軸。
/// Phase 2：啟動 BlueSquadronBridge 進程，以 10 秒輪詢取得威脅事件，
/// 並訂閱即時警告推送，將結果填入 ThreatTimeline。
/// Bridge 找不到或崩潰時優雅降級——態勢評估照常運作，時間軸留空。
/// </summary>
public sealed class BlueSquadronModule : ObservableObject, IDisposable
{
    public BlueSquadronModule()
    {
        // 時間軸有無內容要能反映到畫面（空狀態提示）。
        // 繫結 Collection.Count 配 bool 轉換器是行不通的——Count 是 int，
        // 轉換器只認 bool，會恆回同一個結果且不報錯。
        ThreatTimeline.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasThreatEvents));
            OnPropertyChanged(nameof(TimelineHint));
        };
    }

    /// <summary>時間軸是否有事件（空的時候畫面顯示提示）。</summary>
    public bool HasThreatEvents => ThreatTimeline.Count > 0;

    /// <summary>時間軸空狀態提示：已連線時顯示監測中，未連線時顯示啟動中，與連線狀態同步。</summary>
    public string TimelineHint => HasThreatEvents
        ? ""
        : BridgeAvailable
            ? "即時偵測中…（尚無事件）"
            : "即時偵測事件（正在連線 BlueSquadronBridge 守護進程…）";

    public SecurityPostureService Posture { get; } = new();

    public ObservableCollection<ThreatEvent> ThreatTimeline { get; } = [];

    public ObservableCollection<DefenseLineStatus> DefenseLines { get; } =
    [
        new() { Id = "dma",      Name = "DMA 與記憶體保護", Active = true },
        new() { Id = "firmware", Name = "韌體與啟動鏈",     Active = true },
        new() { Id = "cpu",      Name = "CPU 緩解與記憶體防護", Active = true },
        new() { Id = "storage",  Name = "儲存與資料",       Active = true },
        new() { Id = "drivers",  Name = "驅動與對抗",       Active = true },
        new() { Id = "surface",  Name = "系統攻擊面",       Active = true },
    ];

    private bool _bridgeAvailable;
    public bool BridgeAvailable
    {
        get => _bridgeAvailable;
        private set
        {
            if (SetProperty(ref _bridgeAvailable, value))
                OnPropertyChanged(nameof(TimelineHint));
        }
    }

    private string _bridgeStatus = "尚未連線";
    public string BridgeStatus
    {
        get => _bridgeStatus;
        private set => SetProperty(ref _bridgeStatus, value);
    }

    private bool _bridgeEnabled;   // 預設關：開關由設定檔決定，未設定（新安裝）時不啟動守護進程
    /// <summary>安全頁「守護進程」滑動開關：關閉即停止進程與輪詢（唯讀態勢評估照常），選擇記入設定檔。</summary>
    public bool BridgeEnabled
    {
        get => _bridgeEnabled;
        set
        {
            if (_bridgeEnabled == value) return;
            _bridgeEnabled = value;
            OnPropertyChanged(nameof(BridgeEnabled));
            if (_settings is not null && _settings.BlueSquadronEnabled != value)
                _settings.BlueSquadronEnabled = value;
            if (value)
            {
                if (Engine is null) _ = Task.Run(TryStartBridge);
            }
            else
            {
                StopBridge();
            }
        }
    }

    private SettingsService? _settings;
    private MainViewModel? _vm;   // P0-14：caps 結果要交 DriverReadyService 狀態卡

    // ── Bridge 生命週期 ──────────────────────────────────────────

    public BlueSquadronEngine? Engine { get; private set; }
    private CancellationTokenSource? _pollCts;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>背景初始化：待其他安全服務就緒後首次評估，然後嘗試啟動 Bridge。</summary>
    private int _initStarted;

    public async Task InitializeAsync(MainViewModel vm)
    {
        // 一次性保護：StartupSequence 重跑（一鍵初始化）時不可重複啟動 Bridge 進程
        if (Interlocked.Exchange(ref _initStarted, 1) == 1) return;

        _dispatcher = Application.Current?.Dispatcher
                      ?? Dispatcher.CurrentDispatcher;
        _vm = vm;

        // 套用已保存的開關狀態：上次關閉守護進程的話，這次開機不再自動拉起
        _settings = vm.Settings;
        if (_bridgeEnabled != _settings.BlueSquadronEnabled)
        {
            _bridgeEnabled = _settings.BlueSquadronEnabled;
            OnPropertyChanged(nameof(BridgeEnabled));
        }

        await Task.Delay(3000);  // 讓 PlatformTrust、CpuSecurity、DriverAudit 等先跑完
        try
        {
            Posture.Refresh(vm);
            BridgeStatus = "User-mode 偵測就緒（Phase 1）";
        }
        catch
        {
            BridgeStatus = "初始化失敗——安全評估為附加功能，不影響其他頁面。";
        }

        // Phase 2：啟動 Bridge 進程（開關關閉時只做唯讀態勢評估）
        if (!_bridgeEnabled)
        {
            BridgeStatus = "守護進程已關閉（唯讀態勢評估照常）";
            return;
        }
        await Task.Run(() => TryStartBridge()).ConfigureAwait(false);
    }

    // ── Bridge 啟動 ─────────────────────────────────────────────

    private int _starting;

    /// <summary>帶重入保護的啟動入口：快速來回切換開關或初始化重跑時不會開出第二個進程。</summary>
    private void TryStartBridge()
    {
        if (Engine is not null) return;
        if (Interlocked.Exchange(ref _starting, 1) == 1) return;
        try { TryStartBridgeCore(); }
        finally { Interlocked.Exchange(ref _starting, 0); }
    }

    private void TryStartBridgeCore()
    {
        BlueSquadronEngine engine;
        try
        {
            engine = new BlueSquadronEngine();
        }
        catch (System.IO.FileNotFoundException)
        {
            RunOnUI(() => BridgeStatus = "守護進程未安裝——將 BlueSquadronBridge.exe 與本程式放同一資料夾即會自動啟用");
            return;
        }
        catch
        {
            RunOnUI(() => BridgeStatus = "Bridge 引擎建立失敗");
            return;
        }

        bool ok;
        try
        {
            ok = engine.Start();
        }
        catch (Exception ex)
        {
            engine.Dispose();
            RunOnUI(() => BridgeStatus = $"Bridge 啟動例外: {ex.Message}");
            return;
        }

        if (!ok)
        {
            string err = engine.LastError;
            engine.Dispose();
            RunOnUI(() =>
            {
                BridgeAvailable = false;
                BridgeStatus = $"Bridge 啟動失敗: {err}";
            });
            return;
        }

        Engine = engine;

        // 送 init 指令做握手
        try
        {
            // init 要枚舉全部驅動（數百筆 WMI 查詢），主程式啟動高峰期可能超過 8 秒；
            // 逾時會觸發 MarkDead 直接殺掉進程，故給足 30 秒。
            // P0-13：帶上主程式自己的封鎖清單路徑——Bridge 從 %LOCALAPPDATA% 解壓目錄
            // 跑，看不到主程式目錄；ByovdCompareService 的預設路徑解析直接複用。
            // 沒有清單檔時傳空字串，Bridge 如實進降級模式（不沉默）。
            var initExtra = new Dictionary<string, object>
            {
                ["blocklistPath"] = ByovdCompareService.DefaultBlocklistPath() ?? "",
            };
            var initResp = engine.SendCmd("init", initExtra, 30000);
            bool driverLoaded = false;
            int baselineChanges = 0;
            string blocklistText = "封鎖清單未回報";
            bool anyUnimplementedBlock = false;
            if (initResp is { } jr)
            {
                if (jr.TryGetProperty("driverLoaded", out var dl))
                    driverLoaded = dl.ValueKind == JsonValueKind.True;
                if (jr.TryGetProperty("baselineChanges", out var bc)
                    && bc.TryGetInt32(out int n))
                    baselineChanges = n;
                if (jr.TryGetProperty("byovdBlocklist", out var bl)
                    && bl.ValueKind == JsonValueKind.Object)
                {
                    int rules = bl.TryGetProperty("rules", out var rEl)
                        && rEl.TryGetInt32(out int rn) ? rn : 0;
                    bool degraded = bl.TryGetProperty("degraded", out var dEl)
                        && dEl.ValueKind == JsonValueKind.True;
                    blocklistText = degraded
                        ? $"封鎖清單=降級模式（{rules} 條後備）"
                        : $"封鎖清單=全量 {rules} 條";
                }
            }

            // P0-12：讀一次 status——有防線設過「block」但未實施就明說
            var statusResp = engine.SendCmd("status", 10000);
            if (statusResp is { } sr
                && sr.TryGetProperty("lines", out var ls)
                && ls.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in ls.EnumerateArray())
                {
                    bool modeIsBlock = l.TryGetProperty("mode", out var mEl)
                        && mEl.GetString() == "block";
                    bool implemented = l.TryGetProperty("modeImplemented", out var impEl)
                        && impEl.ValueKind == JsonValueKind.True;
                    if (modeIsBlock && !implemented) { anyUnimplementedBlock = true; break; }
                }
            }

            RunOnUI(() =>
            {
                BridgeAvailable = true;
                string mode = anyUnimplementedBlock
                    ? "（注意：有防線設為「阻擋」但未實施——Phase 1 僅偵測）"
                    : "（Phase 1 僅偵測）";
                BridgeStatus = $"Bridge 已連線（驅動={driverLoaded}, 基線變更={baselineChanges}, " +
                               $"{blocklistText}）{mode}";
            });
        }
        catch
        {
            // init 沒回應但進程在跑——仍可用
            RunOnUI(() =>
            {
                BridgeAvailable = engine.Connected;
                BridgeStatus = "Bridge 已連線（init 未回應）";
            });
        }

        // 訂閱即時警告推送
        engine.AlertReceived += OnAlertReceived;

        // P0-14：能力帳單進驅動就緒狀態卡（「部分就緒」如實顯示）
        try
        {
            var capsResp = engine.SendCmd("caps", 10000);
            if (capsResp is { } cr)
            {
                var captured = cr;
                RunOnUI(() => _vm?.DriverReady.UpdateBlueSquadronDriver(captured));
            }
        }
        catch { /* caps 查不到不影響連線——狀態卡維持「尚未檢測」以外的如實說明 */ }

        // 啟動背景輪詢迴圈
        StartPollingLoop();
    }

    /// <summary>
    /// P3-19：明確的卸載路徑——送 "unload" 給 Bridge（停止並刪除核心服務）。
    /// Bridge 端之後的 status／init 如實回 driverLoaded=false；本方法只負責送達與回報，
    /// 是否提供 UI 入口（設定頁／藍色中隊頁按鈕）屬產品決策——路徑先通，入口不虛構。
    /// </summary>
    public async Task<bool> UnloadDriverAsync()
    {
        var engine = Engine;
        if (engine is null || !engine.Connected) return false;
        try
        {
            var resp = await Task.Run(() => engine.SendCmd("unload", 10000));
            bool ok = resp is { } r && r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            RunOnUI(() => BridgeStatus = ok
                ? "BlueSquadron 核心服務已卸載（user-mode 偵測防線仍在運行）。"
                : "卸載指令送達，但 Bridge 回報未成功——以 caps／status 複核。");
            return ok;
        }
        catch
        {
            RunOnUI(() => BridgeStatus = "卸載指令失敗（Bridge 無回應或已斷線）。");
            return false;
        }
    }

    // ── 輪詢迴圈 ────────────────────────────────────────────────

    private void StartPollingLoop()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();

        var cts = new CancellationTokenSource();
        _pollCts = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token)
                              .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                PollThreatsOnce(cts.Token);
            }
        }, cts.Token);
    }

    private void PollThreatsOnce(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (Engine is not { Connected: true } engine)
        {
            RunOnUI(() =>
            {
                BridgeAvailable = false;
                BridgeStatus = "Bridge 處理程序已結束";
            });
            _pollCts?.Cancel();
            return;
        }

        try
        {
            var resp = engine.SendCmd("threats", 5000);
            if (resp is not { } json) return;
            if (!json.TryGetProperty("threats", out var arr)) return;
            if (arr.ValueKind != JsonValueKind.Array) return;

            foreach (var item in arr.EnumerateArray())
            {
                var te = ParseThreatFromPoll(item);
                if (te is null) continue;

                var captured = te;
                RunOnUI(() =>
                {
                    // 以 (Timestamp, Title) 去重——Bridge 回傳的 threats 沒有穩定 Id
                    bool dup = false;
                    foreach (var existing in ThreatTimeline)
                    {
                        if (existing.Timestamp == captured.Timestamp
                            && existing.Title == captured.Title)
                        {
                            dup = true;
                            break;
                        }
                    }
                    if (!dup) { ThreatTimeline.Insert(0, captured); TrimThreatTimeline(); }
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BlueSquadron] Poll error: {ex.Message}");
        }
    }

    // ── 即時警告處理 ────────────────────────────────────────────

    private void OnAlertReceived(JsonElement alertJson)
    {
        var te = ParseThreatFromAlert(alertJson);
        if (te is null) return;

        var captured = te;
        RunOnUI(() =>
        {
            // 警告可能在下次輪詢中重複出現，以 (Timestamp, Title) 去重
            bool dup = false;
            foreach (var existing in ThreatTimeline)
            {
                if (existing.Timestamp == captured.Timestamp
                    && existing.Title == captured.Title)
                {
                    dup = true;
                    break;
                }
            }
            if (!dup) { ThreatTimeline.Insert(0, captured); TrimThreatTimeline(); }
        });
    }

    /// <summary>〔修復 v3.21〕審計 P2-2：時間軸上限 500——Bridge 常駐數天的事件只進不出，
    /// ObservableCollection 會無界成長，去重線性掃描與 UI 虛擬化同時劣化。</summary>
    private const int TimelineCap = 500;

    private void TrimThreatTimeline()
    {
        while (ThreatTimeline.Count > TimelineCap)
            ThreatTimeline.RemoveAt(ThreatTimeline.Count - 1);
    }

    // ── JSON → ThreatEvent ──────────────────────────────────────

    /// <summary>
    /// 從 Bridge threats 指令回應解析。
    /// 格式：{ "ts":"...", "severity":"...", "line":"...", "title":"...", "detail":"..." }
    /// </summary>
    private static ThreatEvent? ParseThreatFromPoll(JsonElement el)
    {
        try
        {
            // 〔修復 v3.21〕審計 P2-3：Bridge 送 ISO 8601——以 InvariantCulture＋RoundtripKind
            // 解析（原寫法吃當地文化，zh-TW 機器上多半直接失敗）。解析失敗不拿「現在」冒充
            // 事件時間：排序顯示仍取收到當下的時間，但標題加掛「（時間待校）」如實標記。
            var dto = DateTimeOffset.Now;   // 先賦值（&& 短路時 TryParse 不執行）
            bool tsParsed =
                el.TryGetProperty("ts", out var tsEl)
                && DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture,
                                           DateTimeStyles.RoundtripKind, out dto);
            DateTimeOffset ts = tsParsed ? dto : DateTimeOffset.Now;

            string severity = el.TryGetProperty("severity", out var svEl)
                              ? svEl.GetString() ?? "advisory" : "advisory";

            string line = el.TryGetProperty("line", out var lnEl)
                          ? lnEl.GetString() ?? "" : "";

            string title = el.TryGetProperty("title", out var tiEl)
                           ? tiEl.GetString() ?? "" : "";

            string detail = el.TryGetProperty("detail", out var dtEl)
                            ? dtEl.GetString() ?? "" : "";

            if (!tsParsed) title += "（時間待校）";
            return new ThreatEvent(ts, MapSeverity(severity), line, title, detail);
        }
        catch { return null; }
    }

    /// <summary>
    /// 從 Bridge 即時推送的 alert JSON 解析。
    /// 推送行含 "alert":true，具體欄位由 EtwWatcher 決定。
    /// 至少會有 severity / title / detail；沒有 line 時歸為 "drivers"。
    /// </summary>
    private static ThreatEvent? ParseThreatFromAlert(JsonElement el)
    {
        try
        {
            string severity = el.TryGetProperty("severity", out var svEl)
                              ? svEl.GetString() ?? "warning" : "warning";

            string line = el.TryGetProperty("line", out var lnEl)
                          ? lnEl.GetString() ?? "drivers" : "drivers";

            string title = el.TryGetProperty("title", out var tiEl)
                           ? tiEl.GetString() ?? "即時警告" : "即時警告";

            string detail = el.TryGetProperty("detail", out var dtEl)
                            ? dtEl.GetString() ?? "" : "";

            return new ThreatEvent(DateTimeOffset.Now, MapSeverity(severity),
                                   line, title, detail);
        }
        catch { return null; }
    }

    private static SecuritySeverity MapSeverity(string s) => s.ToLowerInvariant() switch
    {
        "critical" => SecuritySeverity.Critical,
        "warning"  => SecuritySeverity.Warning,
        "advisory" => SecuritySeverity.Advisory,
        "good"     => SecuritySeverity.Good,
        _          => SecuritySeverity.Advisory,
    };

    // ── 停止 Bridge ─────────────────────────────────────────────

    /// <summary>
    /// 停止 Bridge 進程與輪詢迴圈。可多次呼叫、未啟動時也安全。
    /// </summary>
    public void StopBridge()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        if (Engine is { } eng)
        {
            eng.AlertReceived -= OnAlertReceived;
            eng.Dispose();
            Engine = null;
        }

        RunOnUI(() =>
        {
            BridgeAvailable = false;
            BridgeStatus = "Bridge 已停止";
        });
    }

    // ── 防線狀態更新 ────────────────────────────────────────────

    /// <summary>
    /// 從態勢評估結果更新五防線即時狀態。
    /// 由 SecurityPostureService 在評估完成後呼叫。
    /// </summary>
    public void UpdateDefenseLines(SecurityPosture? posture)
    {
        if (posture is null) return;
        foreach (var cat in posture.Categories)
        {
            var line = FindLine(cat.Id);
            if (line is null) continue;
            // 一條防線如果整條都讀不到，分數是 0——但那是「沒量到」，不是「全關」。
            // 直接寫成「危險，多項未啟用」就是同一個老問題的縮小版。
            if (cat.AssessedWeight == 0)
            {
                line.Score = 0;
                line.Severity = SecuritySeverity.Advisory;
                line.Summary = "未評估——讀不到（權限、驅動或平台不支援）";
                continue;
            }

            line.Score = cat.Score;
            line.Severity = cat.Severity;
            bool partial = cat.TotalWeight > 0 && cat.AssessedWeight < cat.TotalWeight * 8 / 10;
            line.Summary = (cat.Score switch
            {
                >= 90 => "防禦良好",
                >= 70 => "尚可，有改進空間",
                >= 50 => "偏弱，建議強化",
                _ => "危險，多項未啟用",
            }) + (partial ? "（部分項目未評估）" : "");
        }
    }

    private DefenseLineStatus? FindLine(string id)
    {
        foreach (var l in DefenseLines)
            if (l.Id == id) return l;
        return null;
    }

    // ── UI 執行緒輔助 ───────────────────────────────────────────

    private void RunOnUI(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }

    // ── IDisposable ─────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopBridge();
    }
}
