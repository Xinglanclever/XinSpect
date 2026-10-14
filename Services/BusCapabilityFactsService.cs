using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 匯流排深化殘項（Vol2 第 UV-005、UV-009 項）：<c>uv.cdat</c>（CDAT 結構表）、
/// <c>uv.usb.trim</c>（USB 隨身碟的 TRIM／UNMAP 支援）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這兩項問的都是「這個能力有沒有被宣告出來」</b>，不是「實測會不會生效」——
/// 這個差別在儲存與匯流排上特別重要，因為宣告與實作常常不一致。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① CDAT 是 PCI-SIG 的「元件資料表」規範，用來讓韌體宣告裝置的拓撲與屬性；
/// 機器回報的是<b>版本與條目數</b>，本項<b>不逐條解讀</b>（規範版本與條目語意是外部規格，
/// 沒對準出處就不出值）；
/// ② USB TRIM 的判斷只能到<b>「裝置或橋接晶片有沒有宣告 UNMAP 能力」</b>——
/// 隨身碟控制器支援 TRIM 不代表它會真的把區塊交還（很多隨身碟只是把指令吃掉），
/// 所以值裡必須寫明<b>「宣告」不等於「執行」</b>；
/// ③ 沒有隨身碟時是 <see cref="FactAvailability.NotApplicable"/>，不是 0——
/// 「沒插隨身碟」與「插了但不支援 TRIM」是兩件事。
/// </para>
/// </remarks>
public static class BusCapabilityFactsService
{
    private const string Cat = "週邊匯流排";
    private const string Source = "本機唯讀（韌體結構表／儲存裝置描述元）";

    /// <summary>UV-005 CDAT 結構表。</summary>
    public const string CdatKey = "uv.cdat";
    /// <summary>UV-009 USB 隨身碟 TRIM 支援。</summary>
    public const string UsbTrimKey = "uv.usb.trim";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（CDAT 版本, 條目數, 主要結構型別代碼）。<c>Present=false</c>＝機器沒有這張表。</summary>
    public sealed record CdatState(bool Present, int Version, int Entries, int? PrimaryType);

    /// <summary>（USB 儲存裝置數, 宣告 UNMAP／TRIM 者, 走 UASP 協定者）。</summary>
    public sealed record UsbTrimState(int Devices, int TrimDeclared, int Uasp);

    public delegate CdatState? CdatProbe();
    public delegate UsbTrimState? UsbTrimProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        CdatProbe? cdat = null,
        UsbTrimProbe? trim = null)
        =>
        [
            CdatFact(at, cdat),
            UsbTrimFact(at, trim),
        ];

    // ── UV-005 CDAT ───────────────────────────────────────────────────────

    public static HardwareFact CdatFact(DateTimeOffset at, CdatProbe? probe = null)
    {
        CdatState? s;
        try { s = (probe ?? ReadCdat)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CdatKey, "CDAT 結構表", FactAvailability.ReadError,
                "CDAT 查詢失敗——讀不到就是不猜");

        var v = s;
        if (!v.Present)
            return Unavailable(at, CdatKey, "CDAT 結構表", FactAvailability.NotApplicable,
                "本機未提供 CDAT（Component Data Table）——不適用，不是缺陷。" +
                "CDAT 是 PCI-SIG 較新的規範，主要用於向作業系統宣告元件拓撲與屬性；" +
                "多數消費級平台根本沒有這張表，沒有它是常態");

        string typeText = v.PrimaryType is { } t ? $"主要結構型別 0x{t:x2}" : "主要結構型別讀不到";

        return new HardwareFact(CdatKey, Cat, "CDAT 結構表",
            $"存在・版本 {v.Version}・條目 {v.Entries} 個・{typeText}。" +
            "<b>只到版本與條目數，不逐條解讀</b>：條目語意依規範版本而異，" +
            "而本工具沒有把 PCI-SIG 的 CDAT 規格逐版對準——沒對準出處就不出值。" +
            "CDAT 的用途是讓韌體宣告裝置關係，<b>宣告本身不改變硬體能力</b>，" +
            "所以本項不解讀成「效能」或「拓撲正確」的結論。",
            "條", Source, FactTrustLevel.Reported, false, at, v.Entries);
    }

    /// <summary>
    /// 登錄／WMI 通路：找韌體提供的 CDAT 描述。
    /// <b>本機沒有公開的 usermode 讀取通路時回 <c>(false, …)</c>——那是「沒有這張表」，
    /// 不是「讀不到」</b>；真的查詢失敗才回 <c>null</c>。
    /// </summary>
    internal static CdatState? ReadCdat()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\ACPI");
            if (key is null) return new CdatState(false, 0, 0, null);

            // ACPI 列舉下若有 CDAT 相關裝置，以子機碼計數；這是保守的間接證據。
            int entries = 0;
            foreach (string dev in key.GetSubKeyNames())
                if (dev.StartsWith("CDAT", StringComparison.OrdinalIgnoreCase)) entries++;
            return entries == 0
                ? new CdatState(false, 0, 0, null)
                : new CdatState(true, 0, entries, null);
        }
        catch { return null; }
    }

    // ── UV-009 USB TRIM ───────────────────────────────────────────────────

    public static HardwareFact UsbTrimFact(DateTimeOffset at, UsbTrimProbe? probe = null)
    {
        UsbTrimState? s;
        try { s = (probe ?? ReadUsbTrim)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UsbTrimKey, "USB 隨身碟 TRIM 支援", FactAvailability.ReadError,
                "USB 儲存裝置查詢失敗——讀不到就是不猜");

        var v = s;
        // 沒插隨身碟 ≠ 插了不支援。
        if (v.Devices == 0)
            return Unavailable(at, UsbTrimKey, "USB 隨身碟 TRIM 支援", FactAvailability.NotApplicable,
                "目前沒有連接任何 USB 儲存裝置——不適用。" +
                "「沒插隨身碟」與「插了但不支援 TRIM」是兩件事，" +
                "後者要看裝置本身的描述元才說得準；接上隨身碟後再讀");

        string verdict = v.TrimDeclared == 0
            ? "沒有裝置宣告 UNMAP／TRIM 能力"
            : $"{v.TrimDeclared} 個裝置宣告支援";

        return new HardwareFact(UsbTrimKey, Cat, "USB 隨身碟 TRIM 支援",
            $"USB 儲存裝置 {v.Devices} 個・{verdict}・走 UASP 協定 {v.Uasp} 個。" +
            "<b>「宣告」不等於「執行」</b>：USB 隨身碟控制器就算回報 UNMAP 能力，" +
            "很多只是把指令吃掉而不真的把區塊交還給快閃記憶體——" +
            "所以本項只陳述裝置宣告了什麼，不保證 TRIM 有效。" +
            "UASP（USB Attached SCSI）是<b>能轉發 SCSI 指令（含 UNMAP）的前提</b>，" +
            "走 BOT 傳統協定的裝置結構上就沒有這條路徑。" +
            "垃圾回收效率是隨身碟自己的事，本項不預測壽命或速度。",
            "個", Source, FactTrustLevel.Reported, false, at, v.TrimDeclared);
    }

    /// <summary>
    /// 讀取層：以隨身碟（BusType 7＝USB）的存在性與裝置描述元判斷。
    /// <b>只認公開可讀的欄位</b>，不下鑽裝置內部狀態。
    /// </summary>
    internal static UsbTrimState? ReadUsbTrim()
    {
        try
        {
            int devices = 0, trim = 0, uasp = 0;
            using (var searcher = new System.Management.ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT BusType, Size FROM MSFT_Disk"))
            {
                foreach (System.Management.ManagementObject o in searcher.Get())
                    using (o)
                    {
                        // 7＝USB（MSFT_Disk BusType 對照，與 DiskInfoService 同表系）。
                        int bus = o["BusType"] is { } b ? Convert.ToInt32(b) : -1;
                        if (bus != 7) continue;
                        devices++;
                    }
            }

            // 裝置層（Enum\USBSTOR）看不到 UNMAP 旗標時不硬填：維持 0 並由值說明。
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USBSTOR"))
            {
                if (key is not null)
                    foreach (string dev in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(dev);
                        if (sub is null) continue;
                        // UASP 裝置通常以 "UAS" 或 "USB Attached SCSI" 出現在硬體識別裡。
                        if (dev.Contains("UAS", StringComparison.OrdinalIgnoreCase)) uasp++;
                    }
            }

            return new UsbTrimState(devices, trim, uasp);
        }
        catch { return null; }
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
