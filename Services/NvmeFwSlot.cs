using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XinSpect;

/// <summary>NVMe 韌體槽資訊（log page 0x03）解出來的內容。</summary>
/// <param name="ActiveFirmwareInfo">AFI 原始位元組；bit2:0＝作用中槽、bit6:4＝下次重置啟用槽。</param>
/// <param name="SlotRevisions">七個槽的版本字串（<b>空字串＝該槽沒有版本</b>，與「讀不到」不同）。</param>
public sealed record NvmeFwSlotInfo(byte ActiveFirmwareInfo, IReadOnlyList<string> SlotRevisions)
{
    /// <summary>AFI bits 2:0；0 表示沒有作用中槽回報。</summary>
    public int ActiveSlot => ActiveFirmwareInfo & 0x07;

    /// <summary>AFI bits 6:4；0 表示沒有待啟用槽。</summary>
    public int PendingActivateSlot => (ActiveFirmwareInfo >> 4) & 0x07;

    /// <summary>有版本字串的槽數。</summary>
    public int SlotsWithRevision => SlotRevisions.Count(r => r.Length > 0);

    /// <summary>「槽1＝FWRV、槽2＝…」這種逐槽說法；只列有版本的那些。</summary>
    public string DescribeSlots()
        => SlotRevisions.Select((r, i) => (r, slot: i + 1))
            .Where(x => x.r.Length > 0)
            .Select(x => $"槽{x.slot}＝{x.r}")
            .DefaultIfEmpty("（七個槽都沒有版本字串）")
            .Aggregate((a, b) => $"{a}、{b}");
}

/// <summary>
/// NVMe 韌體槽資訊紀錄（Firmware Slot Information Log）的解碼器。純函式，零硬體相依。
///
/// <para>
/// <b>出處（2026-10-19 核對，兩份都可離線引用的原始碼）：</b>
/// Linux <c>include/linux/nvme.h</c>：<c>NVME_LOG_FW_SLOT = 0x03</c>，
/// 結構 <c>struct nvme_fw_slot_info_log { __u8 afi; __u8 rsvd1[7]; __le64 frs[7]; }</c>；
/// libnvme <c>src/nvme/types.h</c> 的 <c>struct nvme_firmware_slot</c> 寫成
/// <c>__u8 afi; __u8 rsvd1[7]; char frs[7][8]; __u8 rsvd2[448];</c>
/// ——<b>所以是 512 位元組、七個槽、每槽八個 ASCII 字元</b>。兩份一致。
/// </para>
///
/// <para>
/// <b>AFI 的位元語意只展開已對準 Windows SDK <c>nvme.h</c> 的兩個欄位：</b>
/// <c>NVME_FIRMWARE_SLOT_INFO_LOG.AFI</c> 明列 bits 2:0＝ActiveSlot、bits 6:4＝PendingActivateSlot。
/// 「第一槽唯讀」在 Identify Controller 的 <c>FRMW.Slot1ReadOnly</c>（byte 260 bit0），不在 AFI；
/// 因此本型別不把它硬塞進 AFI。
/// </para>
///
/// <para>
/// <b>整片 0 必須判成「讀不到」</b>——同 <see cref="AtaIdentify"/> 的規則：
/// 部分控制器不轉送 NVMe 指令、回的就是一片 0。若把它解成「AFI＝0、七個槽都是空的」，
/// 就會憑空生出一個「這顆碟沒有韌體槽」的硬體事實。
/// </para>
/// </summary>
public static class NvmeFwSlot
{
    /// <summary>紀錄大小：<c>afi(1) + rsvd1(7) + frs(7×8) + rsvd2(448) = 512</c>。</summary>
    public const int DataSize = 512;

    /// <summary>槽數：兩份出處都是 <c>frs[7]</c>。</summary>
    public const int SlotCount = 7;

    private const int FirstSlotOffset = 8;   // afi(1) + rsvd1(7)
    private const int SlotSize = 8;

    /// <summary><c>null</c> 或長度不足或整片 0 ⇒ <c>null</c>（＝讀不到，不是「沒有槽」）。</summary>
    [SpecRef("NVMe Base Specification 2.0, Firmware Slot Information Log（Log Page 03h）：AFI bits 2:0＝ActiveSlot、bits 6:4＝PendingActivateSlot，FRS[7] 每槽 8 bytes；Windows SDK 10.0.26100.0 shared/nvme.h 的 NVME_FIRMWARE_SLOT_INFO_LOG（512 bytes）@0")]
    public static NvmeFwSlotInfo? Decode(byte[]? log)
    {
        if (log is null || log.Length < DataSize) return null;
        if (!log.Any(b => b != 0)) return null;   // 整片 0：讀不到

        var slots = new List<string>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
            slots.Add(AtaStringStyle(log, FirstSlotOffset + i * SlotSize, SlotSize));

        return new NvmeFwSlotInfo(log[0], slots);
    }

    /// <summary>
    /// Identify Controller 的 OACS.FirmwareCommands（byte 256:257 bit 2）。<c>null</c>／長度不足回
    /// <c>null</c>，不把「沒讀到」解成「不支援」。
    /// </summary>
    [SpecRef("NVMe Base Specification 2.0, Identify Controller Data Structure - Optional Admin Command Support（OACS）bit 2 Firmware Commands；Windows SDK 10.0.26100.0 shared/nvme.h，OACS byte 256:257")]
    public static bool? FirmwareCommandsSupported(byte[]? identifyController)
    {
        if (identifyController is null || identifyController.Length < 258) return null;
        return (identifyController[256] & 0x04) != 0;
    }

    /// <summary>
    /// ASCII 字串：遇到 NUL 就停，尾端空白去掉。**逐字元**（不要先切位元組）——
    /// 這一欄沒有 ATA 那種 16 位元 word 的位元組順序問題，八個位元組就是八個字元。
    /// </summary>
    private static string AtaStringStyle(byte[] d, int offset, int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            byte b = d[offset + i];
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString().TrimEnd(' ');
    }
}
