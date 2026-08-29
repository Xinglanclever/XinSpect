using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>一組「場景」要套用的實際動作（內建場景與自訂場景都先歸算成這個形狀再執行）。</summary>
internal sealed record SceneAction(
    int? FanPreset,
    bool EnableFanCurves,
    string? PowerPlanGuid,
    string PowerPlanName,
    double? GpuPowerPercent,
    double? GpuTempLimitC);

/// <summary>
/// 一個場景設定檔：一鍵把風扇曲線、Windows 電源計劃與顯示卡功耗／溫度上限調成同一個取向。
/// </summary>
public sealed class Scene : ObservableObject
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>卡片上的一句說明。</summary>
    public string Summary { get; init; } = "";
    /// <summary>卡片圖示（24×24 Path 資料）。</summary>
    public string IconData { get; init; } = "";
    /// <summary>逐項說明這個場景會動到什麼（供卡片列出）。</summary>
    public IReadOnlyList<string> Details { get; init; } = [];
    /// <summary>是否為使用者自訂場景（UI 額外顯示可調欄位）。</summary>
    public bool IsCustom { get; init; }

    internal int? FanPreset { get; init; }
    internal bool EnableFanCurves { get; init; }
    internal string? PowerPlanGuid { get; init; }
    internal string PowerPlanName { get; init; } = "";
    internal double? GpuPowerPercent { get; init; }
    internal double? GpuTempLimitC { get; init; }

    private bool _active;
    /// <summary>是否為目前使用中的場景。</summary>
    public bool IsActive
    {
        get => _active;
        internal set { if (SetProperty(ref _active, value)) OnPropertyChanged(nameof(ActionText)); }
    }

    public string ActionText => _active ? "使用中" : "套用此場景";
}

/// <summary>
/// 自訂場景的可調欄位（各部分皆可獨立開關，未勾選者套用時完全不動）。
/// </summary>
public sealed class CustomScene : ObservableObject
{
    /// <summary>任一欄位變更時觸發（服務據此存檔）。</summary>
    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    private bool _fanOn = true;
    public bool ApplyFan { get => _fanOn; set { if (SetProperty(ref _fanOn, value)) Raise(); } }

    private int _fanPreset = 1;
    /// <summary>風扇曲線樣板（0 靜音、1 均衡、2 效能）。</summary>
    public int FanPreset
    {
        get => _fanPreset;
        set { if (SetProperty(ref _fanPreset, Math.Clamp(value, 0, 2))) Raise(); }
    }

    private bool _planOn = true;
    public bool ApplyPowerPlan { get => _planOn; set { if (SetProperty(ref _planOn, value)) Raise(); } }

    private int _planIndex = 1;
    /// <summary>Windows 電源計劃索引（對應 <see cref="ProfileService.PowerPlanNames"/>）。</summary>
    public int PowerPlanIndex
    {
        get => _planIndex;
        set { if (SetProperty(ref _planIndex, Math.Clamp(value, 0, ProfileService.PowerPlanNames.Length - 1))) Raise(); }
    }

    private bool _gpuOn;
    public bool ApplyGpu { get => _gpuOn; set { if (SetProperty(ref _gpuOn, value)) Raise(); } }

    private double _gpuPower = 100;
    /// <summary>顯示卡功耗上限（% of 預設 TDP）。</summary>
    public double GpuPowerPercent
    {
        get => _gpuPower;
        set { if (SetProperty(ref _gpuPower, Math.Clamp(Math.Round(value), 50, 130))) Raise(); }
    }

    private double _gpuTemp = 83;
    /// <summary>顯示卡溫度上限（°C）。</summary>
    public double GpuTempLimitC
    {
        get => _gpuTemp;
        set { if (SetProperty(ref _gpuTemp, Math.Clamp(Math.Round(value), 65, 93))) Raise(); }
    }
}

/// <summary>
/// 場景設定檔：把「風扇曲線樣板 + Windows 電源計劃 + 顯示卡功耗／溫度上限」綁成一鍵可切的取向
/// （靜音／均衡／效能／自訂）。所有動作都是真實寫入：風扇走 <see cref="FanCurveService"/>、
/// 電源計劃走 <c>powercfg</c>、顯示卡走 <see cref="GpuOcService"/>（NVML）。
/// 選擇與自訂內容落地於 %APPDATA%\XinSpect\scenes.json。
/// </summary>
public sealed class ProfileService : ObservableObject
{
    // Windows 內建電源計劃的固定 GUID（缺少者以 powercfg -duplicatescheme 就地建立）
    private const string PlanSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string PlanBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PlanHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PlanUltimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    /// <summary>自訂場景可選的電源計劃名稱（順序與 <see cref="PowerPlanGuids"/> 一致）。</summary>
    public static string[] PowerPlanNames { get; } = ["節能", "平衡", "高效能", "最佳效能"];

    /// <summary>對應 <see cref="PowerPlanNames"/> 的計劃 GUID。</summary>
    public static string[] PowerPlanGuids { get; } = [PlanSaver, PlanBalanced, PlanHigh, PlanUltimate];

    private readonly string _file;
    private bool _loading;

    /// <summary>事件時間軸（可選）：每次套用場景都留下一筆調校紀錄。</summary>
    public EventsService? Events { get; set; }
    /// <summary>風扇曲線服務（可選）：場景的風扇部分由它執行。</summary>
    public FanCurveService? Fans { get; set; }
    /// <summary>顯示卡超頻服務（可選，需 NVML）：場景的功耗／溫度上限由它執行。</summary>
    public GpuOcService? Gpu { get; set; }

    /// <summary>自訂場景的可調內容。</summary>
    public CustomScene Custom { get; } = new();

    /// <summary>四個場景（靜音／均衡／效能／自訂），順序即卡片順序。</summary>
    public ObservableCollection<Scene> Scenes { get; } = [];

    public ProfileService(string? folder = null)
    {
        string dir = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        try { Directory.CreateDirectory(dir); } catch { /* 無法建目錄則僅記憶體運作 */ }
        _file = Path.Combine(dir, "scenes.json");

        BuildScenes();
        Custom.Changed += Save;
        Load();
    }

    private void BuildScenes()
    {
        Scenes.Add(new Scene
        {
            Key = "quiet", Name = "靜音",
            Summary = "以最低噪音為先：風扇壓到剛好夠用，處理器與顯示卡讓出一點效能換安靜。",
            IconData = "F1 M4,9 H7 L12,4 V20 L7,15 H4 Z M15.5,9.5 a4,4 0 0 1 0,5 M18,7 a7.5,7.5 0 0 1 0,10",
            Details = ["風扇曲線：靜音樣板並啟用自動調速", "電源計劃：節能", "顯示卡：功耗上限 85 %、溫度上限 75 °C"],
            FanPreset = 0, EnableFanCurves = true,
            PowerPlanGuid = PlanSaver, PowerPlanName = "節能",
            GpuPowerPercent = 85, GpuTempLimitC = 75,
        });
        Scenes.Add(new Scene
        {
            Key = "balanced", Name = "均衡",
            Summary = "日常使用的預設取向：溫度、噪音與效能三者取中，也是離開其他場景後的安全歸位。",
            IconData = "F1 M3,11 H21 V13 H3 Z M6,6 H18 V8 H6 Z M6,16 H18 V18 H6 Z",
            Details = ["風扇曲線：均衡樣板並啟用自動調速", "電源計劃：平衡", "顯示卡：功耗與溫度上限回到原廠值"],
            FanPreset = 1, EnableFanCurves = true,
            PowerPlanGuid = PlanBalanced, PowerPlanName = "平衡",
            GpuPowerPercent = 100, GpuTempLimitC = 83,
        });
        Scenes.Add(new Scene
        {
            Key = "performance", Name = "效能",
            Summary = "遊戲與算圖時的全力取向：先把散熱備足，再放開功耗與溫度上限。",
            IconData = "F1 M13,2 L4,14 L10,14 L9,22 L20,9 L13,9 Z",
            Details = ["風扇曲線：效能樣板並啟用自動調速", "電源計劃：高效能（缺少時自動建立）", "顯示卡：功耗上限 110 %、溫度上限 88 °C"],
            FanPreset = 2, EnableFanCurves = true,
            PowerPlanGuid = PlanHigh, PowerPlanName = "高效能",
            GpuPowerPercent = 110, GpuTempLimitC = 88,
        });
        Scenes.Add(new Scene
        {
            Key = "custom", Name = "自訂",
            Summary = "自己決定這個場景要動哪些部分；未勾選的部分套用時完全不碰。",
            IconData = "F1 M5,4 H7 V20 H5 Z M11,4 H13 V20 H11 Z M17,4 H19 V20 H17 Z "
                     + "M3.5,8 H8.5 V10 H3.5 Z M9.5,13 H14.5 V15 H9.5 Z M15.5,7 H20.5 V9 H15.5 Z",
            IsCustom = true,
        });
    }

    // ── 狀態 ────────────────────────────────────────────────────────────────

    private string _activeKey = "";
    /// <summary>目前使用中的場景鍵（空字串表示尚未套用任何場景）。</summary>
    public string ActiveKey
    {
        get => _activeKey;
        private set
        {
            if (!SetProperty(ref _activeKey, value)) return;
            foreach (var s in Scenes) s.IsActive = s.Key == value;
            OnPropertyChanged(nameof(ActiveName));
        }
    }

    /// <summary>目前場景名稱（未套用時為「未套用」）。</summary>
    public string ActiveName =>
        Scenes.FirstOrDefault(s => s.Key == _activeKey)?.Name ?? "未套用";

    private string _status = "尚未套用任何場景，各項設定維持目前狀態。";
    /// <summary>頁面上的一行狀態摘要（逐項說明剛才實際做了什麼）。</summary>
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }

    private bool _busy;
    /// <summary>是否正在套用（套用期間停用按鈕）。</summary>
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(NotBusy)); }
    }

    public bool NotBusy => !_busy;

    private string _planText = "讀取中…";
    /// <summary>目前 Windows 電源計劃名稱（由 powercfg 讀回）。</summary>
    public string PowerPlanText { get => _planText; private set => SetProperty(ref _planText, value); }

    // ── 套用 ────────────────────────────────────────────────────────────────

    /// <summary>把場景歸算成實際動作（自訂場景取其可調欄位，未勾選的部分回傳 null 表示不動）。</summary>
    internal SceneAction Resolve(Scene s)
    {
        if (!s.IsCustom)
            return new SceneAction(s.FanPreset, s.EnableFanCurves, s.PowerPlanGuid, s.PowerPlanName,
                                   s.GpuPowerPercent, s.GpuTempLimitC);

        int idx = Math.Clamp(Custom.PowerPlanIndex, 0, PowerPlanGuids.Length - 1);
        return new SceneAction(
            Custom.ApplyFan ? Custom.FanPreset : null,
            Custom.ApplyFan,
            Custom.ApplyPowerPlan ? PowerPlanGuids[idx] : null,
            PowerPlanNames[idx],
            Custom.ApplyGpu ? Custom.GpuPowerPercent : null,
            Custom.ApplyGpu ? Custom.GpuTempLimitC : null);
    }

    /// <summary>套用指定場景；回報實際完成的項目（逐項寫入 <see cref="StatusText"/> 與事件時間軸）。</summary>
    public async Task ApplyAsync(string key)
    {
        var scene = Scenes.FirstOrDefault(s => s.Key == key);
        if (scene is null || IsBusy) return;

        IsBusy = true;
        StatusText = $"正在套用「{scene.Name}」…";
        var done = new List<string>();
        var skipped = new List<string>();

        try
        {
            var act = Resolve(scene);

            // 1) 風扇曲線
            if (act.FanPreset is int preset && Fans is not null && Fans.HasCurves)
            {
                Fans.ApplyPresetToAll(preset);
                foreach (var c in Fans.Curves) c.Enabled = act.EnableFanCurves;
                done.Add($"風扇曲線 {FanCurveService.PresetNames[Math.Clamp(preset, 0, 2)]}"
                         + (act.EnableFanCurves ? "（已啟用自動調速）" : ""));
            }
            else if (act.FanPreset is not null)
            {
                skipped.Add("風扇（未偵測到可控風扇）");
            }

            // 2) Windows 電源計劃
            if (act.PowerPlanGuid is string guid)
            {
                bool ok = await Task.Run(() => SetPowerPlan(guid));
                if (ok) done.Add($"電源計劃 {act.PowerPlanName}");
                else skipped.Add($"電源計劃（無法切換到{act.PowerPlanName}）");
            }

            // 3) 顯示卡功耗／溫度上限（需 NVML）
            if (act.GpuPowerPercent is double pw && Gpu is not null && Gpu.NvmlAvailable)
            {
                Gpu.PowerLimitPercent = Math.Clamp(pw, Gpu.PowerPctMin, Gpu.PowerPctMax);
                Gpu.ApplyPowerLimit();
                done.Add($"顯示卡功耗上限 {Gpu.PowerLimitPercent:0} %");

                if (act.GpuTempLimitC is double tl && Gpu.TempControlAvailable)
                {
                    Gpu.TargetTempLimitC = Math.Clamp(tl, Gpu.TempLimitMin, Gpu.TempLimitMax);
                    Gpu.ApplyTempLimit();
                    done.Add($"顯示卡溫度上限 {Gpu.TargetTempLimitC:0} °C");
                }
            }
            else if (act.GpuPowerPercent is not null)
            {
                skipped.Add("顯示卡（NVML 不可用）");
            }

            ActiveKey = scene.Key;
            Save();

            string detail = done.Count == 0 ? "沒有可套用的項目" : string.Join("・", done);
            StatusText = $"已套用「{scene.Name}」：{detail}"
                       + (skipped.Count > 0 ? $"；略過 {string.Join("、", skipped)}" : "");
            Events?.Add(EventKind.Tune, $"場景已套用：{scene.Name}", detail);
        }
        catch (Exception ex)
        {
            StatusText = $"套用「{scene.Name}」時發生問題：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshPowerPlanAsync();
        }
    }

    // ── Windows 電源計劃（powercfg）─────────────────────────────────────────

    /// <summary>讀回目前電源計劃名稱（開機與每次套用後呼叫）。</summary>
    public async Task RefreshPowerPlanAsync()
    {
        string name = await Task.Run(ReadActivePlanName);
        PowerPlanText = string.IsNullOrWhiteSpace(name) ? "無法讀取（powercfg 不可用）" : name;
    }

    // 執行 powercfg 並取回輸出；失敗（找不到執行檔、被政策封鎖）回傳空字串與非零碼。
    private static (int Code, string Out) Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return (-1, "");
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { /* 已結束 */ } return (-1, outp); }
            return (p.ExitCode, outp);
        }
        catch { return (-1, ""); }
    }

    private static readonly System.Text.RegularExpressions.Regex GuidRx =
        new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    // 「電源設定 GUID: xxxxxxxx-…  (平衡)」→ 取括號內的名稱
    private static string ReadActivePlanName()
    {
        var (code, outp) = Run("/getactivescheme");
        if (code != 0 || string.IsNullOrWhiteSpace(outp)) return "";
        int a = outp.LastIndexOf('('), b = outp.LastIndexOf(')');
        if (a >= 0 && b > a) return outp[(a + 1)..b].Trim();
        var m = GuidRx.Match(outp);
        return m.Success ? m.Value : "";
    }

    private static bool Activate(string guid) => Run($"/setactive {guid}").Code == 0;

    // 切換電源計劃；Windows 11 常隱藏「高效能」與「最佳效能」，缺少時以 duplicatescheme 就地建立再切。
    private static bool SetPowerPlan(string guid)
    {
        if (Activate(guid)) return true;

        var (code, outp) = Run($"-duplicatescheme {guid}");
        if (code == 0)
        {
            var m = GuidRx.Match(outp);
            if (m.Success && Activate(m.Value)) return true;
        }
        return false;
    }

    // ── 落地 ────────────────────────────────────────────────────────────────

    private sealed class Persist
    {
        public string Active { get; set; } = "";
        public bool CustomFan { get; set; } = true;
        public int CustomFanPreset { get; set; } = 1;
        public bool CustomPlan { get; set; } = true;
        public int CustomPlanIndex { get; set; } = 1;
        public bool CustomGpu { get; set; }
        public double CustomGpuPower { get; set; } = 100;
        public double CustomGpuTemp { get; set; } = 83;
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            var p = new Persist
            {
                Active = _activeKey,
                CustomFan = Custom.ApplyFan,
                CustomFanPreset = Custom.FanPreset,
                CustomPlan = Custom.ApplyPowerPlan,
                CustomPlanIndex = Custom.PowerPlanIndex,
                CustomGpu = Custom.ApplyGpu,
                CustomGpuPower = Custom.GpuPowerPercent,
                CustomGpuTemp = Custom.GpuTempLimitC,
            };
            AtomicWrite.AllText(_file, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存檔失敗僅影響下次啟動的預設值 */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(_file));
            if (p is null) return;

            _loading = true;
            Custom.ApplyFan = p.CustomFan;
            Custom.FanPreset = p.CustomFanPreset;
            Custom.ApplyPowerPlan = p.CustomPlan;
            Custom.PowerPlanIndex = p.CustomPlanIndex;
            Custom.ApplyGpu = p.CustomGpu;
            Custom.GpuPowerPercent = p.CustomGpuPower;
            Custom.GpuTempLimitC = p.CustomGpuTemp;
            _loading = false;

            if (Scenes.Any(s => s.Key == p.Active))
            {
                ActiveKey = p.Active;
                StatusText = $"上次使用的場景為「{ActiveName}」；重新套用可再次寫入硬體。";
            }
        }
        catch { _loading = false; /* 壞檔視為沒有設定 */ }
    }
}


