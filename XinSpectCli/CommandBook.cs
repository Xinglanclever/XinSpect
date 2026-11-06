using static XinSpectCli.Tui;

namespace XinSpectCli;

/// <summary>
/// 指令書：所有 xsc 指令的分類目錄與逐條詳解。
/// help ＝ 分類功能表；help &lt;指令&gt; ＝ 單一指令的用法頁（用法、範例、說明、相關指令）。
/// </summary>
internal static class CommandBook
{
    internal sealed record Cmd(
        string Name,
        string Category,
        string Summary,
        string Usage,
        string[] Examples,
        string Details,
        string[] Related);

    internal static readonly Cmd[] Commands =
    [
        new("facts", "採集與查詢", "採集一輪全機硬體事實進快取",
            "facts",
            ["facts"],
            "讀 SMBIOS／WMI／PCI／MSR／LHM 全部通路，把事實放進快取。之後的 fact／domain／page 查詢都走這份快取，不必重掃。\n\n靜態鍵共 1,178 把、動態家族 69 個；實際讀到多少依機器與權限而定，讀不到的如實標示，不編數值。",
            ["fact", "keys", "domain"]),
        new("fact <關鍵字>", "採集與查詢", "在事實快取裡搜鍵名或值",
            "fact <關鍵字>",
            ["fact 0x19C", "fact nvme 溫度"],
            "在快取的鍵名與值裡做不分大小寫的搜尋，列出前 30 筆。鍵名是 catalog 裡的正式名稱（如 msr.ia32_therm_status），值是本機實際讀到的內容。",
            ["facts", "domain", "book"]),
        new("keys [字]", "採集與查詢", "搜尋 1,178 把靜態事實鍵目錄",
            "keys [字]",
            ["keys", "keys nvme"],
            "列出靜態鍵目錄（可不帶字＝全部）。這是「這個程式可能讀到什麼」的總清單；本機實際讀到什麼看 facts。",
            ["facts", "domain", "book 6"]),
        new("domain <前綴>", "採集與查詢", "按鍵前綴過濾事實",
            "domain <前綴>",
            ["domain cpu", "domain nvme"],
            "以鍵的網域前綴（如 cpu.、nvme.）過濾快取事實。前綴清單見 keys 或百科第 6 部。",
            ["fact", "keys", "book 6"]),
        new("health", "採集與查詢", "健康摘要：六態分布與對帳矛盾",
            "health",
            ["health"],
            "把快取裡全部事實按可得性攤開：讀到了幾筆、這台沒有的幾筆、要提權的幾筆、讀取失敗的幾筆，加上交叉對帳有沒有矛盾。只陳述、不判決——健康評分在 GUI 的健康頁，讀取失敗明細在 --errors。",
            ["facts", "bsod", "storage"]),
        new("battery", "採集與查詢", "電池健康（pw.battery.*）",
            "battery",
            ["battery"],
            "列出電池的設計容量、實際滿充容量、循環次數與損耗率。桌上型沒有電池時會如實標 NotApplicable——不是空表冒充查過。",
            ["health", "storage"]),
        new("storage", "採集與查詢", "儲存總表（st.*）",
            "storage",
            ["storage"],
            "儲存域的事實總表：可靠性計數器、檔案系統卷冊、iSCSI、VSS 等。SMART 原始屬性只有 ata.smart.attr.lookup（ATA 碟才有；NVMe 一律不讀——實證會卡在核心不可中斷等待，理由記在百科第 8 部）。",
            ["health", "fact", "book 8"]),
        new("net", "採集與查詢", "網路總表（net.* 與 nw.*）",
            "net",
            ["net", "net nw"],
            "net.* ＝ 傾聽埠、TCP offload、WMI 缺口對帳；nw.* ＝ 藍牙、DNS、Wi-Fi、WoL、USB 週邊等摘要。網速測試是 GUI 工具頁的活，終端機不連網。",
            ["security", "health"]),
        new("security", "採集與查詢", "安全姿態事實（sec.*）",
            "security",
            ["security"],
            "sec.* ＝ VBS 狀態、Credential Guard 鏈、登入硬體關聯等安全事實。帶權重的防護分數與威脅事件走 ipc posture／ipc threats（藍色中隊引擎）。",
            ["ipc", "compliance"]),
        new("bsod", "採集與查詢", "藍屏記錄（Minidump 掃描）",
            "bsod",
            ["bsod"],
            "掃 C:\\Windows\\Minidump，解析 DUMP_HEADER64 的 BugCheck 代碼與四個參數，對照內建知識表給名稱。唯讀；沒有傾印就是沒有，不編一筆。",
            ["health", "bsod"]),
        new("posture", "採集與查詢", "安全姿態對帳表（13 把鍵）",
            "posture",
            ["posture"],
            "WMI 直讀 13 把安全鍵逐一對帳：HVCI、VBS、IOMMU、DMA 保護、Credential Guard、CET、Secure Boot、測試簽章、投機執行緩解、驅動數、未簽章數、驅動黑名單、雷電安全層級。來源沒回報就寫「未回報」，不猜。帶權重的分數走 ipc posture。",
            ["security", "ipc", "compliance"]),
        new("usb", "採集與查詢", "USB 鏈路（埠樹與供電）",
            "usb",
            ["usb"],
            "列舉 USB 控制器下的埠：位置、掛著的裝置、USB 版本、現況與判定。讀 USB 節點資訊需要管理員權限。",
            ["pcie", "usb"]),
        new("pcie", "採集與查詢", "PCIe 鏈路（速度／寬度現值與最大值）",
            "pcie",
            ["pcie"],
            "各裝置的 PCIe 鏈路：目前協商速度／寬度、最大能力，與協商落差的判定。鏈路降速是效能問題的常見藏身处。",
            ["usb", "pci", "domain"]),
        new("drivers", "採集與查詢", "驅動盤點（版本／日期／簽章）",
            "drivers",
            ["drivers"],
            "WMI 盤點裝置驅動：裝置名、類別、版本、有沒有簽章。未簽章的驅動是核心安全的經典入口——數量在 posture 的 drivers.unsigned 也有。",
            ["posture", "drivers"]),
        new("firmware", "採集與查詢", "韌體安全總表（fw／uefi／spi／tpm）",
            "firmware",
            ["firmware"],
            "韌體域的事實總表：BIOS 寫入保護、SMRAM 鎖定、ME 狀態、UEFI 開機鏈、SPI 映像、TPM。帶裁決的風險卡在 GUI 韌體安全頁。",
            ["security", "compliance", "book"]),
        new("os", "採集與查詢", "作業系統分析（os.*）",
            "os",
            ["os"],
            "作業系統域的事實總表：版本、授權、更新與安全態勢（Secure Boot／VBS／HVCI／UAC）。",
            ["security", "license"]),
        new("power", "採集與查詢", "電源與睡眠（pw.*）",
            "power",
            ["power"],
            "電源域的事實總表：睡眠能力、喚醒來源、電源計劃漂移、SRUM 能耗讀回。電池細節走 battery。",
            ["battery", "health"]),
        new("catalog <關鍵字>", "採集與查詢", "歷史型號查詢（2006–2026 CPU／GPU）",
            "catalog <關鍵字>",
            ["catalog 5800X", "catalog rtx 3080"],
            "查使用者提供的歷史型號索引，給升級對照用的性能定位。不含主機板；離線，不連網。",
            ["book", "page hardware-catalog"]),
        new("history", "採集與查詢", "歷史回放摘要（近 30 天 13 指標）",
            "history",
            ["history"],
            "讀 GUI 歷史記錄的持久化檔，13 指標（溫度／負載／時脈／功耗／電壓／VRM…）各給 min／avg／max／P95。沒開歷史記錄就如實說沒有。",
            ["health", "page history"]),
        new("sys", "即時監控", "即時一行 KPI 快照",
            "sys",
            ["sys"],
            "印一行即時摘要：CPU 溫度／負載、記憶體、GPU——附進度條與走勢。不採集靜態事實，只看感測器現值。",
            ["top", "dash"]),
        new("fan", "即時監控", "風扇轉速（唯讀）",
            "fan",
            ["fan"],
            "列出 LHM 認出的風扇轉速（主板／SuperIO 感測器）。調速曲線是寫入路徑，只在 GUI 風扇頁——終端機的承諾是唯讀，這裡也一樣。",
            ["sys", "top"]),
        new("top [秒]", "即時監控", "即時表格持續刷新",
            "top [秒]",
            ["top", "top 5"],
            "持續刷新的即時表格，預設 2 秒一拍；Ctrl+C 停下來，提示符會好好回到你手上。",
            ["sys", "dash"]),
        new("dash", "即時監控", "全螢幕即時儀表板",
            "dash",
            ["dash"],
            "切到獨立螢幕（alternate screen）的全螢幕儀表板：溫度／負載／記憶體走勢＋漲落箭頭。q 離開後回到原本對話流，不留殘影。",
            ["sys", "top"]),
        new("msr <索引>", "深層探針", "讀一支 MSR（唯讀）",
            "msr <索引>",
            ["msr 0x19C"],
            "以十六進位索引讀一支 Model Specific Register，只讀不寫。需要 Ring0 通道就緒（驅動已簽章載入）；未就緒時如實標示 [ -- ]。",
            ["pci", "book 6"]),
        new("pci scan", "深層探針", "全匯流排 PCI 掃描（唯讀）",
            "pci scan",
            ["pci scan"],
            "掃 bus 0–255 全部裝置（唯讀），列出廠牌／裝置／類別。",
            ["pci dump", "book 6"]),
        new("pci dump <n>", "深層探針", "看某裝置的 256B 設定空間",
            "pci dump <n>",
            ["pci dump 0"],
            "十六進位傾印第 n 台裝置的完整 256B PCI 設定空間原貌。",
            ["pci scan"]),
        new("ipc <指令>", "深層探針", "送 BlueSquadronBridge（真 JSON 進出）",
            "ipc <指令>",
            ["ipc status"],
            "對守護進程的具名管道送真 JSON、顯示真回應；Bridge 未連線時如實提示，不假裝成功。",
            ["redfish"]),
        new("redfish <目錄>", "證據與合規", "讀離線 Redfish 目錄（帶外事實）",
            "redfish <目錄>",
            ["redfish"],
            "解析離線 Redfish 目錄為 rf.* 帶外事實。值是 BMC 的自我宣告，信任等級一律 Reported——與本機量測分開標示。",
            ["compliance", "book 7"]),
        new("compliance [格式]", "證據與合規", "合規判定輸出",
            "compliance [json|sarif|junit|cef|otel]",
            ["compliance", "compliance sarif", "compliance --format sarif"],
            "把快取事實對照合規基準，依指定格式輸出判定結果。格式可寫在後面（compliance sarif）或用 --format（compliance --format sarif），兩種都通；有快取時直接用快取，不重採。",
            ["merkle", "book 7"]),
        new("merkle", "證據與合規", "穩定事實 Merkle 指紋",
            "merkle",
            ["merkle"],
            "對已確認事實計算 SHA-256 Merkle 樹指紋——同一台機器同一狀態應得同一指紋，可用於稽核對帳。嚴格排除 [ -- ] 與未驗證項。",
            ["compliance", "facts", "book 7"]),
        new("pages", "導覽", "GUI 全頁與 CLI 對照表",
            "pages",
            ["pages"],
            "列出 GUI 65 頁與對應的 CLI 用法——常規版功能在終端的對照入口。",
            ["page", "domain", "book 3"]),
        new("page <key>", "導覽", "看某頁的資料",
            "page <key>",
            ["page cpu"],
            "以該頁的關鍵字過濾事實快取，呈現 GUI 某頁在終端會看到的同一批資料。",
            ["pages", "domain"]),
        new("book [部號|節號|關鍵字]", "導覽", "曦覽百科全書（離線嵌入）",
            "book [部號|節號|關鍵字]",
            ["book", "book 6", "book 6.3", "book 六態"],
            "整本百科（15 部 362 節）離線嵌入，章節編號與文件目錄一致。book＝目錄；book 6＝第 6 部的節清單；book 6.3＝讀該節全文；book 六態＝搜節標題。",
            ["help", "keys", "book 0"]),
        new("settings", "系統", "讀目前使用者設定",
            "settings",
            ["settings"],
            "顯示 settings.json 的目前內容（語言、主題、閾值等）。",
            ["changelog", "ver"]),
        new("changelog [n]", "系統", "看最近 n 版更新紀錄",
            "changelog [n]",
            ["changelog", "changelog 1"],
            "預設 3 版；方框顯示每版做了什麼。全書 122 版沿革在 book 11。",
            ["book 11"]),
        new("ver", "系統", "版號與承諾",
            "ver",
            ["ver"],
            "顯示版號、代號與「唯讀・不寫硬體・不連網」承諾。",
            ["changelog"]),
        new("help [指令]", "系統", "指令書：分類功能表／單一指令詳解",
            "help [指令]",
            ["help", "help book"],
            "不帶字＝分類功能表；帶指令名＝該指令的用法頁（用法、範例、細節、相關指令）。",
            ["book"]),
        new("clear", "系統", "清屏",
            "clear",
            ["clear"],
            "清掉畫面，提示符回到頂端。",
            ["exit"]),
        new("exit", "系統", "離開",
            "exit",
            ["exit"],
            "結束 xsc。Ctrl+C 兩次亦可。",
            []),
    ];

    private static readonly (string Name, string Hint)[] CategoryOrder =
    [
        ("採集與查詢", "先 facts 進快取，再查"),
        ("即時監控", "感測器現值，不落盤"),
        ("深層探針", "Ring0／匯流排／管道，唯讀"),
        ("證據與合規", "稽核出口"),
        ("導覽", "GUI 對照＋百科全書"),
        ("系統", "設定與生命週期"),
    ];

    /// <summary>分類功能表：依類別分組的指令總表。</summary>
    public static void ShowMenu()
    {
        Console.WriteLine($"{Bold}指令書 ・ 全部 {Commands.Length} 個指令{Reset}");
        Console.WriteLine($"  {Dim}help <指令> 看單一指令詳解 ・ book 讀整本百科{Reset}");
        Console.WriteLine();
        foreach (var (cat, hint) in CategoryOrder)
        {
            var rows = Commands.Where(c => c.Category == cat)
                .Select(c => new[] { Bold + c.Name + Reset, c.Summary }).ToArray();
            if (rows.Length == 0) continue;
            Console.WriteLine($"{SwissRed}{Bold}■{Reset} {Bold}{cat}{Reset} {Dim}· {hint}{Reset}");
            Tui.Table(["指令", "說明"], rows);
            Console.WriteLine();
        }
        Console.WriteLine($"  {Dim}承諾：唯讀 ・ 不寫硬體 ・ 不連網——任何通路只讀，讀不到如實標 [ -- ]。{Reset}");
    }

    /// <summary>單一指令的用法頁。</summary>
    public static void ShowCommand(string name)
    {
        var cmd = Commands.FirstOrDefault(c =>
            c.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase));
        if (cmd is null)
        {
            Console.WriteLine($"{Tui.SwissRed}沒有指令「{name}」。{Reset} help 看功能表。");
            return;
        }
        Console.WriteLine($"{BrandRed}{Bold}⏺{Reset} {Bold}{cmd.Name}{Reset}{Dim} ・ {cmd.Category}{Reset}");
        Console.WriteLine();
        Console.WriteLine($"  {Dim}用法{Reset}");
        Console.WriteLine($"    {Bold}{cmd.Usage}{Reset}");
        Console.WriteLine();
        if (cmd.Examples.Length > 0)
        {
            Console.WriteLine($"  {Dim}範例{Reset}");
            foreach (var ex in cmd.Examples)
                Console.WriteLine($"    {BrandRed}>{Reset} {ex}");
            Console.WriteLine();
        }
        Console.WriteLine($"  {Dim}說明{Reset}");
        foreach (var para in cmd.Details.Split('\n'))
            Console.WriteLine("    " + para);
        Console.WriteLine();
        if (cmd.Related.Length > 0)
            Console.WriteLine($"  {Dim}相關{Reset}  {string.Join(" · ", cmd.Related.Select(r => Bold + r + Reset))}");
        Console.WriteLine($"  {Dim}百科對照{Reset}  book 指令書第 9 部（命令列與查詢語言）");
        Console.WriteLine();
    }

    /// <summary>help 的分派：無參數＝功能表；有字＝指令詳解。</summary>
    public static void Run(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) ShowMenu();
        else ShowCommand(arg.Trim());
    }
}
