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
        ["in.cfg.repair"] = "設定自我修復（IN-008）：狀態只有 settings.json 一個來源；修復動作另有往返測試。",
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
