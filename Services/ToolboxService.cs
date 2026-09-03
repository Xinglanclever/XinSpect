using System.Diagnostics;
using System.IO;

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
}

/// <summary>
/// 這個工具動起來會不會弄壞東西。決定畫面上要不要掛一枚警告徽章、以及從插槽啟動前要不要先問一次。
/// </summary>
public enum ToolRisk
{
    /// <summary>唯讀，或改動可逆。工具箱裡絕大多數是這一類。</summary>
    Normal,
    /// <summary>會刪資料或改系統設定，但救得回來（低階格式化、抹除、清理）。</summary>
    Caution,
    /// <summary>會寫入韌體或開機結構：出錯可能開不了機、需要外部燒錄器，資料也拿不回來。</summary>
    Danger,
}

/// <summary>單一工具箱項目。</summary>
public sealed class ToolItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ToolKind Kind { get; init; }
    /// <summary>System：可執行檔 / .msc / .cpl 名稱；WebLink／DetectApp：URL。</summary>
    public required string Target { get; init; }
    /// <summary>DetectApp：候選安裝路徑（找到即啟動）。</summary>
    public string[] Candidates { get; init; } = Array.Empty<string>();
    /// <summary>分組標題（供畫面分類）。</summary>
    public required string Group { get; init; }
    /// <summary>搜尋用的額外詞彙（英文名、別名、俗稱）。</summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();

    /// <summary>危險等級。預設 <see cref="ToolRisk.Normal"/>。</summary>
    public ToolRisk Risk { get; init; } = ToolRisk.Normal;

    /// <summary>
    /// 危險等級的具體後果。非 <see cref="ToolRisk.Normal"/> 時<b>必填</b>——
    /// 「請小心使用」這種話沒有資訊量，要寫清楚最壞情況（見 ToolboxFilterTests 的檢查）。
    /// </summary>
    public string? RiskNote { get; init; }

    public bool HasRisk => Risk != ToolRisk.Normal;

    /// <summary>徽章上的字。</summary>
    public string RiskLabel => Risk switch
    {
        ToolRisk.Danger => "危險",
        ToolRisk.Caution => "注意",
        _ => "",
    };

    /// <summary>徽章的提示：等級 ＋ 最壞情況。</summary>
    public string RiskTip => Risk switch
    {
        ToolRisk.Danger => "危險：" + (RiskNote ?? ""),
        ToolRisk.Caution => "注意：" + (RiskNote ?? ""),
        _ => "",
    };

    /// <summary>曦覽自己已涵蓋同一件事的頁面鍵；null 表示本程式沒有對應功能。</summary>
    /// <remarks>
    /// 1.9.0 起這個欄位<b>不再產生畫面上的徽章</b>。原本每個有對應頁面的項目旁邊會掛一枚
    /// 「曦覽內建：X」的標籤，但那枚標籤自 1.6.2 起就不可點（工具箱不做導覽），
    /// 剩下的只是在每一列後面重複「我們也有」，佔位置又不能做任何事。
    /// 這個鍵現在只用來標明 <see cref="NativeNote"/> 講的是哪一頁，並讓那一頁的標題也能被搜到。
    /// </remarks>
    public string? Native { get; init; }

    /// <summary>對照說明：曦覽的那一頁做到什麼、以及與這個第三方工具的差別（誠實寫出不足）。</summary>
    public string? NativeNote { get; init; }

    /// <summary>對應頁面的顯示標題（供搜尋比對）。</summary>
    public string NativeTitle => Native is { Length: > 0 } ? PageRegistry.FindAny(Native)?.Title ?? "" : "";

    /// <summary>主按鈕的提示：說明 ＋（若有）危險後果 ＋（若有）與曦覽自家功能的差異對照。</summary>
    public string Tip
    {
        get
        {
            var parts = new List<string> { Description };
            if (HasRisk) parts.Add(RiskTip);
            if (!string.IsNullOrEmpty(NativeNote)) parts.Add(NativeNote);
            return string.Join("\n\n", parts);
        }
    }

    /// <summary>是否可裝入本機執行檔（Windows 內建工具不需要插槽）。</summary>
    public bool CanSlot => Kind is not ToolKind.System;

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
/// 系統工具箱。兩種來源刻意混在同一份目錄裡並標明出處：
/// ① Windows 內建診斷 / 管理工具的一鍵啟動；
/// ② 第三方硬體工具的「偵測並啟動，未安裝則前往官方下載」。
/// 基於安全與授權考量，本程式不內含任何第三方執行檔，一律導向官方來源，
/// 避免第三方整合包可能夾帶的廣告或風險軟體。
/// <para>
/// 第三方項目若有曦覽已涵蓋的同一件事，會以 <see cref="ToolItem.Native"/> 指向自家頁面，
/// 並在 <see cref="ToolItem.NativeNote"/> 誠實寫出兩者差別——包含曦覽做不到的部分。
/// 這段對照文字出現在該項目的滑鼠提示裡。
/// </para>
/// <para>
/// 1.6.2 起工具箱<b>不再放曦覽自己的功能</b>：那些頁面本來就在左側欄與 Ctrl+K 裡，
/// 在這裡再擺一排跳頁鈕只是同一件事出現兩次。
/// </para>
/// <para>
/// 1.9.0 起也<b>不再掛「曦覽內建：X」徽章</b>與「只看曦覽做得到的」篩選：那枚徽章自 1.6.2 起
/// 就不可點（工具箱不做導覽），剩下的只是在每一列後面重複「我們也有」，佔位置又不能做任何事；
/// 篩選則是建立在那枚徽章之上，徽章不見了就沒有東西可以對照。對照說明改為只留在滑鼠提示裡。
/// 徽章的位置換給真的需要警告的東西——見 <see cref="ToolRisk"/>。
/// </para>
/// </summary>
public sealed class ToolboxService : ObservableObject
{
    private string _status = "點選工具即可啟動。第三方工具一律導向官方下載，未內含任何外部執行檔。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    public IReadOnlyList<ToolItem> Tools { get; } = new List<ToolItem>
    {
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
        new() { Group = "系統管理", Name = "登錄編輯程式",     Description = "檢視與編輯系統登錄", Kind = ToolKind.System, Target = "regedit",
                Risk = ToolRisk.Caution,
                RiskNote = "改錯或刪錯機碼可能讓某個程式、驅動甚至 Windows 本身開不起來，而且沒有復原按鈕。"
                         + "動之前先在該機碼上「匯出」成 .reg 檔，那是唯一的回頭路。" },

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
                Kind = ToolKind.WebLink, Target = "https://www.diskgenius.com/",
                Keywords = ["分割區", "分區", "救援", "資料恢復", "mbr", "gpt", "partition"],
                Risk = ToolRisk.Caution,
                RiskNote = "分割區的建立、刪除、調整大小與格式化都是真的寫進碟裡。調整大小中途斷電"
                         + "會讓那個分割區處於半完成狀態；重要資料先備份再動。" },
        // ── U 盤／SSD 真偽與表面檢測 ──────────────────────────────
        // 這三支是曦覽自己做不到的：儲存頁讀的是 SMART 與規格，而「這顆碟的容量是不是假的」、
        // 「有沒有壞道」都必須對整顆媒體循序讀寫一遍——那是量測，不是查詢。
        new() { Group = "硬碟工具", Name = "H2testw",
                Description = "寫入亂數再回讀比對，抓假容量的 U 盤與記憶卡；c't／heise 官方",
                Kind = ToolKind.WebLink, Target = "https://www.heise.de/download/product/h2testw-50539",
                Keywords = ["假容量", "真偽", "u盤", "隨身碟", "記憶卡", "sd", "microsd", "黑片",
                            "fake", "flash", "h2testw"],
                Risk = ToolRisk.Caution,
                RiskNote = "它會把整個媒體寫滿測試檔，而且官方要求先格式化成單一分割區：上面原有的資料"
                         + "會沒了，而且測完等於被整片覆寫過，救不回來。只對空白或可以清空的媒體用它。" },
        new() { Group = "硬碟工具", Name = "HDDScan",
                Description = "表面掃描與壞道檢測、SMART 屬性與硬碟自我測試；hddscan.com 官方",
                Kind = ToolKind.WebLink, Target = "https://hddscan.com/",
                Keywords = ["壞道", "壞塊", "表面掃描", "smart", "自我測試", "bad sector", "hddscan"],
                Native = "storage",
                NativeNote = "曦覽的儲存頁讀 SMART 屬性並做健康判讀，但不做逐磁區的表面掃描"
                           + "——那要對整顆碟循序讀一遍，是量測而不是查詢。要抓壞道請用它。",
                Risk = ToolRisk.Caution,
                RiskNote = "除了唯讀的表面掃描，它也能做線性抹除、以及改寫硬碟本身的 AAM／APM 與停轉計時器。"
                         + "抹除是不可逆的；設定變更會留在碟上。選錯模式的後果曦覽無法介入。" },
        new() { Group = "硬碟工具", Name = "HDD Low Level Format Tool",
                Description = "整碟寫零與全裝置 TRIM，走原始裝置路徑；hddguru 官方",
                Kind = ToolKind.WebLink, Target = "https://hddguru.com/software/HDD-LLF-Low-Level-Format-Tool/",
                Keywords = ["低階格式化", "寫零", "抹除", "清除", "trim", "llf", "llftool"],
                Risk = ToolRisk.Danger,
                RiskNote = "整顆碟寫零：分割表、隱藏區與所有資料一起沒了。它走的是原始裝置路徑"
                         + @"（\\.\PhysicalDriveN），不看分割區也不看檔案系統，所以選錯一顆碟就是那顆全毀，"
                         + "沒有復原步驟。TRIM 模式較快，但官方明說不保證完全銷毀。" },
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
                Kind = ToolKind.WebLink, Target = "http://rweverything.com/",
                Keywords = ["暫存器", "msr", "pci", "spi", "底層", "rw"],
                Risk = ToolRisk.Danger,
                RiskNote = "名字就是「什麼都能讀寫」：PCI 設定空間、實體記憶體、I/O 埠、SPI 快閃記憶體都能寫。"
                         + "寫錯的後果從當機到開不了機都有可能，嚴重時要靠外部燒錄器救。"
                         + "曦覽自己所有底層存取都是唯讀的，正是為了避開這一類風險。" },

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
                Kind = ToolKind.WebLink, Target = "https://rufus.ie/",
                Risk = ToolRisk.Caution,
                RiskNote = "製作開機碟會把目標 USB 整個重新分割並格式化，上面原有的檔案全部消失。"
                         + "選錯磁碟就是抹錯一顆——它會列出所有可移除裝置，看清楚容量與代號再按。" },
        new() { Group = "系統維護", Name = "Ventoy",
                Description = "免格式化多鏡像開機 USB，開源官方",
                Kind = ToolKind.WebLink, Target = "https://www.ventoy.net/",
                Risk = ToolRisk.Caution,
                RiskNote = "第一次安裝 Ventoy 到 USB 時會重建它的分割區，那顆隨身碟原有的資料會沒了"
                         + "（之後放鏡像才是單純複製檔案，不必再格式化）。" },
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
                NativeNote = "曦覽的棋類跑分是確定性整數負載，量的東西與 Cinebench 的算圖不同；"
                           + "要業界通行的可比分數請用 Cinebench。" },
        // 記憶體
        new() { Group = "記憶體工具", Name = "Thaiphoon Burner",
                Description = "讀取記憶體 SPD 製造商 / 時序 / 顆粒，Softnology 官方",
                Kind = ToolKind.WebLink, Target = "https://www.softnology.biz/",
                Keywords = ["spd", "顆粒", "時序", "海力士", "三星"],
                Native = "memory",
                NativeNote = "曦覽記憶體頁的時序是解析 CPU-Z 報告來的，並不是自己走 SMBus 讀 SPD——"
                           + "所以顆粒編號（如 Hynix CJR / Samsung B-die）曦覽讀不到，那要靠 Thaiphoon Burner。",
                Risk = ToolRisk.Danger,
                RiskNote = "讀 SPD 是安全的，但它也能「寫」SPD EEPROM（Burner 這個名字就是這個意思）。"
                         + "寫壞了那條記憶體的 SPD，主機板會認不出它、開機直接不過，而且沒有軟體層的復原方式。"
                         + "只用讀取功能就好。" },
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
                Native = "hwtest",
                NativeNote = "曦覽的「硬體檢測 → 動態檢測」是同一件事的原生替代（不需連網），並實測每幀呈現間隔與長幀；"
                           + "TestUFO 的測試圖樣種類多得多。" },
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

        // ── 資料救援與開機修復 ────────────────────────────────────
        // 曦覽自己完全不做這一塊：救援要對整顆碟做映像、逐磁區掃檔頭，那是另一種程式的職責。
        // 這裡只收有官方站、來源查得到的；量產工具（USB 主控刷寫）刻意不收，見本檔末的說明。
        new() { Group = "資料救援與開機修復", Name = "TestDisk & PhotoRec",
                Description = "修復分割表與開機磁區（TestDisk）＋ 依檔頭救回檔案（PhotoRec）；CGSecurity 官方、開源、免安裝",
                Kind = ToolKind.WebLink, Target = "https://www.cgsecurity.org/wiki/TestDisk_Download",
                Keywords = ["資料恢復", "救援", "分割表", "mbr", "gpt", "開機", "引導", "誤刪",
                            "testdisk", "photorec", "partition", "recovery"],
                Risk = ToolRisk.Caution,
                RiskNote = "TestDisk 會改寫分割表與開機磁區。寫下去之前它會要你確認，但寫錯的結果是"
                         + "「原本還認得出來的碟變成認不出來」。救援的第一步永遠是先對整顆碟做映像、"
                         + "在映像上動手，不要直接在唯一那顆碟上試。" },
        new() { Group = "資料救援與開機修復", Name = "Recuva",
                Description = "從資源回收筒、記憶卡與剛格式化的碟救回誤刪檔案；Piriform 官方（有免費版）",
                Kind = ToolKind.WebLink, Target = "https://www.ccleaner.com/recuva",
                Keywords = ["誤刪", "資料恢復", "救回", "回收筒", "recuva", "undelete"],
                Risk = ToolRisk.Caution,
                RiskNote = "救出來的檔案不要存回同一顆碟：任何寫入都可能蓋掉還沒救出來的資料。"
                         + "它另有「安全覆寫」功能，那是不可逆的抹除，別按錯。" },

        // ── 韌體與底層 ────────────────────────────────────────────
        // 曦覽全站的界線是唯讀，所以這一類自己不做，只在這裡標明「有這種工具、以及它有多危險」。
        new() { Group = "韌體與底層", Name = "Intel FPT（fptw64）",
                Description = "Intel Flash Programming Tool：直接讀寫主機板 SPI 快閃記憶體的 BIOS／ME 區域。"
                            + "Intel 未對一般使用者公開發佈，它隨 CSME System Tools 由 OEM／授權管道散佈；"
                            + "此連結為 Intel 官方下載中心（部分內容需登入，且限授權對象）",
                Kind = ToolKind.WebLink, Target = "https://www.intel.com/content/www/us/en/download-center/home.html",
                Keywords = ["bios", "刷寫", "刷bios", "韌體", "firmware", "spi", "fpt", "fptw64",
                            "me", "csme", "flash", "主機板"],
                Risk = ToolRisk.Danger,
                RiskNote = "這是本目錄裡最危險的一項：它直接寫主機板上的 SPI 快閃記憶體。版本必須與平台的"
                         + " ME 世代完全相符；寫錯映像或中途斷電會讓機器「開不了機」，而且不是清 CMOS 能救的"
                         + "——得靠 SPI 燒錄夾（CH341A 之類）把晶片重新燒回去，或整塊板子送修。"
                         + "Intel 只對授權對象發佈它，正是因為這個。曦覽只負責把它啟動，"
                         + "寫進去的內容與後果完全在你手上。" },
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

    /// <summary>清除搜尋。</summary>
    public void ClearFilter()
    {
        _query = "";
        OnPropertyChanged(nameof(Query));
        RefreshFilter();
    }

    /// <summary>重算 <see cref="FilteredGroups"/>；建構時亦呼叫一次以填入完整清單。</summary>
    public void RefreshFilter()
    {
        // 對應頁面的標題也納入搜尋：打「大檔掃描」要找得到 WizTree，那是對照說明在講的那一頁。
        var hit = Tools.Where(t => ToolboxFilter.Matches(
            _query, t.Name, t.Description, t.Group, t.NativeTitle,
            t.Keywords.Length == 0 ? null : string.Join(' ', t.Keywords))).ToList();

        FilteredGroups = hit
            .GroupBy(t => t.Group)
            .Select(g => new ToolGroup { Title = g.Key, Items = g.ToList() })
            .ToList();

        FilterSummary = ToolboxFilter.Summarize(_query, hit.Count, Tools.Count);
    }

    public ToolboxService() => RefreshFilter();

    /// <summary>啟動系統工具、開啟官方網頁，或偵測第三方工具後啟動 / 導向下載。
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
