namespace XinSpect;

/// <summary>
/// 全部導覽頁的單一權威來源。側邊欄（分組 + 圖示 + 標題）、延遲實體化、命令面板搜尋、
/// 感測閘門，全部由此一份清單推導；順序即為側邊欄顯示順序。
/// </summary>
public static class PageRegistry
{
    // 分組標題常數（避免字串散落各處導致分組被拆開）
    private const string GOverview = "總覽";
    private const string GHardware = "硬體";
    private const string GMonitor = "監控";
    private const string GTune = "調校";
    private const string GTools = "工具";
    private const string GSystem = "系統";

    /// <summary>依側邊欄顯示順序排列的完整頁面清單。</summary>
    public static IReadOnlyList<PageDef> Pages { get; } =
    [
        new()
        {
            Key = "overview", Title = "總覽", Group = GOverview,
            IconData = "M3,3 H10 V11 H3 Z M13,3 H21 V8 H13 Z M13,11 H21 V21 H13 Z M3,14 H10 V21 H3 Z",
            Factory = () => new OverviewView(),
            Hint = "整機規格與即時狀態一覽",
            Keywords = ["overview", "dashboard", "首頁", "儀表板"],
        },
        new()
        {
            Key = "ai", Title = "AI 評價", Group = GOverview,
            IconData = "F1 M5,4 H19 A3,3 0 0 1 22,7 V14 A3,3 0 0 1 19,17 H11 L6,21 V17 H5 A3,3 0 0 1 2,14 V7 A3,3 0 0 1 5,4 Z "
                     + "M7.5,10.5 a1.3,1.3 0 1,0 2.6,0 a1.3,1.3 0 1,0 -2.6,0 Z M10.7,10.5 a1.3,1.3 0 1,0 2.6,0 a1.3,1.3 0 1,0 -2.6,0 Z "
                     + "M13.9,10.5 a1.3,1.3 0 1,0 2.6,0 a1.3,1.3 0 1,0 -2.6,0 Z",
            Factory = () => new AiView(),
            Hint = "交給 AI 診斷本機硬體並提出建議",
            Keywords = ["ai", "llm", "ollama", "診斷", "助手", "評語"],
        },
        new()
        {
            Key = "cpu", Title = "處理器", Group = GHardware,
            IconData = "F0 M8,2 H10 V5 H8 Z M14,2 H16 V5 H14 Z M2,8 H5 V10 H2 Z M2,14 H5 V16 H2 Z M19,8 H22 V10 H19 Z "
                     + "M19,14 H22 V16 H19 Z M8,19 H10 V22 H8 Z M14,19 H16 V22 H14 Z M5,5 H19 V19 H5 Z M9,9 H15 V15 H9 Z",
            Factory = () => new CpuView(),
            Hint = "處理器完整規格、快取、拓樸與核心負載",
            Keywords = ["cpu", "processor", "核心", "執行緒", "快取", "拓樸"],
        },
        new()
        {
            Key = "memory", Title = "記憶體", Group = GHardware,
            IconData = "M2,7 H22 V16 H2 Z M5,16 H8 V20 H5 Z M11,16 H14 V20 H11 Z M17,16 H20 V20 H17 Z",
            Factory = () => new MemoryView(),
            Hint = "記憶體模組、SPD 與主／次要時序",
            Keywords = ["memory", "ram", "dram", "spd", "時序", "記憶體"],
        },
        new()
        {
            Key = "mainboard", Title = "主機板", Group = GHardware,
            IconData = "F0 M4,3 H20 A1,1 0 0 1 21,4 V20 A1,1 0 0 1 20,21 H4 A1,1 0 0 1 3,20 V4 A1,1 0 0 1 4,3 Z "
                     + "M7,6 H12 V11 H7 Z M15,6 H18 V9 H15 Z M7,14 H17 V16 H7 Z M7,17.5 H17 V19 H7 Z",
            Factory = () => new MotherboardView(),
            Hint = "主機板、晶片組與 BIOS 資訊",
            Keywords = ["mainboard", "motherboard", "bios", "晶片組", "主機板"],
        },
        new()
        {
            Key = "gpu", Title = "顯示卡", Group = GHardware,
            IconData = "F0 M2,6 H22 V16 H8 L2,16 Z M7,11 m-3,0 a3,3 0 1,0 6,0 a3,3 0 1,0 -6,0 Z M14,8 H20 V10 H14 Z M14,12 H20 V14 H14 Z",
            Factory = () => new GpuView(),
            Hint = "顯示卡規格、顯示記憶體與螢幕色域",
            Keywords = ["gpu", "vga", "顯卡", "顯示卡", "vram", "色域"],
        },
        new()
        {
            Key = "storage", Title = "儲存裝置", Group = GHardware,
            IconData = "F0 M4,4 H20 V20 H4 Z M12,12 m-6,0 a6,6 0 1,0 12,0 a6,6 0 1,0 -12,0 Z "
                     + "M12,12 m-1.5,0 a1.5,1.5 0 1,0 3,0 a1.5,1.5 0 1,0 -3,0 Z",
            Factory = () => new StorageView(),
            Hint = "磁碟容量、健康度與讀寫活動",
            Keywords = ["disk", "ssd", "hdd", "nvme", "smart", "磁碟", "硬碟"],
        },
        new()
        {
            Key = "network", Title = "網路", Group = GHardware,
            IconData = "F0 M12,3 a9,9 0 1,0 0.01,0 Z M3.5,9 H20.5 V11 H3.5 Z M3.5,13 H20.5 V15 H3.5 Z M11,3.5 H13 V20.5 H11 Z",
            Factory = () => new NetworkView(),
            Hint = "網路介面、位址與即時流量",
            Keywords = ["network", "nic", "ip", "網卡", "網路", "流量"],
        },
        new()
        {
            Key = "sensors", Title = "感測器", Group = GMonitor,
            IconData = "M9.5,3 a2.5,2.5 0 0 1 5,0 V13.5 A4.5,4.5 0 1 1 9.5,13.5 Z",
            Factory = () => new SensorsView(),
            Hint = "所有感測器的完整明細總表",
            Keywords = ["sensor", "temp", "溫度", "電壓", "轉速", "感測"],
            // 感測總表是每秒最重的格式化工作，僅在本頁顯示時才更新
            LiveGate = (live, on) => live.DetailedSensorsVisible = on,
        },
        new()
        {
            Key = "history", Title = "歷史回放", Group = GMonitor,
            IconData = "F1 M12,3 a9,9 0 1,1 -8.6,11.6 L5.3,14 A7,7 0 1,0 12,5 V2 L7.5,5.5 L12,9 Z "
                     + "M11,7.5 H12.6 V12.3 L16.6,14.7 L15.8,16 L11,13.2 Z",
            Factory = () => new HistoryView(),
            Hint = "數週的溫度／負載走勢回放與統計",
            Keywords = ["history", "歷史", "回放", "走勢", "統計", "p95", "趨勢", "timeline"],
        },
        new()
        {
            Key = "events", Title = "事件時間軸", Group = GMonitor,
            IconData = "M11,2 H13 V22 H11 Z M13.5,4 H21 V8 H13.5 Z M3,10 H10.5 V14 H3 Z M13.5,16 H20 V20 H13.5 Z",
            Factory = () => new EventsView(),
            Hint = "警示、降頻、磁碟壽命與藍屏的統一時序",
            Keywords = ["event", "事件", "時間軸", "警示", "降頻", "藍屏", "紀錄", "timeline"],
        },
        new()
        {
            Key = "health", Title = "健康", Group = GMonitor,
            IconData = "F1 M12,21.35 L10.55,20.03 C5.4,15.36 2,12.28 2,8.5 2,5.42 4.42,3 7.5,3 9.24,3 10.91,3.81 12,5.09 "
                     + "13.09,3.81 14.76,3 16.5,3 19.58,3 22,5.42 22,8.5 22,12.28 18.6,15.36 13.45,20.04 L12,21.35 Z",
            Factory = () => new HealthView(),
            Hint = "溫度／負載／容量彙整的健康總評",
            Keywords = ["health", "健康", "評分", "狀態燈"],
        },
        new()
        {
            Key = "bench", Title = "效能", Group = GMonitor,
            IconData = "F1 M13,2 L4,14 L10,14 L9,22 L20,9 L13,9 Z",
            Factory = () => new BenchView(),
            Hint = "跑分、烤機、快取延遲與磁碟效能",
            Keywords = ["bench", "benchmark", "跑分", "烤機", "壓力", "superpi", "winsat"],
        },
        new()
        {
            Key = "ceiling", Title = "效能天花板", Group = GMonitor,
            IconData = "F0 M3,3 H21 V5.6 H3 Z M11,7.4 H13 V13 H11 Z M12,21.6 L7.4,15 H16.6 Z "
                     + "M4.6,8.4 H6.4 V12 H4.6 Z M17.6,8.4 H19.4 V12 H17.6 Z",
            Factory = () => new CeilingView(),
            Hint = "為什麼跑不到該有的頻率：溫度牆／功耗牆／電流牆／向量降頻",
            Keywords = ["ceiling", "天花板", "上限", "節流", "降頻", "throttle", "溫度牆", "功耗牆",
                        "電流", "prochot", "rapl", "pl1", "pl2", "tcc", "限制原因", "avx", "降頻原因"],
        },
        new()
        {
            Key = "scenes", Title = "場景", Group = GTune,
            IconData = "F1 M12,2 L14.6,8.6 L21.6,9.2 L16.3,13.8 L17.9,20.7 L12,17 L6.1,20.7 L7.7,13.8 "
                     + "L2.4,9.2 L9.4,8.6 Z",
            Factory = () => new ScenesView(),
            Hint = "一鍵切換靜音／均衡／效能：風扇曲線＋電源計劃＋顯示卡上限",
            Keywords = ["scene", "scenes", "profile", "場景", "設定檔", "靜音", "均衡", "效能", "電源計劃", "powercfg", "一鍵"],
        },
        new()
        {
            Key = "oc", Title = "超頻", Group = GTune,
            IconData = "F1 M20.38,8.57 l-1.23,1.85 a8,8 0 0 1 -0.22,7.58 H5.07 A8,8 0 0 1 15.58,6.85 l1.85,-1.23 "
                     + "A10,10 0 0 0 3.35,19 a2,2 0 0 0 1.72,1 h13.85 a2,2 0 0 0 1.74,-1 10,10 0 0 0 -0.27,-10.44 z "
                     + "M10.59,15.41 a2,2 0 0 0 2.83,0 l5.66,-8.49 -8.49,5.66 a2,2 0 0 0 0,2.83 z",
            Factory = () => new OverclockView(),
            Hint = "透過 Intel XTU 調整倍頻與電壓（測試版）",
            Keywords = ["oc", "overclock", "xtu", "超頻", "倍頻", "電壓"],
            RequiresRiskConsent = true,
        },
        new()
        {
            Key = "gpuoc", Title = "顯示卡超頻", Group = GTune,
            IconData = "F0 M2,5 H22 V17 H2 Z M4,7 H20 V15 H4 Z M12,11 m-4,0 a4,4 0 1,0 8,0 a4,4 0 1,0 -8,0 Z "
                     + "M12,11 m-1.3,0 a1.3,1.3 0 1,0 2.6,0 a1.3,1.3 0 1,0 -2.6,0 Z M6,19 H18 V21 H6 Z",
            Factory = () => new GpuOcView(),
            Hint = "NVML 功耗／風扇 + NVAPI 時脈偏移（測試版）",
            Keywords = ["gpu oc", "nvapi", "nvml", "顯卡超頻", "功耗", "時脈"],
        },
        new()
        {
            Key = "fan", Title = "系統風扇", Group = GTune,
            IconData = "F1 M12,12 m-2,0 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0 Z M12,10 C12,6 13,3 16,3 C19,3 19,7 15,9 C13.8,9.6 12.8,10 12,10 Z "
                     + "M14,12 C18,12 21,13 21,16 C21,19 17,19 15,15 C14.4,13.8 14,12.8 14,12 Z "
                     + "M12,14 C12,18 11,21 8,21 C5,21 5,17 9,15 C10.2,14.4 11.2,14 12,14 Z "
                     + "M10,12 C6,12 3,11 3,8 C3,5 7,5 9,9 C9.6,10.2 10,11.2 10,12 Z",
            Factory = () => new FanControlView(),
            Hint = "主機板可控風扇的手動調速與曲線",
            Keywords = ["fan", "風扇", "轉速", "rpm", "曲線", "散熱"],
            // 風扇即時轉速讀取同樣昂貴，僅在本頁顯示時才每秒讀取
            LiveGate = (live, on) => live.FanControlsVisible = on,
        },
        new()
        {
            Key = "toolbox", Title = "工具箱", Group = GTools,
            IconData = "M22.7,19 l-9.1,-9.1 c0.9,-2.3 0.4,-5 -1.5,-6.9 -2,-2 -5,-2.4 -7.4,-1.3 l4.3,4.3 -3,3 -4.3,-4.3 "
                     + "C0.5,9.1 1,12.1 3,14.1 c1.9,1.9 4.6,2.4 6.9,1.5 l9.1,9.1 c0.4,0.4 1,0.4 1.4,0 l2.3,-2.3 c0.5,-0.4 0.5,-1.1 0.1,-1.5 z",
            Factory = () => new ToolboxView(),
            Hint = "Windows 內建工具與第三方工具導向",
            Keywords = ["toolbox", "工具箱", "圖吧", "檢測"],
        },
        new()
        {
            Key = "utilities", Title = "實用工具", Group = GTools,
            IconData = "M3,3 h7 v7 h-7 z M14,3 h7 v7 h-7 z M3,14 h7 v7 h-7 z M14,14 h7 v7 h-7 z",
            Factory = () => new UtilitiesView(),
            Hint = "連接埠、Hosts、藍屏分析、清理、電池、天梯…",
            Keywords = ["utility", "實用", "hosts", "藍屏", "清理", "電池", "天梯", "連接埠", "啟動項", "dns"],
        },
        new()
        {
            Key = "setup", Title = "一鍵裝機", Group = GTools,
            IconData = "F0 M4,3 H20 A1,1 0 0 1 21,4 V17 A1,1 0 0 1 20,18 H13 V20 H16 V22 H8 V20 H11 V18 H4 A1,1 0 0 1 3,17 V4 A1,1 0 0 1 4,3 Z "
                     + "M11,6 H13 V9 H16 V11 H13 V14 H11 V11 H8 V9 H11 Z",
            Factory = () => new SetupView(),
            Hint = "以 winget 批次安裝常用軟體",
            Keywords = ["setup", "winget", "裝機", "安裝", "軟體"],
        },
        new()
        {
            Key = "browser", Title = "瀏覽器", Group = GTools,
            IconData = "F0 M12,2 a10,10 0 1,0 0.01,0 Z M3,12 H21 M12,2.5 C15,6 15,18 12,21.5 C9,18 9,6 12,2.5 Z M4.5,7 H19.5 M4.5,17 H19.5",
            Factory = () => new BrowserView(),
            Hint = "內建 WebView2 瀏覽器",
            Keywords = ["browser", "web", "瀏覽器", "網頁"],
        },
        new()
        {
            Key = "terminal", Title = "終端", Group = GTools,
            IconData = "F1 M2,5 L22,5 L22,19 L2,19 Z M4,7 L20,7 L20,17 L4,17 Z "
                     + "M6.2,9.3 L9,11.5 L6.2,13.7 L7.3,13.7 L10.1,11.5 L7.3,9.3 Z M11.5,12.6 H16.5 V13.8 H11.5 Z",
            Factory = () => new TerminalView(),
            Hint = "常駐 cmd／PowerShell 真實終端",
            Keywords = ["terminal", "cmd", "powershell", "終端", "命令列", "shell"],
        },
        new()
        {
            Key = "settings", Title = "設定", Group = GSystem,
            IconData = "F1 M12,8 a4,4 0 1,0 0.01,0 Z M12,10 a2,2 0 1,1 -0.01,0 Z "
                     + "M10.5,1.5 h3 l0.5,2.6 a7.5,7.5 0 0 1 2.1,1.2 l2.5,-1 2,3.4 -2,1.7 a7.5,7.5 0 0 1 0,2.4 l2,1.7 -2,3.4 "
                     + "-2.5,-1 a7.5,7.5 0 0 1 -2.1,1.2 l-0.5,2.6 h-3 l-0.5,-2.6 a7.5,7.5 0 0 1 -2.1,-1.2 l-2.5,1 -2,-3.4 "
                     + "2,-1.7 a7.5,7.5 0 0 1 0,-2.4 l-2,-1.7 2,-3.4 2.5,1 a7.5,7.5 0 0 1 2.1,-1.2 Z",
            Factory = () => new SettingsView(),
            Hint = "更新間隔、外觀、記錄、警示、AI 與一鍵初始化",
            Keywords = ["settings", "設定", "偏好", "主題", "外觀", "強調色", "選項"],
        },
        new()
        {
            Key = "about", Title = "關於", Group = GSystem,
            IconData = "F0 M12,3 a9,9 0 1,0 0.01,0 Z M11,7 H13 V9 H11 Z M11,10.5 H13 V17 H11 Z",
            Factory = () => new AboutView(),
            Hint = "版本、授權與專案連結",
            Keywords = ["about", "關於", "版本", "授權", "github"],
        },
    ];

    /// <summary>
    /// 「實用工具」頁內的子工具註冊表（同樣取代其 1.x 的 _tools[] ↔ SubNav 索引平行對應）。
    /// 圖示以 16×16 座標繪製，由 UtilitiesView 的 Canvas 承載。
    /// </summary>
    public static IReadOnlyList<PageDef> Utilities { get; } =
    [
        new()
        {
            Key = "port", Title = "連接埠占用", Group = GTools,
            IconData = "M2,3 h12 a1,1 0 0 1 1,1 v3 h-14 v-3 a1,1 0 0 1 1,-1 z M1,9 h14 v3 a1,1 0 0 1 -1,1 h-12 a1,1 0 0 1 -1,-1 z "
                     + "M4,5.5 h1 v0.01 h-1 z M4,10.5 h1 v0.01 h-1 z",
            Factory = () => new PortUsageView(),
            Hint = "查出是哪個行程占用了連接埠", Keywords = ["port", "tcp", "udp", "連接埠", "埠", "占用"],
        },
        new()
        {
            Key = "hosts", Title = "Hosts 編輯器", Group = GTools,
            IconData = "M2,2 h12 v3 h-12 z M2,6.5 h12 v3 h-12 z M2,11 h12 v3 h-12 z M4,3 h1 v0.01 h-1 z M4,7.5 h1 v0.01 h-1 z M4,12 h1 v0.01 h-1 z",
            Factory = () => new HostsEditorView(),
            Hint = "編輯系統 hosts 檔", Keywords = ["hosts", "網域", "解析", "編輯"],
        },
        new()
        {
            Key = "bsod", Title = "藍屏分析", Group = GTools,
            IconData = "M8,1 L15,14 H1 Z M7.2,6 h1.6 v4 h-1.6 z M7.2,11 h1.6 v1.6 h-1.6 z",
            Factory = () => new BsodView(),
            Hint = "解讀 minidump 與停止碼", Keywords = ["bsod", "藍屏", "minidump", "當機", "停止碼", "dump"],
        },
        new()
        {
            Key = "cleanup", Title = "垃圾清理", Group = GTools,
            IconData = "M3,2 h10 l-1,3 h-8 z M4,6 h8 v7 a1,1 0 0 1 -1,1 h-6 a1,1 0 0 1 -1,-1 z M6.5,8 h1 v4 h-1 z M8.5,8 h1 v4 h-1 z",
            Factory = () => new CleanupView(),
            Hint = "清理暫存、快取與更新殘留", Keywords = ["clean", "清理", "垃圾", "暫存", "temp", "快取"],
        },
        new()
        {
            Key = "battery", Title = "電池分析", Group = GTools,
            IconData = "M1,4 h12 a1,1 0 0 1 1,1 v6 a1,1 0 0 1 -1,1 h-12 a1,1 0 0 1 -1,-1 v-6 a1,1 0 0 1 1,-1 z M15,6 h1 v4 h-1 z M2,5.5 h6 v5 h-6 z",
            Factory = () => new BatteryView(),
            Hint = "電池健康度與循環次數", Keywords = ["battery", "電池", "續航", "循環", "損耗"],
        },
        new()
        {
            Key = "ctxmenu", Title = "右鍵選單", Group = GTools,
            IconData = "M3,1.5 L3,13 L6,10 L8,14.5 L10,13.5 L8,9 L12.5,9 Z",
            Factory = () => new ContextMenuView(),
            Hint = "管理檔案總管右鍵選單項目", Keywords = ["context", "右鍵", "選單", "檔案總管", "shell"],
        },
        new()
        {
            Key = "netspeed", Title = "網速測試", Group = GTools,
            IconData = "M8,1 a7,7 0 1 0 0.01,0 z M8,3 a5,5 0 1 1 -0.01,0 z M8,5 a3,3 0 1 0 0.01,0 z M7.3,7.3 h1.4 v1.4 h-1.4 z",
            Factory = () => new NetworkSpeedView(),
            Hint = "測試上下行頻寬與延遲", Keywords = ["speed", "網速", "頻寬", "延遲", "ping", "測速"],
        },
        new()
        {
            Key = "memclean", Title = "記憶體整理", Group = GTools,
            IconData = "M2,3 h12 a1,1 0 0 1 1,1 v8 a1,1 0 0 1 -1,1 h-12 a1,1 0 0 1 -1,-1 v-8 a1,1 0 0 1 1,-1 z "
                     + "M4,5.5 h1.4 v5 h-1.4 z M6.6,5.5 h1.4 v5 h-1.4 z M9.2,5.5 h1.4 v5 h-1.4 z M11.8,5.5 h1.4 v5 h-1.4 z",
            Factory = () => new MemoryCleanView(),
            Hint = "釋放工作集與待用清單", Keywords = ["memory clean", "記憶體整理", "釋放", "工作集"],
        },
        new()
        {
            Key = "startup", Title = "開機啟動項", Group = GTools,
            IconData = "M8,0.5 a1.6,1.6 0 0 1 1.6,1.6 v0.9 a5.5,5.5 0 0 1 1.5,0.9 l0.8,-0.45 a1.6,1.6 0 0 1 1.6,2.77 "
                     + "l-0.8,0.45 a5.5,5.5 0 0 1 0,1.76 l0.8,0.45 a1.6,1.6 0 0 1 -1.6,2.77 l-0.8,-0.45 a5.5,5.5 0 0 1 -1.5,0.9 "
                     + "v0.9 a1.6,1.6 0 0 1 -3.2,0 v-0.9 a5.5,5.5 0 0 1 -1.5,-0.9 l-0.8,0.45 a1.6,1.6 0 0 1 -1.6,-2.77 "
                     + "l0.8,-0.45 a5.5,5.5 0 0 1 0,-1.76 l-0.8,-0.45 a1.6,1.6 0 0 1 1.6,-2.77 l0.8,0.45 a5.5,5.5 0 0 1 1.5,-0.9 "
                     + "v-0.9 a1.6,1.6 0 0 1 1.6,-1.6 z M8,5 a3,3 0 1 0 0.01,0 z",
            Factory = () => new StartupView(),
            Hint = "停用不必要的開機自啟項", Keywords = ["startup", "啟動項", "自啟", "開機", "autorun"],
        },
        new()
        {
            Key = "dns", Title = "DNS 切換", Group = GTools,
            IconData = "M8,1 a7,7 0 1 0 0.01,0 z M8,2.4 c1.2,0 2.6,2 2.8,5.1 h-5.6 c0.2,-3.1 1.6,-5.1 2.8,-5.1 z "
                     + "M2.5,7.5 h2.9 c0.05,-1.7 0.4,-3.2 0.95,-4.3 a5.6,5.6 0 0 0 -3.85,4.3 z "
                     + "M10.6,3.2 c0.55,1.1 0.9,2.6 0.95,4.3 h2.9 a5.6,5.6 0 0 0 -3.85,-4.3 z "
                     + "M5.2,8.5 h5.6 c-0.2,3.1 -1.6,5.1 -2.8,5.1 c-1.2,0 -2.6,-2 -2.8,-5.1 z "
                     + "M2.5,8.5 a5.6,5.6 0 0 0 3.85,4.3 c-0.55,-1.1 -0.9,-2.6 -0.95,-4.3 h-2.9 z "
                     + "M10.65,8.5 c-0.05,1.7 -0.4,3.2 -0.95,4.3 a5.6,5.6 0 0 0 3.85,-4.3 h-2.9 z",
            Factory = () => new DnsView(),
            Hint = "一鍵切換公用 DNS 並測延遲", Keywords = ["dns", "網域", "解析", "切換", "cloudflare"],
        },
        new()
        {
            Key = "diskscan", Title = "大檔掃描", Group = GTools,
            IconData = "M2,2 h7 l3,3 v9 a1,1 0 0 1 -1,1 h-9 a1,1 0 0 1 -1,-1 v-11 a1,1 0 0 1 1,-1 z M9,2 v3 h3 z "
                     + "M4.5,7.5 h5 v1.2 h-5 z M4.5,9.8 h5 v1.2 h-5 z M4.5,12.1 h3.2 v1.2 h-3.2 z",
            Factory = () => new DiskScanView(),
            Hint = "找出占空間的大檔與資料夾", Keywords = ["disk scan", "大檔", "掃描", "空間", "占用"],
        },
        new()
        {
            Key = "ranking", Title = "效能天梯", Group = GTools,
            IconData = "M2,13 h3 v-6 h-3 z M6.5,13 h3 v-11 h-3 z M11,13 h3 v-4 h-3 z",
            Factory = () => new RankingView(),
            Hint = "CPU／顯示卡跑分排行與本機定位", Keywords = ["ranking", "天梯", "排行", "跑分", "排名", "ladder"],
        },
        new()
        {
            Key = "frametime", Title = "幀時間監測", Group = GTools,
            IconData = "M1,7.5 h3 v-3 h2 v3 h2 v-5 h2 v7 h2 v-4 h2 v4 h1 v2 h-14 z",
            Factory = () => new FrameTimeView(),
            Hint = "任何程式的真實幀時間與 1% Low（ETW，不注入）", Keywords = ["frame time", "fps", "幀時間", "掉幀", "頓", "遊戲", "1% low"],
        },
        new()
        {
            Key = "dpc", Title = "DPC 延遲", Group = GTools,
            IconData = "M2,2 h5 v3 h-5 z M9,2 h5 v5 h-5 z M2,7 h5 v7 h-5 z M9,9 h5 v5 h-5 z",
            Factory = () => new DpcLatencyView(),
            Hint = "排出造成音訊爆音／輸入停頓的肇事驅動（ETW）", Keywords = ["dpc", "isr", "延遲", "latency", "爆音", "驅動", "latencymon"],
        },
    ];

    /// <summary>以 <see cref="PageDef.Key"/> 取頁；找不到回傳 null。</summary>
    public static PageDef? Find(string key)
    {
        for (int i = 0; i < Pages.Count; i++)
            if (string.Equals(Pages[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Pages[i];
        return null;
    }

    /// <summary>以 <see cref="PageDef.Key"/> 取「實用工具」子頁；找不到回傳 null。</summary>
    public static PageDef? FindUtility(string key)
    {
        for (int i = 0; i < Utilities.Count; i++)
            if (string.Equals(Utilities[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Utilities[i];
        return null;
    }

    /// <summary>以 <see cref="PageDef.Key"/> 在主頁面與子工具兩份註冊表中查找；找不到回傳 null。</summary>
    public static PageDef? FindAny(string key) => Find(key) ?? FindUtility(key);

    /// <summary>以 <see cref="PageDef.Key"/> 取頁在側邊欄中的索引；找不到回傳 -1。</summary>
    public static int IndexOf(string key)
    {
        for (int i = 0; i < Pages.Count; i++)
            if (string.Equals(Pages[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
