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
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "XinSpect";

    private bool _loading;   // 載入期間不觸發存檔，避免逐欄寫檔

    // ── 一般 ─────────────────────────────────────────────
    private int _updateIntervalSec = 1;
    /// <summary>感測器輪詢與時鐘更新間隔（秒），1–10。</summary>
    public int UpdateIntervalSec { get => _updateIntervalSec; set { if (SetProperty(ref _updateIntervalSec, Math.Clamp(value, 1, 10))) Save(); } }

    private bool _startWithWindows;
    /// <summary>開機時自動啟動（寫入 HKCU Run 機碼；僅影響目前使用者，可還原）。</summary>
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

    // ── 警示閾值 ─────────────────────────────────────────
    private bool _alertsEnabled = true;
    public bool AlertsEnabled { get => _alertsEnabled; set { if (SetProperty(ref _alertsEnabled, value)) Save(); } }

    private double _cpuTempThreshold = 90;
    public double CpuTempThreshold { get => _cpuTempThreshold; set { if (SetProperty(ref _cpuTempThreshold, value)) Save(); } }

    private double _gpuTempThreshold = 85;
    public double GpuTempThreshold { get => _gpuTempThreshold; set { if (SetProperty(ref _gpuTempThreshold, value)) Save(); } }

    private double _cpuLoadThreshold = 95;
    public double CpuLoadThreshold { get => _cpuLoadThreshold; set { if (SetProperty(ref _cpuLoadThreshold, value)) Save(); } }

    private double _memLoadThreshold = 92;
    public double MemLoadThreshold { get => _memLoadThreshold; set { if (SetProperty(ref _memLoadThreshold, value)) Save(); } }

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

    private sealed class Persist
    {
        public int UpdateIntervalSec { get; set; } = 1;
        public bool StartWithWindows { get; set; }
        public int DefaultEra { get; set; }
        public int LogIntervalSec { get; set; } = 2;
        public string? LogFolder { get; set; }
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
        public Dictionary<string, string>? ToolSlots { get; set; }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            if (File.Exists(FilePath))
            {
                var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(FilePath));
                if (p is not null)
                {
                    _updateIntervalSec = Math.Clamp(p.UpdateIntervalSec, 1, 10);
                    _startWithWindows = p.StartWithWindows;
                    _defaultEra = p.DefaultEra;
                    _logIntervalSec = Math.Clamp(p.LogIntervalSec, 1, 60);
                    if (!string.IsNullOrWhiteSpace(p.LogFolder)) _logFolder = p.LogFolder;
                    _alertsEnabled = p.AlertsEnabled;
                    _cpuTempThreshold = p.CpuTempThreshold;
                    _gpuTempThreshold = p.GpuTempThreshold;
                    _cpuLoadThreshold = p.CpuLoadThreshold;
                    _memLoadThreshold = p.MemLoadThreshold;
                    _aiProvider = p.AiProvider;
                    if (!string.IsNullOrWhiteSpace(p.AiBaseUrl)) _aiBaseUrl = p.AiBaseUrl;
                    _aiApiKey = p.AiApiKey ?? "";
                    if (!string.IsNullOrWhiteSpace(p.AiModel)) _aiModel = p.AiModel;
                    _aiTemperature = Math.Clamp(p.AiTemperature, 0, 2);
                    if (!string.IsNullOrWhiteSpace(p.AiSystemPrompt)) _aiSystemPrompt = p.AiSystemPrompt;
                    if (p.ToolSlots is not null) _toolSlots = new(p.ToolSlots, StringComparer.Ordinal);
                }
            }
        }
        catch { /* 設定毀損則沿用預設值 */ }
        finally { _loading = false; }
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var p = new Persist
            {
                UpdateIntervalSec = _updateIntervalSec,
                StartWithWindows = _startWithWindows,
                DefaultEra = _defaultEra,
                LogIntervalSec = _logIntervalSec,
                LogFolder = _logFolder,
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
                ToolSlots = _toolSlots.Count > 0 ? _toolSlots : null,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存檔失敗（權限/磁碟）不影響執行期設定 */ }
    }

    private static void ApplyAutoStart(bool enable)
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
        catch { /* 開機自啟為選用；寫入登錄失敗（權限）不影響其餘設定 */ }
    }
}
