using System.Collections.ObjectModel;
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
        ThreatTimeline.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasThreatEvents));
    }

    /// <summary>時間軸是否有事件（空的時候畫面顯示提示）。</summary>
    public bool HasThreatEvents => ThreatTimeline.Count > 0;

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
        private set => SetProperty(ref _bridgeAvailable, value);
    }

    private string _bridgeStatus = "尚未連線";
    public string BridgeStatus
    {
        get => _bridgeStatus;
        private set => SetProperty(ref _bridgeStatus, value);
    }

    // ── Bridge 生命週期 ──────────────────────────────────────────

    public BlueSquadronEngine? Engine { get; private set; }
    private CancellationTokenSource? _pollCts;
    private Dispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>背景初始化：待其他安全服務就緒後首次評估，然後嘗試啟動 Bridge。</summary>
    public async Task InitializeAsync(MainViewModel vm)
    {
        _dispatcher = Application.Current?.Dispatcher
                      ?? Dispatcher.CurrentDispatcher;

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

        // Phase 2：啟動 Bridge 進程
        await Task.Run(() => TryStartBridge()).ConfigureAwait(false);
    }

    // ── Bridge 啟動 ─────────────────────────────────────────────

    private void TryStartBridge()
    {
        BlueSquadronEngine engine;
        try
        {
            engine = new BlueSquadronEngine();
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
            var initResp = engine.SendCmd("init", 8000);
            bool driverLoaded = false;
            int baselineChanges = 0;
            if (initResp is { } jr)
            {
                if (jr.TryGetProperty("driverLoaded", out var dl))
                    driverLoaded = dl.ValueKind == JsonValueKind.True;
                if (jr.TryGetProperty("baselineChanges", out var bc)
                    && bc.TryGetInt32(out int n))
                    baselineChanges = n;
            }

            RunOnUI(() =>
            {
                BridgeAvailable = true;
                BridgeStatus = $"Bridge 已連線（驅動={driverLoaded}, 基線變更={baselineChanges}）";
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

        // 啟動背景輪詢迴圈
        StartPollingLoop();
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
                    if (!dup) ThreatTimeline.Insert(0, captured);
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
            if (!dup) ThreatTimeline.Insert(0, captured);
        });
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
            DateTimeOffset ts =
                el.TryGetProperty("ts", out var tsEl)
                && DateTimeOffset.TryParse(tsEl.GetString(), out var dto)
                    ? dto : DateTimeOffset.Now;

            string severity = el.TryGetProperty("severity", out var svEl)
                              ? svEl.GetString() ?? "advisory" : "advisory";

            string line = el.TryGetProperty("line", out var lnEl)
                          ? lnEl.GetString() ?? "" : "";

            string title = el.TryGetProperty("title", out var tiEl)
                           ? tiEl.GetString() ?? "" : "";

            string detail = el.TryGetProperty("detail", out var dtEl)
                            ? dtEl.GetString() ?? "" : "";

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
            line.Score = cat.Score;
            line.Severity = cat.Severity;
            line.Summary = cat.Score switch
            {
                >= 90 => "防禦良好",
                >= 70 => "尚可，有改進空間",
                >= 50 => "偏弱，建議強化",
                _ => "危險，多項未啟用",
            };
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
