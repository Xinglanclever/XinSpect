namespace XinSpect;

/// <summary>SMART 知識表收錄的六家廠商。</summary>
public enum SmartVendor
{
    Samsung,
    Seagate,
    /// <summary>Western Digital（別名 WD／WDC）。</summary>
    WesternDigital,
    Kingston,
    /// <summary>Micron（別名 Crucial——消費品牌同一家）。</summary>
    Micron,
    SanDisk,
}

/// <summary>
/// 一列 SMART 屬性知識：ID → 名稱、raw 欄位語意、normalized 語意、單位。
/// <para>
/// <b>raw 與 normalized 是兩種編碼，刻意分開陳述</b>：normalized 是韌體換算後的 0–253 相對值
/// （100 起頭遞減、低於門檻即徵候），raw 是 48 位元原樣——同一個 ID 在不同廠商手裡
/// raw 位元組拆解可以完全不同（Seagate 的屬性 1 是多欄位打包、Samsung 的溫度是
/// 三個 16 位元字組）。每列都帶出處；查不到出處的 ID 不收錄——
/// 猜出來的語意比「未知」更糟（V7 誠實契約）。
/// </para>
/// </summary>
public sealed record SmartAttributeInfo(
    byte Id,
    string Name,
    /// <summary>raw 六位元組的語意（含位元組／字組拆解；沒有官方拆解文件時如實說明）。</summary>
    string RawSemantics,
    /// <summary>normalized（0–253 相對值）的語意；來源文件未描述公式時如實標記。</summary>
    string NormalizedSemantics,
    /// <summary>計量單位（個、小時、°C、%、GiB…）；無量綱的狀態碼／比率為「—」。</summary>
    string Unit,
    /// <summary>true＝廠商私有 ID；false＝業界共同 ID（語意仍以該廠商文件為準）。</summary>
    bool VendorSpecific,
    /// <summary>出處標記：文件名（廠商官方）或專案＋條目／版本＋抓取日期。</summary>
    string Source);

/// <summary>
/// SMART 廠商屬性知識表（ULTIMATE-10000 第 14 列：六家屬性 ID→語意對照，raw/normalized 分開）。
/// <b>知識層的純查詢</b>——不改寫 <see cref="StorageSmartService"/> 的顯示（誰要消費由接線決定）；
/// 未收錄的 ID／廠商回 null（未知），不丟例外、不猜語意。
/// <para>
/// 出處（皆為 2026-10-25 抓取）：
/// <list type="bullet">
/// <item><b>Samsung</b>：官方《Samsung SSD Application Note for Data Centers — S.M.A.R.T.》
/// （© 2014 Samsung Electronics，semiconductor.samsung.com/resources/others/SSD_Application_Note_SMART_final.pdf，
/// 845 DC PRO／EVO 屬性表）；溫度字組拆解另引 smartmontools drivedb.h〈Samsung based SSDs〉。</item>
/// <item><b>Seagate</b>：官方 openSeaChest Wiki〈Drive Health and SMART〉
/// （GitHub Seagate/openSeaChest）。</item>
/// <item><b>Western Digital</b>：官方 KB Answer ID 4192〈S.M.A.R.T. Attributes Supported by
/// WD SATA Hard Drives〉（support-en.wd.com）。</item>
/// <item><b>Kingston</b>：官方《SMART Attribute Details》MKP-306.2（© 2015 Kingston Technology，
/// media.kingston.com/support/downloads/mkp_306_smart_attribute.pdf，SF-2000 系列屬性表，
/// 含逐屬性 normalized 公式與 raw 欄位用法）。</item>
/// <item><b>Micron</b>：smartmontools drivedb.h〈Crucial/Micron Client SSDs〉條目
/// （該條目自註對照 Micron 官方 TN-FD-22《Client SSD SMART Attributes》Rev. E，2018-09-28）。</item>
/// <item><b>SanDisk</b>：smartmontools drivedb.h〈Marvell based SanDisk SSDs〉條目
/// （SanDisk 未發布官方屬性文件——官方論壇明言不公開，故引開源解碼表）。</item>
/// </list>
/// </para>
/// </summary>
[SpecRef("六家屬性出處見各列 Source 欄與類別摘要；溫度字組拆解另見 smartmontools atacmds.cpp RAWFMT_TEMPMINMAX")]
public static class SmartAttributeKnowledge
{
    /// <summary>全部出處的抓取日期（列在每列 Source 欄尾）。</summary>
    public const string FetchedDate = "2026-10-25";

    // ── 出處常數（逐列引用，稽核面）─────────────────────────────────────

    private const string SamsungDoc =
        "Samsung SSD Application Note for Data Centers《S.M.A.R.T.》（© 2014 Samsung Electronics，semiconductor.samsung.com，2026-10-25 抓取）";
    private const string SamsungDrivedb =
        "smartmontools drivedb.h〈Samsung based SSDs〉（GitHub master，2026-10-25 抓取）";
    private const string SeagateWiki =
        "Seagate openSeaChest Wiki〈Drive Health and SMART〉（GitHub Seagate/openSeaChest，2026-10-25 抓取）";
    private const string WdKb =
        "Western Digital KB Answer ID 4192〈S.M.A.R.T. Attributes Supported by WD SATA Hard Drives〉（support-en.wd.com，2026-10-25 抓取）";
    private const string KingstonDoc =
        "Kingston《SMART Attribute Details》MKP-306.2（© 2015 Kingston Technology，media.kingston.com，2026-10-25 抓取，SF-2000 系列）";
    private const string MicronDb =
        "smartmontools drivedb.h〈Crucial/Micron Client SSDs〉（GitHub master，2026-10-25 抓取；條目自註對照 Micron TN-FD-22 Rev. E，2018-09-28）";
    private const string MicronTempFmt =
        "smartmontools atacmds.cpp RAWFMT_TEMPMINMAX（GitHub master，2026-10-25 抓取）";
    private const string SanDiskDb =
        "smartmontools drivedb.h〈Marvell based SanDisk SSDs〉（GitHub master，2026-10-25 抓取；SanDisk 未發布官方屬性文件）";

    /// <summary>ATA 慣例的通用說明——Samsung／WD 文件只描述 raw 時，normalized 如實標記不猜公式。</summary>
    private const string NormConvention =
        "normalized 0–253 相對值（100 起頭遞減）為 ATA 慣例；來源文件未給本屬性的換算公式，不猜";

    // ── 查詢介面 ─────────────────────────────────────────────────────────

    /// <summary>一家廠商的整張表（ID 升冪）。用於守門與匯出。</summary>
    public static IReadOnlyList<SmartAttributeInfo> Table(SmartVendor vendor) =>
        vendor switch
        {
            SmartVendor.Samsung => SamsungTable,
            SmartVendor.Seagate => SeagateTable,
            SmartVendor.WesternDigital => WdTable,
            SmartVendor.Kingston => KingstonTable,
            SmartVendor.Micron => MicronTable,
            SmartVendor.SanDisk => SanDiskTable,
            _ => [],
        };

    /// <summary>純查詢：廠商＋屬性 ID → 知識列。未收錄回 null（未知），不丟例外。</summary>
    public static SmartAttributeInfo? Lookup(SmartVendor vendor, byte attributeId)
    {
        var table = vendor switch
        {
            SmartVendor.Samsung => SamsungIndex,
            SmartVendor.Seagate => SeagateIndex,
            SmartVendor.WesternDigital => WdIndex,
            SmartVendor.Kingston => KingstonIndex,
            SmartVendor.Micron => MicronIndex,
            SmartVendor.SanDisk => SanDiskIndex,
            _ => null,
        };
        return table is not null && table.TryGetValue(attributeId, out var info) ? info : null;
    }

    /// <summary>字串版查詢：廠商名先正規化（大小寫不拘，WD／WDC／Western Digital 同義，
    /// Crucial 歸 Micron——消費品牌同一家，drivedb.h 條目名「Crucial/Micron」為證）。
    /// 認不得的廠商回 null——「未知廠商」與「未知 ID」都是未知，不是錯誤。</summary>
    public static SmartAttributeInfo? Lookup(string? vendor, byte attributeId) =>
        TryNormalizeVendor(vendor, out SmartVendor v) ? Lookup(v, attributeId) : null;

    /// <summary>廠商名正規化：比照知識庫「未收錄回未知不猜」的慣例。</summary>
    public static bool TryNormalizeVendor(string? name, out SmartVendor vendor)
    {
        switch ((name ?? "").Trim())
        {
            case var s when s.Equals("Samsung", StringComparison.OrdinalIgnoreCase):
                vendor = SmartVendor.Samsung; return true;
            case var s when s.Equals("Seagate", StringComparison.OrdinalIgnoreCase):
                vendor = SmartVendor.Seagate; return true;
            case var s when s.Equals("Western Digital", StringComparison.OrdinalIgnoreCase)
                         || s.Equals("WD", StringComparison.OrdinalIgnoreCase)
                         || s.Equals("WDC", StringComparison.OrdinalIgnoreCase):
                vendor = SmartVendor.WesternDigital; return true;
            case var s when s.Equals("Kingston", StringComparison.OrdinalIgnoreCase):
                vendor = SmartVendor.Kingston; return true;
            case var s when s.Equals("Micron", StringComparison.OrdinalIgnoreCase)
                         || s.Equals("Crucial", StringComparison.OrdinalIgnoreCase):
                vendor = SmartVendor.Micron; return true;
            case var s when s.Equals("SanDisk", StringComparison.OrdinalIgnoreCase):
                vendor = SmartVendor.SanDisk; return true;
            default:
                vendor = default; return false;
        }
    }

    /// <summary>六家的列數（守門與覆蓋申報用）。</summary>
    public static int Count(SmartVendor vendor) => Table(vendor).Count;

    // ── Samsung（官方 Application Note，845 DC PRO／EVO；共 19 列）────────
    // raw 欄位語意逐條出自該文件；文件只描述 raw 者不猜 normalized 公式。

    private static readonly IReadOnlyList<SmartAttributeInfo> SamsungTable =
    [
        new(5, "Reallocated Sector Count（重配置磁區數）",
            "因讀取／寫入／驗證錯誤而搬移的磁區數；數值應為低", "文件未給換算公式；「數量應低」為文件原話", "個", false, SamsungDoc),
        new(9, "Power-on Hours（通電時間）",
            "累計通電小時數", NormConvention, "小時", false, SamsungDoc),
        new(12, "Power-on Count（電源循環數）",
            "累計電源開／關循環數（含異常斷電與正常關機）", NormConvention, "次", false, SamsungDoc),
        new(177, "Wear Leveling Count（耗損平衡計數）",
            "區塊被抹寫的次數；raw＝區塊平均抹寫循環數", "與 SSD 壽命直接相關；文件未給換算公式", "次", true, SamsungDoc),
        new(179, "Used Reserved Block Count (total)（已用保留區塊數）",
            "因讀取／程式化／抹除失敗而動用的保留區塊數", "與屬性 5 相關、隨 SSD 基礎群不同；未給公式", "個", true, SamsungDoc),
        new(180, "Unused Reserved Block Count (total)（未用保留區塊數）",
            "尚未被配置用於讀／程式化／抹除操作的保留區塊數", NormConvention, "個", true, SamsungDoc),
        new(181, "Program Fail Count (total)（程式化失敗數）",
            "失敗的程式化（寫入）請求總數", NormConvention, "個", true, SamsungDoc),
        new(182, "Erase Fail Count (total)（抹除失敗數）",
            "失敗的抹除請求總數", NormConvention, "個", true, SamsungDoc),
        new(183, "Runtime Bad Count (total)（運行壞塊數）",
            "等於屬性 181＋182＋讀取失敗計數之和——全部讀／程式化／抹除失敗的合計", NormConvention, "個", true, SamsungDoc),
        new(184, "End to End Error data path Error Count（端對端錯誤數）",
            "SSD 資料路徑內（host↔NAND 或 NAND↔host）發生的錯誤數", NormConvention, "個", true, SamsungDoc),
        new(187, "Uncorrectable Error Count（不可修正錯誤數）",
            "無法以 ECC 復原的錯誤總數", NormConvention, "個", false, SamsungDoc),
        new(190, "Airflow Temperature（氣流溫度）",
            "NAND 晶片周邊的目前溫度；drivedb.h 將 Samsung 溫度類屬性標 tempminmax——raw 三個小端 16 位元字組：字組 0＝現值，其後為歷史最低／最高",
            NormConvention, "°C", false, $"{SamsungDoc}；字組拆解見 {SamsungDrivedb}"),
        new(195, "ECC Error Rate（ECC 錯誤率）",
            "ECC 可修正錯誤的百分比", NormConvention, "%", false, SamsungDoc),
        new(199, "CRC Error Count（CRC 錯誤數）",
            "主機與 SSD 之間的 CRC 錯誤數（介面問題時 CRC 引擎累計）", NormConvention, "個", false, SamsungDoc),
        new(202, "SSD Mode Status（SSD 模式狀態）",
            "內部狀態碼：0x00＝正常運作、0x10＝tarnish（銹蝕）失敗、0x20＝唯讀服務模式", "狀態碼本身即語意（非相對值）", "—", true, SamsungDoc),
        new(235, "POR Recovery Count（斷電復原計數）",
            "突然斷電、韌體必須復原映射與使用者資料的次數", NormConvention, "次", true, SamsungDoc),
        new(241, "Total LBA Written（累計寫入 LBA）",
            "OS 發給 SSD 的寫入 LBA 總量；×512＝位元組數", NormConvention, "LBA（×512＝位元組）", false, SamsungDoc),
        new(242, "Total LBA Read（累計讀取 LBA）",
            "OS 讀取的 LBA 總量", NormConvention, "LBA", false, SamsungDoc),
        new(250, "SATA Interface Downshifts (total)（SATA 介面降速次數）",
            "host 與裝置間協商降速的次數（目前速度低於前次協商速度時累計）", NormConvention, "次", true, SamsungDoc),
    ];

    // ── Seagate（官方 openSeaChest Wiki；HDD 為主，SSD 條目單獨標注）──────

    private static readonly IReadOnlyList<SmartAttributeInfo> SeagateTable =
    [
        new(1, "Read Error Rate（讀取錯誤率）",
            "Seagate 把多個欄位打包進 raw（數值看似天文數字）——不是單純錯誤計數，是韌體為軟體換算保留的多欄位結構；屬性值由「無錯讀取磁區 vs 需重試／失敗磁區」算出",
            "normalized 為換算後的健康值；raw 天文數字在 Seagate 上屬常態，不當故障看", "—", false, SeagateWiki),
        new(3, "Spin Up Time（馬達起轉時間）",
            "起轉至就緒的時間；超過預期（約 30 秒級）會壓低 normalized", "時間超過預期基準即遞減", "秒", false, SeagateWiki),
        new(4, "Start/Stop Count（啟停計數）",
            "啟停次數；與 Head Load Events（裝置統計）相連動", NormConvention, "次", false, SeagateWiki),
        new(5, "Retired Sectors Count（退役磁區數）",
            "已重配置的邏輯磁區數；512e 磁碟上可能一次跳 8（一個實體磁區含 8 個邏輯磁區）",
            "另有一種 normalized 變體＝重配置資源已用百分比", "個", false, SeagateWiki),
        new(7, "Seek Error Rate（尋道錯誤率）",
            "僅計尋道定位錯誤（讀寫結果本身不計入）", NormConvention, "—", false, SeagateWiki),
        new(9, "Power On Hours（通電時間）",
            "通電小時；Seagate 可能以部分 raw 位元組放「小時內毫秒」做細粒度", NormConvention, "小時", false, SeagateWiki),
        new(10, "Spin Retry Count（起轉重試數）",
            "最近 8 次主軸啟動中需要重試的次數；SATA 版 FARM 紀錄會把 raw／normalized／worst 分欄曝出", NormConvention, "次", false, SeagateWiki),
        new(12, "Drive Power Cycle Count（電源循環數）",
            "電源循環次數；對應 Lifetime Power On Resets（裝置統計）", NormConvention, "次", false, SeagateWiki),
        new(18, "Head Health Self Assessment（磁頭健康自評）",
            "raw＝逐磁頭位元映射：每個為 1 的位元＝韌體判定該磁頭已壞（與 Storage Element Depopulation 連動；較新型號才有）",
            "以 raw 位元映射為準；normalized 不另外定義", "—", true, SeagateWiki),
        new(174, "Unexpected Power Loss（異常斷電數）",
            "未預告的斷電次數；關機前先下 standby-immediate 即不累計", NormConvention, "次", false, SeagateWiki),
        new(183, "PHY Counter Events（PHY 計數事件）",
            "SATA phy 降到低於本次上電已協商速度的次數", NormConvention, "次", false, SeagateWiki),
        new(184, "IOEDC Count（IOEDC 計數）",
            "介面與碟片之間的 IO 錯誤偵測碼命中；驅動會把它以介面 CRC 錯誤回報", NormConvention, "個", false, SeagateWiki),
        new(187, "Reported Uncorrectable Count（回報不可修正錯誤）",
            "每「回報一次」不可修正讀取就累計一次——同一個壞 LBA 讀 5 次累計 5，不是唯一磁區數", NormConvention, "個", false, SeagateWiki),
        new(188, "Command Timeout（命令逾時）",
            "Seagate 的 raw 分欄：總逾時數、超過 5 秒的、超過 7.5 秒的——各自獨立欄位",
            NormConvention, "次", false, SeagateWiki),
        new(189, "High Fly Writes（高飛寫入）",
            "磁頭飛高超出最佳高度時的寫入次數（寫入場變弱）", NormConvention, "次", false, SeagateWiki),
        new(190, "Airflow Temperature（氣流溫度）",
            "raw 打包 PCB 感測器的現值／最高／最低三個溫度", NormConvention, "°C", false, SeagateWiki),
        new(191, "Shock Sensor Counter（震動感測計數）",
            "超額限震事件（超出裝置額定，如摔落）對應的計數", NormConvention, "次", false, SeagateWiki),
        new(192, "Emergency Retract Count（緊急縮回計數）",
            "高優先載出事件（異常斷電、自由落體保護等）；與標準化統計的計法可能略有出入", NormConvention, "次", false, SeagateWiki),
        new(193, "Load-Unload Count（載入載出計數）",
            "對應 Head Load Events（裝置統計）", NormConvention, "次", false, SeagateWiki),
        new(194, "Temperature（溫度）",
            "各型號 raw 格式不同：僅現值／現值＋最高／現值＋最高＋最低都有，位元組位置與位元組序也各異——未經廠商格式確認前不解讀細部欄位", NormConvention, "°C", false, SeagateWiki),
        new(195, "ECC On The Fly Count（即時 ECC 修正數）",
            "讀回給 host 時的 ECC 修正次數；SAS 版對應讀取／驗證錯誤紀錄的「修正演算法處理總次數」", NormConvention, "個", false, SeagateWiki),
        new(197, "Pending-Sparing Count（待定磁區數）",
            "有讀取錯誤、等待重寫或重配置的唯一磁區數；host 寫入可以清掉其中項目", NormConvention, "個", false, SeagateWiki),
        new(198, "Offline Uncorrectable Sector Count（離線不可修正磁區數）",
            "多為背景掃描時發現的錯誤；部分型號會與 197 回報同一數字", NormConvention, "個", false, SeagateWiki),
        new(199, "Ultra DMA CRC Error Count（UDMA CRC 錯誤數）",
            "出廠以來的介面 CRC 錯誤總數", NormConvention, "個", false, SeagateWiki),
        new(200, "Pressure Measurement Limit（壓力量測下限）",
            "氦氣碟專屬：簡單通／不通——氦壓低到無法維持飛高時觸發（空氣碟不支援此屬性）",
            "通／不通二值", "—", true, SeagateWiki),
        new(230, "Life Curve Status（壽命曲線狀態；SSD）",
            "SSD 專屬：nominal 90 表示依設計需求可能開始節流", "nominal 90 為節流起始訊號", "—", true, SeagateWiki),
        new(231, "SSD Life Left（SSD 壽命餘量）",
            "為 Percentage Used Endurance Indicator 的反向：已用 %＝100−壽命餘量", "%", "%", true, SeagateWiki),
        new(235, "SSD Power Loss Management Life Left（斷電保護壽命）",
            "斷電保護電容隨老化的健康度追蹤（SSD 專屬）", NormConvention, "%", true, SeagateWiki),
        new(240, "Head Flight Hours（磁頭飛行時數）",
            "磁頭懸浮於介質上的時數（含載入後閒置）", NormConvention, "小時", false, SeagateWiki),
        new(241, "Lifetime Writes from Host（累計寫入）",
            "HDD 以 LBA 計；Seagate SSD 各型號混用 GiB／LBA／10MB 單位——單位不可跨型號假設", NormConvention, "LBA（SSD 單位不一）", false, SeagateWiki),
        new(242, "Lifetime Reads from Host（累計讀取）",
            "單位歧異同 241", NormConvention, "LBA（SSD 單位不一）", false, SeagateWiki),
        new(254, "Free Fall Event Count（自由落體事件數）",
            "自由落體偵測次數（每次觸發緊急縮回）", NormConvention, "次", false, SeagateWiki),
    ];

    // ── Western Digital（官方 KB Answer ID 4192；共 17 列）────────────────
    // 該 KB 只列 ID、名稱與 Pre-Failure／Advisory 分類，不描述 raw 編碼——
    // raw 欄一律如實寫「KB 未提供位元組拆解」，不借用他廠公式。

    private static readonly IReadOnlyList<SmartAttributeInfo> WdTable =
    [
        new(1, "Read Error Rate（讀取錯誤率）",
            "KB 未提供 raw 位元組拆解；以原樣計數呈現（不借用他廠公式）",
            "WD 分類＝Pre-Failure：低於門檻即近期故障徵候", "—", false, WdKb),
        new(3, "Spin-up Time（起轉時間）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Pre-Failure", "秒", false, WdKb),
        new(4, "Start/Stop Count（啟停計數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory：追蹤耗損、不直接預測故障", "次", false, WdKb),
        new(5, "Re-allocated Sector Count（重配置磁區數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Pre-Failure", "個", false, WdKb),
        new(7, "Seek Error Rate（尋道錯誤率）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "—", false, WdKb),
        new(9, "Power-on Hours Count（通電時數）",
            "KB 未提供 raw 位元組拆解（小時數為 ATA 慣例）", "WD 分類＝Advisory", "小時", false, WdKb),
        new(10, "Spin-up Retry Count（起轉重試數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "次", false, WdKb),
        new(11, "Drive Calibration Retry Count（校準重試數）",
            "KB 未提供 raw 位元組拆解；此 ID 屬 WD 自有用法", "WD 分類＝Advisory", "次", true, WdKb),
        new(12, "Drive Power Cycle Count（電源循環數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "次", false, WdKb),
        new(192, "Emergency Retract Count（緊急縮回計數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "次", false, WdKb),
        new(193, "Load/Unload Count（載入載出計數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "次", false, WdKb),
        new(194, "HDA Temperature（磁頭碟組件溫度）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "°C", false, WdKb),
        new(196, "Relocation Event Count（重配置事件數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "次", false, WdKb),
        new(197, "Current Pending Sector Count（現行待定磁區數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "個", false, WdKb),
        new(198, "Off-line Scan Uncorrectable Sector Count（離線掃描不可修正磁區數）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "個", false, WdKb),
        new(199, "Ultra DMA CRC Error Rate（UDMA CRC 錯誤率）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory（介面／排線問題的指標）", "個", false, WdKb),
        new(200, "Multi-zone Error Rate（多區域錯誤率）",
            "KB 未提供 raw 位元組拆解", "WD 分類＝Advisory", "—", false, WdKb),
    ];

    // ── Kingston（官方 MKP-306.2，SF-2000 系列；28 列，含逐位元組 raw 用法）──

    private static readonly IReadOnlyList<SmartAttributeInfo> KingstonTable =
    [
        new(1, "Raw Read Error Rate",
            "[3-0] 本次上電讀取磁區數；[6-4] 讀取錯誤數（UECC＋URAISE）",
            "10log10(BitsRead/(ReadErrors+1))；最佳 120、最差 38（BER 1.00E-12–1.66E-04 對數尺度）", "—", false, KingstonDoc),
        new(5, "Retired Block Count",
            "[3-0] 退役區塊數；[6-4] 無（0x00）",
            "100−(RBC+SBM)（SBM＝出廠備用區塊數）；最佳 100、最差 1", "個", false, KingstonDoc),
        new(9, "Power-On Hours (POH)",
            "[3-0] 通電小時總數；[6-4] 自上次整點更新以來的毫秒數",
            "100−(POH+HPPT/10)（HPPT＝小時數的千分之一轉換）；最差 1", "小時", false, KingstonDoc),
        new(12, "Device Power Cycle Count",
            "[3-0] 累計電源循環（PCC）；[6-4] 無（0x00）",
            "100−PCC/1024；最佳 100、最差 1", "次", false, KingstonDoc),
        new(13, "Soft Read Error Rate",
            "[3-0] 本次上電讀取磁區數；[6-4] 可修正 ECC（CECC）數",
            "10log10(BitsRead/SoftReadErrors+1)；最佳 120、最差 38；超過 10^10 才計算，10^12 歸零重算", "—", false, KingstonDoc),
        new(100, "Gigabytes Erased（抹除 GB 數）",
            "[3-0] 抹除的 GB 數；[6-4] 無（0x00）", "文件標 Normalized＝None——以 raw 為準", "GB", true, KingstonDoc),
        new(170, "Reserve Block Count（保留區塊數）",
            "[2-0] 保留區塊數；[6-4] 無（0x00）",
            "文件標 Normalized＝None；與屬性 5 相關（所有在役區塊含備用）", "個", true, KingstonDoc),
        new(171, "Program Fail Count",
            "[3-0] 程式化錯誤數；[6-4] 無（0x00）", "文件標 Normalized＝None（與屬性 181 相同）", "個", true, KingstonDoc),
        new(172, "Erase Fail Count",
            "[3-0] 抹除錯誤數；[6-4] 無（0x00）", "文件標 Normalized＝None（與屬性 182 相同）", "個", true, KingstonDoc),
        new(174, "Unexpected Power Loss",
            "[3-0] 異常斷電事件數；[6-4] 無（0x00）", "文件標 Normalized＝None", "次", true, KingstonDoc),
        new(177, "Wear Range Delta（耗損域差）",
            "[3-0] 耗損域差；[6-4] 無（0x00）",
            "normalized 無；raw＝(MW−LW)/MRW×100——最磨與最省區塊抹寫次數差對額定壽命的百分比", "—", true, KingstonDoc),
        new(181, "Program Fail Count",
            "[3-0] 程式化錯誤數；[6-4] 無（0x00）", "文件標 Normalized＝None（與屬性 171 相同）", "個", true, KingstonDoc),
        new(182, "Erase Fail Count",
            "[3-0] 抹除錯誤數；[6-4] 無（0x00）", "文件標 Normalized＝None（與屬性 172 相同）", "個", true, KingstonDoc),
        new(184, "Reported IO Error Detection Code Errors",
            "[3-0] 累計 IOEDC 錯誤數；[6-4] 無（0x00）",
            "100−IOEDCErrors；最佳 100、最差 1", "個", false, KingstonDoc),
        new(187, "Reported Uncorrectable Errors (URAISE)",
            "[1-0] 累計 URAISE 錯誤數（上限 65535）；[6-2] 無（0x00）",
            "100−URAISE；最佳 100、最差 1", "個", false, KingstonDoc),
        new(194, "Temperature",
            "[1-0] 目前溫度 °C（感測器）；[3-2] 生命期最高；[5-4] 生命期最低——注意與 Samsung 的最低／最高字組次序不同",
            "normalized＝目前溫度 °C", "°C", false, KingstonDoc),
        new(195, "ECC On-the-Fly Error Count",
            "[3-0] 本次上電讀取磁區數；[6-4] UECC 數",
            "10log10(BitsRead/ECCOnTheFlyErrors+1)；最佳 120、最差 38", "—", false, KingstonDoc),
        new(196, "Relocation Event",
            "[3-0] 退役區塊數；[6-4] 無（0x00）",
            "100−(RBC+MBC)；MBC＝Grown 的退役區塊、MRE＝映射可承受最大重配置數；最佳 100、最差 1", "次", false, KingstonDoc),
        new(198, "Uncorrectable Sector Count",
            "[3-0] 本次上電 URAISE 數；[6-4] 讀取磁區數",
            "10log10(BitsRead/URAISE+1)；最佳 120；超過 10^10 才計算", "個", false, KingstonDoc),
        new(199, "SATA R Errors Error Count",
            "[3-0] 本次上電目前 RErrors 數；[6-4] 無（0x00）",
            "200−(RErrors×SampleSize/NumSectorsTransferred)；樣本達 8000 前 normalized 恆 200", "個", false, KingstonDoc),
        new(201, "Uncorrectable Soft Read Error Rate (UECC)",
            "[3-0] 本次上電讀取磁區數；[6-4] UECC 數",
            "10log10(BitsRead/UECC+1)；最佳 120、最差 38；計算門檻與 195 相同", "—", false, KingstonDoc),
        new(204, "Soft ECC Correction Rate (UECC)",
            "[3-0] 本次上電讀取磁區數；[6-4] Soft ECC 修正數",
            "10log10(BitsRead/UECC+1)；與 195／201 同式", "—", false, KingstonDoc),
        new(230, "Drive Life Protection Status",
            "raw 恆 None（0x000）——語意在 normalized",
            "100＝不需啟動保護；90＝Life Curve 保護機制現正作用中", "—", true, KingstonDoc),
        new(231, "SSD Life Left",
            "[0] 0＝壽命由 PE 循環驅動、1＝由備用區塊驅動；[6-1] 無（0x00）",
            "MIN(MAX(termA,10),termB)（termA＝(Unused/Rated)×100）；100＝滿壽命、1＝可用區塊不足以維持 SSD 運作", "%", true, KingstonDoc),
        new(232, "Available Reserved Space",
            "[3-0] (RB×BPR)/1024^3（GB）；[6-4] 無（0x00）；RB／BPR 同屬性 170",
            "文件標 Normalized＝None；以 1 GB 解析度隨寫入遞減至 (n−1)", "GB", true, KingstonDoc),
        new(233, "Power Fail Backup Health",
            "[1-0] 最近一次 SuperCap 測試放電時間（ms）；[6-2] 無（0x00）",
            "min(100, (100−(tCapDischgMs−10)/(tCapDischgTimeouts, tcapDischgMs−10)))；100＝SuperCap 測試通過、1＝hold-up 能力已劣化", "—", true, KingstonDoc),
        new(241, "Lifetime Writes from Host System",
            "[3-0] 寫入 GB 數（1 GB 解析度）；[6-4] 無（0x00）",
            "文件標 Normalized＝None；舊韌體（<520ABF0）在 64 GB 邊界前不更新", "GB", false, KingstonDoc),
        new(242, "Lifetime Reads to Host System",
            "[3-0] 讀取位元組數（64 GB 解析度）；[6-4] 無（0x00）",
            "文件標 Normalized＝None；數到 64 GB 才加 1", "GB", false, KingstonDoc),
    ];

    // ── Micron（drivedb.h〈Crucial/Micron Client SSDs〉，對照官方 TN-FD-22；24 列）──
    // drivedb.h 未收錄語意的 ID（223/249/251–254 標 Unkn_CrucialMicron_Attr）一律不收。

    private static readonly IReadOnlyList<SmartAttributeInfo> MicronTable =
    [
        new(5, "Reallocate NAND Blk Cnt（重配置 NAND 區塊數）",
            "48 位元單一計數（raw48）", NormConvention, "個", false, MicronDb),
        new(9, "Power On Hours（通電時間）",
            "raw24(raw8)：位元組 0–2 為小時數", NormConvention, "小時", false, MicronDb),
        new(12, "Power Cycle Count（電源循環數）",
            "48 位元單一計數（raw48）", NormConvention, "次", false, MicronDb),
        new(170, "Reserved Block Count（已用保留區塊數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, MicronDb),
        new(171, "Program Fail Count（程式化失敗數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, MicronDb),
        new(172, "Erase Fail Count（抹除失敗數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, MicronDb),
        new(173, "Ave Block-Erase Count（區塊平均抹寫次數）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, MicronDb),
        new(174, "Unexpected Power Loss Ct（異常斷電數）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, MicronDb),
        new(180, "Unused Reserve NAND Blk（未用保留區塊數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, MicronDb),
        new(183, "SATA Interface Downshift（SATA 介面降速次數）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, MicronDb),
        new(184, "Error Correction Count（錯誤修正數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, MicronDb),
        new(187, "Reported Uncorrect（回報不可修正錯誤）",
            "48 位元單一計數（raw48）", NormConvention, "個", false, MicronDb),
        new(194, "Temperature_Celsius（溫度）",
            "tempminmax：raw 三個小端 16 位元字組——字組 0＝現值，其後為歷史最低／最高（位元組層級檢核見 atacmds.cpp）",
            NormConvention, "°C", false, $"{MicronDb}；格式見 {MicronTempFmt}"),
        new(195, "Cumulative Corrected ECC（累計已修正 ECC）",
            "48 位元單一計數（raw48）", NormConvention, "個", false, MicronDb),
        new(196, "Reallocated Event Count（重配置事件數）",
            "raw16(raw16)：以 16 位元字組為單位的事件數", NormConvention, "次", false, MicronDb),
        new(197, "Current Pending ECC Cnt（現行待定 ECC 磁區數）",
            "48 位元單一計數（raw48）；MX500 上可能 0↔1 翻轉（drivedb.h 註解，ticket #1227）",
            NormConvention, "個", false, MicronDb),
        new(198, "Offline Uncorrectable（離線不可修正）",
            "48 位元單一計數（raw48）", NormConvention, "個", false, MicronDb),
        new(199, "UDMA CRC Error Count（UDMA CRC 錯誤數）",
            "48 位元單一計數（raw48）", NormConvention, "個", false, MicronDb),
        new(202, "Percent Lifetime Remain（剩餘壽命百分比）",
            "48 位元單一計數（raw48）",
            "drivedb.h 註解：normalized＝max(100−raw, 0)；raw＝已用壽命百分比", "%", false, MicronDb),
        new(206, "Write Error Rate（寫入錯誤率）",
            "48 位元單一計數（raw48）", NormConvention, "—", true, MicronDb),
        new(210, "Success RAIN Recovery Cnt（RAIN 成功復原數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, MicronDb),
        new(246, "Total LBAs Written（累計寫入 LBA）",
            "48 位元單一計數（raw48）", NormConvention, "LBA", false, MicronDb),
        new(247, "Host Program Page Count（主機程式化頁數）",
            "48 位元單一計數（raw48）", NormConvention, "頁", true, MicronDb),
        new(248, "FTL Program Page Count（FTL 程式化頁數）",
            "48 位元單一計數（raw48）", NormConvention, "頁", true, MicronDb),
    ];

    // ── SanDisk（drivedb.h〈Marvell based SanDisk SSDs〉；20 列）──────────
    // drivedb.h 標 Unknown_Marvell_Attr 的 170／243 不收——名稱未知就是未知。

    private static readonly IReadOnlyList<SmartAttributeInfo> SanDiskTable =
    [
        new(165, "Total Write/Erase Count（總抹寫次數）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, SanDiskDb),
        new(166, "Min W/E Cycle（最低抹寫循環）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, SanDiskDb),
        new(167, "Min Bad Block/Die（每 Die 最少壞塊）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, SanDiskDb),
        new(168, "Maximum Erase Cycle（最高抹寫循環）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, SanDiskDb),
        new(169, "Total Bad Block（總壞塊數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, SanDiskDb),
        new(171, "Program Fail Count（程式化失敗數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, SanDiskDb),
        new(172, "Erase Fail Count（抹除失敗數）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, SanDiskDb),
        new(173, "Avg Write/Erase Count（平均抹寫次數）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, SanDiskDb),
        new(174, "Unexpected Power Loss Ct（異常斷電數）",
            "48 位元單一計數（raw48）", NormConvention, "次", true, SanDiskDb),
        new(199, "SATA CRC Error（SATA CRC 錯誤）",
            "48 位元單一計數（raw48）", NormConvention, "個", false, SanDiskDb),
        new(201, "Lifetime Remaining%（壽命剩餘百分比）",
            "48 位元單一計數（raw48）", NormConvention, "%", true, SanDiskDb),
        new(212, "SATA PHY Error（SATA PHY 錯誤）",
            "48 位元單一計數（raw48）", NormConvention, "個", true, SanDiskDb),
        new(230, "Perc Write/Erase Count（抹寫循環已用百分比）",
            "raw16：16 位元計數", NormConvention, "%", true, SanDiskDb),
        new(232, "Perc Avail Resrvd Space（可用保留空間百分比）",
            "48 位元單一計數（raw48）", NormConvention, "%", true, SanDiskDb),
        new(233, "Total NAND Writes GiB（NAND 累計寫入 GiB）",
            "48 位元單一計數（raw48）——單位 GiB（含 host 以外的內部寫入）", NormConvention, "GiB", true, SanDiskDb),
        new(234, "Perc Write/Erase Ct BC（抹寫循環已用百分比·BC）",
            "48 位元單一計數（raw48）", NormConvention, "%", true, SanDiskDb),
        new(241, "Total Writes GiB（累計寫入 GiB）",
            "48 位元單一計數（raw48）——單位 GiB（非 LBA）", NormConvention, "GiB", false, SanDiskDb),
        new(242, "Total Reads GiB（累計讀取 GiB）",
            "48 位元單一計數（raw48）——單位 GiB（非 LBA）", NormConvention, "GiB", false, SanDiskDb),
        new(244, "Thermal Throttle（熱節流）",
            "48 位元單一計數（raw48）", NormConvention, "—", true, SanDiskDb),
        new(249, "TLC NAND GB Writes（TLC NAND 寫入 GB）",
            "48 位元單一計數（raw48）", NormConvention, "GB", true, SanDiskDb),
    ];

    // ── 查詢索引（懶建——表本身是不可變清單，索引只是 Dictionary 視圖）────

    private static readonly Dictionary<byte, SmartAttributeInfo> SamsungIndex = Index(SamsungTable);
    private static readonly Dictionary<byte, SmartAttributeInfo> SeagateIndex = Index(SeagateTable);
    private static readonly Dictionary<byte, SmartAttributeInfo> WdIndex = Index(WdTable);
    private static readonly Dictionary<byte, SmartAttributeInfo> KingstonIndex = Index(KingstonTable);
    private static readonly Dictionary<byte, SmartAttributeInfo> MicronIndex = Index(MicronTable);
    private static readonly Dictionary<byte, SmartAttributeInfo> SanDiskIndex = Index(SanDiskTable);

    private static Dictionary<byte, SmartAttributeInfo> Index(IReadOnlyList<SmartAttributeInfo> table) =>
        table.ToDictionary(a => a.Id, a => a);
}
