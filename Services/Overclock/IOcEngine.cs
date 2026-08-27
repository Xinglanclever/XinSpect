namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 超頻引擎抽象。真正的寫入由 XtuOcEngine 透過反射呼叫 Intel XTU SDK 完成；
// 找不到 SDK 或平台不支援時，改用 NullOcEngine 誠實回報「不可用」。
// UI 只認這個介面，不直接碰反射。
// ───────────────────────────────────────────────────────────────────────────

public enum OcEngineStatus
{
    NotInitialized,   // 尚未初始化
    Ready,            // 已就緒，可真實讀寫
    Unsupported,      // 找到 SDK 但此平台不支援超頻
    Missing,          // 找不到 Intel XTU SDK
    Failed,           // 初始化過程發生例外
}

/// <summary>超頻引擎介面。所有寫入方法皆以「回讀 ActiveValue」作為成功與否的證據。</summary>
public interface IOcEngine : IDisposable
{
    string Name { get; }
    OcEngineStatus Status { get; }
    string StatusMessage { get; }

    // ── 能力探測（Initialize 後有效）──────────────────────────────────────
    bool CoreTunable { get; }
    bool BclkTunable { get; }
    bool MemoryTunable { get; }
    bool CacheTunable { get; }
    bool SpeedOptimizerSupported { get; }
    int ProcessorFamily { get; }

    // ── 硬體看門狗狀態（唯讀，SDK 無法由軟體武裝硬體看門狗）────────────────
    bool WatchdogPresent { get; }
    bool WatchdogRunning { get; }
    bool WatchdogFailed { get; }

    /// <summary>載入 SDK、初始化並列舉可調控制項。回傳是否就緒。可在背景執行緒呼叫。</summary>
    bool Initialize();

    /// <summary>列舉到的真實可調控制項（僅數值區間型）。</summary>
    IReadOnlyList<OcKnob> Knobs { get; }

    /// <summary>自硬體重新讀取所有旋鈕的現值（Active）。</summary>
    void RefreshActives();

    /// <summary>透過 SDK 讀取即時核心電壓（V）；不可用時回 null。</summary>
    double? ReadCoreVoltage();

    /// <summary>讀取名稱含指定字樣的 XTU 監控值（如 VRM 溫度 / 有效頻率，LHM 常缺）；不可用時回 null。</summary>
    double? ReadMonitor(params string[] nameContains);

    /// <summary>套用單一旋鈕：Tune → ApplyChanges → 回讀 ActiveValue 驗證。</summary>
    OcApplyResult Apply(OcKnob knob, double value);

    /// <summary>取消所有尚未套用的變更（DiscardChanges）。</summary>
    bool Discard();

    /// <summary>把所有可寫旋鈕還原為預設值（Default）。</summary>
    OcApplyResult RestoreDefaults();

    /// <summary>設定「開機還原使用者值」（BSOD/斷電後回穩的軟體層保險）。</summary>
    void SetBootRestore(bool on);

    // ── Intel Speed Optimizer（可逆的一鍵自動超頻）────────────────────────
    int SpeedOptimizerState { get; }
    OcApplyResult SetSpeedOptimizer(bool on, bool extreme);
}
