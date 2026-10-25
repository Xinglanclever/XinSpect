using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 全裝置韌體版本盤點的存在面（U10-FW-0104，鍵 <c>pdev.fw.inventory</c>＋家族 <c>pdev.fw.inv.{i}</c>）。
///
/// <para>
/// <b>與既有兩把鍵的分工（寫進事實，避免三把鍵互相代言）：</b>
/// <c>pdev.fw.version</c>（P2-096）陳述<b>自述了版本字串</b>的裝置；它讓網卡這類
/// 「Windows 沒有韌體欄位」的裝置在敘述裡變成一句「缺源」。
/// <c>pdev.fw.surface</c>（U10-SE-0036）是<b>可更新性</b>的攻擊面彙整。
/// 這一項回答的是更前面的問題：<b>「哪些裝置有自述版本、哪些不自述——不自述的也不從清單消失」</b>。
/// </para>
///
/// <para>
/// <b>範圍（如實，不虛胖）：</b>硬碟／SSD 走<b>既有的同一份列舉</b>
/// （<see cref="PeripheralFirmwareFactsService.ReadInventory"/>，不重查 Win32_DiskDrive），
/// <b>逐顆</b>一筆家族鍵——自述了韌體版本的帶版本，<b>欄位空白的標「不自述」並留在清單裡</b>。
/// PnP 裝置（Win32_PnPEntity）只計數不逐個開鍵：WMI 的這個類別<b>沒有韌體欄位</b>，
/// 「自述了韌體版本的 PnP 裝置」在這個來源上是<b>確定的零</b>——為兩百多個裝置各開一把
/// 內容只有「不自述」的鍵，是把缺源偽裝成盤點；計數＋成文說明才是誠實的形狀。
/// </para>
///
/// <para>
/// <b>界線：</b>「不自述」與「沒有韌體」是不同的斷言（很多裝置的韌體版本要別的通路才讀得到，
/// 而本專案零網路、不載驅動、唯讀）；這一項不判斷新舊（那是 P2-097 知識包的事）。
/// </para>
/// </summary>
public static class PeripheralFirmwareInventoryFactsService
{
    private const string Category = "儲存裝置";
    private const string Source =
        "WMI Win32_DiskDrive（既有的 PeripheralFirmwareFactsService 列舉，不重查）；Win32_PnPEntity 僅計數";

    public const string InventoryKey = "pdev.fw.inventory";

    /// <summary>逐碟明細的<strong>家族前綴</strong>——有幾顆碟是機器的事實，成員編譯期列不完。</summary>
    private const string InvPrefix = "pdev.fw.inv.";

    public delegate PeripheralFirmwareFactsService.PeripheralInventory? InventoryProbe();
    public delegate int? PnpCountProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        InventoryProbe? probe = null, PnpCountProbe? pnpProbe = null)
    {
        PeripheralFirmwareFactsService.PeripheralInventory? inv;
        try { inv = (probe ?? new InventoryProbe(PeripheralFirmwareFactsService.ReadInventory))(); }
        catch { inv = null; }

        if (inv is null)
            return [new HardwareFact(InventoryKey, Category, "全裝置韌體版本盤點", "", "顆", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "**硬碟列舉本身失敗（WMI 不可用或查詢被拒）。**"
                + "**「讀不到」與「這台機器沒有裝置」是不同的斷言**——把前者說成後者，"
                + "會讓一次查詢失敗看起來像一個盤點結論")];

        if (inv.Disks.Count == 0)
            return [new HardwareFact(InventoryKey, Category, "全裝置韌體版本盤點", "", "顆", Source,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "**沒有列舉到實體硬碟**——這是確定的答案，不是讀不到"
                + "（虛擬機與部分儲存裝置不會出現在 Win32_DiskDrive 的實體列舉裡）")];

        int withFirmware = inv.Disks.Count(d => !string.IsNullOrWhiteSpace(d.Firmware));
        string pnpPart = DescribePnp(pnpProbe);
        string nicPart = inv.PhysicalNics is int n
            ? $"實體網卡 {n} 張（Win32_NetworkAdapter 沒有韌體欄位——不自述）"
            : "實體網卡列舉失敗（如實標，不計數）";

        var facts = new List<HardwareFact>(inv.Disks.Count + 1)
        {
            new(InventoryKey, Category, "全裝置韌體版本盤點",
                $"硬碟／SSD {inv.Disks.Count} 顆：自述韌體版本 {withFirmware} 顆、"
                + $"**不自述 {inv.Disks.Count - withFirmware} 顆（逐顆見 pdev.fw.inv.*，不自述者不從清單消失）**；"
                + $"PnP 現用裝置{pnpPart}；{nicPart}。"
                + "**與 pdev.fw.version／pdev.fw.surface 的分工**：version 是自述版本字串、"
                + "surface 是可更新性彙整；這一把是「哪些裝置有自述版本、哪些沒有」的**存在面**。"
                + "「不自述」≠「沒有韌體」——很多裝置的版本要別的通路才讀得到，"
                + "而本專案零網路、不載驅動、唯讀",
                "顆", Source, FactTrustLevel.Reported, false, at, withFirmware,
                FactAvailability.Present, null),
        };
        for (int i = 0; i < inv.Disks.Count; i++)
            facts.Add(DiskFact(at, i, inv.Disks[i]));
        return facts;
    }

    private static string DescribePnp(PnpCountProbe? pnpProbe)
    {
        int? count;
        try { count = pnpProbe is { } p ? p() : CountPnpDevices(); }
        catch { count = null; }
        return count is { } c
            ? $" {c} 個（**Win32_PnPEntity 沒有韌體欄位**＝這一類在 WMI 上不自述，是缺源不是「沒有韌體」；"
              + "為不逐個開鍵如實說明，裝置沒有消失）"
            : "列舉失敗（如實標）";
    }

    private static HardwareFact DiskFact(DateTimeOffset at, int index, PeripheralFirmwareFactsService.DeviceFirmware disk)
    {
        string key = InvPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(disk.Firmware))
            return new HardwareFact(key, Category, $"韌體盤點 {index}",
                $"{disk.Kind} {disk.Model}：**不自述**（FirmwareRevision 空白或未回報——如實標，不代填）",
                "", Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);

        return new HardwareFact(key, Category, $"韌體盤點 {index}",
            $"{disk.Kind} {disk.Model}＝{disk.Firmware}（裝置自述，不判新舊）",
            "", Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    /// <summary>真實計數：現用 PnP 裝置個數（只數不收——盤點不該順便蒐集 DeviceID 清單）。</summary>
    internal static int? CountPnpDevices()
    {
        int count = 0;
        using (var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID FROM Win32_PnPEntity WHERE Present = True"))
        using (var rows = searcher.Get())
            foreach (ManagementObject o in rows)
                using (o) count++;
        return count;
    }
}
