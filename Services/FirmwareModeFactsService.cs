using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 開機模式與韌體介面（ULTIMATE-1000 階段二 P2-040）與
/// 韌體版本與日期（P2-041）。
///
/// <para>
/// <b>為什麼這一項的措辭比數值重要：</b>
/// 「UEFI」與「Legacy」不是兩個值，是<b>兩條不同的開機路徑</b>——
/// 而一台機器可以兩者都支援。「目前用哪一個開機」與「支援哪一個」是不同的問題，
/// 混在一起就會得到一個看起來明確、實際上沒有回答任何問題的答案。
/// </para>
///
/// <para>
/// <b>界線：本服務陳述的是「這台機器現在是怎麼開起來的」，不是「它支援什麼」。</b>
/// 韌體設定裡可能同時有 CSM 與 UEFI，而目前這條路徑只說明這一次開機選了哪一個。
/// </para>
/// </summary>
public static class FirmwareModeFactsService
{
    private const string Category = "韌體";

    public const string ModeKey = "fw.mode";
    public const string VendorKey = "fw.vendor";
    public const string VersionKey = "fw.version";
    public const string DateKey = "fw.date";

    /// <summary>開機模式的來源——由呼叫端注入，測試不必真的碰系統。</summary>
    public sealed record ModeInputs(
        int? FirmwareType,          // 0 = 未知、1 = BIOS、2 = UEFI（GetFirmwareType）
        string? BiosVendor,         // Win32_BIOS Manufacturer
        string? BiosVersion,        // Win32_BIOS SMBIOSBIOSVersion
        string? BiosDate,           // Win32_BIOS ReleaseDate
        string? SystemVendor);      // Win32_ComputerSystem Manufacturer

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, ModeInputs x)
    {
        return
        [
            Mode(at, x.FirmwareType),
            Vendor(at, x.BiosVendor, x.SystemVendor),
            Version(at, x.BiosVersion),
            Date(at, x.BiosDate),
        ];
    }

    private static HardwareFact Mode(DateTimeOffset at, int? type)
    {
        const string key = ModeKey;
        const string source = "GetFirmwareType ＋ 開機環境匯出變數";

        if (type is null)
            return new HardwareFact(key, Category, "開機模式", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "取不到韌體型別（GetFirmwareType 未回報）——本服務不從其他跡象推測開機模式，"
                + "因為那些跡象（例如有 EFI 分割區）只說明「支援」而不是「這次用了哪一個」");

        string name = type switch
        {
            1 => "Legacy BIOS（CSM）",
            2 => "UEFI",
            _ => $"未知（代碼 {type}）",
        };

        return new HardwareFact(key, Category, "開機模式", name, "", source,
            FactTrustLevel.Measured, false, at, type,
            type is 1 or 2 ? FactAvailability.Present : FactAvailability.Unknown,
            type switch
            {
                2 => "這是**這一次開機走的路徑**，不是「這台機器支援什麼」——"
                   + "同一台機器的韌體設定裡可能同時保留 CSM 與 UEFI，"
                   + "而目前這條路徑只說明這次選了哪一個",
                1 => "這是**這一次開機走的路徑**。Legacy／CSM 路徑的存在不代表沒有 UEFI 支援"
                   + "——但它會影響 Secure Boot、GPT 開機與大於 2 TiB 的開機磁碟",
                _ => "韌體回報了未知的型別代碼——如實陳述，不代為判斷",
            });
    }

    private static HardwareFact Vendor(DateTimeOffset at, string? biosVendor, string? systemVendor)
    {
        const string key = VendorKey;
        const string source = "Win32_BIOS Manufacturer／Win32_ComputerSystem Manufacturer";

        // 兩個來源各給一個，並列而不合併——它們指的常常不是同一件事：
        // 一個是 BIOS 廠（AMI／Phoenix），一個是整機廠（Dell／ASUS）。
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(biosVendor)) parts.Add($"BIOS 廠 {biosVendor.Trim()}");
        if (!string.IsNullOrWhiteSpace(systemVendor)) parts.Add($"系統廠 {systemVendor.Trim()}");

        if (parts.Count == 0)
            return new HardwareFact(key, Category, "韌體與系統廠商", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "兩個來源都沒有值——**BIOS 廠與系統廠是不同的兩件事**，"
                + "把它們合成一個「廠商」欄位會讓「誰做的韌體」這個問題失去答案");

        return new HardwareFact(key, Category, "韌體與系統廠商", string.Join("；", parts), "",
            source, FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
            "**BIOS 廠與系統廠是不同的兩件事**：一個做韌體（AMI／Phoenix／Insyde），"
            + "一個賣整機（Dell／ASUS／Lenovo）。"
            + "本服務不合併它們，因為「誰做的韌體」與「誰的機器」在除錯時是不同的問題");
    }

    private static HardwareFact Version(DateTimeOffset at, string? version)
    {
        const string key = VersionKey;
        const string source = "Win32_BIOS SMBIOSBIOSVersion";

        if (string.IsNullOrWhiteSpace(version))
            return new HardwareFact(key, Category, "韌體版本", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "WMI 沒有提供韌體版本——SMBIOS Type 0 是另一個獨立來源");

        return new HardwareFact(key, Category, "韌體版本", version.Trim(), "", source,
            FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
            "這一項與 SMBIOS Type 0 的版本是兩個獨立來源，"
            + "不同時的原因是「WMI 這一欄常被 OEM 覆寫成自己的版號」——"
            + "那不是錯誤，是兩邊各說各話");
    }

    private static HardwareFact Date(DateTimeOffset at, string? date)
    {
        const string key = DateKey;
        const string source = "Win32_BIOS ReleaseDate（格式 yyyyMMddHHmmss.ffffff+000）";

        if (string.IsNullOrWhiteSpace(date))
            return new HardwareFact(key, Category, "韌體發行日", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "WMI 沒有提供發行日");

        // WMI 給的是緊湊格式（例如 20240315000000.000000+000），
        // 直接顯示會變成一串沒人看得懂的數字，所以拆成可讀日期——
        // **但原始字串也保留**，因為轉換會失去時區資訊。
        string raw = date.Trim();
        string readable = raw;
        if (raw.Length >= 8 && raw[..8].All(char.IsDigit))
        {
            string y = raw[..4], m = raw.Substring(4, 2), d = raw.Substring(6, 2);
            readable = $"{y}-{m}-{d}（原始 {raw}）";
        }

        return new HardwareFact(key, Category, "韌體發行日", readable, "", source,
            FactTrustLevel.Measured, false, at, null, FactAvailability.Present,
            "這是**韌體這一版的發行日**，不是「這台機器上一次更新韌體的時間」——"
            + "後者要看更新記錄，是另一件事");
    }

    /// <summary>
    /// 從真實系統讀取輸入。每一項各自 try/catch——
    /// 一個查詢失敗不該讓其他三項也變成「沒有值」。
    /// </summary>
    public static ModeInputs Read()
    {
        int? fwType = null;
        try { fwType = (int)Native.GetFirmwareType(); }
        catch (Exception ex) { Diag.Swallow("韌體型別", ex, "開機模式讀不到"); }

        string? biosVendor = null, biosVersion = null, biosDate = null, systemVendor = null;

        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\CIMV2",
                "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (System.Management.ManagementObject o in s.Get())
                using (o)
                {
                    biosVendor = Prop(o, "Manufacturer");
                    biosVersion = Prop(o, "SMBIOSBIOSVersion");
                    biosDate = Prop(o, "ReleaseDate");
                    break;
                }
        }
        catch (Exception ex) { Diag.Swallow("BIOS 版本查詢", ex, "BIOS 欄位讀不到"); }

        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\CIMV2",
                "SELECT Manufacturer FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject o in s.Get())
                using (o) { systemVendor = Prop(o, "Manufacturer"); break; }
        }
        catch (Exception ex) { Diag.Swallow("系統廠商查詢", ex, "系統廠商讀不到"); }

        return new ModeInputs(fwType, biosVendor, biosVersion, biosDate, systemVendor);
    }

    private static string? Prop(System.Management.ManagementObject o, string name)
    {
        try
        {
            string? v = o[name]?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    private static class Native
    {
        public enum FirmwareType { Unknown = 0, Bios = 1, Uefi = 2, Max = 3 }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern FirmwareType GetFirmwareTypeRaw();

        /// <summary>0 = 未知、1 = BIOS、2 = UEFI。失敗時回 0 而不是丟例外。</summary>
        public static int GetFirmwareType()
        {
            try { return (int)GetFirmwareTypeRaw(); }
            catch { return 0; }
        }
    }
}
