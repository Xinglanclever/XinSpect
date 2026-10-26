using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 事件記錄裡某個（供應者 × 事件 ID）組合的查詢結果。
/// </summary>
/// <param name="Count">時間窗內符合的事件數；<paramref name="Error"/> 或 <paramref name="MissingChannel"/> 非空時無意義。</param>
/// <param name="Truncated">達到讀取上限就停——那時 Count 是「至少這麼多」，不是總數。</param>
/// <param name="Error">查詢失敗的原因（權限、記錄檔損毀…）。</param>
/// <param name="MissingChannel">頻道不存在（例如該功能的記錄檔未啟用）。</param>
public sealed record EvtProbeResult(int Count, bool Truncated, string? Error, string? MissingChannel);

/// <summary>
/// Windows 事件 ID 的事實（U10-EV-0001 ～ U10-EV-0020，二十把鍵）。
///
/// <para>
/// <b>這一支回答的是「這台機器的事件記錄裡，有沒有出現過這些已被稽核過的事件」</b>，
/// 不是「機器有沒有問題」。事件 ID 的語意全部來自 <c>docs/ULTIMATE-10000/AUDIT-pending.md</c>
/// 的逐條稽核（每列都附出處），本服務<b>不新增任何沒有出處的 ID 語意</b>。
/// </para>
///
/// <para>
/// <b>三種「沒有」必須分開陳述，這是整支服務的重點：</b>
/// </para>
/// <list type="number">
/// <item><b>查到了，0 筆</b>——這是量測結果（事件記錄裡真的沒有），標 Present 並帶數值 0。</item>
/// <item><b>頻道不存在</b>（該功能的記錄檔未啟用）——NotSupported，附頻道名。</item>
/// <item><b>查詢失敗</b>（權限、記錄檔損毀）——ReadError，附原因。</item>
/// </list>
/// <para>
/// 另外還有一種第四態：<b>ID 本身尚未定案</b>。稽核清單裡有七列的 ID 至今沒有公開出處
/// （Kernel-Storage 512/514、StorageSpaces/SenseData、WHEA 47、Kernel-Boot 28、
/// Chkdsk 供應者、Ntfs 41/42、熱節流通知）。<b>那七列不查、不猜、不命名</b>，
/// 標 NotApplicable 並把「缺的是什麼文件」寫進理由——「查不到」與「不存在」是兩件事。
/// </para>
///
/// <para>
/// <b>唯讀、零特權、零網路：</b>只走 <see cref="EventLogReader"/> 的查詢，不寫入、不清理、
/// 不匯出記錄檔。時間窗預設 30 天，逐查詢有讀取上限（到達上限標「至少」）。
/// </para>
/// </summary>
public static class EventIdEvidenceFactsService
{
    private const string Category = "系統與軟體";

    /// <summary>時間窗：只看最近 30 天。更早的事件屬於「歷史」，不是「這台機器現在的狀態」。</summary>
    public const int WindowDays = 30;

    /// <summary>單一查詢的讀取上限——到達就停並標「至少」，不讓一個吵雜的供應者拖垮收集。</summary>
    private const int ReadCap = 2000;

    /// <summary>待核來源彙總鍵（U10-EV-0020）。</summary>
    public const string PendingKey = "os.ev.pending.sources";

    // ── 逐列的事實鍵 ──────────────────────────────────────────────────────
    // 每一個都寫成 const：事實鍵掃描器只認字面值（目錄≡掃描的守門靠它），
    // 而這些鍵同時是「目錄裡的一把鍵」與「這張表的列名」——兩份清單不許各寫一次。

    public const string KernelBootExtraKey = "os.ev.kernelboot.extra";
    public const string TimeChangeKey = "os.ev.timechange";
    public const string ModernStandbyWakeKey = "os.ev.modernstandby.wake";
    public const string ModernStandbyPhaseKey = "os.ev.modernstandby.phase";
    public const string WheaInternalKey = "os.ev.whea.internal";
    public const string WheaPcieKey = "os.ev.whea.pcie";
    public const string WheaMiscKey = "os.ev.whea.misc";
    public const string ChkdskKey = "os.ev.chkdsk";
    public const string NtfsExtraKey = "os.ev.ntfs.extra";
    public const string KStorageKey = "os.ev.kstorage";
    public const string UsbHubKey = "os.ev.usb.hub";
    public const string DisplayTdr2Key = "os.ev.display.tdr2";
    public const string UserPnpInstallKey = "os.ev.userpnp.install";
    public const string DsmKey = "os.ev.dsm";
    public const string PowerSourceKey = "os.ev.power.source";
    public const string KppParkingKey = "os.ev.kpp.parking";
    public const string ThermalNotifyKey = "os.ev.thermal.notify";
    public const string WininitChkdskKey = "os.ev.wininit.chkdsk";
    public const string SpacesHealthKey = "os.ev.spaces.health";

    /// <summary>一列的狀態。</summary>
    public enum RowState
    {
        /// <summary>ID 與語意都有出處，查得到就報數。</summary>
        Confirmed,

        /// <summary>部分子項仍未定案：查已定案的那些，並在值裡點名缺哪一個。</summary>
        Partial,

        /// <summary>ID 沒有公開出處——不查、不命名。</summary>
        Pending,

        /// <summary>ID 取自供應者的 manifest（標準事件），但本專案未逐條稽核語意。</summary>
        ManifestOnly,

        /// <summary>不是事件記錄裡的事件（例如 ETW 追蹤供應者）。</summary>
        NotInEventLog,
    }

    /// <summary>一列的定義。語意與出處逐字取自稽核文件，不在這裡改寫。</summary>
    /// <param name="Key">事實鍵。</param>
    /// <param name="Title">中文標題。</param>
    /// <param name="Channel">事件記錄頻道；<see cref="RowState.Pending"/> 與 <see cref="RowState.NotInEventLog"/> 時可為空。</param>
    /// <param name="Provider">供應者名稱；同上。</param>
    /// <param name="Ids">要查的事件 ID；同上。</param>
    /// <param name="Meaning">語意（稽核結論）。</param>
    /// <param name="Source">出處。</param>
    /// <param name="State">狀態。</param>
    /// <param name="Gap">仍缺什麼（<see cref="RowState.Pending"/> 與 <see cref="RowState.Partial"/> 必填）。</param>
    public sealed record Row(
        string Key, string Title, string Channel, string Provider, int[] Ids,
        string Meaning, string Source, RowState State, string? Gap);

    private const string Audited = "docs/ULTIMATE-10000/AUDIT-pending.md（EV 組逐條稽核）";

    /// <summary>
    /// 二十列的定義。<b>這一張表就是本服務的全部知識</b>——沒有列在這裡的事件 ID 不會被查、
    /// 也不會被命名。
    /// </summary>
    public static IReadOnlyList<Row> Rows { get; } =
    [
        new(KernelBootExtraKey, "開機類型補充事件",
            "System", "Microsoft-Windows-Kernel-Boot", [25, 27],
            "25＝開機功能表原則（0x1 標準／0x2 Legacy）、27＝開機類型（0x0 冷開機／0x1 混合快速啟動／0x2 睡眠喚醒）",
            Audited + "；Microsoft-Windows-Kernel-Boot manifest",
            RowState.Partial, "28 在官方 manifest 只是未命名的 task_028（Informational），語意未定案——不查、不命名"),

        new(TimeChangeKey, "系統時間變更事件",
            "System", "Microsoft-Windows-Kernel-General", [12, 13],
            "12＝作業系統啟動時間戳記、13＝系統時間被變更（時鐘證據鏈的兩個端點）",
            "Microsoft-Windows-Kernel-General manifest（12＝系統啟動時間、13＝系統時間變更）——**本專案未逐條稽核語意**",
            RowState.ManifestOnly, null),

        new(ModernStandbyWakeKey, "現代待機退出",
            "System", "Microsoft-Windows-Kernel-Power", [109],
            "109＝ShutdownAction（關機動作）。**不是**現代待機喚醒——退出事件是 507",
            Audited + "（EV-0003 勘誤：Geoff Chappell Kernel-Power 頁＋Kernel-Power.xml 109 template=ShutdownActionArgs）",
            RowState.Confirmed, null),

        new(ModernStandbyPhaseKey, "現代待機階段轉換",
            "System", "Microsoft-Windows-Kernel-Power", [506, 507],
            "506＝進入 connected standby（CsEnterReason）、507＝退出 connected standby（CsExitReason）",
            Audited + "（EV-0004 勘誤：Geoff Chappell 全文＋Kernel-Power.xml template 名）",
            RowState.Partial, "508 的語意已更正為 DynamicTickDisabled（週期性滴答受限），不是階段轉換——本列不把它算進階段事件"),

        new(WheaInternalKey, "WHEA 致命硬體錯誤",
            "System", "Microsoft-Windows-WHEA-Logger", [1],
            "1＝「A fatal hardware error has occurred」（資料段含 WHEA_ERROR_RECORD；Param1＝錯誤源）。**不是**「內部錯誤」",
            Audited + "（EV-0005 勘誤：Kernel-WHEA.xml event 1＋微軟問答事件全文）",
            RowState.Confirmed, null),

        new(WheaPcieKey, "WHEA 已修正硬體錯誤",
            "System", "Microsoft-Windows-WHEA-Logger", [23],
            "23＝通用的「A corrected hardware error has occurred」。PCIe 專屬的修正錯誤是 17、記憶體是 19——本列只查 23，不代填那兩個",
            Audited + "（EV-0006 語意更正：Kernel-WHEA.xml＋多方事件實錄；23 屬 Win11 新增）",
            RowState.Confirmed, null),

        new(WheaMiscKey, "WHEA 通用硬體錯誤（46）",
            "System", "Microsoft-Windows-WHEA-Logger", [46],
            "46＝「A hardware error has occurred. A record describing the condition is contained in the data section」（帶 WHEA_ERROR_RECORD，常見記憶體 MCE）",
            Audited + "（EV-0007 部分定案：微軟問答 46 全文＋Kernel-WHEA.xml）",
            RowState.Partial, "47 無公開記載、18990 manifest 也未收錄——不查、不命名"),

        new(ChkdskKey, "開機自動檢查（chkdsk）結果",
            "Application", "Wininit", [1001],
            "開機 autocheck 的 chkdsk 完整輸出承載於 Application 記錄的 Wininit 事件 1001（多階段輸出同一個事件）",
            Audited + "（EV-0008／EV-0018：4iT KB、TenForums 教學、Ask Leo 三者一致）",
            RowState.Partial, "「Chkdsk」供應者自己的事件 ID 沒有公開清單——本列只查 Wininit 1001，不代填"),

        new(NtfsExtraKey, "NTFS 結構損毀事件",
            "System", "Microsoft-Windows-Ntfs", [55, 98, 130, 131, 132, 133],
            "55＝檔案系統結構損毀／已修復、98＝索引結構發現損毀、130–133＝損毀相關群",
            Audited + "（EV-0009 部分定案：Microsoft-Windows-Ntfs.xml＋SparkLogs 參考表）",
            RowState.Partial, "41／42 查無公開記載——不查、不命名"),

        new(KStorageKey, "Kernel-Storage IO 失敗事件",
            "", "", [],
            "（未定案）公開正典的磁碟 IO 失敗事件是「disk」供應者的 51／153／154／129，但那不是本列原本指的 Kernel-Storage 512／514",
            Audited + "（EV-0010 仍待核）",
            RowState.Pending, "Kernel-Storage 512／514 無任何公開記載——需要 Microsoft-Windows-Kernel-Storage manifest；不查、不命名"),

        new(UsbHubKey, "USB 集線器重置事件",
            "", "", [],
            "（不在事件記錄裡）USBHUB3／USBPORT 是 ETW 追蹤供應者：USBHUB3 120／121／122＝HubResetStart／Stop／完成，USBPORT 103–107＝USBUSER_OP_RAW_RESET_PORT 作業流",
            Audited + "（EV-0011：repnz/etw-providers-docs＋微軟 ETW/NetMon 案例）",
            RowState.NotInEventLog, "ETW 追蹤需要即時會話與 USB ETW parser（NetMon），不是事件記錄查詢能做的事——本版不實作，如實標"),

        new(DisplayTdr2Key, "顯示驅動逾時復原（TDR）",
            "System", "Display", [4101, 4109],
            "4101＝驅動停止回應後已成功復原（TDR 已恢復）、4109＝應用程式被擋住存取圖形硬體（TDR 後果）。兩者常併發，但**不是同一件事**",
            Audited + "（EV-0012 勘誤：Reddit 事件全文對照＋微軟問答）",
            RowState.Confirmed, null),

        new(UserPnpInstallKey, "驅動安裝結果事件",
            "System", "Microsoft-Windows-UserPnp", [20001, 20003],
            "20001＝驅動安裝流程完成、20003＝驅動安裝失敗（安裝史的權威來源）",
            "Microsoft-Windows-UserPnp manifest（20001＝安裝完成、20003＝安裝失敗）——**本專案未逐條稽核語意**",
            RowState.ManifestOnly, null),

        new(DsmKey, "裝置安裝管理事件",
            "Microsoft-Windows-DeviceSetupManager/Admin", "Microsoft-Windows-DeviceSetupManager", [112],
            "112＝裝置首次連接或接到新埠時的記錄（USB 取證常用）；同供應者 131＝中詮資料暫存失敗、200–202＝WMIS 連線警告",
            Audited + "（EV-0014：Swift Forensics、windowsir、微軟問答）",
            RowState.Confirmed, null),

        new(PowerSourceKey, "電源來源變更",
            "System", "Microsoft-Windows-Kernel-Power", [105],
            "105＝電源來源變更（AC／DC 切換，1703 起進 System 通道）。106 是同一個狀態的 Rundown 事件，只在診斷通道——本列不查 106",
            Audited + "（EV-0015：Geoff Chappell Kernel-Power 頁＋Kernel-Power.xml AcDcStateChangeArgs）",
            RowState.Confirmed, null),

        new(KppParkingKey, "處理器效能網域資訊",
            "System", "Microsoft-Windows-Kernel-Processor-Power", [128],
            "128＝PepPerfDomainInfo（PEP 效能網域資訊，Platform Extension Plugin 電源管理遙測）。**不是**核心停泊——停泊狀態在 Parking 相關 ETW 事件",
            Audited + "（EV-0016 勘誤：repnz/etw-providers-docs Kernel-Processor-Power.xml）",
            RowState.Confirmed, null),

        new(ThermalNotifyKey, "熱區列舉事件",
            "System", "Microsoft-Windows-Kernel-Power", [125],
            "125＝ThermalZoneEnumerated（熱區列舉，6.2+ 起取代 89）",
            Audited + "（EV-0017 部分定案：Geoff Chappell Event 125＋Kernel-Power.xml ThermalZoneEnumerated125Args）",
            RowState.Partial, "「熱節流通知」的特定事件 ID 沒有公開清單——不查、不命名"),

        new(WininitChkdskKey, "chkdsk 完整輸出（同一事件的第二問）",
            "Application", "Wininit", [1001],
            "與 os.ev.chkdsk 是**同一個事件**（Wininit 1001）：本列問的是「有沒有另一個『後續』事件承載完整輸出」——答案是沒有，完整輸出就在 1001 的正文裡",
            Audited + "（EV-0018：TenForums 截例＋4iT KB）",
            RowState.Confirmed, null),

        new(SpacesHealthKey, "儲存空間集區健康事件",
            "", "", [],
            "（未定案）StorageSpaces-Driver manifest 的 200–208／300–313／400+ 全是未命名的 task_NNN 佔位，沒有語意；也沒有名為 SenseData 的供應者（只有 Defender 的 SENSE）",
            Audited + "（EV-0019 仍待核）",
            RowState.Pending, "需要 Win11 StorageSpaces-Driver／SpaceManager 的訊息 DLL 或新版 manifest——不查、不命名"),
    ];

    /// <summary>查詢接縫：頻道＋供應者＋ID 清單＋起點 → 結果。測試注入假件即可完全離線。</summary>
    public delegate EvtProbeResult EvtProbe(string channel, string provider, int[] ids, DateTimeOffset since);

    /// <summary>收集這二十把鍵。時間窗固定 <see cref="WindowDays"/> 天。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, EvtProbe? probe = null)
    {
        var since = at.AddDays(-WindowDays);
        var run = probe ?? DefaultProbe;
        var facts = new List<HardwareFact>(Rows.Count + 1);

        foreach (var row in Rows)
        {
            facts.Add(row.State switch
            {
                RowState.Pending => new HardwareFact(row.Key, Category, row.Title, "", "", row.Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "**事件 ID 尚未定案**：" + row.Gap + "。本服務不查、不猜、不命名——"
                    + "「查不到」與「不存在」是兩件事，把未定案的 ID 當成 0 筆會讓它看起來像「沒發生過」"),

                RowState.NotInEventLog => new HardwareFact(row.Key, Category, row.Title, row.Meaning, "", row.Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "**不在事件記錄裡**：" + row.Gap),

                _ => Query(at, run, since, row),
            });
        }

        // 待核來源彙總（U10-EV-0020）：把「還缺哪些文件」變成一個看得見的數字。
        var pending = Rows.Where(r => r.State is RowState.Pending or RowState.Partial)
            .Select(r => r.Key).ToList();
        facts.Add(new HardwareFact(PendingKey, Category, "事件 ID 待核清單",
            $"{pending.Count} 列仍缺出處或只部分定案：{string.Join("、", pending)}。"
            + "這個數字回答的是「本專案的知識還缺多少」，不是「機器有多少問題」。",
            "列", Audited, FactTrustLevel.Derived, false, at, pending.Count));

        return facts;
    }

    private static HardwareFact Query(DateTimeOffset at, EvtProbe probe, DateTimeOffset since, Row row)
    {
        EvtProbeResult result;
        try { result = probe(row.Channel, row.Provider, row.Ids, since); }
        catch (Exception ex)
        {
            return new HardwareFact(row.Key, Category, row.Title, "", "筆", row.Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.ReadError,
                $"事件記錄查詢失敗（{ex.Message}）——讀不到不等於沒發生");
        }

        if (result.MissingChannel is { Length: > 0 } missing)
            return new HardwareFact(row.Key, Category, row.Title, "", "筆", row.Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotSupported,
                $"頻道「{row.Channel}」不存在或未啟用（{missing}）——"
                + "該功能的記錄檔沒開，所以「查不到」不代表「沒發生過」");

        if (result.Error is { Length: > 0 } error)
            return new HardwareFact(row.Key, Category, row.Title, "", "筆", row.Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.ReadError,
                $"查詢失敗（{error}）——讀不到不等於沒發生");

        string ids = string.Join("／", row.Ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        string countText = result.Truncated
            ? $"至少 {result.Count} 筆（達到單次讀取上限 {ReadCap} 就停，不是總數）"
            : $"{result.Count} 筆";
        string gap = row.Gap is { Length: > 0 } g ? "仍缺：" + g + "。" : "";
        if (row.State == RowState.ManifestOnly)
            gap = "**這一列的語意取自供應者的 manifest，本專案沒有逐條稽核**——"
                + "ID 是標準事件，但「它到底代表什麼」沒有經過本專案的證據流程。" + gap;

        return new HardwareFact(row.Key, Category, row.Title,
            $"最近 {WindowDays} 天：{countText}。事件 ID {ids}（{row.Provider}）；{row.Meaning}。{gap}"
            + "界線：這裡只陳述「事件記錄裡有沒有出現」，不判斷嚴重程度、也不建因果。",
            "筆", row.Source, FactTrustLevel.Reported, false, at, result.Count,
            result.Truncated ? FactAvailability.Present : FactAvailability.Present);
    }

    /// <summary>
    /// 預設查詢：<see cref="EventLogQuery"/> 走 XPath，頻道不存在與查詢失敗分開回報。
    /// 唯讀——不寫入、不清理、不匯出。
    /// </summary>
    public static EvtProbeResult DefaultProbe(string channel, string provider, int[] ids, DateTimeOffset since)
    {
        if (string.IsNullOrEmpty(channel) || ids.Length == 0)
            return new EvtProbeResult(0, false, "沒有可查的頻道或事件 ID", null);

        string idFilter = string.Join(" or ", ids.Select(i =>
            "EventID=" + i.ToString(CultureInfo.InvariantCulture)));
        string xpath = "*[System[Provider[@Name='" + provider + "'] and (" + idFilter + ")"
                     + " and TimeCreated[@SystemTime>='"
                     + since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                     + "']]]";

        try
        {
            var query = new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < ReadCap)
            {
                using var record = reader.ReadEvent();
                if (record is null) return new EvtProbeResult(count, false, null, null);
                count++;
            }
            return new EvtProbeResult(count, true, null, null);
        }
        catch (EventLogNotFoundException ex)
        {
            return new EvtProbeResult(0, false, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new EvtProbeResult(0, false, ex.Message, null);
        }
    }
}
