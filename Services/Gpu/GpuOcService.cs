using System.Security.Principal;

namespace XinSpect;

/// <summary>
/// 顯示卡超頻模組（測試版）：對 NVIDIA 顯示卡進行「真實」寫入。
/// 功耗上限與風扇轉速走 NVML（官方文件化、穩定）；核心／顯示記憶體頻率偏移與溫度上限走
/// NVAPI 私有介面（版本標頭保護，佈局不符只會「無法套用」而非寫入亂數）。
/// 讀取全程即時更新；每一項寫入皆檢查回傳碼並誠實回報成功或失敗，絕不假裝成功。
/// 未偵測到 NVIDIA 顯示卡或介面不可用時，降級為唯讀（或整體停用），不影響其餘分頁。
/// </summary>
public sealed class GpuOcService : ObservableObject, IDisposable
{
    private IntPtr _dev;
    private bool _nvmlInited;
    private uint _numFans;

    // ── 可用性 ────────────────────────────────────────────────────────────────
    private bool _nvmlOk;
    public bool NvmlAvailable { get => _nvmlOk; private set => SetProperty(ref _nvmlOk, value); }
    private bool _nvapiOk;
    public bool NvapiAvailable { get => _nvapiOk; private set => SetProperty(ref _nvapiOk, value); }
    private bool _tempCtlOk;
    public bool TempControlAvailable { get => _tempCtlOk; private set => SetProperty(ref _tempCtlOk, value); }
    public bool Available => NvmlAvailable || NvapiAvailable;
    public bool NotAvailable => !Available;

    private string _availabilityText = "初始化中…";
    public string AvailabilityText { get => _availabilityText; private set => SetProperty(ref _availabilityText, value); }

    private bool _isAdmin;
    public bool IsAdmin { get => _isAdmin; private set => SetProperty(ref _isAdmin, value); }

    private string _gpuName = "—";
    public string GpuName { get => _gpuName; private set => SetProperty(ref _gpuName, value); }

    // ── NVML 深度資訊（唯讀硬核規格，顯示於「顯示卡」分頁）──────────────────────────
    private List<GpuNvmlGroup> _nvmlGroups = new();
    public List<GpuNvmlGroup> NvmlGroups { get => _nvmlGroups; private set => SetProperty(ref _nvmlGroups, value); }
    public bool HasNvmlGroups => _nvmlGroups.Count > 0;


    // ── 即時讀取 ──────────────────────────────────────────────────────────────
    private double _coreClock; public double CoreClockMhz { get => _coreClock; private set => SetProperty(ref _coreClock, value); }
    private double _memClock;  public double MemClockMhz  { get => _memClock;  private set => SetProperty(ref _memClock, value); }
    private double _tempC;     public double TempC        { get => _tempC;     private set => SetProperty(ref _tempC, value); }
    private double _powerW;    public double PowerW       { get => _powerW;    private set => SetProperty(ref _powerW, value); }
    private double _fanPct;    public double FanPercent   { get => _fanPct;    private set => SetProperty(ref _fanPct, value); }

    private double _powerLimitW;   public double PowerLimitW   { get => _powerLimitW;   private set => SetProperty(ref _powerLimitW, value); }
    private double _powerDefaultW; public double PowerDefaultW { get => _powerDefaultW; private set => SetProperty(ref _powerDefaultW, value); }
    private double _coreOffNow;    public double CoreOffsetNow { get => _coreOffNow;    private set => SetProperty(ref _coreOffNow, value); }
    private double _memOffNow;     public double MemOffsetNow  { get => _memOffNow;     private set => SetProperty(ref _memOffNow, value); }
    private double _tempLimitNow;  public double TempLimitNow  { get => _tempLimitNow;  private set => SetProperty(ref _tempLimitNow, value); }

    // ── 可調目標值（滑桿）───────────────────────────────────────────────────────
    private double _powerPct = 100;
    public double PowerLimitPercent { get => _powerPct; set => SetProperty(ref _powerPct, value); }
    private double _tgtTempLimit = 84;
    public double TargetTempLimitC { get => _tgtTempLimit; set => SetProperty(ref _tgtTempLimit, value); }
    private double _tgtCoreOff;
    public double TargetCoreOffsetMhz { get => _tgtCoreOff; set => SetProperty(ref _tgtCoreOff, value); }
    private double _tgtMemOff;
    public double TargetMemOffsetMhz { get => _tgtMemOff; set => SetProperty(ref _tgtMemOff, value); }
    private bool _fanManual;
    public bool FanManual { get => _fanManual; set => SetProperty(ref _fanManual, value); }
    private double _tgtFan = 50;
    public double TargetFanPercent { get => _tgtFan; set => SetProperty(ref _tgtFan, value); }

    // 滑桿範圍（初始化時以驅動實際回報值覆寫；此處為安全預設）
    private double _powerPctMin = 50;   public double PowerPctMin { get => _powerPctMin; private set => SetProperty(ref _powerPctMin, value); }
    private double _powerPctMax = 120;  public double PowerPctMax { get => _powerPctMax; private set => SetProperty(ref _powerPctMax, value); }
    private double _coreOffMin = -200;  public double CoreOffMin { get => _coreOffMin; private set => SetProperty(ref _coreOffMin, value); }
    private double _coreOffMax = 1200;  public double CoreOffMax { get => _coreOffMax; private set => SetProperty(ref _coreOffMax, value); }
    private double _memOffMin = -1000;  public double MemOffMin { get => _memOffMin; private set => SetProperty(ref _memOffMin, value); }
    private double _memOffMax = 1000;   public double MemOffMax { get => _memOffMax; private set => SetProperty(ref _memOffMax, value); }
    private double _tempLimitMin = 65;  public double TempLimitMin { get => _tempLimitMin; private set => SetProperty(ref _tempLimitMin, value); }
    private double _tempLimitMax = 93;  public double TempLimitMax { get => _tempLimitMax; private set => SetProperty(ref _tempLimitMax, value); }

    // ── 各動作狀態列（誠實回報）─────────────────────────────────────────────────
    private string _powerStatus = ""; public string PowerStatus { get => _powerStatus; private set => SetProperty(ref _powerStatus, value); }
    private string _coreStatus = "";  public string CoreStatus  { get => _coreStatus;  private set => SetProperty(ref _coreStatus, value); }
    private string _memStatus = "";   public string MemStatus   { get => _memStatus;   private set => SetProperty(ref _memStatus, value); }
    private string _tempStatus = "";  public string TempStatus  { get => _tempStatus;  private set => SetProperty(ref _tempStatus, value); }
    private string _fanStatus = "";   public string FanStatus   { get => _fanStatus;   private set => SetProperty(ref _fanStatus, value); }

    public Task InitializeAsync() => Task.Run(Initialize);

    private void Initialize()
    {
        try { IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { IsAdmin = false; }

        // NVML：型號 / 遙測 / 功耗上限 / 風扇
        try
        {
            if (NvmlInterop.Init() == 0 && NvmlInterop.GetHandleByIndex(0, out _dev) == 0)
            {
                _nvmlInited = true;
                NvmlAvailable = true;
                GpuName = NvmlInterop.GetName(_dev) is { Length: > 0 } n ? n : "NVIDIA GPU";
                if (NvmlInterop.GetNumFans(_dev, out var nf) == 0) _numFans = nf;

                if (NvmlInterop.GetPowerLimitDefault(_dev, out var defMw) == 0 && defMw > 0)
                {
                    PowerDefaultW = defMw / 1000.0;
                    if (NvmlInterop.GetPowerLimitConstraints(_dev, out var minMw, out var maxMw) == 0 && maxMw > 0)
                    {
                        PowerPctMin = Math.Round(minMw * 100.0 / defMw);
                        PowerPctMax = Math.Round(maxMw * 100.0 / defMw);
                        OnPropertyChanged(nameof(PowerPctMin));
                        OnPropertyChanged(nameof(PowerPctMax));
                    }
                    if (NvmlInterop.GetPowerLimit(_dev, out var curMw) == 0 && curMw > 0)
                        PowerLimitPercent = Math.Round(curMw * 100.0 / defMw);
                }

                // 溫度上限：以官方文件化的 NVML 溫度門檻實作（ACOUSTIC_CURR＝可調目標溫度）。
                // 範圍取 ACOUSTIC_MIN／ACOUSTIC_MAX，目前值取 ACOUSTIC_CURR。本機實測皆 rc=0。
                if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_CURR, out var cur) == 0)
                {
                    TempControlAvailable = true;
                    if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_MIN, out var lo) == 0 && lo > 0)
                        TempLimitMin = lo;
                    if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_MAX, out var hi) == 0 && hi > 0)
                        TempLimitMax = hi;
                    TempLimitNow = cur;
                    TargetTempLimitC = cur;
                }
            }
        }
        catch { NvmlAvailable = false; }

        // NVAPI：核心／顯示記憶體頻率偏移
        try
        {
            NvapiAvailable = NvapiInterop.Initialize();
            if (NvapiAvailable)
            {
                // 以驅動 P0 回報的合法偏移範圍覆寫滑桿上下限（kHz→MHz）
                if (NvapiInterop.GetClockOffsetRange(NvapiInterop.CLOCK_GRAPHICS) is { } cr)
                { CoreOffMin = cr.min / 1000.0; CoreOffMax = cr.max / 1000.0; }
                if (NvapiInterop.GetClockOffsetRange(NvapiInterop.CLOCK_MEMORY) is { } mr)
                { MemOffMin = mr.min / 1000.0; MemOffMax = mr.max / 1000.0; }
            }
        }
        catch { NvapiAvailable = false; }

        RefreshWriteReadbacks();
        UpdateAvailabilityText();
        BuildNvmlGroups();
    }

    // ══ NVML 深度資訊建構（每欄皆檢查回傳碼，失敗即誠實省略）═══════════════════════
    private void BuildNvmlGroups()
    {
        if (!_nvmlInited) return;
        try
        {
            var groups = new List<GpuNvmlGroup>();

            // 識別與驅動
            var ident = new List<GpuNvmlField>();
            AddStr(ident, "型號", NvmlInterop.GetName(_dev));
            if (NvmlInterop.GetBrand(_dev, out var brand) == 0) AddStr(ident, "品牌", BrandName(brand));
            if (NvmlInterop.GetArchitecture(_dev, out var arch) == 0) AddStr(ident, "架構", ArchName(arch));
            if (NvmlInterop.GetComputeCap(_dev, out var maj, out var min) == 0) ident.Add(new("計算能力", $"{maj}.{min}"));
            if (NvmlInterop.GetNumGpuCores(_dev, out var cores) == 0 && cores > 0) ident.Add(new("CUDA 核心數", $"{cores}"));
            AddStr(ident, "VBIOS 版本", NvmlInterop.VbiosVersion(_dev));
            AddStr(ident, "驅動版本", NvmlInterop.DriverVersion());
            AddStr(ident, "NVML 版本", NvmlInterop.NvmlVersion());
            if (NvmlInterop.SysCudaDriverVer(out var cuda) == 0 && cuda > 0)
                ident.Add(new("CUDA 驅動", $"{cuda / 1000}.{cuda % 1000 / 10}"));
            AddStr(ident, "UUID", NvmlInterop.Uuid(_dev));
            AddStr(ident, "序號", NvmlInterop.Serial(_dev));
            if (ident.Count > 0) groups.Add(new("識別與驅動", ident));

            // PCI Express
            var pci = new List<GpuNvmlField>();
            var pi = new NvmlInterop.NvmlPciInfo { BusIdLegacy = new byte[16], BusId = new byte[32] };
            if (NvmlInterop.GetPciInfo(_dev, ref pi) == 0)
            {
                pci.Add(new("裝置 ID", $"0x{pi.PciDeviceId:X8}"));
                pci.Add(new("子系統 ID", $"0x{pi.PciSubSystemId:X8}"));
                pci.Add(new("匯流排位置", $"{pi.Domain:X4}:{pi.Bus:X2}:{pi.Device:X2}"));
            }
            int? gen = TryI(NvmlInterop.GetPcieGen), genMax = TryI(NvmlInterop.GetPcieGenMax);
            int? w = TryI(NvmlInterop.GetPcieWidth), wMax = TryI(NvmlInterop.GetPcieWidthMax);
            if (gen is int g && genMax is int gm) pci.Add(new("連結世代", $"PCIe {g}.0 / 最高 {gm}.0"));
            if (w is int ww && wMax is int wm) pci.Add(new("連結寬度", $"x{ww} / 最高 x{wm}"));
            if (NvmlInterop.GetPcieReplay(_dev, out var replay) == 0) pci.Add(new("重送計數", $"{replay}"));
            if (pci.Count > 0) groups.Add(new("PCI Express", pci));

            // 顯示記憶體
            var mem = new List<GpuNvmlField>();
            var mi = new NvmlInterop.NvmlMemory();
            if (NvmlInterop.GetMemoryInfo(_dev, ref mi) == 0 && mi.Total > 0)
            {
                mem.Add(new("總容量", Gib(mi.Total)));
                mem.Add(new("已使用", $"{Gib(mi.Used)}（{mi.Used * 100.0 / mi.Total:0}%）"));
                mem.Add(new("可用", Gib(mi.Free)));
            }
            if (mem.Count > 0) groups.Add(new("顯示記憶體", mem));

            // 時脈上限（NVML 回報的各網域最高時脈）
            var clk = new List<GpuNvmlField>();
            if (NvmlInterop.GetMaxClock(_dev, NvmlInterop.CLOCK_GRAPHICS, out var mg) == 0 && mg > 0) clk.Add(new("核心最高", $"{mg} MHz"));
            if (NvmlInterop.GetMaxClock(_dev, NvmlInterop.CLOCK_SM, out var ms) == 0 && ms > 0) clk.Add(new("SM 最高", $"{ms} MHz"));
            if (NvmlInterop.GetMaxClock(_dev, NvmlInterop.CLOCK_MEM, out var mm) == 0 && mm > 0) clk.Add(new("顯示記憶體最高", $"{mm} MHz"));
            if (NvmlInterop.GetMaxClock(_dev, NvmlInterop.CLOCK_VIDEO, out var mv) == 0 && mv > 0) clk.Add(new("影像編碼最高", $"{mv} MHz"));
            if (clk.Count > 0) groups.Add(new("時脈上限", clk));

            // 功耗與溫度門檻
            var pt = new List<GpuNvmlField>();
            if (NvmlInterop.GetEnforcedPowerLimit(_dev, out var epl) == 0 && epl > 0) pt.Add(new("實際功耗上限", $"{epl / 1000.0:0} W"));
            if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_SHUTDOWN, out var sd) == 0 && sd > 0) pt.Add(new("強制關機溫度", $"{sd} °C"));
            if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_SLOWDOWN, out var sl) == 0 && sl > 0) pt.Add(new("降頻保護溫度", $"{sl} °C"));
            if (pt.Count > 0) groups.Add(new("功耗與溫度門檻", pt));

            // 狀態與模式
            var st = new List<GpuNvmlField>();
            if (NvmlInterop.GetPerfState(_dev, out var ps) == 0 && ps >= 0 && ps <= 15) st.Add(new("效能狀態", $"P{ps}"));
            if (NvmlInterop.GetThrottleReasons(_dev, out var tr) == 0) st.Add(new("目前限制原因", ThrottleText(tr)));
            if (NvmlInterop.GetPersistenceMode(_dev, out var pm) == 0) st.Add(new("常駐模式", pm == 1 ? "開啟" : "關閉"));
            if (NvmlInterop.GetComputeMode(_dev, out var cm) == 0) st.Add(new("運算模式", ComputeModeName(cm)));
            if (NvmlInterop.GetEccMode(_dev, out var ecc, out _) == 0) st.Add(new("ECC 記憶體", ecc == 1 ? "開啟" : "關閉"));
            if (NvmlInterop.GetEncoderUtil(_dev, out var eu, out _) == 0) st.Add(new("編碼器使用率", $"{eu} %"));
            if (NvmlInterop.GetDecoderUtil(_dev, out var du, out _) == 0) st.Add(new("解碼器使用率", $"{du} %"));
            if (st.Count > 0) groups.Add(new("狀態與模式", st));

            NvmlGroups = groups;
            OnPropertyChanged(nameof(HasNvmlGroups));
        }
        catch { /* 深度資訊為盡力而為，失敗不影響主功能 */ }
    }

    private static void AddStr(List<GpuNvmlField> list, string label, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) list.Add(new(label, value!)); }

    private delegate int OutIntFn(IntPtr d, out int v);
    private int? TryI(OutIntFn fn) => fn(_dev, out var v) == 0 ? v : null;

    private static string Gib(ulong bytes) => $"{bytes / 1073741824.0:0.0} GiB";

    private static string BrandName(uint b) => b switch
    {
        1 => "Quadro", 2 => "Tesla", 3 => "NVS", 4 => "GRID",
        5 => "GeForce", 6 => "TITAN", 7 => "NVIDIA vApps",
        _ => "NVIDIA",
    };

    private static string ArchName(uint a) => a switch
    {
        2 => "Kepler", 3 => "Maxwell", 4 => "Pascal", 5 => "Volta",
        6 => "Turing", 7 => "Ampere", 8 => "Ada Lovelace", 9 => "Hopper", 10 => "Blackwell",
        _ => "未知",
    };

    private static string ComputeModeName(int m) => m switch
    {
        0 => "預設（Default）", 1 => "獨佔執行緒", 2 => "禁止", 3 => "獨佔行程",
        _ => "未知",
    };

    private static string ThrottleText(ulong r)
    {
        if (r == 0) return "無（全速運行）";
        var parts = new List<string>();
        if ((r & 0x1) != 0) parts.Add("GPU 閒置");
        if ((r & 0x2) != 0) parts.Add("應用時脈設定");
        if ((r & 0x4) != 0) parts.Add("軟體功耗上限");
        if ((r & 0x8) != 0) parts.Add("硬體降速");
        if ((r & 0x10) != 0) parts.Add("同步加速");
        if ((r & 0x20) != 0) parts.Add("軟體溫度降速");
        if ((r & 0x40) != 0) parts.Add("硬體溫度降速");
        if ((r & 0x80) != 0) parts.Add("硬體功耗剎車");
        if ((r & 0x100) != 0) parts.Add("顯示時脈設定");
        return parts.Count > 0 ? string.Join("、", parts) : $"0x{r:X}";
    }

    private void UpdateAvailabilityText()
    {
        if (!Available)
        {
            AvailabilityText = "未偵測到可控制的 NVIDIA 顯示卡（找不到 nvml.dll／nvapi64.dll 或無相容裝置）。本模組停用，其餘功能不受影響。";
            return;
        }
        var parts = new List<string>();
        parts.Add(NvmlAvailable ? "功耗上限／風扇：可用（NVML）" : "功耗上限／風扇：不可用");
        parts.Add(NvapiAvailable ? "核心／顯示記憶體頻率偏移：可用（NVAPI）" : "核心／顯示記憶體頻率偏移：不可用");
        parts.Add(TempControlAvailable ? "溫度上限：可用（NVML 溫度門檻）" : "溫度上限：不可用");
        var admin = IsAdmin ? "" : "　※ 寫入需系統管理員權限，目前非管理員身分，套用可能失敗。";
        AvailabilityText = string.Join("；", parts) + "。" + admin;
    }

    /// <summary>每秒即時遙測（由主檢視模型的計時器呼叫）。</summary>
    public void Tick()
    {
        if (!_nvmlInited) return;
        try
        {
            if (NvmlInterop.GetClock(_dev, NvmlInterop.CLOCK_GRAPHICS, out var gc) == 0) CoreClockMhz = gc;
            if (NvmlInterop.GetClock(_dev, NvmlInterop.CLOCK_MEM, out var mc) == 0) MemClockMhz = mc;
            if (NvmlInterop.GetTemperature(_dev, NvmlInterop.TEMPERATURE_GPU, out var t) == 0) TempC = t;
            if (NvmlInterop.GetPowerUsage(_dev, out var pw) == 0) PowerW = pw / 1000.0;
            if (NvmlInterop.GetFanSpeed(_dev, out var f) == 0) FanPercent = f;
            if (NvmlInterop.GetPowerLimit(_dev, out var lim) == 0) PowerLimitW = lim / 1000.0;
        }
        catch { /* 單拍讀取失敗不影響後續 */ }
        RefreshWriteReadbacks();   // 頻率偏移／溫度上限的目前值一併每拍更新
    }

    private bool _tempSeeded;

    /// <summary>回讀寫入類數值（頻率偏移、溫度上限）。每拍呼叫；溫度目標只在首次以真實值種入一次，
    /// 之後不覆寫使用者拖動的滑桿。</summary>
    private void RefreshWriteReadbacks()
    {
        if (NvapiAvailable)
        {
            try
            {
                if (NvapiInterop.GetClockOffset(NvapiInterop.CLOCK_GRAPHICS) is { } co) CoreOffsetNow = co / 1000.0;
                if (NvapiInterop.GetClockOffset(NvapiInterop.CLOCK_MEMORY) is { } mo) MemOffsetNow = mo / 1000.0;
            }
            catch { }
        }
        if (TempControlAvailable && _nvmlInited)
        {
            try
            {
                if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_CURR, out var tl) == 0 && tl > 0)
                {
                    TempLimitNow = tl;
                    if (!_tempSeeded) { TargetTempLimitC = tl; _tempSeeded = true; }
                }
            }
            catch { }
        }
    }

    // ══ 寫入動作 ═══════════════════════════════════════════════════════════════
    public void ApplyPowerLimit()
    {
        if (!NvmlAvailable || PowerDefaultW <= 0) { PowerStatus = "NVML 不可用，無法設定功耗上限。"; return; }
        try
        {
            uint mw = (uint)Math.Round(PowerDefaultW * 1000.0 * PowerLimitPercent / 100.0);
            int rc = NvmlInterop.SetPowerLimit(_dev, mw);
            PowerStatus = rc == 0
                ? $"已套用：功耗上限 {PowerLimitPercent:0}%（約 {mw / 1000.0:0} W）"
                : $"套用失敗（NVML 代碼 {rc}）{(IsAdmin ? "" : "；請以系統管理員身分執行")}";
        }
        catch (Exception ex) { PowerStatus = "套用失敗：" + ex.Message; }
    }

    public void ApplyCoreOffset()
    {
        if (!NvapiAvailable) { CoreStatus = "NVAPI 不可用，無法設定頻率偏移。"; return; }
        try
        {
            int rc = NvapiInterop.SetClockOffset(NvapiInterop.CLOCK_GRAPHICS, (int)Math.Round(TargetCoreOffsetMhz * 1000));
            CoreStatus = DescribeClock(rc, "核心");
            if (NvapiInterop.GetClockOffset(NvapiInterop.CLOCK_GRAPHICS) is { } co) CoreOffsetNow = co / 1000.0;
        }
        catch (Exception ex) { CoreStatus = "套用失敗：" + ex.Message; }
    }

    public void ApplyMemOffset()
    {
        if (!NvapiAvailable) { MemStatus = "NVAPI 不可用，無法設定頻率偏移。"; return; }
        try
        {
            int rc = NvapiInterop.SetClockOffset(NvapiInterop.CLOCK_MEMORY, (int)Math.Round(TargetMemOffsetMhz * 1000));
            MemStatus = DescribeClock(rc, "顯示記憶體");
            if (NvapiInterop.GetClockOffset(NvapiInterop.CLOCK_MEMORY) is { } mo) MemOffsetNow = mo / 1000.0;
        }
        catch (Exception ex) { MemStatus = "套用失敗：" + ex.Message; }
    }

    private string DescribeClock(int rc, string what) => rc switch
    {
        0 => $"已套用：{what}頻率偏移 {(what == "核心" ? TargetCoreOffsetMhz : TargetMemOffsetMhz):+0;-0;0} MHz",
        -1 => "NVAPI 不可用。",
        -3 => $"驅動未提供可調整的{what}頻率網域（此卡可能不支援）。",
        -9 => $"{what}結構版本不相容，驅動未寫入任何資料（NVAPI 代碼 -9）。",
        _ => $"{what}套用失敗（NVAPI 代碼 {rc}）{(IsAdmin ? "" : "；請以系統管理員身分執行")}",
    };

    public void ApplyTempLimit()
    {
        if (!TempControlAvailable || !_nvmlInited) { TempStatus = "溫度上限控制不可用。"; return; }
        try
        {
            int want = (int)Math.Round(TargetTempLimitC);
            int applied = want;
            int rc = NvmlInterop.SetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_CURR, ref applied);
            TempStatus = rc == 0
                ? $"已套用：溫度上限 {applied}°C"
                : $"套用失敗（NVML 代碼 {rc}）{(IsAdmin ? "" : "；請以系統管理員身分執行")}";
            if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_CURR, out var tl) == 0) TempLimitNow = tl;
        }
        catch (Exception ex) { TempStatus = "套用失敗：" + ex.Message; }
    }

    public void ApplyFan()
    {
        if (!NvmlAvailable) { FanStatus = "NVML 不可用，無法控制風扇。"; return; }
        try
        {
            uint fans = _numFans == 0 ? 1 : _numFans;
            if (!FanManual)
            {
                int rcA = 0;
                for (uint i = 0; i < fans; i++) { var r = NvmlInterop.SetDefaultFanSpeed(_dev, i); if (r != 0) rcA = r; }
                FanStatus = rcA == 0 ? "已還原為自動風扇控制。" : $"還原自動失敗（NVML 代碼 {rcA}）";
                return;
            }
            uint pct = (uint)Math.Clamp(TargetFanPercent, 0, 100);
            int rc = 0;
            for (uint i = 0; i < fans; i++) { var r = NvmlInterop.SetFanSpeed(_dev, i, pct); if (r != 0) rc = r; }
            FanStatus = rc == 0
                ? $"已套用：手動風扇 {pct}%（共 {fans} 顆風扇）"
                : $"套用失敗（NVML 代碼 {rc}）{(IsAdmin ? "" : "；請以系統管理員身分執行")}";
        }
        catch (Exception ex) { FanStatus = "套用失敗：" + ex.Message; }
    }

    /// <summary>一鍵套用目前所有目標值（依可用性逐項嘗試）。</summary>
    public void ApplyAll()
    {
        if (NvmlAvailable) { ApplyPowerLimit(); ApplyFan(); }
        if (NvapiAvailable) { ApplyCoreOffset(); ApplyMemOffset(); }
        if (TempControlAvailable) ApplyTempLimit();
    }

    /// <summary>還原安全預設：功耗 100%、頻率偏移歸零、風扇自動、溫度上限回原廠預設值。</summary>
    public void RestoreDefaults()
    {
        try
        {
            if (NvmlAvailable)
            {
                PowerLimitPercent = 100; ApplyPowerLimit();
                FanManual = false; ApplyFan();
            }
            if (NvapiAvailable)
            {
                TargetCoreOffsetMhz = 0; ApplyCoreOffset();
                TargetMemOffsetMhz = 0; ApplyMemOffset();
            }
            if (TempControlAvailable && _nvmlInited)
            {
                // 溫度上限還原為驅動的預設目標溫度（ACOUSTIC_MAX 為原廠上限值）
                if (NvmlInterop.GetTempThreshold(_dev, NvmlInterop.THRESHOLD_ACOUSTIC_MAX, out var def) == 0 && def > 0)
                    TargetTempLimitC = def;
                ApplyTempLimit();
            }
        }
        catch { /* 還原為盡力而為 */ }
    }

    public void Dispose()
    {
        try { if (NvapiAvailable) NvapiInterop.Shutdown(); } catch { }
        try { if (_nvmlInited) NvmlInterop.Shutdown(); } catch { }
    }
}
