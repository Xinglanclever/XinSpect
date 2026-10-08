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
