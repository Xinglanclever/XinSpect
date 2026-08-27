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

/// <summary>單一工具箱項目。</summary>
public sealed class ToolItem
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ToolKind Kind { get; init; }
    /// <summary>System：可執行檔 / .msc / .cpl 名稱；WebLink：URL；DetectApp：官方下載 URL。</summary>
    public required string Target { get; init; }
    /// <summary>DetectApp：候選安裝路徑（找到即啟動）。</summary>
    public string[] Candidates { get; init; } = Array.Empty<string>();
    /// <summary>分組標題（供畫面分類）。</summary>
    public required string Group { get; init; }
}

/// <summary>工具箱的分組（供畫面依類別呈現）。</summary>
public sealed class ToolGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<ToolItem> Items { get; init; }
}

/// <summary>
/// 系統工具箱：內建 Windows 診斷 / 管理工具的一鍵啟動，以及第三方硬體工具（DDU、GPU-Z 等）的
/// 「偵測並啟動，未安裝則前往官方下載」。基於安全與授權考量，本程式不內含任何第三方執行檔，
/// 一律導向官方來源，避免第三方整合包可能夾帶的廣告或風險軟體。
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
        new() { Group = "磁碟與記憶體", Name = "磁碟清理",         Description = "清除暫存與系統無用檔案（cleanmgr）", Kind = ToolKind.System, Target = "cleanmgr" },
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
                Candidates = new[]
                {
                    @"C:\Program Files\CPUID\CPU-Z\cpuz.exe",
                    @"C:\Program Files (x86)\CPUID\CPU-Z\cpuz.exe",
                } },
        new() { Group = "處理器工具", Name = "Core Temp",
                Description = "各核心即時溫度監控，ALCPU 官方",
                Kind = ToolKind.WebLink, Target = "https://www.alcpu.com/CoreTemp/" },
        new() { Group = "處理器工具", Name = "ThrottleStop",
                Description = "監控 CPU 降頻並解除功耗限制，TechPowerUp 官方鏡像",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/download/techpowerup-throttlestop/" },
        new() { Group = "處理器工具", Name = "Prime95",
                Description = "GIMPS 分散式計算客戶端，常用於 CPU 穩定性烤機",
                Kind = ToolKind.WebLink, Target = "https://www.mersenne.org/download/" },
        // ── 顯示卡工具（官方來源）─────────────────────────────────
        new() { Group = "顯示卡工具", Name = "GPU-Z",
                Description = "顯示卡詳細規格與感測，TechPowerUp 官方",
                Kind = ToolKind.WebLink, Target = "https://www.techpowerup.com/gpuz/" },
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
                Kind = ToolKind.WebLink, Target = "https://www.msi.com/Landing/afterburner/graphics-cards" },

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
                Kind = ToolKind.WebLink, Target = "https://diskanalyzer.com/" },
        new() { Group = "硬碟工具", Name = "WinDirStat",
                Description = "以樹狀圖檢視磁碟空間分布，開源官方",
                Kind = ToolKind.WebLink, Target = "https://windirstat.net/" },

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
                Kind = ToolKind.WebLink, Target = "https://www.hwinfo.com/download/" },
        new() { Group = "綜合檢測", Name = "HWMonitor",
                Description = "電壓 / 溫度 / 風扇即時監控，CPUID 官方",
                Kind = ToolKind.WebLink, Target = "https://www.cpuid.com/softwares/hwmonitor.html" },
        new() { Group = "綜合檢測", Name = "AIDA64",
                Description = "全面的系統資訊與壓力測試，FinalWire 官方",
                Kind = ToolKind.WebLink, Target = "https://www.aida64.com/downloads" },
        new() { Group = "綜合檢測", Name = "Speccy",
                Description = "簡明的整機硬體規格總覽，Piriform 官方",
                Kind = ToolKind.WebLink, Target = "https://www.ccleaner.com/speccy" },
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
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/autoruns" },
        new() { Group = "系統維護", Name = "Process Monitor",
                Description = "即時監控檔案 / 登錄 / 程序活動，Sysinternals 官方",
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/procmon" },
        new() { Group = "系統維護", Name = "RAMMap",
                Description = "詳細分析實體記憶體使用分布，Sysinternals 官方",
                Kind = ToolKind.WebLink, Target = "https://learn.microsoft.com/sysinternals/downloads/rammap" },
        new() { Group = "系統維護", Name = "Rufus",
                Description = "製作可開機 USB 隨身碟，Akeo 官方",
                Kind = ToolKind.WebLink, Target = "https://rufus.ie/" },
        new() { Group = "系統維護", Name = "Ventoy",
                Description = "免格式化多鏡像開機 USB，開源官方",
                Kind = ToolKind.WebLink, Target = "https://www.ventoy.net/" },
        new() { Group = "系統維護", Name = "FanControl",
                Description = "自訂風扇曲線的通用風扇控制，開源官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/Rem0o/FanControl.Releases/releases" },
        new() { Group = "系統維護", Name = "Microsoft PowerToys",
                Description = "微軟官方進階系統增強工具集（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/microsoft/PowerToys/releases" },
        new() { Group = "系統維護", Name = "Dism++",
                Description = "系統清理 / 映像維護 / 開機管理，初雨團隊官方（GitHub）",
                Kind = ToolKind.WebLink, Target = "https://github.com/Chuyu-Team/Dism-Multi-language/releases" },
        new() { Group = "系統維護", Name = "BlueScreenView",
                Description = "解析藍色當機（BSOD）傾印檔，NirSoft 官方",
                Kind = ToolKind.WebLink, Target = "https://www.nirsoft.net/utils/blue_screen_view.html" },
        new() { Group = "系統維護", Name = "LatencyMon",
                Description = "分析系統即時延遲（DPC）與卡頓來源，Resplendence 官方",
                Kind = ToolKind.WebLink, Target = "https://www.resplendence.com/latencymon" },
    };

    /// <summary>依 <see cref="ToolItem.Group"/> 分組後的工具清單（保留定義順序）。</summary>
    public IReadOnlyList<ToolGroup> Groups => Tools
        .GroupBy(t => t.Group)
        .Select(g => new ToolGroup { Title = g.Key, Items = g.ToList() })
        .ToList();

    /// <summary>依項目類型啟動系統工具、開啟官方網頁，或偵測第三方工具後啟動 / 導向下載。</summary>
    public void Launch(ToolItem tool)
    {
        try
        {
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
}
