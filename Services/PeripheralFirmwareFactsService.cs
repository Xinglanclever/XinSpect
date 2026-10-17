using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 周邊韌體版本盤點（ULTIMATE-1000 階段二 P2-096）：硬碟／SSD／網卡的韌體版本。
///
/// <para>
/// <b>這一項的關鍵界線是「誰說的」：</b>硬碟的韌體版本是<b>裝置自述</b>
/// （<c>Win32_DiskDrive.FirmwareRevision</c>，Trust＝Reported），
/// <b>不是</b>本專案拿原廠清單比對後判定的「最新版」——
/// 那件事是 P2-097 <c>pdev.fw.delta</c>，需要知識包，不在这里冒充。
/// </para>
///
/// <para>
/// <b>網卡沒有這條路，而這件事必須說出來，不能安靜地少一項。</b>
/// Windows 的 <c>Win32_NetworkAdapter</c> <b>沒有韌體欄位</b>；
/// 唯一看起來像的 <c>DriverVersion</c> 是驅動程式的版本，<b>不是網卡韌體</b>。
/// 本專案不以驅動版本冒充韌體版本——所以網卡那半邊如實標成「缺源」，
/// 並且把「查了幾張實體網卡」一起寫進去，讓「缺源」與「沒查」分得開。
/// </para>
///
/// <para>
/// <b>不列序號。</b>型號與韌體版本是可公開的盤點資訊，序號不是（見 L3 禁項掃描）。
/// </para>
///
/// <para>
/// 與 <c>fw.rom.list</c>／<c>fw.psp</c> 等鍵的區隔：那些是<b>主機板上的</b>韌體元件；
/// 這一項是<b>外接／內接周邊自己的</b>韌體。兩者都在「韌體」這個大題目下，但來源與對象不同。
/// </para>
/// </summary>
public static class PeripheralFirmwareFactsService
{
    private const string Category = "儲存裝置";
    private const string Source = "WMI Win32_DiskDrive（FirmwareRevision，唯讀）；Win32_NetworkAdapter 僅用來數實體網卡";

    public const string VersionKey = "pdev.fw.version";

    /// <summary>一顆周邊裝置的自述：種類、型號、韌體版本（<paramref name="Firmware"/> 可為空＝裝置沒回報）。</summary>
    public sealed record DeviceFirmware(string Kind, string Model, string Firmware);

    /// <summary>
    /// <paramref name="Disks"/> 是列舉到的實體硬碟；
    /// <paramref name="PhysicalNics"/> 是查到的實體網卡張數，<c>null</c> 代表<b>那條查詢本身失敗</b>——
    /// 「查不到」與「查到 0 張」是不同的斷言。
    /// </summary>
    public sealed record PeripheralInventory(IReadOnlyList<DeviceFirmware> Disks, int? PhysicalNics);

    public delegate PeripheralInventory? InventoryProbe();

    /// <summary><c>null</c> 代表列舉本身失敗（WMI 不可用、查詢被拒）——與「沒有硬碟」不同。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, InventoryProbe? probe = null)
        => [VersionFact(at, probe)];

    public static HardwareFact VersionFact(DateTimeOffset at, InventoryProbe? probe = null)
    {
        PeripheralInventory? inv;
        try { inv = (probe ?? new InventoryProbe(ReadInventory))(); }
        catch { inv = null; }

        if (inv is null)
            return new HardwareFact(VersionKey, Category, "周邊韌體版本盤點", "", "顆", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "**WMI 列舉本身失敗（類別不存在或查詢被拒）。**"
                + "**「讀不到」與「這台機器沒有硬碟」是不同的斷言**——"
                + "把前者說成後者，會讓一次查詢失敗看起來像一個硬體事實");

        if (inv.Disks.Count == 0)
            return new HardwareFact(VersionKey, Category, "周邊韌體版本盤點", "沒有列舉到實體硬碟", "顆", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "**這是確定的答案，不是讀不到**——來源可用、查詢成功，只是回空。"
                + "（虛擬機與部分儲存裝置不會出現在 Win32_DiskDrive 的實體列舉裡）");

        var withFirmware = inv.Disks.Where(d => !string.IsNullOrWhiteSpace(d.Firmware)).ToList();
        string listed = string.Join("、", withFirmware.Take(5)
            .Select(d => $"{d.Kind} {d.Model}＝{d.Firmware}"));
        string truncated = withFirmware.Count > 5 ? $"（以下省略 {withFirmware.Count - 5} 顆）" : "";

        string diskPart = withFirmware.Count > 0
            ? $"硬碟／SSD {inv.Disks.Count} 顆，其中 {withFirmware.Count} 顆自述韌體版本：{listed}{truncated}。"
            : $"硬碟／SSD {inv.Disks.Count} 顆，**沒有任何一顆自述韌體版本**"
              + "（欄位存在、來源可用，是裝置不回報——不代填成別的推測）。";

        string nicPart = inv.PhysicalNics is int n
            ? $"網卡：實體 {n} 張，**Windows 的 Win32_NetworkAdapter 沒有韌體欄位**"
              + "（DriverVersion 是驅動程式的版本，不是網卡韌體——本專案不拿它冒充），"
              + "故網卡韌體＝**缺源**，不是「沒有」。"
            : "網卡：**連實體網卡的列舉都失敗**，故網卡韌體＝缺源（讀不到，不是沒有）。";

        string boundary = "這串是**裝置自述**，不是對照原廠清單後判定的「最新版」"
            + "——版本新舊的判斷需要知識包（P2-097）。";

        return new HardwareFact(VersionKey, Category, "周邊韌體版本盤點",
            $"{diskPart} {nicPart} {boundary}",
            "顆", Source, FactTrustLevel.Reported, false, at, withFirmware.Count,
            FactAvailability.Present,
            // Present 的事實不得帶 unavailableReason（HardwareSnapshotService 的不變式）：
            // 「網卡缺源」寫在 Value 裡（那是斷言的一部分），不是寫在這裡（那是「為什麼讀不到」）。
            null);
    }

    /// <summary>
    /// 真實列舉：硬碟走 <c>Win32_DiskDrive</c>；網卡只數張數（<c>PhysicalAdapter=True</c>，
    /// 排除虛擬／隧道介面），<b>因為它沒有可讀的韌體欄位</b>。
    /// </summary>
    private static PeripheralInventory ReadInventory()
    {
        var disks = new List<DeviceFirmware>();
        using (var searcher = new ManagementObjectSearcher(
            "SELECT Model, FirmwareRevision, MediaType FROM Win32_DiskDrive"))
        using (var rows = searcher.Get())
        {
            foreach (ManagementObject o in rows)
            {
                using (o)
                {
                    string model = (o["Model"] as string)?.Trim() ?? "";
                    string firmware = (o["FirmwareRevision"] as string)?.Trim() ?? "";
                    string media = (o["MediaType"] as string)?.Trim() ?? "";
                    string kind = media.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD" : "硬碟";
                    disks.Add(new DeviceFirmware(kind,
                        model.Length > 0 ? model : "（裝置未自述型號）", firmware));
                }
            }
        }

        int? nics;
        try
        {
            int count = 0;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True"))
            using (var rows = searcher.Get())
                foreach (ManagementObject o in rows)
                    using (o) count++;
            nics = count;
        }
        catch { nics = null; }   // 這條失敗不影響硬碟那半邊——分開如實標

        return new PeripheralInventory(disks, nics);
    }
}
