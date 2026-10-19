using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 周邊韌體「可更新性」宣告（ULTIMATE-1000 階段二 P2-098，鍵 <c>pdev.fw.updatable</c>）。
///
/// <para>
/// <b>題目是「各元件是否可更新」，而它可以引用地讀出來的只有「裝置自述支援不支援韌體下載命令」。</b>
/// 兩條通道各有自己的旗標，而且都已經有出處可逐位元核對（見下）；其餘部分如實標缺源。
/// </para>
///
/// <para>
/// <b>出處（2026-10-19 逐位元核對，三份都可離線引用的原文）：</b>
/// </para>
/// <list type="bullet">
/// <item><b>NVMe 槽結構：</b>Firmware Slot Information Log（log page <c>0x03</c>）回七個槽與版本字串，
/// 讀取走既有的 <see cref="StorageSmartService.TryReadNvmeLog"/>（同一個 IOCTL 路徑、同一套外層預算與斷路器）；
/// 佈局見 <see cref="NvmeFwSlot"/>。</item>
/// <item><b>NVMe 能力旗標：</b>Identify Controller 的 <c>OACS</c> bit 2（Firmware Commands），
/// 由 <see cref="NvmeFwSlot.FirmwareCommandsSupported"/> 讀出（三態：讀不到就是「未回報」）。</item>
/// <item><b>ATA：</b><c>IDENTIFY DEVICE</c> 有兩個位元可引用——word 83（Command Set Support）bit 0＝
/// <c>DownloadMicrocode</c>（「The DOWNLOAD MICROCODE command is supported」）、
/// word 69（Additional Supported）bit 8＝<c>DownloadMicrocodeDmaSupported</c>。
/// 兩者的有效性看 word 53 bit 1（words 70:64 valid）與 word 83 的 <c>WordValid83</c>。
/// 這三件事在本專案<b>已經有解碼器</b>（<see cref="AtaIdentify"/> 的
/// <c>FirmwareUpdateCapability</c> 三態），所以這裡<b>不重寫一份</b>，直接引用同一組欄位。
/// <b>原文出處是本機的 Windows SDK 標頭</b>：
/// <c>Windows Kits\10\Include\10.0.28000.0\km\ata.h</c>（word 83 的 <c>CommandSetSupport.DownloadMicrocode</c>
/// 與 word 69 的 <c>AdditionalSupported.DownloadMicrocodeDmaSupported</c>）。</item>
/// </list>
///
/// <para>
/// <b>這一輪最重要的一課（記在這裡，免得下一輪又重演）：</b>先前一度把 ATA 那一半判成「缺源」，
/// 依據是 Linux <c>include/linux/ata.h</c>——但那份檔案只定義<b>核心自己會用到</b>的位元
/// （word 69 它只用 TRIM 的 <c>0x4020</c> 與 zoned 的 <c>0x3</c>），
/// <b>「核心沒用到的位元」不等於「規格沒定義那個位元」</b>。
/// 換到規格原文（Windows SDK 標頭）一看，word 69 bit 8 與 word 83 bit 0 明明白白寫著 Download Microcode。
/// 教訓：<b>用「某份原始碼怎麼用」反推「規格有沒有這一欄」，方向是錯的</b>；要否定一個欄位，
/// 得看規格本身，而不是看某個消費者的使用情形。
/// </para>
///
/// <para>
/// <b>界線（寫進事實本身）：</b>①這是<b>裝置自述的支援旗標</b>，不是我們驗證過的可更新性
/// （本程式不燒寫、不做韌體更新、不連網查原廠清單）；②「支援下載命令」<b>不等於</b>
/// 「原廠還提供更新」，也<b>不等於</b>「需要更新」；③讀不到＝缺源，
/// <b>不代表不可更新，也不代表可以更新</b>。
/// </para>
///
/// <para>
/// 與 <c>pdev.fw.version</c>（P2-096）的區隔：那一把是<b>裝置自述的版本字串</b>，
/// 這一項是<b>裝置自述的支援旗標與槽結構</b>。兩者都不判斷新舊——新舊是 P2-097，需要知識包。
/// </para>
/// </summary>
public static class FirmwareUpdatableFactsService
{
    public const string Key = "pdev.fw.updatable";

    private const string Category = "儲存裝置";
    private const string SourceText =
        "NVMe：Firmware Slot Information（log page 0x03）＋ Identify Controller OACS bit2；"
        + "ATA：IDENTIFY DEVICE word 83 bit0／word 69 bit8（皆為裝置自述，唯讀）";

    // 匯流排分群問 StorageSmartService.ChannelOf（全專案唯一一份）——這裡不寫第二份映射。
    private static bool IsNvmeBus(uint bus) => StorageSmartService.ChannelOf(bus) == StorageSmartService.StorageBusChannel.Nvme;
    private static bool IsAtaBus(uint bus) => StorageSmartService.ChannelOf(bus) == StorageSmartService.StorageBusChannel.Ata;

    /// <summary>
    /// 一顆裝置的可更新性證據。兩條通道各自獨立：<b>沒讀到的欄位一律是 <c>null</c></b>
    /// （＝未回報／缺源），不得壓成 <c>false</c>（那個值的意思是「明確不支援」）。
    /// </summary>
    /// <param name="Slots">NVMe 的韌體槽結構；沒讀到＝<c>null</c>。</param>
    /// <param name="NvmeFirmwareCommands">NVMe 的 OACS bit 2；三態。<c>null</c>＝Identify Controller 讀不到。</param>
    /// <param name="AtaDownloadMicrocode"><see cref="AtaIdentify"/> 的韌體下載能力三態；
    /// <c>null</c>＝IDENTIFY 讀不到<b>或</b>該欄位有效性不足（未回報）。</param>
    public sealed record DeviceUpdatability(
        string Kind, string Model, string Firmware, int Index, uint BusType, string BusName,
        NvmeFwSlotInfo? Slots, bool? NvmeFirmwareCommands, bool? AtaDownloadMicrocode)
    {
        /// <summary>這顆走哪條通道（NVMe／ATA／其他＝匯流排名稱）。</summary>
        public string ChannelName => IsNvmeBus(BusType) ? "NVMe" : IsAtaBus(BusType) ? "ATA" : BusName;

        /// <summary>有沒有讀到<b>任何</b>可引用的可更新性證據。</summary>
        public bool HasEvidence =>
            Slots is not null || NvmeFirmwareCommands is not null || AtaDownloadMicrocode is not null;
    }

    /// <summary><c>null</c> 代表列舉本身失敗（WMI 不可用／查詢被拒）——與「沒有硬碟」不同。</summary>
    public delegate IReadOnlyList<DeviceUpdatability>? UpdatabilityProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, UpdatabilityProbe? probe = null)
        => [UpdatableFact(at, probe)];

    public static HardwareFact UpdatableFact(DateTimeOffset at, UpdatabilityProbe? probe = null)
    {
        IReadOnlyList<DeviceUpdatability>? devices;
        try { devices = (probe ?? new UpdatabilityProbe(ReadUpdatability))(); }
        catch { devices = null; }

        if (devices is null)
            return new HardwareFact(Key, Category, "周邊韌體可更新性宣告", "", "顆", SourceText,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "**裝置列舉本身失敗（WMI 不可用或查詢被拒）。**"
                + "**「讀不到」與「這台機器沒有可宣告的裝置」是不同的斷言**——"
                + "把前者說成後者，會讓一次查詢失敗看起來像一個硬體事實");

        if (devices.Count == 0)
            return new HardwareFact(Key, Category, "周邊韌體可更新性宣告", "沒有列舉到實體硬碟", "顆", SourceText,
                FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                "**這是確定的答案，不是讀不到**——來源可用、查詢成功，只是回空。"
                + "（虛擬機與部分儲存裝置不會出現在 Win32_DiskDrive 的實體列舉裡）");

        var nvme = devices.Where(d => IsNvmeBus(d.BusType)).ToList();
        var ata = devices.Where(d => IsAtaBus(d.BusType)).ToList();
        var other = devices.Where(d => !IsNvmeBus(d.BusType) && !IsAtaBus(d.BusType)).ToList();

        var parts = new List<string> { $"硬碟／SSD {devices.Count} 顆。" };
        if (nvme.Count > 0) parts.Add(NvmePart(nvme));
        if (ata.Count > 0) parts.Add(AtaPart(ata));
        if (other.Count > 0) parts.Add(OtherPart(other));
        parts.Add(Boundary);

        return new HardwareFact(Key, Category, "周邊韌體可更新性宣告",
            string.Join(" ", parts),
            "顆", SourceText, FactTrustLevel.Reported, false, at, devices.Count(d => d.HasEvidence),
            FactAvailability.Present,
            // Present 的事實不得帶 unavailableReason（HardwareSnapshotService 的不變式）：
            // 「某幾顆讀不到」是斷言的一部分，寫在 Value；不是「為什麼這筆事實讀不到」。
            null);
    }

    private static string NvmePart(IReadOnlyList<DeviceUpdatability> nvme)
    {
        var withSlots = nvme.Where(d => d.Slots is not null).ToList();
        var withoutSlots = nvme.Where(d => d.Slots is null).ToList();

        string slotText = withSlots.Count > 0
            ? string.Join("；", withSlots.Take(3).Select(d =>
                $"{d.Model}：作用中槽 {ActiveSlotText(d.Slots!) }、{d.Slots!.DescribeSlots()}"))
            : "**沒有任何一顆回報槽資訊**";

        string missing = withoutSlots.Count > 0
            ? $"，另 {withoutSlots.Count} 顆沒回報槽資訊（讀不到＝缺源，不代表不可更新）"
            : "";

        string caps = CountTri(nvme.Select(d => d.NvmeFirmwareCommands),
            "自述支援韌體命令（OACS bit2）", "明確不支援韌體命令（OACS bit2＝0）", "OACS 讀不到＝未回報");

        return $"NVMe {nvme.Count} 顆：{slotText}{missing}；{caps}。";
    }

    private static string ActiveSlotText(NvmeFwSlotInfo slots)
        => slots.ActiveSlot == 0 ? "未回報（AFI 原始值 0x00）" : $"槽 {slots.ActiveSlot}";

    private static string AtaPart(IReadOnlyList<DeviceUpdatability> ata)
    {
        string caps = CountTri(ata.Select(d => d.AtaDownloadMicrocode),
            "自述支援 DOWNLOAD MICROCODE", "明確不支援（兩個欄位都有效且皆為 0）", "未回報（IDENTIFY 讀不到＝缺源）");
        return $"ATA／SATA {ata.Count} 顆（word 83 bit0／word 69 bit8）：{caps}。";
    }

    private static string OtherPart(IReadOnlyList<DeviceUpdatability> other)
    {
        string names = string.Join("、", other.Take(3).Select(d => $"{d.ChannelName} {d.Model}").Distinct());
        string more = other.Count > 3 ? $"（以下省略 {other.Count - 3} 顆）" : "";
        return $"其他匯流排 {other.Count} 顆（{names}{more}）："
            + "**這兩條通道都不適用，沒有任何可引用的可更新性來源＝缺源**，不是「不可更新」。";
    }

    private const string Boundary =
        "以上都是**裝置自述的支援旗標**，不是我們驗證過的可更新性："
        + "①支援下載命令**不等於**原廠還提供更新，也**不等於**需要更新；"
        + "②本程式不燒寫、不做韌體更新、不連網查原廠清單；"
        + "③NVMe 的作用中槽由規格欄位（AFI bits 2:0）讀出，AFI 其餘位元不解讀。";

    /// <summary>
    /// 三態計數：<c>true</c>／<c>false</c>／<c>null</c> 三種各算幾顆，各自帶自己的說法。
    /// <b>三態必須分開講</b>——把 <c>null</c> 併進 <c>false</c> 會把「未回報」講成「不支援」。
    /// </summary>
    private static string CountTri(IEnumerable<bool?> values, string yes, string no, string unknown)
    {
        var list = values.ToList();
        var parts = new List<string>();
        if (list.Count(v => v == true) is int y && y > 0) parts.Add($"{y} 顆{yes}");
        if (list.Count(v => v == false) is int n && n > 0) parts.Add($"{n} 顆{no}");
        if (list.Count(v => v is null) is int u && u > 0) parts.Add($"{u} 顆{unknown}");
        return parts.Count > 0 ? string.Join("、", parts) : "沒有裝置可判";
    }

    /// <summary>
    /// 真實路徑：<b>沿用 <see cref="PeripheralFirmwareFactsService"/> 的同一次列舉</b>
    /// （不重查 <c>Win32_DiskDrive</c>——列舉一次、多個事實共用），
    /// 再依匯流排走<b>一條</b>對應的讀取：NVMe 讀槽紀錄頁與 Identify Controller，ATA 讀 IDENTIFY DEVICE。
    /// 兩條都經過 <see cref="StorageSmartService"/> 的看門狗與斷路器（本服務不自己開檔案）。
    /// </summary>
    private static IReadOnlyList<DeviceUpdatability> ReadUpdatability()
    {
        var inventory = PeripheralFirmwareFactsService.ReadInventory();
        var list = new List<DeviceUpdatability>(inventory.Disks.Count);
        foreach (var disk in inventory.Disks)
        {
            uint bus = disk.Index >= 0 ? StorageSmartService.TryGetBusType(disk.Index, out _) : 0;
            string busName = disk.Index >= 0 ? BusName(bus) : "未知（沒有磁碟編號可查）";

            NvmeFwSlotInfo? slots = null;
            bool? nvmeCaps = null;
            bool? ataCap = null;

            if (disk.Index >= 0 && IsNvmeBus(bus))
            {
                slots = NvmeFwSlot.Decode(
                    StorageSmartService.TryReadNvmeLog(disk.Index, StorageSmartService.NvmeLogPageFirmwareSlot));
                nvmeCaps = StorageSmartService.TryReadNvmeIdentify(disk.Index) is { } ic
                    ? NvmeFwSlot.FirmwareCommandsSupported(ic)
                    : null;
            }
            else if (disk.Index >= 0 && IsAtaBus(bus))
            {
                ataCap = StorageSmartService.TryReadAtaIdentify(disk.Index) is { } raw
                    ? AtaIdentify.Decode(raw)?.FirmwareUpdateCapability
                    : null;
            }

            list.Add(new DeviceUpdatability(disk.Kind, disk.Model, disk.Firmware, disk.Index, bus, busName,
                slots, nvmeCaps, ataCap));
        }
        return list;
    }

    private static string BusName(uint bus) => StorageSmartService.BusTypeName(bus);
}
