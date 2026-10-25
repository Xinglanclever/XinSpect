namespace XinSpect;

/// <summary>
/// 動態鍵前綴目錄——「哪些鍵家族会在執行期出现、但成员编译期数不出来」的显式申报。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要這一份：</b><see cref="FactKeyCatalog"/> 把全鍵固定在編譯期，是為了解決
/// 「發佈版掃不到原始碼、把 0 讀成全覆蓋」（v2.36 缺陷）。但有一類鍵的<b>成員</b>由機器決定：
/// 匯流排上有幾張卡、SuperIO 探到哪個埠、PMU 有幾個固定計數器——全鍵目錄列不完，也不該假裝列得完。
/// 2026-10-10 的執行期對帳（FactKeyRuntimeReconcileTests）掃出 137 把這種鍵：它們遊蕩在
/// 「目錄說沒有、執行期天天生產」的縫隙裡，覆蓋申報完全看不見。
/// </para>
/// <para>
/// <b>规矩：</b>執行期出現的鍵要嘛是目錄全鍵、要嘛命中這裡的某個前綴，否則對帳紅燈。
/// 前綴本身必須在原始碼掃得到（<c>FactKeyCatalogTests</c> 守：登記了卻沒人生產＝殭屍前綴，紅燈），
/// 每筆要寫「為什麼编译期数不出成員」與「成員由什麼決定」——這是一份申報，不是一個免死金牌。
/// </para>
/// </remarks>
public static class FactKeyDynamicCatalog
{
    /// <summary>前綴 → （為什麼是動態家族＋成員由什麼決定）。前綴一律以「.」結尾，除非末段是埠號前綴。</summary>
    public static IReadOnlyDictionary<string, string> Families { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pci.dev."] =
                "bus 0 盤點每張存在的裝置一筆（成員＝裝置號 dev.fn）——有哪些裝置是機器的事實，编译期列不完",
            ["pci.res."] =
                "PCI 擴充資源（BAR）每張有資源的裝置一筆——同上，成員由匯流排枚舉決定",
            ["sio.0x"] =
                "Super I/O 探測結果按實際探到的指標埠命名（0x2E／0x4E，可能还有别的）——埠號是平台事實，不是编译期常量",
            ["sio.hwm.0x"] =
                "SuperIO HWM 感測（風扇／溫度／電壓／輸入電壓）按探測到的埠分組——同 sio.0x，埠號由探測決定",
            ["pmu.fixed."] =
                "固定功能計數器逐個一筆（pmu.fixed.0…），數量由 CPUID 0xA 的報告決定（2／3／4 個的架構都存在），不寫死成員",
            ["reconcile."] =
                "交叉對帳的結論鍵，每條規則一筆（鍵由規則 id 決定）——結論不是被對帳的事實，刻意不進覆蓋申報的分母，但它是真實的執行期輸出，在這裡如實登記",
            // ── EFI 簽章清單的逐條鍵（2026-10-17 補登記）────────────────────────
            // **為什麼是家族而不是目錄裡的固定鍵**：有幾條清單取決於那支變數的內容，
            // 而本服務只取前 5 條（超過的不假裝列完）——成員由資料決定，编译期列不完。
            // 這是先前漏掉的：`uefi.db.count`／`uefi.dbx.count`／`uefi.kek.count` 與
            // `uefi.{db,dbx,kek}.list.0..4` 都是執行期輸出，但目錄與家族都沒有它們；
            // 本機讀不到 EFI 變數（未進 Setup Mode）所以從未出現——
            // **換一台讀得到的機器，執行期對帳就會亮紅燈**（鍵不在目錄也不在家族）。
            ["uefi.db.list."] =
                "db（允許簽章資料庫）的簽章清單逐條一筆（uefi.db.list.0…）——有幾條清單由該變數的內容決定，本服務最多列 5 條",
            ["uefi.dbx.list."] =
                "dbx（撤銷簽章資料庫）的簽章清單逐條一筆——同 uefi.db.list.，成員由變數內容決定，最多 5 條",
            ["uefi.kek.list."] =
                "KEK 的簽章清單逐條一筆——同 uefi.db.list.，成員由變數內容決定，最多 5 條",
            // ── 開機條目的逐條鍵（2026-10-18 補登記）────────────────────────
            // **又是同一種形狀：鍵早就以執行期鍵的形式在產，而目錄與家族都沒有它。**
            // `UefiSignatureFactsService.CollectBootEntries` 把讀得到的 Boot#### 寫成 uefi.boot.{索引}，
            // 而「有幾個開機條目、編號是多少」是這台機器的 NVRAM 事實（本機 9 個：0–6、8、9——
            // 7 不存在，所以成員連密集都算不上）。本機先前讀不到 EFI 變數，這一批從未出現，
            // 所以守門一直是綠的；把 1314 修掉、真機第一次讀到值之後，執行期對帳立刻照出來。
            // 彙總鍵 uefi.boot_entries.count 成員固定，仍在靜態目錄。
            ["uefi.boot."] =
                "UEFI 開機條目逐條一筆（uefi.boot.{Boot#### 編號}）——有幾個條目、編號是哪些，"
                + "都是這台機器 NVRAM 的事實（本機 0–6、8、9，中間跳號），編譯期列不完也不該列",
            ["audio.latency."] =
                "音訊端點逐端點一筆（端點名來自 MMDevice 枚舉）——這台機器裝了什麼音訊裝置由使用者環境決定",
            ["audio.endpoint."] =
                "音訊端點逐一個一筆（MMDevice 列舉的索引）——端點數量由機器的音訊裝置決定，编译期数不出來",
            ["drvinsp."] =
                "驅動靜態檢視逐檔一筆（索引＋欄位名，如 0.summary）——盘點到幾個可疑驅動是掃描結果，不是编译期常量",
            ["storage.reliability."] =
                "單碟可靠性計數器逐碟逐欄一筆（碟序 × 19 欄）——有幾顆碟、哪幾欄提供者沒給，都是機器與驅動器韌體的事實",
            ["asset.field."] =
                "SMBIOS 資產欄位逐一筆（欄名來自表定義：系統製造商／主機板序號…）——欄位集是規範的性質不是常數，且缺失欄也要有地方標缺",
            ["sa.ifeo."] =
                "IFEO 逐映像一筆（映像名來自登錄檔枚舉，任意字串）——哪些映像掛了 Debugger 由機器的事實決定；成員是『掛了東西』的映像，沒掛的不產鍵",
            ["sa.lsp."] =
                "Winsock LSP 逐條一筆（條目號）——裝了哪些分層服務提供者是機器的安裝事實；sa.lsp.count 是固定全鍵，在目錄",
            ["display.adapter."] =
                "顯示轉接器逐張一筆（名稱來自 WMI）——有幾張卡、叫什麼名是機器的事實",
            ["mon."] =
                "顯示器連線逐台一筆（索引）——接了幾台螢幕由環境決定",
            ["monitor."] =
                "顯示器實體逐台一筆（實例名 DesktopMonitorN…）——名稱與數量由 WMI 枚舉決定；monitor.summary 是固定全鍵，在目錄",
            ["net.listen."] =
                "監聽埠逐埠一筆（協定＋埠號）——這台機器在等誰連入是機器狀態，埠集由連線表決定，编译期列不完",
            ["numa.node."] =
                "NUMA 節點逐個一筆（節點號）——節點數由 GetNumaHighestNodeNumber 回報，單節點到 8 節點的平台都存在",
            ["acpi.table."] = "ACPI 表逐表一筆（簽章＋同簽章序號）——這台機器的韌體提供了哪些表是機器的事實，簽章與數量編譯期列不完；acpi.tables 彙總鍵仍在靜態目錄。",
            ["in.deps."] =
                "已載入模組逐條一筆（模組檔名）——載入哪些 DLL 由這個行程當下的事實決定，檔名任意；" +
                "只有被判『值得看』（未簽章／無法驗證／側載候選）的模組才產鍵，in.deps 彙總鍵仍在靜態目錄。",
            ["dl.dll."] =
                "逐行程模組明細（行程名來自列舉，任意字串）——有幾個行程、叫什麼名是機器當下的事實；" +
                "只有明示同意（L2）時才產鍵，dl.dll.summary 彙總鍵仍在靜態目錄。",
            ["role.installed."] =
                "Windows 伺服器角色／功能逐一筆（角色名）——裝了哪些是这台機器的安裝事實，一台 Core 機能有幾十個角色；name 含大寫與連字號，本就不符合全鍵目錄的格式規定，必須走家族",
            ["sb.memmap."] =
                "SMBIOS Type 19 宣告的記憶體映射逐段一筆（sb.memmap.0…）——有幾段是韌體的事實，編譯期列不完；每段要能被單獨選取與匯出（彙總鍵 sb.memmap.count／total 仍在靜態目錄）",
            ["tpm.replay.pcr."] =
                "TPM 開機鏈重放逐 PCR 一筆（PCR 索引），本機只算 SHA-256——" +
                "該跑哪些 PCR、機器上實作了哪幾條，由 TCG log 與 TPM 自己決定；tpm.replay.summary 彙總鍵仍在靜態目錄。",
            // ── U10 檢測域（U10-TS）第一批唯讀檢測（2026-10-25）────────────────
            // **為什麼是家族**：成員＝端點／裝置索引，有幾個端點、幾台螢幕、幾支筆是機器環境的事實，
            // 編譯期列不完（與 audio.endpoint.／mon. 同形）。各服務的 *Prefix 常數是字面來源，
            // 基底鍵（如 ts.aud.formats）成員固定、仍在靜態目錄——「列不出成員」與「基底」是兩件事。
            ["ts.disp.refresh.verify."] =
                "刷新率核對逐顯示裝置一筆（ts.disp.refresh.verify.{i}）——接了幾台螢幕由 EnumDisplaySettingsEx 列舉決定；單一裝置時直接用基底鍵",
            ["ts.disp.modes.cycle."] =
                "顯示模式清單逐裝置一筆（ts.disp.modes.cycle.{i}）——模式集合與裝置數由顯示驅動回報，編譯期列不完",
            ["ts.aud.formats."] =
                "音訊格式支援枚舉逐端點一筆（ts.aud.formats.{i}）——端點數由 MMDevice 列舉決定（與 audio.endpoint. 同一個分母來源）",
            ["ts.aud.jackdetect."] =
                "音訊插孔描述逐端點一筆（ts.aud.jackdetect.{i}）——端點數由 MMDevice 列舉決定，哪些端點提供 KS 插孔屬性由驅動決定",
            ["ts.aud.endpoint.state."] =
                "端點音量/靜音核對逐端點一筆（ts.aud.endpoint.state.{i}）——端點數由 MMDevice 列舉決定",
            ["ts.pen.pressure."] =
                "觸控筆壓力能力逐筆裝置一筆（ts.pen.pressure.{i}）——有幾支筆由 GetPointerDevices 列舉決定，沒有筆的機器一個都不產",
            // ── U10-FW-0005（第 269 輪）：dbt／dbr 簽章資料庫逐條清單 ──────────
            // **與 uefi.db.list.／dbx.list.／kek.list. 完全同構**：最多列 5 條、
            // 成員由變數內容決定；變數不存在（韌體未佈建）＝整個家族一條都不產，
            // 計數鍵 uefi.dbt.count／uefi.dbr.count 成員固定、走靜態目錄。
            ["uefi.dbt.list."] =
                "dbt（EFI_IMAGE_SECURITY_DATABASE2，授權時戳簽章資料庫）逐條一筆（uefi.dbt.list.0…）——" +
                "有幾條清單由該變數的內容決定，本服務最多列 5 條",
            ["uefi.dbr.list."] =
                "dbr（授權復原簽章資料庫）逐條一筆（uefi.dbr.list.0…）——同 uefi.dbt.list.，成員由變數內容決定",
            // ── U10-CP-0148：SGX EPC 逐區（leaf 0x12 sub≥2）──────────────────
            // 有幾個 EPC 區由 CPUID 列舉決定（Valid＝false 即止，最多列 32 區）；
            // 計數寫在彙總鍵 mem.encryption.sgx 的值與 NumericValue，成員固定走靜態目錄。
            ["mem.encryption.sgx.epc."] =
                "SGX EPC 逐區一筆（mem.encryption.sgx.epc.0…，CPUID leaf 0x12 sub≥2 列舉）——" +
                "有幾區由這台機器的 CPUID 決定，编译期列不完",
            // ── 第 270 輪（U10 新列第一批之二）────────────────────────────
            ["mem.mmio.hole."] =
                "MMIO 洞逐段一筆（mem.mmio.hole.{i}）——RESOURCEMAP 開機記憶體映射裡有幾個洞是平台事實，編譯期列不完",
            ["sb.type45.item."] =
                "SMBIOS Type 45 韌體清單逐組件一筆（sb.type45.item.{i}）——韌體宣告了幾個組件是機器的事實；SMBIOS <3.5 時整個家族一筆都不產",
            ["tpm.ev.type."] =
                "TCG log 逐事件類型一筆（tpm.ev.type.{十進位碼}）——log 裡實際出現哪些類型是這次開機的事實；沿用『不命名 EV_*』紀律（本專案無對照表出處）",
            ["pdev.fw.inv."] =
                "周邊韌體盤點逐碟一筆（pdev.fw.inv.{i}）——有幾顆碟由 Win32_DiskDrive 列舉決定；不自述韌體版本的也在清單裡",
            ["os.wmi.fields."] =
                "WMI 未用欄位盤點逐類一筆（os.wmi.fields.{類別名}）——成員＝已納入盤點的 WMI 類別（processor/diskdrive/videocontroller），成員固定但走家族與逐類明細並列（彙總鍵 os.wmi.fields 在靜態目錄）",
            // ── 第 272 輪（U10 第二圈：TS 自負載檢測）──────────────────────
            // 成員＝寬度×相位／快取層級／核心×實作／核序號——皆由機器枚舉或檢測矩陣決定；
            // 量測觸發與界線（使用者觸發才跑、啟發式門檻不判決）寫在各服務檔。
            ["ts.vector.flops."] =
                "向量寬度吞吐逐寬度逐相位一筆（ts.vector.flops.{scalar|128|256|512}.{st|mt}）——AVX-512 不支援時該寬度如實 NotSupported 不降級",
            ["ts.cache.bw."] =
                "快取帶寬階梯逐層級一筆（ts.cache.bw.{l1|l2|l3}）——層級由工作集掃描推估；DRAM 層由 memory.stream-bandwidth 收口不重測",
            ["ts.copy.kernel."] =
                "拷貝核心帶寬逐核心逐實作一筆（ts.copy.kernel.{memcpy|memset}.{crt|simd|byte}）——CRT 路徑照實量 managed 呼叫到的實作，不自判 FSRM 選路",
            ["ts.tsc.skew.core."] =
                "TSC 跨核偏移逐核一筆（ts.tsc.skew.core.{i}）——核集由拓撲決定；通路是既有 WinRing0 MSR 橋唯讀，不可用時整組 NotSupported",
            ["ts.core.uniformity."] =
                "逐核一致性逐核一筆（ts.core.uniformity.core.{i}）與 spread 總覽——核集由拓撲決定；偏差門檻是啟發式（±15%），值裡標「疑似」不判決",
        };

    /// <summary>執行期鍵是否命中某個已登記的動態家族。</summary>
    public static bool Matches(string key)
    {
        foreach (var prefix in Families.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }
}
