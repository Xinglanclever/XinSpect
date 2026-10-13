using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 事實鍵的對帳覆蓋申報：哪些鍵有規則考慮、哪些明文豁免、哪些還沒被任何規則碰到。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要申報而不是要求全覆蓋：</b>本程式有數百個事實鍵，而對帳規則只有二十幾條。
/// 要求「每個鍵都被規則考慮」不現實，硬湊出來的規則只會製造假訊號。
/// 但「哪些鍵還沒被考慮」必須<b>看得見</b>——因為新增事實時最常見的疏漏，
/// 就是事實進了快照、卻沒有任何規則會去看它，於是那個數字永遠不會被交叉檢查，
/// 而畫面上看起來一切正常。
/// </para>
/// <para>
/// <b>三種狀態：</b>
/// </para>
/// <list type="bullet">
/// <item><b>已覆蓋</b>——至少一條對帳規則把它列為輸入。</item>
/// <item><b>明文豁免</b>——刻意不對帳，且附理由（見 <see cref="Exemptions"/>）。</item>
/// <item><b>未覆蓋</b>——沒被規則碰到、也沒豁免。這<b>不是錯誤</b>，但應該是一個已知的數字。</item>
/// </list>
/// <para>
/// <b>本類別只申報，不強制。</b>把「未覆蓋」當成紅燈會逼出一堆湊數的規則，
/// 那比誠實申報一個數字更糟。
/// </para>
/// </remarks>
public static class FactCoverageReport
{
    /// <summary>明文豁免：刻意不對帳的事實鍵前綴與理由。</summary>
    /// <remarks>
    /// 豁免的門檻是「這類事實沒有第二個來源可以交叉」——不是「懶得寫規則」。
    /// 每一條都要寫得出為什麼。
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Exemptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── 單一來源：這類事實本來就沒有第二條路徑可以對 ──────────────────
            ["asset."] = "識別欄位（序號、SKU、資產標籤）只有韌體一個來源，沒有第二個來源可交叉。"
                       + "可對帳的是「有沒有填」，那已寫在 asset.identify 的判讀裡，不是值的本身。",
            ["audio."] = "音訊端點緩衝區是 IAudioClient 的配置結果，單一來源。"
                       + "它與取樣率的關係是算術（毫秒＝框架÷取樣率），不是對帳。",
            ["role."] = "已安裝角色與功能來自選用功能與服務清單，單一來源；服務狀態是同一份資料的延伸。",
            ["display."] = "顯示轉接器識別來自 WMI 單一來源。PNP 匯流排來源與 EDID 的交叉已寫在判讀層。",
            ["monitor."] = "顯示器組成來自 WMI 的 EDID 解析結果，單一來源。",
            ["mon."] = "顯示器連接介面（VideoOutputTechnology）單一來源。",
            ["virt."] = "虛擬化的三個觀測值（CPUID／選用功能／服務）本來就是刻意分開陳述的三件事。"
                     + "硬綁成一條對帳規則，會把「裝了但未載入」這種正常狀態判成矛盾。",
            ["nic."] = "網卡落差已在判讀層把 PCIe 與線路兩側並列。兩者本來就該不同，不構成矛盾。",
            ["ent."] = "企業儲存偵測（iSCSI／MPIO／FC）來自 SCM 與 WMI 的服務存在性，屬環境事實。",
            ["oob."] = "帶外管理存在性探測（SMBIOS Type 38）；本機無硬體時本來就不適用。",
            ["raid."] = "RAID 控制器存在性（PCI 類別碼盤點），單一來源。",
            ["cam."] = "攝影機裝置列舉來自 Windows 媒體裝置類別，單一來源，沒有第二條路徑可交叉。",
            ["usbstor."] = "USB 儲存裝置列舉來自裝置介面 GUID 查詢，單一來源，沒有第二條路徑可交叉。",
            ["usb."] = "USB 拓撲列舉來自 USB 控制器與中樞的裝置樹，單一來源，沒有第二條路徑可交叉。",
            ["ups."] = "電池／UPS 事實來自 Windows 電池 API，單一來源。",
            ["cert."] = "憑證存放區列舉，單一來源（CertOpenStore）。",
            ["defender."] = "Defender 狀態與排除清單來自 WMI，單一來源。",
            ["byovd."] = "易受攻擊驅動比對的來源是微軟的封鎖清單，沒有第二份清單可對。",
            ["hpa."] = "HPA／DCO 容量落差本身就是「宣稱容量 vs 可定址容量」的對帳，已內含。",
            ["time."] = "時間來源事實（HPET／PM Timer／漂移）是量測結果，單一來源。",
            ["dbg."] = "除錯與診斷輸出（開機選項、核心旗標），不是硬體事實。",
            ["evt."] = "事件記錄摘要的來源就是事件記錄本身，自我對帳無意義。",
            ["audit."] = "稽核政策事實來自 LSA 與事件記錄，單一來源。",
            ["boot."] = "開機耗時與 POST 代碼是量測與唯讀埠值，單一來源。",
            ["chassis."] = "機箱安全狀態（入侵偵測、鎖）來自 SMBIOS Type 3，單一來源。",
            ["cxl."] = "CXL 事實來自 ACPI 表（CEDT／CHBS），單一來源；本機無 CXL 硬體。",
            ["gp."] = "遊戲／繪圖管線的合成負載結果，無第二來源。",
            ["smbus."] = "SMBus 唯讀事實（TSOD）走單一控制器路徑。",
            ["acpi."] = "ACPI 表的檢查和自檢已內含在解碼器裡（逐表驗證），不另立跨來源規則。",
            ["amd."] = "AMD 專屬事實（SMU／PSP 存在性）本機為 Intel 平台，無法驗證；"
                     + "有真機時應補對帳規則，屆時本條豁免要移除。",
            ["numa."] = "NUMA 拓撲來自 kernel32 與 ACPI SLIT，兩者已在解碼層並列；"
                      + "單節點機器的跨節點項目本來就無從量測。",

            // ── 刻意不豁免（列在下方註解，讓它們出現在「未覆蓋」裡）────────────────
            // psu.：PMBus 市電 vs RAPL vs NVML 是可以對的（藍圖的「功率守恆交叉對帳」），
            //       有硬體時應該補規則——不豁免才能讓它出現在未覆蓋清單上被看見。
            // gpu.：NVML、WMI 與登錄檔的 VRAM 三者可交叉，尚未寫規則。
            // mem.／smart.：SPD、SMBIOS 與 SMART 三方對記憶體與磁碟的說法可交叉。
            // tpm.：TPM 的 PCR 與 UEFI 的 db/dbx 可交叉（開機鏈存證）。
            // cmos.：RTC 與系統時間、NTP 可交叉。
            // topology.／gauntlet.／reconcile.：這三個家族不經「事實鍵」的建立路徑
            //   （topology 與 gauntlet 是深測指標、reconcile 是對帳結果本身），
            //   因此不會出現在掃描結果裡——不列豁免，免得豁免看起來比實際乾淨。
        // Vol2 維護／執行環境／收尾批次（2026-10-12，第 32–55 項）：以下全是「單一來源類」事實——
        // 沒有第二個來源可以交叉（進度只有審計鏈、報告只有事實集自己、環境聲明只有 WMI 一張嘴）。
        // 豁免不是免檢：每條都寫明為什麼無法交叉，界線另外寫在服務註解與值裡。
        ["ng.progress"] = "檢查進度記憶（NG-003）：唯一來源是審計日誌，無第二來源可交叉；數字是計數不是結論。",
        ["ng.report"] = "診斷報告整合（NG-004）：輸入是全部事實的可用性分布，自己對自己交叉沒有意義（刻意無遞迴）。",
        ["qs.trust"] = "來源信任分布（QS-002）：只列 Trust 標示的分布，刻意不做分數與判定。",
        ["ng.tree"] = "診斷樹導航（NG-001）：頁面清單是 UI 導航結構，不是硬體事實。",
        ["hm.unknown."] = "未知裝置（HM-001）：WMI 單來源；缺驅動不等於故障，判決不適合機器對帳。",
        ["hm.prob."] = "裝置錯誤碼（HM-002）：ConfigManagerErrorCode 單來源；逐項意義已由 DeviceDiagnosticRules 承載。",
        ["hm.tree"] = "裝置樹總覽（HM-008）：與裝置診斷頁是同一來源的兩種呈現。",
        ["fs.bits."] = "BITS 佇列痕跡（FS-008）：事件記錄單來源；下載內容與 URL 刻意不讀。",
        ["fs.insthist."] = "安裝痕跡（FS-011）：MsiInstaller 事件單來源；產品名屬 L1 軟體清單。",
        ["uf.source"] = "更新來源（UF-006）：政策登錄單來源；該不該更新不在事實層。",
        ["uf.servicing"] = "服務堆疊版本（UF-009）：CBS 登錄單來源，無第二來源。",
        ["uf.channel"] = "更新通道（UF-007）：政策登錄單來源。",
        ["uf.dash"] = "更新健康儀表（UF-010）：由 uf.source/channel/servicing 湊成，交叉會自我指涉。",
        ["in.env"] = "執行環境自我聲明（IN-003）：WMI 單來源；刻意不做反 VM，無交叉對象。",
        ["in.update.sig"] = "自我更新完整性（IN-006）：本工具沒有自我更新通道——沒有就是申報本身。",
        ["in.elevation."] = "提權最小化審計（IN-005）：輸入是本輪事實的可用性，自己對自己交叉無意義。",
        ["ng.history"] = "導覽歷史（NG-005）：只在記憶體的使用者行為軌跡（L1），刻意不落盤不交叉。",
        ["dl.sympath"] = "符號路徑（DL-003）：環境變數單來源。",
        ["dl.threadpool."] = "線程池（DL-008）：本行程執行期狀態，無第二來源。",
        ["dl.gc."] = "GC 模式與計數（DL-009）：本行程執行期狀態，無第二來源。",
        ["dl.modconflict."] = "模組衝突（DL-010）：Process.Modules 單來源；同名不等於惡意，判定不機器化。",
        ["hm.wake."] = "裝置喚醒（HM-004）：powercfg 原樣輸出、不解析在地化文字——無法機器交叉。",
        ["fs.privacy.boundary"] = "隱私禁項邊界（FS-013）：禁令清單本身，由 ForensicBanGateTests 機器釘住。",
        ["qs.unit"] = "單位標示檢查（QS-004）：讀的是事實集自己的 Unit 欄，沒有第二來源可交叉。",
        ["qs.index"] = "查詢索引統計（QS-006）：域前綴計數，純鍵集運算。",
        ["qs.keydiff"] = "鍵集 diff（QS-009）：兩份鍵集的集合運算，結果由DiffKeySets決定。",
        ["qs.help"] = "說明覆蓋率（QS-005）：HelpCatalog 與 PageRegistry 的對照，兩者都是本專案自己的資產。",
        ["se.grade"] = "安全摘要分級（SE-014）：可用性計數彙總；判定理由在對應事實與對帳規則裡，不再另立規則。",
        ["se.pipe."] = "Named pipe 列舉（SE-004）：pipe 目錄單來源；列舉不等於可連線、也不代表有人在用。",
        ["rs.sig"] = "報告簽章（RS-010）：審計鏈頭與驗證結果，鏈的完整性由 AuditVerifier 的專門測試守，不再用規則對帳。",
        ["hm.hwdiff"] = "硬體變更時間軸（HM-007）：輸入是兩份快照的比較結果，已是第三者對照，不再疊規則。",
        ["zr.passkey"] = "Passkey 支援（ZR-005）：檔案存在＋組建號的條件陳述，無第二來源。",
        ["dl.dump"] = "行程傾摘閘門（DL-002）：只宣告同意狀態，本版無傾印實作——沒有值可以對帳。",
        ["zr.mdm"] = "MDM 註冊狀態（ZR-001）：Enrollments 登錄單來源；只報狀態不讀識別資料。",
        ["zr.aadj"] = "Entra 加入狀態（ZR-002）：AAD／WorkplaceJoin 登錄單來源。",
        ["zr.wipe"] = "遠端抹除能力（ZR-007）：由 MDM／FindMyDevice 推導的能力聲明，本工具永不觸發。",
        ["ds.cert."] = "憑證時間線（DS-005）：X509Store 單來源，只取時間窗計數（不列主體）。",
        ["se.audit2."] = "進階稽核政策（SE-005）：auditpol 原樣輸出、不解析在地化文字，無法機器對帳。",
        ["dl.cmdline."] = "行程命令列（DL-005）：L2 明示同意才讀；同意與否本身決定有無值。",
        ["hm.sensdrift"] = "感測器交叉（HM-005）：多點溫度彼此比較，跨幅是事實不是判準，無第二來源可對。",
        ["mu.hdr"] = "HDR 影片就緒（MU-008）：VideoSettings 登錄單來源。",
        ["ds.efs."] = "EFS 憑證數（DS-003）：X509Store 單來源計數；不列主體。",
        ["ds.hello."] = "Windows Hello 註冊數（DS-004）：登錄計數；生物特徵永不讀。",
        ["ds.cert.auto"] = "憑證自動註冊（DS-006）：AutoEnrollment 政策單來源。",
        ["ds.selfsigned."] = "自簽憑證數（DS-007）：X509Store 的簽章型態計數。",
        ["ds.drm"] = "PlayReady（DS-008）：登錄鍵存在性單來源。",
        ["ds.tls."] = "TLS 停用（DS-009）：SCHANNEL 登錄單來源；未列版本不代表啟用。",
        ["ds.sac"] = "Smart App Control（DS-010）：登錄狀態單來源。",
        ["ds.devpol."] = "裝置安裝政策（DS-011）：政策登錄單來源。",
        ["se.rdp.cert"] = "RDP 憑證狀態（SE-006）：登錄鍵存在性；憑證內容不匯出。",
        ["se.ntlm"] = "NTLM 政策（SE-007）：LmCompatibilityLevel 單來源。",
        ["se.kerb."] = "Kerberos 政策（SE-008）：Parameters 鍵政策值，生命週期由 KDC 決定。",
        ["se.winre."] = "WinRE 存在性（SE-013）：登錄控制鍵；Reagentc 才是權威來源。",
        ["zr.cba"] = "憑證型登入（ZR-003）：政策單來源；生效與否取決於 CA 與卡片註冊。",
        ["zr.dynlock"] = "動態鎖定（ZR-006）：政策單來源；proximity 硬體不在宣稱範圍。",
        ["zr.report"] = "合規報告（ZR-008）：聚合陳述、不做判決。",
        ["uf.ready"] = "功能更新就緒度（UF-004）：登錄線索的唯讀評估。",
        ["hm.conflict."] = "PnP 資源衝突（HM-003）：WMI 問題碼 12/24/31；根因不重述。",
        ["mu.cam."] = "相機裝置數（MU-004）：只數裝置、不開啟鏡頭。",
        ["dm.sense"] = "Storage Sense 狀態（DM-002）：登錄單來源。",
        ["dm.conflict"] = "維護視窗（DM-004）：登錄設定；衝突判定需任務清單交叉，本項只報視窗。",
        ["dm.clean"] = "清理處理器數（DM-005）：VolumeCaches 子鍵計數；不執行清理。",
        ["dm.timeline."] = "任務運行時間線（DM-006）：TaskScheduler 事件 200/201，痕跡不是現況。",
        ["dm.dash"] = "維護摘要（DM-008）：本組可用性聚合，分母不含自己。",
        ["ds.bl.summary"] = "BitLocker 卷級（DS-001）：WMI 單來源，不含金鑰；保護器交叉在 FW-003。",
        ["qs.history"] = "查詢歷史（QS-008）：記憶體環狀緩衝、不落盤（L1）；由快取測試釘住。",
        ["qs.grammar"] = "文法版本（QS-010）：常數＋semver 斷言。",
        ["uf.timeline"] = "更新時間軸（UF-005）：由既有 wu.history.* 收口；來源本身已有專屬服務。",
        ["pl.forward"] = "CLI 轉發單一實例（PL-009）：既有機制的描述；訊號存在性由原始碼掃描測試釘住。",
        ["ds.summary"] = "資料安全摘要（DS-012）：本組事實的可用性聚合，分母不含自己。",
        ["sg."] = "情境引導（SG-006/007/009/013/014）：PnP 名稱比對與導航文字，不下判決、無第二來源可機器對帳。",
        ["mu.codec."] = "編解碼器清單（MU-001）：Drivers32 登錄單來源；清單不等於可用性。",
        ["mu.mic."] = "擷取端點數（MU-005）：MMDevices 登錄計數；不錄音、不讀內容。",
        ["mu.mf."] = "媒體基礎（MU-007）：mfplat.dll 版本與 Platform 鍵；不測播放。",
        ["rs.jsonl"] = "JSONL 串流（RS-006）：匯出能力的描述；格式由序列化測試釘住。",
        ["qs.cache"] = "查詢結果快取（QS-007）：本行程記憶體狀態；指紋失效由專門測試守。",
        ["se.wpbt"] = "WPBT 表存在性（SE-010）：由 acpi.tables 推導的存在性聲明，只宣告存在、不解析內容，無第二來源。", 
        ["dm.maint.summary"] = "自動維護狀態史（DM-001）：TaskScheduler 操作記錄單來源；事件是痕跡，不是維護現況。",
        ["ds.dha"] = "DHA 未施測聲明（DS-002）：本機沒有可驗證的 attest 通路，這是誠實聲明，不是量測值。",
        ["el.browser.versions"] = "瀏覽器版本（EL-003）：Edge／Chrome／Firefox 更新登錄單來源；只讀版本，不讀瀏覽記錄。",
        ["el.office.version"] = "Office 版本（EL-002）：ClickToRun 登錄單來源；不做 EOL 判定。",
        ["pl.portable"] = "綠色零寫入邊界（PL-005）：宣告加原始碼掃描守門，沒有第二個執行期來源。",
        ["pl.releasenote"] = "版本公告草稿（PL-010）：由 ChangelogCatalog 產生，草稿不代表已發佈，無第二來源可交叉。",
        ["rs.diff.json"] = "機器可讀 diff JSON（RS-009）：序列化表示法，格式由測試釘住，沒有第二條事實來源。",
        ["rs.fleet"] = "機隊 CSV（RS-008）：序列化表示法，格式與敏感值政策由測試釘住，沒有第二條事實來源。",
        ["rs.rdiff"] = "報告版本比較（RS-011）：兩份鍵集的集合運算，結果由 QueryInsightFactsService.DiffKeySets 決定。",
        ["xt.csharp"] = "C# 常數產生器（XT-001）：鍵集的程式碼表示法，格式由產生器測試釘住，沒有第二條事實來源。",
        ["xt.py"] = "Python 離線讀取範例（XT-002）：格式範例不是機器事實；零網路由範例測試釘住。",
        ["xt.trigger"] = "本機檔案觸發（XT-004）：觸發檔是呼叫端指定的訊號，單一來源；不揭露 token 或路徑。",
        ["xt.snapshot.fs"] = "快照檔案整合（XT-005）：包裝既有 HardwareSnapshotService，完整性由該服務與專屬測試負責。",
        ["xt.registry"] = "schema registry（XT-006）：由 FactKeyCatalog／FactKeyDynamicCatalog 匯出，兩者已是同一份程式目錄。",
        ["rs.helpfmt"] = "匿名求助範本（RS-001）：事實集的 Markdown 表示法；遮蔽由專屬測試釘住，無第二來源可交叉。",
        ["rs.svg"] = "SVG 圖表（RS-003）：事實集的視覺表示法；不是新的量測來源。",
        ["rs.xlsx"] = "xlsx 匯出（RS-004）：事實集的 OOXML 表示法；格式由 zip／worksheet 測試釘住。",
        ["rs.print"] = "列印版面（RS-005）：既有 HtmlReportService 的列印入口，內容仍由同一份事實集決定。",
        ["rs.manual"] = "離線手冊（RS-012）：HelpCatalog 的 HTML 再呈現，不是新的說明內容來源。",
        ["ag.headless"] = "CLI headless 模式目錄（AG-001）：本程式命令面的自我描述，沒有第二個硬體來源可交叉。",
        ["ag.compose"] = "YAML 檢查組合（AG-002）：輸入是呼叫端給定的清單文件，結果由組合規則決定，不是機器量測值。",
        ["ag.exitcode"] = "退出碼對照（AG-003）：本程式自己的 CLI 契約，由測試釘住；不適用硬體對帳。",
        ["ag.stdin"] = "stdin 批次模式（AG-004）：只包裝既有事實集合，查詢結果仍由原事實來源負責。",
        ["ag.rotate"] = "排程輸出輪轉（AG-006）：呼叫端指定檔案的搬移規則，不是本機硬體事實。",
        ["ag.remote"] = "遠端查詢（AG-007）：明文不做；這是零網路政策聲明，沒有值可交叉。",
        ["sg.boot.summary"] = "開機引導摘要（SG-001）：聚合開機事件與啟動登錄計數；服務明細屬導航不是診斷。",
        ["sg.mem.summary"] = "記憶體佔用摘要（SG-003）：Process.WorkingSet64 單來源快照；不以此判洩漏。",
        ["mu.hwdec.summary"] = "HEVC／AV1 硬解（MU-002）：D3D11/MFT 的能力宣告，單一來源；不跑影片所以不實測速度。",
        ["xt.sysmon.map"] = "Sysmon 格式對照（XT-007）：外部事件格式對照表，不是本機量測，無第二來源可交叉。",
        ["rc.hiber.summary"] = "休眠檔審計（RC-013）：系統根目錄檔案 metadata＋HiberbootEnabled，單一來源。",
        ["rc.fastboot.summary"] = "快速啟動（RC-014）：HiberbootEnabled 單一登錄值；未設定不代填 Windows 預設。",
        ["pw.usbsel"] = "USB 選擇性暫停（PW-002）：作用中電源計畫的 AC／DC 設定索引，單一來源。",
        ["pw.shutdown.summary"] = "停機時長（PW-007）：System 6006／6005 事件配對；事件是痕跡不是精確關機耗時。",
        ["ie.grafana"] = "Grafana 匯出（IE-002）：面板骨架產生器，輸出由鍵清單決定，非機器量測。",
        ["ie.ps.template"] = "PowerShell 範本（IE-005）：本機 CLI 呼叫範例；零網路，範例不執行。",
        ["ie.deeplink"] = "deep-link（IE-006）：URI 格式產生器；不註冊 scheme、不啟動外部程式。",
        ["ie.completion"] = "參數補完（IE-007）：CLI 契約產生的 PowerShell 補完腳本，單一來源。",
        ["dk.alias.summary"] = "鍵別名（DK-003）：固定別名到正式鍵的映射；別名不是第二個事實來源。",
        ["dk.snapshot.age"] = "快照期限（DK-008）：擷取時間與現在時間的生命週期標籤；保存政策提示，不是硬體狀態。",
        ["pw.sleep.cap"] = "睡眠能力矩陣（PW-003）：CallNtPowerInformation 平台宣告單一來源；目前計畫是否可用另判。",
        ["dv.wsl.summary"] = "WSL 發行版盤點（DV-001）：使用者登錄單一來源；不列發行版名稱或路徑。",
        ["dv.env.path"] = "PATH 重複審計（DV-004）：Machine／User PATH 聯集計數；不列本機路徑。",
        ["dv.container.summary"] = "容器 runtime（DV-007）：Docker／containerd／Podman 服務單一來源；不等於映像或容器可用。",
        ["dv.toolchain.summary"] = "工具鏈版本（DV-008）：PATH 上找到的工具與 FileVersionInfo；不代表專案使用的版本。",
        ["gd.shcache.summary"] = "著色器快取（GD-005）：LOCALAPPDATA 常見快取目錄 metadata，單一來源。",
        ["gd.hdr.summary"] = "HDR 能力鏈（GD-006）：EDID 宣告與 DisplayConfig 開關合併；不做畫質判定。",
        ["gd.tablet.count"] = "繪圖板／數位筆（GD-008）：PnP 名稱比對計數；不開啟裝置。",
        ["sc.uptime.summary"] = "重啟摘要（SC-007）：Environment.TickCount64 是系統 uptime；不等於更新已套用。",
        ["sc.netdrop.summary"] = "網路中斷事件（SC-009）：NetworkProfile 事件痕跡；不是根因判決。",
        ["in.cfg.repair"] = "設定自我修復（IN-008）：狀態只有 settings.json 一個來源；修復動作另有往返測試。",
        ["nw.vnic.summary"] = "虛擬網卡盤點（NW-006）：Win32_NetworkAdapter 分類計數；不列名稱、MAC 或 IP。",
        ["nw.legacy.summary"] = "LPT／COM 盤點（NW-022）：SERIALCOMM 裝置對映計數；0 是值，不列裝置名稱或路徑。",
        ["ws.svc.graph"] = "服務依賴圖（WS-002）：Win32_Service 的圖形摘要；不列服務名稱，未對應依賴不判故障。",
        ["ws.dumpcfg.summary"] = "傾印設定審計（WS-007）：CrashControl 設定值；只審計不調整，不揭露傾印路徑。",
        ["st.ntfs.summary"] = "NTFS 特性審計（MS-012）：卷冊數與檔案系統設定原值；不做效能或安全判決。",
        ["st.xcheck.summary"] = "實體磁碟交叉（ST-016）：Win32_DiskDrive 與 MSFT_PhysicalDisk 容量多重集；不自動判定來源正確性。",
        ["st.legacy.summary"] = "舊式儲存盤點（ST-019）：光碟機／軟碟機計數；不讀媒體內容。",
        ["pv.telemetry"] = "遙測層級事實（PV-001）：AllowTelemetry 政策字面值；未設定不代填 Windows 預設。",
        ["tm.tzdb"] = "時區／DST 資源（TM-004）：TimeZoneInfo 與 tzres.dll 版本；Windows 不提供 IANA tzdb 版本。",
        ["pv.ddv"] = "診斷記錄器狀態（PV-003）：Autologger 登錄計數；不啟停記錄器、不讀事件內容。",
        ["nw.print.summary"] = "列印佇列健康（NW-012）：Win32_Printer 狀態計數；不列印表機名稱或連接埠。",
        ["nw.wol.summary"] = "WOL 事實化（NW-014）：電源管理登錄宣告；設定關不等於硬體不支援。",
        ["nw.route.summary"] = "路由歸屬（NW-016）：路由表計數與 metric；不列網段、閘道或介面名稱。",
        ["nw.sensor.summary"] = "感應器清單（NW-018）：PnP 名稱／類別計數；不讀值、不開裝置。",
        ["ws.channel.summary"] = "事件通道健全性（WS-005）：通道啟用計數；不讀事件內容，停用不等於清空。",
        ["st.ss.summary"] = "Storage Spaces（ST-013）：集區／虛擬／實體碟計數；不讀成員名稱或資料。",
        ["st.pstrans.summary"] = "磁碟電源轉換（ST-015）：裝置自報累計計數；不等於 idle↔active 觀察次數。",
        ["pv.adid.summary"] = "廣告與活動歷史設定（PV-002）：政策原值；未設定不代填預設。",
        ["pv.speech.summary"] = "語音／輸入設定（PV-005）：政策原值；不讀語音或輸入內容。",
        ["tm.ntp.summary"] = "NTP 同步狀態（TM-003）：本機服務與登錄狀態；不出網查詢。",
        ["cm.cache.share.summary"] = "快取共享拓撲（CM-005）：CPUID leaf 0x04 共享遮罩單一來源；上界由硬體回報，無第二通路可交叉。",
        ["dv.nested"] = "巢狀虛擬化能力（DV-002）：Hypervisor／韌體／VBS／選用功能合併陳述；能力存在不等於來賓必定可巢狀，不判決。",
        ["dv.runtime.summary"] = ".NET／VC++ 執行階段（DV-003）：登錄與目前 CLR 版本單一來源；不做 EOL 判決，不代表專案使用版本。",
        ["pw.waketask.summary"] = "排程喚醒對照（PW-008）：schtasks XML 唯讀解析；不列任務名稱或命令，也不判是否一定喚醒。",
        ["hist.mon.summary"] = "多螢幕拓撲快照（GD-004）：EnumDisplayMonitors 排列與 DPI 單一來源；不記序號或 EDID。",
        ["dk.anon.stability"] = "匿名 ID 穩定性（DK-005）：正規化硬體描述的 SHA-256；不讀序號或 UUID，不保跨機唯一性。",
        ["dk.roundtrip"] = "匯出／匯入往返（DK-009）：記憶體 JSON 往返測試；驗序列化一致性，不驗外部媒體。",
        ["sc.io.summary"] = "I/O 來源聚合（SC-004）：PerformanceData 程序計數器 since-boot 聚合；不是即時佔用也不判根因。",
        ["mu.drm.summary"] = "DRM 就緒（MU-003）：PlayReady 登錄存在性；不測播放、不讀授權或金鑰。",
        ["mu.spatial.summary"] = "空間音訊（MU-006）：MMDevices 端點登錄宣告；不開裝置、不測空間效果。",
        };

    /// <summary>一個鍵的覆蓋狀態。</summary>
    public enum Coverage
    {
        /// <summary>至少一條對帳規則把它列為輸入。</summary>
        Covered,
        /// <summary>明文豁免（附理由）。</summary>
        Exempt,
        /// <summary>沒被規則碰到、也沒豁免。</summary>
        Uncovered,
    }

    /// <summary>覆蓋申報的整體結果。</summary>
    /// <param name="Total">掃到的事實鍵總數。</param>
    /// <param name="Covered">已覆蓋數。</param>
    /// <param name="Exempt">明文豁免數。</param>
    /// <param name="Uncovered">未覆蓋數。</param>
    /// <param name="UncoveredKeys">未覆蓋的鍵（排序後，供人核對）。</param>
    /// <param name="RuleCount">對帳規則條數。</param>
    /// <param name="RuleInputCount">規則引用的相異鍵數。</param>
    public readonly record struct Summary(
        int Total, int Covered, int Exempt, int Uncovered,
        IReadOnlyList<string> UncoveredKeys, int RuleCount, int RuleInputCount)
    {
        /// <summary>已申報（覆蓋＋豁免）的比例。未覆蓋的不算失敗，但這個數字應該被看見。</summary>
        public double DeclaredRatio => Total > 0 ? (Covered + Exempt) / (double)Total : 0;

        /// <summary>一句話申報。措辭刻意不說「通過」——未覆蓋不是錯誤。</summary>
        public string Headline =>
            $"事實鍵 {Total} 個：{Covered} 個有規則考慮、{Exempt} 個明文豁免、{Uncovered} 個尚未被任何規則碰到"
            + $"（{RuleCount} 條規則共引用 {RuleInputCount} 個鍵）。";
    }

    /// <summary>判斷一個鍵的覆蓋狀態。</summary>
    [SpecRef("本程式的對帳覆蓋約定（非外部規格）：事實鍵有規則考慮＝Covered、明文豁免＝Exempt（見 Exemptions）、其餘＝Uncovered。覆蓋率不設門檻——強制全覆蓋會逼出湊數的規則，那比誠實申報一個數字更糟。")]
    public static Coverage Classify(string key, IReadOnlyCollection<string> ruleInputs)
    {
        if (ruleInputs.Contains(key)) return Coverage.Covered;
        foreach (string prefix in Exemptions.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return Coverage.Exempt;
        return Coverage.Uncovered;
    }

    /// <summary>產生申報。</summary>
    [SpecRef("同上：彙總事實鍵的對帳覆蓋狀態，並列出未覆蓋的鍵供人核對。")]
    public static Summary Report(
        IReadOnlyCollection<string> factKeys,
        IReadOnlyList<(string Id, IReadOnlyList<string> Inputs)> rules)
    {
        var ruleInputs = rules.SelectMany(r => r.Inputs).ToHashSet(StringComparer.Ordinal);
        int covered = 0, exempt = 0;
        var uncovered = new List<string>();
        foreach (string key in factKeys)
        {
            switch (Classify(key, ruleInputs))
            {
                case Coverage.Covered: covered++; break;
                case Coverage.Exempt: exempt++; break;
                default: uncovered.Add(key); break;
            }
        }
        uncovered.Sort(StringComparer.Ordinal);
        return new Summary(factKeys.Count, covered, exempt, uncovered.Count, uncovered,
                           rules.Count, ruleInputs.Count);
    }

    /// <summary>
    /// 每一條豁免都要有理由，且理由不得是「不知道」「待補」這類佔位。
    /// </summary>
    [SpecRef("同上：豁免的品質約定——沒有理由的豁免等於沒豁免。")]
    public static IReadOnlyList<string> ExemptionsWithoutReasons()
        => Exemptions.Where(kv => string.IsNullOrWhiteSpace(kv.Value)
                               || kv.Value.Length < 20
                               || kv.Value.Contains("待補", StringComparison.Ordinal)
                               || kv.Value.Contains("TODO", StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToList();
}
