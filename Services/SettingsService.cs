using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>集中式使用者設定：更新間隔、開機自啟、預設紀年、感測器記錄與警示閾值。
/// 以 JSON 持久化於 %APPDATA%\XinSpect\settings.json，任一屬性變更即存檔。</summary>
public sealed class SettingsService : ObservableObject
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
    /// <summary>設定檔的實際位置（設定頁會據實顯示這條路徑，而不是寫死一句「已儲存」）。</summary>
    public static string FilePath { get; } = Path.Combine(Dir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "XinSpect";
    private const string TaskName = "XinSpect";   // 工作排程器上的排定名稱

    private bool _loading;   // 載入期間不觸發存檔，避免逐欄寫檔

    // ── 一般 ─────────────────────────────────────────────
    private int _updateIntervalSec = 1;
    /// <summary>感測器輪詢與時鐘更新間隔（秒），1–10。</summary>
    public int UpdateIntervalSec { get => _updateIntervalSec; set { if (SetProperty(ref _updateIntervalSec, Math.Clamp(value, 1, 10))) Save(); } }

    private bool _startWithWindows;
    /// <summary>開機時自動啟動：以工作排程器註冊「登入觸發・最高權限」排定工作（requireAdministrator
    /// 的程式掛 HKCU Run 在登入時會被 UAC 擋下，形同不生效）；僅影響目前使用者，可還原。</summary>
    public bool StartWithWindows { get => _startWithWindows; set { if (SetProperty(ref _startWithWindows, value)) { ApplyAutoStart(value); Save(); } } }

    private int _defaultEra;
    /// <summary>啟動時預設採用的紀年（對應 <see cref="EraMode"/>）。</summary>
    public int DefaultEra { get => _defaultEra; set { if (SetProperty(ref _defaultEra, value)) Save(); } }

    // ── 感測器記錄 ───────────────────────────────────────
    private int _logIntervalSec = 2;
    /// <summary>記錄取樣間隔（秒），1–60。</summary>
    public int LogIntervalSec { get => _logIntervalSec; set { if (SetProperty(ref _logIntervalSec, Math.Clamp(value, 1, 60))) Save(); } }

    private string _logFolder = Path.Combine(Dir, "Logs");
    /// <summary>CSV 記錄檔輸出資料夾。</summary>
    public string LogFolder { get => _logFolder; set { if (SetProperty(ref _logFolder, value)) Save(); } }

    // ── 歷史記錄（歷史回放頁的資料來源）─────────────────────
    private bool _historyEnabled = true;
    /// <summary>是否持續累積歷史資料（關閉後歷史回放頁只剩既有資料）。</summary>
    public bool HistoryEnabled { get => _historyEnabled; set { if (SetProperty(ref _historyEnabled, value)) Save(); } }

    private int _historyRetentionDays = 30;
    /// <summary>歷史資料保留天數，1–120。</summary>
    public int HistoryRetentionDays { get => _historyRetentionDays; set { if (SetProperty(ref _historyRetentionDays, Math.Clamp(value, 1, 120))) Save(); } }

    // ── 警示閾值 ─────────────────────────────────────────
    private bool _alertsEnabled = true;
    public bool AlertsEnabled { get => _alertsEnabled; set { if (SetProperty(ref _alertsEnabled, value)) Save(); } }

    private double _cpuTempThreshold = 90;
    /// <summary>CPU 溫度警示門檻（°C），50–110。</summary>
    public double CpuTempThreshold { get => _cpuTempThreshold; set { if (SetProperty(ref _cpuTempThreshold, Math.Clamp(value, 50, 110))) Save(); } }

    private double _gpuTempThreshold = 85;
    /// <summary>GPU 溫度警示門檻（°C），40–110。</summary>
    public double GpuTempThreshold { get => _gpuTempThreshold; set { if (SetProperty(ref _gpuTempThreshold, Math.Clamp(value, 40, 110))) Save(); } }

    private double _cpuLoadThreshold = 95;
    /// <summary>CPU 負載警示門檻（%），10–100。</summary>
    public double CpuLoadThreshold { get => _cpuLoadThreshold; set { if (SetProperty(ref _cpuLoadThreshold, Math.Clamp(value, 10, 100))) Save(); } }

    private double _memLoadThreshold = 92;
    /// <summary>記憶體負載警示門檻（%），10–100。</summary>
    public double MemLoadThreshold { get => _memLoadThreshold; set { if (SetProperty(ref _memLoadThreshold, Math.Clamp(value, 10, 100))) Save(); } }

    // ── AI 評價 ──────────────────────────────────────────
    private int _aiProvider;   // 0=Ollama（本機免費）、1=OpenAI 相容 API
    /// <summary>AI 供應商（對應 <see cref="AiProvider"/>）。</summary>
    public int AiProvider { get => _aiProvider; set { if (SetProperty(ref _aiProvider, value)) { OnPropertyChanged(nameof(AiProviderEnum)); Save(); } } }
    public AiProvider AiProviderEnum => (AiProvider)_aiProvider;

    private string _aiBaseUrl = "";
    /// <summary>API 端點（OpenAI 相容）。預設留空，請自行填入，例：本機 Ollama 為 http://localhost:11434/v1。</summary>
    public string AiBaseUrl { get => _aiBaseUrl; set { if (SetProperty(ref _aiBaseUrl, value)) Save(); } }

    private string _aiApiKey = "";
    /// <summary>API 金鑰（本機 Ollama 可留空）。僅存於本機 settings.json。</summary>
    public string AiApiKey { get => _aiApiKey; set { if (SetProperty(ref _aiApiKey, value)) Save(); } }

    private string _aiModel = "";
    /// <summary>模型名稱（預設留空，請自行填入或用「一鍵獲取」選擇），例：llama3.2 / qwen2.5 / gpt-4o-mini。</summary>
    public string AiModel { get => _aiModel; set { if (SetProperty(ref _aiModel, value)) Save(); } }

    private double _aiTemperature = 0.7;
    /// <summary>取樣溫度 0–2：越低越保守一致，越高越有創意發散。</summary>
    public double AiTemperature { get => _aiTemperature; set { if (SetProperty(ref _aiTemperature, Math.Clamp(value, 0, 2))) Save(); } }

    private string _aiSystemPrompt = AiService.DefaultSystemPrompt;
    /// <summary>系統提示詞（可由使用者自訂；一鍵重置回內建預設）。</summary>
    public string AiSystemPrompt { get => _aiSystemPrompt; set { if (SetProperty(ref _aiSystemPrompt, value)) Save(); } }

    private bool _aiStreaming = true;
    /// <summary>逐字串流回覆（SSE）：關閉則等模型整段產生完才顯示。端點不支援時會自動退回整段模式。</summary>
    public bool AiStreaming { get => _aiStreaming; set { if (SetProperty(ref _aiStreaming, value)) Save(); } }

    private bool _aiAgentMode = true;
    /// <summary>診斷代理：允許 AI 主動呼叫本機唯讀查詢工具（溫度、事件、磁碟健康、歷史統計等）。</summary>
    public bool AiAgentMode { get => _aiAgentMode; set { if (SetProperty(ref _aiAgentMode, value)) Save(); } }

    private bool _aiKeepHistory = true;
    /// <summary>
    /// 保留對話：把 AI 對話存於本機 aichat.json，下次啟動自動接續。
    /// 關閉時 <see cref="AiService"/> 會立刻刪除該檔——說「不保留」就真的不留，不是只停止續寫。
    /// </summary>
    public bool AiKeepHistory { get => _aiKeepHistory; set { if (SetProperty(ref _aiKeepHistory, value)) Save(); } }

    private int _aiMaxTokens;
    /// <summary>
    /// 單次回覆的最大 token 數（<c>max_tokens</c>）。0 表示不送這個欄位，由端點自行決定上限。
    /// 上限 32768：設得比模型實際能力大只會被端點自己夾回去，不會失敗。
    /// </summary>
    public int AiMaxTokens { get => _aiMaxTokens; set { if (SetProperty(ref _aiMaxTokens, Math.Clamp(value, 0, 32768))) Save(); } }

    private int _aiHistoryTurns = 12;
    /// <summary>
    /// 每次請求最多回送幾則舊對話（不含系統提示與硬體快照）。0 表示全部送出。
    /// 硬體快照本身就很長，整段歷史一併送出會越談越貴、也容易撞上模型的上下文上限。
    /// </summary>
    public int AiHistoryTurns { get => _aiHistoryTurns; set { if (SetProperty(ref _aiHistoryTurns, Math.Clamp(value, 0, 240))) Save(); } }

    private bool _aiProactive;
    /// <summary>
    /// 主動診斷：溫度／負載警示觸發時，自動請 AI 就地分析一次原因與處置。
    /// 預設關閉——這會在使用者沒開口時送出請求（本機 Ollama 之外還可能計費），必須由使用者明示同意。
    /// </summary>
    public bool AiProactive { get => _aiProactive; set { if (SetProperty(ref _aiProactive, value)) Save(); } }

    // ── 報告匯出 ─────────────────────────────────────────
    private bool _reportMaskIdentity;
    /// <summary>
    /// 匯出報告時遮蔽可識別資訊：主機名稱、使用者名稱、MAC 位址與磁碟序號改為「（已遮蔽）」。
    /// 預設關閉——自己留存的報告該是完整的；要貼到公開場合再開。其餘規格與讀值一律照實輸出。
    /// </summary>
    public bool ReportMaskIdentity { get => _reportMaskIdentity; set { if (SetProperty(ref _reportMaskIdentity, value)) Save(); } }

    // ── 迷你浮動監視器 ───────────────────────────────────
    private double? _miniLeft;
    private double? _miniTop;
    /// <summary>上次拖曳到的位置；<c>null</c> 表示尚未擺放過，改由程式貼齊右上角。</summary>
    /// <remarks>用可空型別而非 NaN：System.Text.Json 預設不接受 NaN，寫檔會整份失敗。</remarks>
    public double? MiniLeft { get => _miniLeft; set { if (SetProperty(ref _miniLeft, value)) Save(); } }
    public double? MiniTop { get => _miniTop; set { if (SetProperty(ref _miniTop, value)) Save(); } }

    private double _miniOpacity = 0.9;
    /// <summary>底板不透明度 0.4–1.0（讀值文字本身不跟著淡，維持可讀）。</summary>
    public double MiniOpacity { get => _miniOpacity; set { if (SetProperty(ref _miniOpacity, Math.Clamp(value, 0.4, 1.0))) Save(); } }

    private bool _miniCompact;
    /// <summary>精簡模式：只留四行讀值，收起兩條波形，佔用更小。</summary>
    public bool MiniCompact { get => _miniCompact; set { if (SetProperty(ref _miniCompact, value)) Save(); } }

    private bool _miniTopmost = true;
    /// <summary>釘選在最上層。取消後會被其他視窗蓋住，但仍留在畫面上。</summary>
    public bool MiniTopmost { get => _miniTopmost; set { if (SetProperty(ref _miniTopmost, value)) Save(); } }

    // ── 總覽儀表板磁貼 ───────────────────────────────────
    private string _dashboardTiles = "";
    /// <summary>
    /// 總覽頁磁貼的順序與顯示狀態，格式見 <see cref="DashboardLayout"/>（逗號分隔的識別碼，隱藏者加減號）。
    /// 空字串＝沿用內建版面。
    /// </summary>
    public string DashboardTiles { get => _dashboardTiles; set { if (SetProperty(ref _dashboardTiles, value)) Save(); } }

    // ── 工具箱插槽 ───────────────────────────────────────
    private Dictionary<string, string> _toolSlots = new(StringComparer.Ordinal);
    /// <summary>工具箱「插槽」：工具名稱 → 使用者裝入的本機可執行檔路徑（下載後裝進插槽，之後直接啟動）。</summary>
    public IReadOnlyDictionary<string, string> ToolSlots => _toolSlots;

    /// <summary>設定或移除某工具的插槽路徑（path 為空即移除），並立即持久化。</summary>
    public void SetToolSlot(string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) _toolSlots.Remove(name);
        else _toolSlots[name] = path;
        Save();
    }

    public SettingsService() => Load();

    /// <summary>
    /// 現行設定檔結構版本。1 ＝ <see cref="EraMode"/> 於 1.5.1 重編號之後的編號體系。
    /// </summary>
    public const int CurrentSchema = 1;

    private sealed class Persist
    {
        /// <summary>
        /// 設定檔結構版本。缺少（舊檔）＝0；<see cref="CurrentSchema"/> 為現行版本。
        /// 只在「同一個欄位的意思改變了」時才需要遞增——新增欄位不必，因為缺欄位會拿到預設值。
        /// </summary>
        public int SchemaVersion { get; set; }
        public int UpdateIntervalSec { get; set; } = 1;
        public bool StartWithWindows { get; set; }
        public int DefaultEra { get; set; }
        public int LogIntervalSec { get; set; } = 2;
        public string? LogFolder { get; set; }
        public bool HistoryEnabled { get; set; } = true;
        public int HistoryRetentionDays { get; set; } = 30;
        public bool AlertsEnabled { get; set; } = true;
        public double CpuTempThreshold { get; set; } = 90;
        public double GpuTempThreshold { get; set; } = 85;
        public double CpuLoadThreshold { get; set; } = 95;
        public double MemLoadThreshold { get; set; } = 92;
        public int AiProvider { get; set; }
        public string? AiBaseUrl { get; set; }
        public string? AiApiKey { get; set; }
        public string? AiModel { get; set; }
        public double AiTemperature { get; set; } = 0.7;
        public string? AiSystemPrompt { get; set; }
        public bool AiStreaming { get; set; } = true;
        public bool AiAgentMode { get; set; } = true;
        public bool AiKeepHistory { get; set; } = true;
        public int AiMaxTokens { get; set; }
        public int AiHistoryTurns { get; set; } = 12;
        public bool AiProactive { get; set; }
        public bool ReportMaskIdentity { get; set; }
        public double? MiniLeft { get; set; }
        public double? MiniTop { get; set; }
        public double MiniOpacity { get; set; } = 0.9;
        public bool MiniCompact { get; set; }
        public bool MiniTopmost { get; set; } = true;
        public string? DashboardTiles { get; set; }
        public Dictionary<string, string>? ToolSlots { get; set; }
    }

    private void Load()
    {
        _loading = true;
        bool upgraded = false;
        try
        {
            if (File.Exists(FilePath))
            {
                var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(FilePath));
                if (p is not null)
                {
                    upgraded = p.SchemaVersion < CurrentSchema;
                    _updateIntervalSec = Math.Clamp(p.UpdateIntervalSec, 1, 10);
                    _startWithWindows = p.StartWithWindows;
                    // 舊結構（SchemaVersion 缺或 0）存的是 1.5.1 之前的紀年編號，意思與現行不同，須遷移。
                    _defaultEra = (int)(p.SchemaVersion < 1
                        ? EraCalendar.MigrateLegacyValue(p.DefaultEra)
                        : EraCalendar.Coerce(p.DefaultEra));
                    _logIntervalSec = Math.Clamp(p.LogIntervalSec, 1, 60);
                    if (!string.IsNullOrWhiteSpace(p.LogFolder)) _logFolder = p.LogFolder;
                    _historyEnabled = p.HistoryEnabled;
                    _historyRetentionDays = Math.Clamp(p.HistoryRetentionDays, 1, 120);
                    _alertsEnabled = p.AlertsEnabled;
                    _cpuTempThreshold = Math.Clamp(p.CpuTempThreshold, 50, 110);
                    _gpuTempThreshold = Math.Clamp(p.GpuTempThreshold, 40, 110);
                    _cpuLoadThreshold = Math.Clamp(p.CpuLoadThreshold, 10, 100);
                    _memLoadThreshold = Math.Clamp(p.MemLoadThreshold, 10, 100);
                    _aiProvider = p.AiProvider;
                    if (!string.IsNullOrWhiteSpace(p.AiBaseUrl)) _aiBaseUrl = p.AiBaseUrl;
                    _aiApiKey = p.AiApiKey ?? "";
                    if (!string.IsNullOrWhiteSpace(p.AiModel)) _aiModel = p.AiModel;
                    _aiTemperature = Math.Clamp(p.AiTemperature, 0, 2);
                    if (!string.IsNullOrWhiteSpace(p.AiSystemPrompt)) _aiSystemPrompt = p.AiSystemPrompt;
                    _aiStreaming = p.AiStreaming;
                    _aiAgentMode = p.AiAgentMode;
                    _aiKeepHistory = p.AiKeepHistory;
                    _aiMaxTokens = Math.Clamp(p.AiMaxTokens, 0, 32768);
                    _aiHistoryTurns = Math.Clamp(p.AiHistoryTurns, 0, 240);
                    _aiProactive = p.AiProactive;
                    _reportMaskIdentity = p.ReportMaskIdentity;
                    _miniLeft = p.MiniLeft;
                    _miniTop = p.MiniTop;
                    _miniOpacity = Math.Clamp(p.MiniOpacity, 0.4, 1.0);
                    _miniCompact = p.MiniCompact;
                    _miniTopmost = p.MiniTopmost;
                    _dashboardTiles = p.DashboardTiles ?? "";
                    if (p.ToolSlots is not null) _toolSlots = new(p.ToolSlots, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) { Diag.Swallow("設定載入", ex, "設定檔毀損或無法讀取，本次沿用預設值"); }
        finally { _loading = false; }

        // 遷移後立刻回寫，讓檔案帶上新的 SchemaVersion：否則下次載入會把「已是新編號」的值
        // 再當成舊編號遷移一次（民國 → 宣統），變成每次啟動都往後跳一格。
        if (upgraded) Save();
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var p = new Persist
            {
                SchemaVersion = CurrentSchema,
                UpdateIntervalSec = _updateIntervalSec,
                StartWithWindows = _startWithWindows,
                DefaultEra = _defaultEra,
                LogIntervalSec = _logIntervalSec,
                LogFolder = _logFolder,
                HistoryEnabled = _historyEnabled,
                HistoryRetentionDays = _historyRetentionDays,
                AlertsEnabled = _alertsEnabled,
                CpuTempThreshold = _cpuTempThreshold,
                GpuTempThreshold = _gpuTempThreshold,
                CpuLoadThreshold = _cpuLoadThreshold,
                MemLoadThreshold = _memLoadThreshold,
                AiProvider = _aiProvider,
                AiBaseUrl = _aiBaseUrl,
                AiApiKey = _aiApiKey,
                AiModel = _aiModel,
                AiTemperature = _aiTemperature,
                AiSystemPrompt = _aiSystemPrompt,
                AiStreaming = _aiStreaming,
                AiAgentMode = _aiAgentMode,
                AiKeepHistory = _aiKeepHistory,
                AiMaxTokens = _aiMaxTokens,
                AiHistoryTurns = _aiHistoryTurns,
                AiProactive = _aiProactive,
                ReportMaskIdentity = _reportMaskIdentity,
                MiniLeft = _miniLeft,
                MiniTop = _miniTop,
                MiniOpacity = _miniOpacity,
                MiniCompact = _miniCompact,
                MiniTopmost = _miniTopmost,
                DashboardTiles = _dashboardTiles.Length > 0 ? _dashboardTiles : null,
                ToolSlots = _toolSlots.Count > 0 ? _toolSlots : null,
            };
            // 原子寫入：寫一半的 settings.json 會讓下次載入整份回落預設值（見 AtomicWrite）
            AtomicWrite.AllText(FilePath, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存檔失敗（權限/磁碟）不影響執行期設定 */ }
    }

    // ── 開機自啟 ───────────────────────────────────────────────────────────
    //
    // requireAdministrator 的程式掛在 HKCU Run，登入時無法靜默通過 UAC、Windows 會直接擋下，
    // 形同不生效。故改以工作排程器註冊「登入觸發＋最高權限」的排定工作（COM API：僅註冊者本人
    // 登入時觸發、互動權杖、不設執行時限），登入即可靜默啟動。排程不可用（受限環境）時退回
    // HKCU Run（行為同 1.3.1）；走排程時一律清掉 Run 舊值，避免兩個入口重複啟動。
    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            bool viaTask = enable ? RegisterLogonTask() : DeleteLogonTask();
            ApplyRunKey(viaTask ? false : enable);
        }
        catch { /* 開機自啟為選用；失敗不影響其餘設定 */ }
    }

    private static bool RegisterLogonTask()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            // Task Scheduler COM 以動態繫結操作，毋須額外套件；常數取自 TASK_* 列舉。
            dynamic ts = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ts.Connect();
            dynamic td = ts.NewTask(0);
            td.Principal.RunLevel = 1;               // TASK_RUNLEVEL_HIGHEST
            td.Principal.LogonType = 3;              // TASK_LOGON_INTERACTIVE_TOKEN
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.ExecutionTimeLimit = "PT0S"; // 監控常駐：取消預設的 72 小時執行時限
            td.Triggers.Create(9);                   // TASK_TRIGGER_LOGON（僅註冊者本人登入）
            dynamic action = td.Actions.Create(0);   // TASK_ACTION_EXEC
            action.Path = exe;
            ts.GetFolder("\\").RegisterTaskDefinition(TaskName, td, 6, null, null, 3);   // TASK_CREATE_OR_UPDATE
            return true;
        }
        catch { return false; }
    }

    private static bool DeleteLogonTask()
    {
        try
        {
            dynamic ts = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ts.Connect();
            ts.GetFolder("\\").DeleteTask(TaskName, 0);
            return true;
        }
        catch { return false; }   // 工作不存在或環境受限 → 視為未走排程，交由 Run 機碼處理
    }

    // 退路與清理：HKCU Run 機碼（排程不可用時沿用舊機制；亦用於清掉 1.3.1 之前寫入的殘留值）
    private static void ApplyRunKey(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValue, $"\"{exe}\"");
            }
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { /* 寫入登錄失敗（權限）不影響其餘設定 */ }
    }
}
