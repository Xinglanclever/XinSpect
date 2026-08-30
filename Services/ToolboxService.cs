using System.Diagnostics;
using System.IO;
using System.Windows;

namespace XinSpect;

/// <summary>工具箱項目的動作類型。</summary>
public enum ToolKind
{
    /// <summary>Windows 內建系統工具（直接啟動）。</summary>
    System,
    /// <summary>開啟官方下載 / 資訊網頁（以瀏覽器）。</summary>
    WebLink,
    /// <summary>偵測是否已安裝：找到則啟動，否則開啟官方下載頁。</summary>
    DetectApp,
    /// <summary>曦覽自己做的功能：<see cref="ToolItem.Target"/> 為
    /// <see cref="PageRegistry"/> 的頁面鍵（主頁面或「實用工具」子頁皆可），點選即跳頁。</summary>
    Builtin,
    /// <summary>曦覽自己做的全螢幕硬體檢測視窗（<see cref="ToolItem.Target"/> 為視窗代號）。</summary>
    BuiltinWindow,
}

/// <summary>單一工具箱項目。</summary>
public sealed class ToolItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ToolKind Kind { get; init; }
    /// <summary>System：可執行檔 / .msc / .cpl 名稱；WebLink／DetectApp：URL；
    /// Builtin：PageRegistry 頁面鍵；BuiltinWindow：檢測視窗代號。</summary>
    public required string Target { get; init; }
    /// <summary>DetectApp：候選安裝路徑（找到即啟動）。</summary>
    public string[] Candidates { get; init; } = Array.Empty<string>();
    /// <summary>分組標題（供畫面分類）。</summary>
    public required string Group { get; init; }
    /// <summary>搜尋用的額外詞彙（英文名、別名、俗稱）。</summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();

    /// <summary>曦覽自己已涵蓋同一件事的頁面鍵；null 表示本程式沒有對應功能。</summary>
    public string? Native { get; init; }

    /// <summary>對照說明：曦覽的那一頁做到什麼、以及與這個第三方工具的差別（誠實寫出不足）。</summary>
    public string? NativeNote { get; init; }

    /// <summary>本身就是曦覽做的功能（跳頁或開檢測視窗）。</summary>
    public bool IsBuiltin => Kind is ToolKind.Builtin or ToolKind.BuiltinWindow;

    /// <summary>有標註曦覽的對應頁面（且該頁確實存在於註冊表中）。</summary>
    public bool HasNative => Native is { Length: > 0 } && PageRegistry.FindAny(Native) is not null;

    /// <summary>對應頁面的顯示標題（供徽章文字與搜尋比對）。</summary>
    public string NativeTitle => Native is { Length: > 0 } ? PageRegistry.FindAny(Native)?.Title ?? "" : "";

    /// <summary>對應頁面徽章的文字。</summary>
    public string NativeLabel => $"曦覽內建：{NativeTitle}";

    /// <summary>對應頁面徽章的提示（帶誠實的差異說明）。</summary>
    public string NativeTip => string.IsNullOrEmpty(NativeNote)
        ? $"曦覽的「{NativeTitle}」頁已涵蓋同一件事，點此直接前往。"
        : $"曦覽的「{NativeTitle}」頁已涵蓋同一件事，點此直接前往。\n\n{NativeNote}";

    /// <summary>主按鈕的提示：說明加上（若有）與曦覽自家功能的差異對照。</summary>
    public string Tip => string.IsNullOrEmpty(NativeNote) ? Description : $"{Description}\n\n{NativeNote}";

    /// <summary>是否可裝入本機執行檔（Windows 內建工具與曦覽自己的功能都不需要插槽）。</summary>
    public bool CanSlot => Kind is not (ToolKind.System or ToolKind.Builtin or ToolKind.BuiltinWindow);

    private string? _slotPath;
    /// <summary>使用者裝入插槽的本機可執行檔路徑（下載後「裝進去」，之後主鈕即直接啟動它）。</summary>
    public string? SlotPath
    {
        get => _slotPath;
        set
        {
            if (SetProperty(ref _slotPath, value))
            {
                OnPropertyChanged(nameof(HasSlot));
                OnPropertyChanged(nameof(SlotLabel));
                OnPropertyChanged(nameof(SlotTip));
            }
        }
    }

    /// <summary>插槽已裝入且該檔案確實存在。</summary>
    public bool HasSlot => !string.IsNullOrEmpty(_slotPath) && File.Exists(_slotPath);

    /// <summary>插槽按鈕文字：未裝入顯示「裝入」，已裝入顯示勾號。</summary>
    public string SlotLabel => HasSlot ? "✓ 已裝入" : "裝入";

    /// <summary>插槽按鈕的提示說明。</summary>
    public string SlotTip => HasSlot
        ? $"已裝入：{_slotPath}\n主鈕即直接啟動此檔；點此可更換，右鍵可移除或開啟所在資料夾。"
        : "下載此工具後，點「裝入」選擇它的可執行檔（.exe）放進插槽，之後即可從工具箱直接啟動。";
}

/// <summary>工具箱的分組（供畫面依類別呈現）。</summary>
public sealed class ToolGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<ToolItem> Items { get; init; }
}

/// <summary>
/// 系統工具箱。三種來源刻意混在同一份目錄裡並標明出處：
/// ①「曦覽內建」——本程式自己實作的功能，點了就在程式內完成，不需要下載任何東西；
/// ② Windows 內建診斷 / 管理工具的一鍵啟動；
/// ③ 第三方硬體工具的「偵測並啟動，未安裝則前往官方下載」。
/// 基於安全與授權考量，本程式不內含任何第三方執行檔，一律導向官方來源，
/// 避免第三方整合包可能夾帶的廣告或風險軟體。
/// <para>
/// 第三方項目若有曦覽已涵蓋的同一件事，會以 <see cref="ToolItem.Native"/> 指向自家頁面，
/// 並在 <see cref="ToolItem.NativeNote"/> 誠實寫出兩者差別——包含曦覽做不到的部分。
/// 這是本工具箱與「整合包」的分別：目的是讓使用者知道哪些工具其實不必再裝，
/// 而不是把清單堆長。
/// </para>
/// </summary>
public sealed class ToolboxService : ObservableObject
{
    private string _status = "點選工具即可啟動。第三方工具一律導向官方下載，未內含任何外部執行檔。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    public IReadOnlyList<ToolItem> Tools { get; } = new List<ToolItem>
    {
        // ── 曦覽內建（本程式自己做的，點了就在程式內完成）──────────
        new() { Group = "曦覽內建", Name = "螢幕檢測",       Description = "全螢幕純色循環，檢查亮點／暗點（壞點）、漏光與背光均勻度。按 Esc 離開。",
                Kind = ToolKind.BuiltinWindow, Target = "screen", Keywords = ["dead pixel", "壞點", "亮點", "螢幕", "純色"] },
        new() { Group = "曦覽內建", Name = "滑鼠檢測",       Description = "測試左／右／中／側鍵、滾輪與移動軌跡，估計回報率並偵測連點抖動。按 Esc 離開。",
                Kind = ToolKind.BuiltinWindow, Target = "mouse", Keywords = ["mouse", "滑鼠", "雙擊", "回報率", "polling"] },
        new() { Group = "曦覽內建", Name = "鍵盤檢測",       Description = "虛擬鍵盤逐鍵確認觸發，統計同時按鍵數（防鬼鍵／NKRO）與鍵碼。按 Esc 離開。",
                Kind = ToolKind.BuiltinWindow, Target = "keyboard", Keywords = ["keyboard", "鍵盤", "按鍵", "卡鍵"] },
        new() { Group = "曦覽內建", Name = "喇叭檢測",       Description = "即時合成左／右／雙聲道測試音與 20 Hz～20 kHz 掃頻，確認接線、聲道對調與破音。按 Esc 離開。",
                Kind = ToolKind.BuiltinWindow, Target = "speaker", Keywords = ["speaker", "喇叭", "聲道", "音訊", "audio"] },
        new() { Group = "曦覽內建", Name = "動態檢測",       Description = "移動條判讀拖影與過衝，並實測每幀呈現間隔、長幀與中斷次數（TestUFO 的原生替代，不需連網）。按 Esc 離開。",
                Kind = ToolKind.BuiltinWindow, Target = "motion", Keywords = ["testufo", "拖影", "殘影", "更新率", "motion"] },

        new() { Group = "曦覽內建", Name = "處理器完整規格", Description = "CPUID / MSR 直讀的規格、頻率真相與管線歸因",
                Kind = ToolKind.Builtin, Target = "cpu", Keywords = ["cpu", "cpuid", "msr", "處理器", "頻率"] },
        new() { Group = "曦覽內建", Name = "感測器總表",     Description = "溫度 / 電壓 / 風扇 / 功耗的即時讀值與記錄",
                Kind = ToolKind.Builtin, Target = "sensors", Keywords = ["sensor", "感測", "溫度", "電壓", "風扇"] },
        new() { Group = "曦覽內建", Name = "健康與體檢",     Description = "整機健康評估、MCA / WHEA、平台可信度與電源政策",
                Kind = ToolKind.Builtin, Target = "health", Keywords = ["health", "健康", "體檢", "mca", "whea"] },
        new() { Group = "曦覽內建", Name = "效能測試與烤機", Description = "象棋跑分、綜合分數、烤機穩定度與 Top-down 歸因",
                Kind = ToolKind.Builtin, Target = "bench", Keywords = ["bench", "跑分", "烤機", "stress", "topdown"] },
        new() { Group = "曦覽內建", Name = "DPC 延遲分析",   Description = "以 ETW 排出造成爆音 / 停頓的肇事驅動",
                Kind = ToolKind.Builtin, Target = "dpc", Keywords = ["dpc", "isr", "latencymon", "延遲", "爆音"] },
        new() { Group = "曦覽內建", Name = "幀時間監測",     Description = "遊戲幀時間與 1% / 0.1% low 統計",
                Kind = ToolKind.Builtin, Target = "frametime", Keywords = ["frametime", "fps", "幀", "capframex"] },
        new() { Group = "曦覽內建", Name = "藍屏傾印分析",   Description = "解析 minidump 找出當機模組與錯誤碼",
                Kind = ToolKind.Builtin, Target = "bsod", Keywords = ["bsod", "藍屏", "dump", "當機", "bluescreenview"] },
        new() { Group = "曦覽內建", Name = "開機啟動項",     Description = "檢視與停用開機自啟項目",
                Kind = ToolKind.Builtin, Target = "startup", Keywords = ["startup", "autoruns", "啟動", "開機"] },
        new() { Group = "曦覽內建", Name = "大檔空間掃描",   Description = "掃出佔空間的大檔與資料夾",
                Kind = ToolKind.Builtin, Target = "diskscan", Keywords = ["wiztree", "windirstat", "空間", "大檔", "掃描"] },
        new() { Group = "曦覽內建", Name = "垃圾清理",       Description = "暫存 / 快取 / 更新殘留清理（列出後再決定刪除）",
                Kind = ToolKind.Builtin, Target = "cleanup", Keywords = ["cleanup", "cleanmgr", "垃圾", "清理", "暫存"] },
        new() { Group = "曦覽內建", Name = "連接埠占用",     Description = "查出是哪個行程占著某個埠",
                Kind = ToolKind.Builtin, Target = "port", Keywords = ["port", "netstat", "連接埠", "占用"] },
        new() { Group = "曦覽內建", Name = "系統風扇控制",   Description = "自訂風扇曲線並可一鍵還原自動",
                Kind = ToolKind.Builtin, Target = "fan", Keywords = ["fan", "fancontrol", "風扇", "轉速", "曲線"] },
        new() { Group = "曦覽內建", Name = "顯示卡超頻",     Description = "功耗 / 風扇 / 溫度上限與核心 / 記憶體偏移",
                Kind = ToolKind.Builtin, Target = "gpuoc", Keywords = ["afterburner", "超頻", "顯示卡", "overclock", "nvml"] },
        new() { Group = "曦覽內建", Name = "記憶體真實面貌", Description = "認可量、認可上限與尖峰，說清楚什麼叫「用掉」",
                Kind = ToolKind.Builtin, Target = "memory", Keywords = ["rammap", "記憶體", "commit", "認可", "分頁檔"] },
        new() { Group = "曦覽內建", Name = "電池損耗分析",   Description = "設計容量 / 目前滿電容量 / 循環次數與損耗率",
                Kind = ToolKind.Builtin, Target = "battery", Keywords = ["battery", "電池", "循環", "損耗", "batteryinfoview"] },

        // ── 系統診斷 ──────────────────────────────────────────────
        new() { Group = "系統診斷", Name = "裝置管理員",       Description = "檢視與管理硬體裝置、驅動程式", Kind = ToolKind.System, Target = "devmgmt.msc" },
        new() { Group = "系統診斷", Name = "DirectX 診斷",     Description = "顯示 DirectX、顯示卡與音效資訊", Kind = ToolKind.System, Target = "dxdiag" },
        new() { Group = "系統診斷", Name = "系統資訊",         Description = "完整的硬體與作業系統摘要（msinfo32）", Kind = ToolKind.System, Target = "msinfo32" },
        new() { Group = "系統診斷", Name = "資源監視器",       Description = "即時的 CPU / 記憶體 / 磁碟 / 網路使用", Kind = ToolKind.System, Target = "resmon" },
        new() { Group = "系統診斷", Name = "效能監視器",       Description = "詳細效能計數器與記錄", Kind = ToolKind.System, Target = "perfmon" },
        new() { Group = "系統診斷", Name = "工作管理員",       Description = "程序、效能與啟動項管理", Kind = ToolKind.System, Target = "taskmgr" },

        // ── 磁碟與記憶體 ──────────────────────────────────────────
        new() { Group = "磁碟與記憶體", Name = "磁碟清理",         Description = "清除暫存與系統無用檔案（cleanmgr）", Kind = ToolKind.System, Target = "cleanmgr",
                Keywords = ["清理", "暫存", "垃圾"],
                Native = "cleanup",
                NativeNote = "曦覽的垃圾清理會先列出要刪什麼、各占多少空間，再由你決定刪除，"
                           + "並涵蓋 cleanmgr 不處理的項目（如各家瀏覽器快取）。" },
        new() { Group = "磁碟與記憶體", Name = "磁碟管理",         Description = "磁碟分割、格式化與磁碟區管理", Kind = ToolKind.System, Target = "diskmgmt.msc" },
        new() { Group = "磁碟與記憶體", Name = "重組與最佳化磁碟機", Description = "對 HDD 重組、對 SSD 執行 TRIM", Kind = ToolKind.System, Target = "dfrgui" },
        new() { Group = "磁碟與記憶體", Name = "Windows 記憶體診斷", Description = "重開機後檢測記憶體是否穩定", Kind = ToolKind.System, Target = "MdSched.exe" },

        // ── 系統管理 ──────────────────────────────────────────────
        new() { Group = "系統管理", Name = "服務",             Description = "啟動 / 停止 Windows 服務", Kind = ToolKind.System, Target = "services.msc" },
        new() { Group = "系統管理", Name = "電腦管理",         Description = "整合式系統管理主控台", Kind = ToolKind.System, Target = "compmgmt.msc" },
        new() { Group = "系統管理", Name = "程式和功能",       Description = "解除安裝或變更已安裝的程式", Kind = ToolKind.System, Target = "appwiz.cpl" },
        new() { Group = "系統管理", Name = "登錄編輯程式",     Description = "檢視與編輯系統登錄（請謹慎使用）", Kind = ToolKind.System, Target = "regedit" },

        // ── 處理器工具（官方來源）─────────────────────────────────
        new() { Group = "處理器工具", Name = "CPU-Z",
                Description = "處理器 / 主機板 / 記憶體規格檢測，CPUID 官方",
                Kind = ToolKind.DetectApp, Target = "https://www.cpuid.com/softwares/cpu-z.html",
                Keywords = ["cpuz", "規格", "spd"],
                Native = "cpu",
                NativeNote = "曦覽的處理器頁是自己讀 CPUID 與 MSR 得來的，另有 CPU-Z 沒有的頻率真相（實測 BCLK / 倍頻表）"
                           + "與 Top-down 管線歸因。CPU-Z 仍有曦覽沒有的東西：記憶體 SPD 逐槽原始值，以及它自己的跑分。",
                Candidates = new[]
                {
                    @"C:\Program Files\CPUID\CPU-Z\cpuz.exe",
                    @"C:\Program Files (x86)\CPUID\CPU-Z\cpuz.exe",
                } },
        new() { Group = "處理器工具", Name = "Core Temp",
                Description = "各核心即時溫度監控，ALCPU 官方",
                Kind = ToolKind.WebLink, Target = "https://www.alcpu.com/CoreTemp/",
                Keywords = ["溫度", "coretemp"],
                Native = "sensors",
                NativeNote = "曦覽的感測器頁同樣逐核心讀溫度，並可記錄成 CSV、設定溫度警示。" },
        new() { Group = "處理器工具", Name = "ThrottleStop",
                Description = "監控 CPU 降頻並解除功耗限制，TechPowerUp 官方鏡像",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/download/techpowerup-throttlestop/",
                Keywords = ["節流", "降頻", "throttle", "限制"],
                Native = "health",
                NativeNote = "曦覽的健康頁讀 MSR 的黏滯節流位元，能說出「這台機器曾經因為什麼原因被降頻」。"
                           + "但曦覽只讀不寫：ThrottleStop 那種解除功耗限制、改電壓偏移的寫入動作，曦覽不做。" },
        new() { Group = "處理器工具", Name = "Prime95",
                Description = "GIMPS 分散式計算客戶端，常用於 CPU 穩定性烤機",
                Kind = ToolKind.WebLink, Target = "https://www.mersenne.org/download/",
                Keywords = ["烤機", "stress", "穩定"],
                Native = "bench",
                NativeNote = "曦覽的效能頁有自己的烤機（全執行緒滿載並觀察溫度 / 頻率 / 是否降頻）。"
                           + "但 Prime95 的 Small FFT 對 AVX 單元的壓迫比曦覽的整數負載更兇，要驗證極限穩定度仍應用它。" },
        // ── 顯示卡工具（官方來源）─────────────────────────────────
        new() { Group = "顯示卡工具", Name = "GPU-Z",
                Description = "顯示卡詳細規格與感測，TechPowerUp 官方",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/gpuz/",
                Keywords = ["gpuz", "顯卡", "規格"],
                Native = "gpu",
                NativeNote = "曦覽的顯示卡頁走 NVML / NVAPI 直讀規格與感測。GPU-Z 仍有曦覽沒有的 BIOS 讀取備份與繪圖 API 支援矩陣。" },
        new() { Group = "顯示卡工具", Name = "DDU 驅動移除工具",
                Description = "徹底移除顯示卡驅動殘留，Wagnardsoft 官方",
                Kind = ToolKind.DetectApp, Target = "https://www.wagnardsoft.com/",
                Candidates = new[]
                {
                    @"C:\Program Files\Display Driver Uninstaller\Display Driver Uninstaller.exe",
                    @"C:\Program Files (x86)\Display Driver Uninstaller\Display Driver Uninstaller.exe",
                } },
        new() { Group = "顯示卡工具", Name = "NVIDIA 驅動下載",
                Description = "取得 GeForce / Quadro 最新官方驅動",
                Kind = ToolKind.WebLink, Target = "https://www.nvidia.com/Download/index.aspx" },
        new() { Group = "顯示卡工具", Name = "AMD 驅動下載",
                Description = "取得 Radeon 最新官方驅動與軟體",
                Kind = ToolKind.WebLink, Target = "https://www.amd.com/en/support" },
        new() { Group = "顯示卡工具", Name = "NVIDIA Profile Inspector",
                Description = "深入調整 NVIDIA 驅動設定檔，開源官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/Orbmu2k/nvidiaProfileInspector/releases" },
        new() { Group = "顯示卡工具", Name = "MSI Afterburner",
                Description = "顯示卡超頻 / 風扇 / 監控，MSI 官方",
                Kind = ToolKind.WebLink, Target = "https://www.msi.com/Landing/afterburner/graphics-cards",
                Keywords = ["超頻", "overclock", "風扇", "afterburner"],
                Native = "gpuoc",
                NativeNote = "曦覽的顯示卡超頻頁以 NVML 調功耗 / 風扇 / 溫度上限、以 NVAPI 下核心與記憶體偏移，且一律可一鍵還原。"
                           + "Afterburner 仍有曦覽沒有的東西：電壓曲線編輯器、螢幕疊加顯示（RTSS）與 AMD 卡支援。" },

        // ── 烤機與效能測試（官方來源）─────────────────────────────
        new() { Group = "烤機與測試", Name = "FurMark",
                Description = "顯示卡極限烤機與穩定度測試，Geeks3D 官方",
                Kind = ToolKind.WebLink, Target = "https://geeks3d.com/furmark/" },
        new() { Group = "烤機與測試", Name = "GpuTest",
                Description = "跨平台 OpenGL / Vulkan 顯示卡壓力測試，Geeks3D 官方",
                Kind = ToolKind.WebLink, Target = "https://geeks3d.com/gputest/" },
        // ── 硬碟與 SSD（官方來源）─────────────────────────────────
        new() { Group = "硬碟工具", Name = "CrystalDiskInfo",
                Description = "硬碟 S.M.A.R.T. 健康與溫度監控，Crystal Dew World 官方",
                Kind = ToolKind.DetectApp, Target = "https://crystalmark.info/en/software/crystaldiskinfo/",
                Keywords = ["smart", "健康", "硬碟", "cdi"],
                Native = "storage",
                NativeNote = "曦覽的儲存裝置頁同樣讀 S.M.A.R.T. 與 NVMe 健康記錄頁，並判讀壽命與寫入量。"
                           + "CrystalDiskInfo 的廠商專屬屬性字典比曦覽完整，冷門型號仍建議用它交叉核對。",
                Candidates = new[]
                {
                    @"C:\Program Files\CrystalDiskInfo\DiskInfo64.exe",
                    @"C:\Program Files (x86)\CrystalDiskInfo\DiskInfo64.exe",
                } },
        new() { Group = "硬碟工具", Name = "CrystalDiskMark",
                Description = "硬碟循序 / 隨機讀寫速度測試，Crystal Dew World 官方",
                Kind = ToolKind.WebLink, Target = "https://crystalmark.info/en/software/crystaldiskmark/" },
        new() { Group = "硬碟工具", Name = "AS SSD Benchmark",
                Description = "SSD 效能與存取延遲測試，作者官方站",
                Kind = ToolKind.WebLink, Target = "https://www.alex-is.de/PHP/fusion/downloads.php" },
        new() { Group = "硬碟工具", Name = "ATTO Disk Benchmark",
                Description = "不同區塊大小的磁碟吞吐量測試，ATTO 官方",
                Kind = ToolKind.WebLink, Target = "https://www.atto.com/disk-benchmark/" },
        new() { Group = "硬碟工具", Name = "HD Tune",
                Description = "硬碟效能、健康與錯誤掃描，EFD Software 官方",
                Kind = ToolKind.WebLink, Target = "https://www.hdtune.com/download.html" },
        new() { Group = "硬碟工具", Name = "DiskGenius",
                Description = "分割區管理與資料救援，Eassos 官方",
                Kind = ToolKind.WebLink, Target = "https://www.diskgenius.com/" },
        new() { Group = "硬碟工具", Name = "WizTree",
                Description = "極速掃描磁碟空間佔用，Antibody Software 官方",
                Kind = ToolKind.WebLink, Target = "https://diskanalyzer.com/",
                Keywords = ["空間", "大檔", "掃描"],
                Native = "diskscan",
                NativeNote = "曦覽的大檔掃描列出最占空間的檔案與資料夾。WizTree 直讀 NTFS 的 MFT，全碟掃描比曦覽快得多，"
                           + "而且有樹狀圖視覺化——要掃整顆大容量硬碟仍以它為佳。" },
        new() { Group = "硬碟工具", Name = "WinDirStat",
                Description = "以樹狀圖檢視磁碟空間分布，開源官方",
                Kind = ToolKind.WebLink, Target = "https://windirstat.net/",
                Keywords = ["空間", "樹狀圖", "掃描"],
                Native = "diskscan",
                NativeNote = "曦覽的大檔掃描給的是清單而不是樹狀圖；要看空間的面積分布請用 WinDirStat。" },

        // ── 記憶體工具（官方來源）─────────────────────────────────
        new() { Group = "記憶體工具", Name = "TechPowerUp MemTest64",
                Description = "Windows 下的記憶體穩定性測試，TechPowerUp 官方",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/download/techpowerup-memtest64/" },
        new() { Group = "記憶體工具", Name = "MemTest86",
                Description = "可開機的權威記憶體檢測工具，PassMark 官方",
                Kind = ToolKind.WebLink, Target = "https://www.memtest86.com/" },
        new() { Group = "記憶體工具", Name = "ZenTimings",
                Description = "檢視 Ryzen 記憶體時序與電壓，作者官方",
                Kind = ToolKind.WebLink, Target = "https://zentimings.protonrom.com/" },
        // ── 綜合檢測（官方來源）───────────────────────────────────
        new() { Group = "綜合檢測", Name = "HWiNFO",
                Description = "極詳盡的硬體資訊與感測器監控，REALiX 官方",
                Kind = ToolKind.WebLink, Target = "https://www.hwinfo.com/download/",
                Keywords = ["感測", "監控", "hwinfo"],
                Native = "sensors",
                NativeNote = "曦覽的感測器頁走同一個底層（LibreHardwareMonitor）。HWiNFO 的感測器覆蓋面仍明顯更廣，"
                           + "尤其筆電 EC 與冷門主機板的專屬感測器——讀不到東西時請以它為準。" },
        new() { Group = "綜合檢測", Name = "HWMonitor",
                Description = "電壓 / 溫度 / 風扇即時監控，CPUID 官方",
                Kind = ToolKind.WebLink, Target = "https://www.cpuid.com/softwares/hwmonitor.html",
                Keywords = ["電壓", "溫度", "風扇", "監控"],
                Native = "sensors",
                NativeNote = "曦覽的感測器頁同樣給最小 / 目前 / 最大三欄，並可輸出 CSV 記錄。" },
        new() { Group = "綜合檢測", Name = "AIDA64",
                Description = "全面的系統資訊與壓力測試，FinalWire 官方",
                Kind = ToolKind.WebLink, Target = "https://www.aida64.com/downloads",
                Keywords = ["aida", "資訊", "壓力測試"] },
        new() { Group = "綜合檢測", Name = "Speccy",
                Description = "簡明的整機硬體規格總覽，Piriform 官方",
                Kind = ToolKind.WebLink, Target = "https://www.ccleaner.com/speccy",
                Keywords = ["總覽", "規格", "speccy"],
                Native = "overview",
                NativeNote = "曦覽的總覽頁就是這件事，且可直接匯出完整報告。" },
        new() { Group = "綜合檢測", Name = "RWEverything",
                Description = "讀寫幾乎所有底層硬體暫存器（進階），官方站",
                Kind = ToolKind.WebLink, Target = "http://rweverything.com/" },

        // ── 系統維護與工具（官方來源）─────────────────────────────
        new() { Group = "系統維護", Name = "Everything",
                Description = "毫秒級全碟檔名搜尋，voidtools 官方",
                Kind = ToolKind.DetectApp, Target = "https://www.voidtools.com/",
                Candidates = new[]
                {
                    @"C:\Program Files\Everything\Everything.exe",
                    @"C:\Program Files (x86)\Everything\Everything.exe",
                } },
        new() { Group = "系統維護", Name = "Process Explorer",
                Description = "強化版工作管理員，微軟 Sysinternals 官方",
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/process-explorer" },
        new() { Group = "系統維護", Name = "Autoruns",
                Description = "檢視與管理所有開機自啟項目，Sysinternals 官方",
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/autoruns",
                Keywords = ["啟動", "開機", "自啟"],
                Native = "startup",
                NativeNote = "曦覽的開機啟動項涵蓋常見的登錄與啟動資料夾位置。Autoruns 覆蓋的位置多得多"
                           + "（服務、驅動、WMI、Winlogon、排程等數十類），要抓惡意持久化仍該用它。" },
        new() { Group = "系統維護", Name = "Process Monitor",
                Description = "即時監控檔案 / 登錄 / 程序活動，Sysinternals 官方",
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/procmon" },
        new() { Group = "系統維護", Name = "RAMMap",
                Description = "詳細分析實體記憶體使用分布，Sysinternals 官方",
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/rammap",
                Keywords = ["記憶體", "分布", "commit"],
                Native = "memory",
                NativeNote = "曦覽的記憶體頁有「記憶體真實面貌」卡片，說清楚認可量 / 認可上限 / 尖峰的差別。"
                           + "RAMMap 能做曦覽做不到的事：逐類別（待命、修改、分頁集區）拆解與清空待命清單。" },
        new() { Group = "系統維護", Name = "Rufus",
                Description = "製作可開機 USB 隨身碟，Akeo 官方",
                Kind = ToolKind.WebLink, Target = "https://rufus.ie/" },
        new() { Group = "系統維護", Name = "Ventoy",
                Description = "免格式化多鏡像開機 USB，開源官方",
                Kind = ToolKind.WebLink, Target = "https://www.ventoy.net/" },
        new() { Group = "系統維護", Name = "FanControl",
                Description = "自訂風扇曲線的通用風扇控制，開源官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/Rem0o/FanControl.Releases/releases",
                Keywords = ["風扇", "曲線", "轉速"],
                Native = "fan",
                NativeNote = "曦覽的系統風扇頁可真實寫入轉速並一鍵還原自動控制。"
                           + "FanControl 的曲線編輯、感測來源混合與外掛生態比曦覽完整得多。" },
        new() { Group = "系統維護", Name = "Microsoft PowerToys",
                Description = "微軟官方進階系統增強工具集（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/microsoft/PowerToys/releases" },
        new() { Group = "系統維護", Name = "Dism++",
                Description = "系統清理 / 映像維護 / 開機管理，初雨團隊官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/Chuyu-Team/Dism-Multi-language/releases" },
        new() { Group = "系統維護", Name = "BlueScreenView",
                Description = "解析藍色當機（BSOD）傾印檔，NirSoft 官方",
                Kind = ToolKind.WebLink, Target = "https://www.nirsoft.net/utils/blue_screen_view.html",
                Keywords = ["藍屏", "bsod", "dump", "當機"],
                Native = "bsod",
                NativeNote = "曦覽的藍屏分析同樣解析 minidump，列出錯誤碼與可疑模組。" },
        new() { Group = "系統維護", Name = "LatencyMon",
                Description = "分析系統即時延遲（DPC）與卡頓來源，Resplendence 官方",
                Kind = ToolKind.WebLink, Target = "https://www.resplendence.com/latencymon",
                Keywords = ["dpc", "isr", "延遲", "爆音", "卡頓"],
                Native = "dpc",
                NativeNote = "曦覽的 DPC 延遲頁以 ETW 排出肇事驅動，屬同一件事。" },

        // ── 圖吧工具箱同款擴充（一律導向官方來源，仍不內含任何第三方執行檔）──
        // 處理器
        new() { Group = "處理器工具", Name = "Intel XTU",
                Description = "Intel 官方超頻與監控工具（Extreme Tuning Utility）",
                Kind = ToolKind.WebLink, Target = "https://www.intel.com/content/www/us/en/download/17881/intel-extreme-tuning-utility-intel-xtu.html",
                Keywords = ["超頻", "xtu", "overclock", "電壓"],
                Native = "oc",
                NativeNote = "曦覽的超頻頁做的是同一類事，且每一項都可還原。XTU 能改的項目（快取比例、AVX 偏移、電壓曲線）"
                           + "比曦覽多，且是 Intel 自己維護的——要細調仍以它為主。" },
        new() { Group = "處理器工具", Name = "SuperPI",
                Description = "計算圓周率衡量 CPU 單核效能，TechPowerUp 官方鏡像",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/download/super-pi/",
                Keywords = ["跑分", "單核", "圓周率"],
                Native = "bench",
                NativeNote = "曦覽的效能頁有自己的單 / 多執行緒跑分，但那組分數只在同一台機器的前後比較有意義；"
                           + "要跟網路上的成績對照，請用 SuperPI 這類公認基準。" },
        new() { Group = "處理器工具", Name = "wPrime",
                Description = "多執行緒質數運算衡量多核效能，TechPowerUp 官方鏡像",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/download/wprime/",
                Keywords = ["跑分", "多核", "質數"],
                Native = "bench",
                NativeNote = "同上：曦覽的分數不跨機器比較，跨機器對照請用 wPrime。" },
        new() { Group = "處理器工具", Name = "Cinebench",
                Description = "以實際算圖評測 CPU 單 / 多核效能，Maxon 官方",
                Kind = ToolKind.WebLink, Target = "https://www.maxon.net/en/cinebench",
                Keywords = ["跑分", "算圖", "多核", "cinebench"],
                Native = "bench",
                NativeNote = "曦覽的象棋跑分是確定性整數負載，量的東西與 Cinebench 的算圖不同；"
                           + "要業界通行的可比分數請用 Cinebench。" },
        // 記憶體
        new() { Group = "記憶體工具", Name = "Thaiphoon Burner",
                Description = "讀取記憶體 SPD 製造商 / 時序 / 顆粒，Softnology 官方",
                Kind = ToolKind.WebLink, Target = "https://www.softnology.biz/",
                Keywords = ["spd", "顆粒", "時序", "海力士", "三星"],
                Native = "memory",
                NativeNote = "曦覽記憶體頁的時序是解析 CPU-Z 報告來的，並不是自己走 SMBus 讀 SPD——"
                           + "所以顆粒編號（如 Hynix CJR / Samsung B-die）曦覽讀不到，那要靠 Thaiphoon Burner。" },
        new() { Group = "記憶體工具", Name = "TestMem5 (TM5)",
                Description = "輕量而嚴苛的記憶體穩定性測試，開源官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/CoolCmd/TestMem5" },
        new() { Group = "記憶體工具", Name = "HCI MemTest",
                Description = "多開視窗長時間燒機記憶體，HCI Design 官方",
                Kind = ToolKind.WebLink, Target = "https://hcidesign.com/memtest/" },
        // 硬碟
        new() { Group = "硬碟工具", Name = "SpaceSniffer",
                Description = "以樹狀圖直觀呈現磁碟空間佔用，Uderzo 官方",
                Kind = ToolKind.WebLink, Target = "http://www.uderzo.it/main_products/space_sniffer/",
                Keywords = ["空間", "樹狀圖", "掃描"],
                Native = "diskscan",
                NativeNote = "曦覽的大檔掃描給清單；要面積化的樹狀圖請用 SpaceSniffer。" },
        new() { Group = "硬碟工具", Name = "Defraggler",
                Description = "可對指定檔案 / 資料夾重組的磁碟重組工具，Piriform 官方",
                Kind = ToolKind.WebLink, Target = "https://www.ccleaner.com/defraggler" },
        // 烤機與測試
        new() { Group = "烤機與測試", Name = "OCCT",
                Description = "CPU / GPU / 電源 / 記憶體綜合穩定性與烤機，OCBASE 官方",
                Kind = ToolKind.WebLink, Target = "https://www.ocbase.com/",
                Keywords = ["烤機", "穩定", "stress", "電源"],
                Native = "bench",
                NativeNote = "曦覽的烤機只壓 CPU 並觀察降頻；OCCT 能同時壓 CPU＋GPU＋電源並自動偵測運算錯誤，"
                           + "驗證超頻是否真的穩定應以它為準。" },
        new() { Group = "烤機與測試", Name = "MSI Kombustor",
                Description = "基於 FurMark 的顯示卡壓力測試與跑分，Geeks3D 官方",
                Kind = ToolKind.WebLink, Target = "https://geeks3d.com/furmark/kombustor/",
                Keywords = ["烤機", "顯卡", "壓力"] },
        new() { Group = "烤機與測試", Name = "Unigine Superposition",
                Description = "高負載顯示卡跑分與穩定性測試，Unigine 官方",
                Kind = ToolKind.WebLink, Target = "https://benchmark.unigine.com/superposition",
                Keywords = ["跑分", "顯卡", "unigine"] },
        new() { Group = "烤機與測試", Name = "CapFrameX",
                Description = "遊戲幀時間 / 幀生成時間擷取與分析，開源官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/CXWorld/CapFrameX",
                Keywords = ["幀時間", "fps", "frametime", "1% low"],
                Native = "frametime",
                NativeNote = "曦覽的幀時間監測給即時曲線與 1% / 0.1% low。CapFrameX 的擷取與統計分析（含多次結果比較、"
                           + "PresentMon 全參數）遠比曦覽完整，正式評測請用它。" },
        // 顯示器
        new() { Group = "顯示器工具", Name = "TestUFO",
                Description = "線上檢測螢幕更新率與運動模糊，Blur Busters 官方",
                Kind = ToolKind.WebLink, Target = "https://www.testufo.com/",
                Keywords = ["testufo", "拖影", "更新率", "模糊"],
                NativeNote = "曦覽的「動態拖影檢測」是同一件事的原生替代（不需連網）；TestUFO 的測試圖樣種類多得多。" },
        new() { Group = "顯示器工具", Name = "Windows HDR 校準",
                Description = "微軟官方 HDR 校準工具（Microsoft Store）",
                Kind = ToolKind.WebLink, Target = "https://apps.microsoft.com/detail/9n7f2sm5d1lr" },
        new() { Group = "顯示器工具", Name = "MonInfo 顯示器資訊",
                Description = "讀取顯示器 EDID / 面板 / 色域資訊，EnTech 官方",
                Kind = ToolKind.WebLink, Target = "https://www.entechtaiwan.com/util/moninfo.shtm",
                Keywords = ["edid", "色域", "面板", "顯示器"],
                NativeNote = "本頁上方的「螢幕色域 EDID」卡片已直接讀出本機各螢幕的 EDID 與色域座標；"
                           + "MonInfo 另可解析原始 EDID 位元組與詳細時序描述子。" },
        // 系統維護
        new() { Group = "系統維護", Name = "Geek Uninstaller",
                Description = "強力解除安裝並清理殘留，Geek 官方",
                Kind = ToolKind.WebLink, Target = "https://geekuninstaller.com/" },
        new() { Group = "系統維護", Name = "HiBit Uninstaller",
                Description = "批次 / 強制解除安裝與殘留清理，HiBit 官方",
                Kind = ToolKind.WebLink, Target = "https://www.hibitsoft.ir/Uninstaller.html" },
        new() { Group = "系統維護", Name = "BatteryInfoView",
                Description = "筆電電池容量 / 循環 / 損耗查看，NirSoft 官方",
                Kind = ToolKind.WebLink, Target = "https://www.nirsoft.net/utils/battery_information_view.html",
                Keywords = ["電池", "循環", "損耗", "battery"],
                Native = "battery",
                NativeNote = "曦覽的電池分析同樣給設計容量 / 目前滿電容量 / 循環次數與損耗率。" },
        new() { Group = "系統維護", Name = "ShareX",
                Description = "截圖 / 錄影 / 錄製 GIF 的開源工具，官方站",
                Kind = ToolKind.WebLink, Target = "https://getsharex.com/" },
        new() { Group = "系統維護", Name = "Snappy Driver Installer Origin",
                Description = "離線 / 線上驅動安裝與更新，開源官方",
                Kind = ToolKind.WebLink, Target = "https://www.glenn.delahoy.com/snappy-driver-installer-origin/" },
        new() { Group = "系統維護", Name = "DesktopOK",
                Description = "保存與還原桌面圖示排列，SoftwareOK 官方",
                Kind = ToolKind.WebLink, Target = "https://www.softwareok.com/?seite=Freeware/DesktopOK" },
    };

    /// <summary>依 <see cref="ToolItem.Group"/> 分組後的工具清單（保留定義順序）。</summary>
    public IReadOnlyList<ToolGroup> Groups => Tools
        .GroupBy(t => t.Group)
        .Select(g => new ToolGroup { Title = g.Key, Items = g.ToList() })
        .ToList();

    // ── 搜尋與篩選 ────────────────────────────────────────────────
    private string _query = "";
    /// <summary>搜尋字串；多個詞彙以空白分隔且須全部命中（見 <see cref="ToolboxFilter"/>）。</summary>
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value)) RefreshFilter(); }
    }

    private bool _onlyBuiltin;
    /// <summary>只列出曦覽自己做得到的項目（本身內建，或有標註自家對應頁面者）。</summary>
    public bool OnlyBuiltin
    {
        get => _onlyBuiltin;
        set { if (SetProperty(ref _onlyBuiltin, value)) RefreshFilter(); }
    }

    private IReadOnlyList<ToolGroup> _filtered = Array.Empty<ToolGroup>();
    /// <summary>畫面實際呈現的分組（套用搜尋與篩選後；空的分組不會出現）。</summary>
    public IReadOnlyList<ToolGroup> FilteredGroups
    {
        get => _filtered;
        private set => SetProperty(ref _filtered, value);
    }

    private string _summary = "";
    /// <summary>搜尋結果摘要（沒有命中時明說，不留白）。</summary>
    public string FilterSummary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>清除搜尋與篩選。</summary>
    public void ClearFilter()
    {
        _query = "";
        _onlyBuiltin = false;
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(OnlyBuiltin));
        RefreshFilter();
    }

    /// <summary>重算 <see cref="FilteredGroups"/>；建構時亦呼叫一次以填入完整清單。</summary>
    public void RefreshFilter()
    {
        var hit = Tools.Where(t =>
        {
            if (_onlyBuiltin && !t.IsBuiltin && !t.HasNative) return false;
            return ToolboxFilter.Matches(_query, t.Name, t.Description, t.Group, t.NativeTitle,
                                         t.Keywords.Length == 0 ? null : string.Join(' ', t.Keywords));
        }).ToList();

        FilteredGroups = hit
            .GroupBy(t => t.Group)
            .Select(g => new ToolGroup { Title = g.Key, Items = g.ToList() })
            .ToList();

        FilterSummary = ToolboxFilter.Summarize(_query, _onlyBuiltin, hit.Count, Tools.Count);
    }

    public ToolboxService() => RefreshFilter();

    /// <summary>依項目類型跳到曦覽自家頁面、開啟內建檢測視窗、啟動系統工具、開啟官方網頁，
    /// 或偵測第三方工具後啟動 / 導向下載。
    /// 若該工具已裝入插槽（使用者下載後放進的本機執行檔），一律優先直接啟動插槽內的檔案。</summary>
    public void Launch(ToolItem tool)
    {
        try
        {
            // 插槽優先：已裝入且檔案存在時，直接啟動使用者放進去的本機執行檔。
            if (tool.HasSlot && tool.SlotPath is { } slot)
            {
                Process.Start(new ProcessStartInfo(slot)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(slot) ?? "",
                });
                StatusLine = $"已啟動插槽內的 {tool.Name}";
                return;
            }

            switch (tool.Kind)
            {
                case ToolKind.Builtin:
                    if (NavigateTo(tool.Target)) StatusLine = $"已前往曦覽內建的「{tool.Name}」";
                    else StatusLine = $"找不到頁面 {tool.Target}（內部錯誤，請回報）";
                    break;

                case ToolKind.BuiltinWindow:
                    if (OpenTestWindow(tool.Target)) StatusLine = $"已開啟內建檢測：{tool.Name}";
                    else StatusLine = $"未知的檢測視窗代號 {tool.Target}（內部錯誤，請回報）";
                    break;

                case ToolKind.System:
                    Process.Start(new ProcessStartInfo(tool.Target) { UseShellExecute = true });
                    StatusLine = $"已啟動：{tool.Name}";
                    break;

                case ToolKind.WebLink:
                    OpenUrl(tool.Target);
                    StatusLine = $"已於瀏覽器開啟官方頁面：{tool.Name}";
                    break;

                case ToolKind.DetectApp:
                    var found = tool.Candidates.FirstOrDefault(File.Exists);
                    if (found is not null)
                    {
                        Process.Start(new ProcessStartInfo(found)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(found) ?? "",
                        });
                        StatusLine = $"已啟動本機安裝的 {tool.Name}";
                    }
                    else
                    {
                        OpenUrl(tool.Target);
                        StatusLine = $"未偵測到 {tool.Name}，已開啟官方下載頁面。";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"啟動 {tool.Name} 失敗：{ex.Message}";
        }
    }

    /// <summary>跳到曦覽自家頁面（主頁面或「實用工具」子頁）；找不到該鍵時回傳 false。</summary>
    public static bool NavigateTo(string pageKey)
    {
        if (Shell.Main is not { } shell) return false;
        if (PageRegistry.Find(pageKey) is not null) { shell.NavigateToKey(pageKey); return true; }
        if (PageRegistry.FindUtility(pageKey) is not null) { shell.NavigateToUtility(pageKey); return true; }
        return false;
    }

    // 內建全螢幕檢測視窗（零外部相依，純輸入／顯示事件）。代號集中在此，
    // 以免 XAML 與服務兩邊各記一份而走鏽。
    private static bool OpenTestWindow(string code)
    {
        Window? w = code switch
        {
            "screen"   => new ScreenTestWindow(),
            "mouse"    => new MouseTestWindow(),
            "keyboard" => new KeyboardTestWindow(),
            "speaker"  => new SpeakerTestWindow(),
            "motion"   => new MotionTestWindow(),
            _          => null,
        };
        if (w is null) return false;
        w.Owner = Shell.TopWindow;
        w.Show();
        return true;
    }

    private static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ── 插槽（下載後裝入的本機執行檔）─────────────────────────────
    private SettingsService? _settings;

    /// <summary>接上設定服務並套用已保存的插槽路徑；於主視窗初始化時呼叫一次。
    /// （工具箱先於設定服務建立，故插槽需由此處回填，而非建構子。）</summary>
    public void AttachSettings(SettingsService settings)
    {
        _settings = settings;
        foreach (var t in Tools)
            if (settings.ToolSlots.TryGetValue(t.Name, out var path))
                t.SlotPath = path;
    }

    /// <summary>將某工具裝入指定的本機執行檔並持久化。</summary>
    public void AssignSlot(ToolItem tool, string path)
    {
        tool.SlotPath = path;
        _settings?.SetToolSlot(tool.Name, path);
        StatusLine = tool.HasSlot
            ? $"已將 {tool.Name} 裝入插槽，之後可從工具箱直接啟動。"
            : $"選擇的檔案不存在：{path}";
    }

    /// <summary>移除某工具的插槽並持久化。</summary>
    public void ClearSlot(ToolItem tool)
    {
        tool.SlotPath = null;
        _settings?.SetToolSlot(tool.Name, null);
        StatusLine = $"已移除 {tool.Name} 的插槽。";
    }
}
