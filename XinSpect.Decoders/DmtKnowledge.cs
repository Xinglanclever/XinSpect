using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// VESA DMT（Display Monitor Timing）逐模式對照的一條登記（U10 第四圈批次二，VC-0001～0021）。
/// </summary>
/// <param name="Code">VESA DMT 的模式代碼（0x01–0x58；EDID established timing III descriptor 的代碼同源）。</param>
/// <param name="Name">解析度（如 640x480）。</param>
/// <param name="RefreshHz">垂直刷新率字串（原樣保留；59.94 這類非整數不捨入）。</param>
/// <param name="ReducedBlanking">是否 CVT-RB（減化空白）變體。</param>
/// <param name="ClockKhz">像素時脈（kHz）。</param>
/// <param name="HActive">水平可視。</param>
/// <param name="HSyncStart">水平同步起。</param>
/// <param name="HSyncEnd">水平同步訖。</param>
/// <param name="HTotal">水平總計。</param>
/// <param name="VActive">垂直可視。</param>
/// <param name="VSyncStart">垂直同步起。</param>
/// <param name="VSyncEnd">垂直同步訖。</param>
/// <param name="VTotal">垂直總計。</param>
/// <param name="HSync">水平同步極性（"P"＝正、"N"＝負，照旗標原樣）。</param>
/// <param name="VSync">垂直同步極性（"P"＝正、"N"＝負，照旗標原樣）。</param>
/// <param name="Interlaced">是否交錯式（DMT 表中僅 0x0f 一條）。</param>
[SpecRef("VESA Display Monitor Timing (DMT) Standard v1.0 rev.12——經 Linux drivers/gpu/drm/drm_edid.c 的 drm_dmt_modes[] 逐欄對照（kernel.org，2026-11-15 取得）")]
public sealed record DmtModeInfo(
    int Code, string Name, string RefreshHz, bool ReducedBlanking,
    int ClockKhz, int HActive, int HSyncStart, int HSyncEnd, int HTotal,
    int VActive, int VSyncStart, int VSyncEnd, int VTotal,
    string HSync, string VSync, bool Interlaced);

/// <summary>
/// EDID 基本塊 established timing 位圖（位元組 32–34，24 位中 17 位有定義）的一條登記。
/// </summary>
/// <param name="Bit">位元位置（0 起算，照 E-EDID §3.8.1 的定義順序）。</param>
/// <param name="Width">水平可視。</param>
/// <param name="Height">垂直可視。</param>
/// <param name="RefreshHz">刷新率。</param>
/// <param name="Interlaced">是否交錯式（1024x768@43 一條）。</param>
[SpecRef("VESA E-EDID v1.4 §3.8 Established Timing（經 Linux drm_edid.c 的 edid_est_modes[] 逐條對照）")]
public sealed record DmtEst1Entry(int Bit, int Width, int Height, string RefreshHz, bool Interlaced);

/// <summary>
/// Established Timing III 描述元位圖的一條登記（6 位元組、44 位有定義）。
/// </summary>
/// <param name="Bit">位元位置（從描述元第 0 位元組的 bit7 起算）。</param>
/// <param name="Width">水平可視。</param>
/// <param name="Height">垂直可視。</param>
/// <param name="RefreshHz">刷新率。</param>
/// <param name="ReducedBlanking">是否 CVT-RB 變體。</param>
[SpecRef("VESA E-EDID Established Timing III descriptor——經 Linux drm_edid.c 的 est3_modes[] 逐條對照")]
public sealed record DmtEst3Entry(int Bit, int Width, int Height, string RefreshHz, bool ReducedBlanking);

/// <summary>
/// DMT 顯示時序知識包（U10 第四圈批次二）：把 VESA DMT 全表、EDID established timing 兩張位圖對照
/// 變成可查詢的資料，讓 EDID 裡的 detailed timing／standard timing 能被「叫出名字」。
/// </summary>
/// <remarks>
/// <para>
/// <b>這是知識包，不是行為</b>：零 I/O、零讀取面——與第 276 輪的 VIC 包（Cta861.cs）同一形狀。
/// 唯一分母：DMT **88** 條（0x01–0x58，含 RB 變體與唯一一條交錯式 0x0f）、
/// established timing I **17** 條、established timing III **44** 條，由 <c>DmtKnowledgeTests</c> 釘住。
/// </para>
/// <para>
/// <b>HDMI VIC 與 CTA VIC 是兩個命名空間</b>（VC-0038）：CTA-861 的 VIC（含 4K 組）與
/// HDMI 1.3+ VSDB 的 HDMI_VIC（僅 4 個專屬值）不可互換——本包只管 DMT 與 established timing，
/// VIC 的解碼在第 276 輪的 Cta861.cs。
/// </para>
/// </remarks>
[SpecRef("VESA DMT v1.0r12＋VESA E-EDID v1.4 §3.8——經 Linux drm_edid.c 對照")]
public static class DmtKnowledge
{
    /// <summary>DMT 全表：88 條（代碼 0x01–0x58，全唯一）。</summary>
    [SpecRef("VESA DMT v1.0r12（Linux drm_edid.c drm_dmt_modes[] 逐欄對照）")]
    public static IReadOnlyList<DmtModeInfo> Modes { get; } =
    [
        new(0x01, "640x350", "85", false, 31500, 640, 672, 736, 832, 350, 382, 385, 445, "P", "N", false),
        new(0x02, "640x400", "85", false, 31500, 640, 672, 736, 832, 400, 401, 404, 445, "N", "P", false),
        new(0x03, "720x400", "85", false, 35500, 720, 756, 828, 936, 400, 401, 404, 446, "N", "P", false),
        new(0x04, "640x480", "60", false, 25175, 640, 656, 752, 800, 480, 490, 492, 525, "N", "N", false),
        new(0x05, "640x480", "72", false, 31500, 640, 664, 704, 832, 480, 489, 492, 520, "N", "N", false),
        new(0x06, "640x480", "75", false, 31500, 640, 656, 720, 840, 480, 481, 484, 500, "N", "N", false),
        new(0x07, "640x480", "85", false, 36000, 640, 696, 752, 832, 480, 481, 484, 509, "N", "N", false),
        new(0x08, "800x600", "56", false, 36000, 800, 824, 896, 1024, 600, 601, 603, 625, "P", "P", false),
        new(0x09, "800x600", "60", false, 40000, 800, 840, 968, 1056, 600, 601, 605, 628, "P", "P", false),
        new(0x0A, "800x600", "72", false, 50000, 800, 856, 976, 1040, 600, 637, 643, 666, "P", "P", false),
        new(0x0B, "800x600", "75", false, 49500, 800, 816, 896, 1056, 600, 601, 604, 625, "P", "P", false),
        new(0x0C, "800x600", "85", false, 56250, 800, 832, 896, 1048, 600, 601, 604, 631, "P", "P", false),
        new(0x0D, "800x600", "120", true, 73250, 800, 848, 880, 960, 600, 603, 607, 636, "P", "N", false),
        new(0x0E, "848x480", "60", false, 33750, 848, 864, 976, 1088, 480, 486, 494, 517, "P", "P", false),
        new(0x0F, "1024x768i", "43", false, 44900, 1024, 1032, 1208, 1264, 768, 768, 776, 817, "P", "P", true),
        new(0x10, "1024x768", "60", false, 65000, 1024, 1048, 1184, 1344, 768, 771, 777, 806, "N", "N", false),
        new(0x11, "1024x768", "70", false, 75000, 1024, 1048, 1184, 1328, 768, 771, 777, 806, "N", "N", false),
        new(0x12, "1024x768", "75", false, 78750, 1024, 1040, 1136, 1312, 768, 769, 772, 800, "P", "P", false),
        new(0x13, "1024x768", "85", false, 94500, 1024, 1072, 1168, 1376, 768, 769, 772, 808, "P", "P", false),
        new(0x14, "1024x768", "120", true, 115500, 1024, 1072, 1104, 1184, 768, 771, 775, 813, "P", "N", false),
        new(0x15, "1152x864", "75", false, 108000, 1152, 1216, 1344, 1600, 864, 865, 868, 900, "P", "P", false),
        new(0x55, "1280x720", "60", false, 74250, 1280, 1390, 1430, 1650, 720, 725, 730, 750, "P", "P", false),
        new(0x16, "1280x768", "60", true, 68250, 1280, 1328, 1360, 1440, 768, 771, 778, 790, "P", "N", false),
        new(0x17, "1280x768", "60", false, 79500, 1280, 1344, 1472, 1664, 768, 771, 778, 798, "N", "P", false),
        new(0x18, "1280x768", "75", false, 102250, 1280, 1360, 1488, 1696, 768, 771, 778, 805, "N", "P", false),
        new(0x19, "1280x768", "85", false, 117500, 1280, 1360, 1496, 1712, 768, 771, 778, 809, "N", "P", false),
        new(0x1A, "1280x768", "120", true, 140250, 1280, 1328, 1360, 1440, 768, 771, 778, 813, "P", "N", false),
        new(0x1B, "1280x800", "60", true, 71000, 1280, 1328, 1360, 1440, 800, 803, 809, 823, "P", "N", false),
        new(0x1C, "1280x800", "60", false, 83500, 1280, 1352, 1480, 1680, 800, 803, 809, 831, "N", "P", false),
        new(0x1D, "1280x800", "75", false, 106500, 1280, 1360, 1488, 1696, 800, 803, 809, 838, "N", "P", false),
        new(0x1E, "1280x800", "85", false, 122500, 1280, 1360, 1496, 1712, 800, 803, 809, 843, "N", "P", false),
        new(0x1F, "1280x800", "120", true, 146250, 1280, 1328, 1360, 1440, 800, 803, 809, 847, "P", "N", false),
        new(0x20, "1280x960", "60", false, 108000, 1280, 1376, 1488, 1800, 960, 961, 964, 1000, "P", "P", false),
        new(0x21, "1280x960", "85", false, 148500, 1280, 1344, 1504, 1728, 960, 961, 964, 1011, "P", "P", false),
        new(0x22, "1280x960", "120", true, 175500, 1280, 1328, 1360, 1440, 960, 963, 967, 1017, "P", "N", false),
        new(0x23, "1280x1024", "60", false, 108000, 1280, 1328, 1440, 1688, 1024, 1025, 1028, 1066, "P", "P", false),
        new(0x24, "1280x1024", "75", false, 135000, 1280, 1296, 1440, 1688, 1024, 1025, 1028, 1066, "P", "P", false),
        new(0x25, "1280x1024", "85", false, 157500, 1280, 1344, 1504, 1728, 1024, 1025, 1028, 1072, "P", "P", false),
        new(0x26, "1280x1024", "120", true, 187250, 1280, 1328, 1360, 1440, 1024, 1027, 1034, 1084, "P", "N", false),
        new(0x27, "1360x768", "60", false, 85500, 1360, 1424, 1536, 1792, 768, 771, 777, 795, "P", "P", false),
        new(0x28, "1360x768", "120", true, 148250, 1360, 1408, 1440, 1520, 768, 771, 776, 813, "P", "N", false),
        new(0x51, "1366x768", "60", false, 85500, 1366, 1436, 1579, 1792, 768, 771, 774, 798, "P", "P", false),
        new(0x56, "1366x768", "60", false, 72000, 1366, 1380, 1436, 1500, 768, 769, 772, 800, "P", "P", false),
        new(0x29, "1400x1050", "60", true, 101000, 1400, 1448, 1480, 1560, 1050, 1053, 1057, 1080, "P", "N", false),
        new(0x2A, "1400x1050", "60", false, 121750, 1400, 1488, 1632, 1864, 1050, 1053, 1057, 1089, "N", "P", false),
        new(0x2B, "1400x1050", "75", false, 156000, 1400, 1504, 1648, 1896, 1050, 1053, 1057, 1099, "N", "P", false),
        new(0x2C, "1400x1050", "85", false, 179500, 1400, 1504, 1656, 1912, 1050, 1053, 1057, 1105, "N", "P", false),
        new(0x2D, "1400x1050", "120", true, 208000, 1400, 1448, 1480, 1560, 1050, 1053, 1057, 1112, "P", "N", false),
        new(0x2E, "1440x900", "60", true, 88750, 1440, 1488, 1520, 1600, 900, 903, 909, 926, "P", "N", false),
        new(0x2F, "1440x900", "60", false, 106500, 1440, 1520, 1672, 1904, 900, 903, 909, 934, "N", "P", false),
        new(0x30, "1440x900", "75", false, 136750, 1440, 1536, 1688, 1936, 900, 903, 909, 942, "N", "P", false),
        new(0x31, "1440x900", "85", false, 157000, 1440, 1544, 1696, 1952, 900, 903, 909, 948, "N", "P", false),
        new(0x32, "1440x900", "120", true, 182750, 1440, 1488, 1520, 1600, 900, 903, 909, 953, "P", "N", false),
        new(0x53, "1600x900", "60", false, 108000, 1600, 1624, 1704, 1800, 900, 901, 904, 1000, "P", "P", false),
        new(0x33, "1600x1200", "60", false, 162000, 1600, 1664, 1856, 2160, 1200, 1201, 1204, 1250, "P", "P", false),
        new(0x34, "1600x1200", "65", false, 175500, 1600, 1664, 1856, 2160, 1200, 1201, 1204, 1250, "P", "P", false),
        new(0x35, "1600x1200", "70", false, 189000, 1600, 1664, 1856, 2160, 1200, 1201, 1204, 1250, "P", "P", false),
        new(0x36, "1600x1200", "75", false, 202500, 1600, 1664, 1856, 2160, 1200, 1201, 1204, 1250, "P", "P", false),
        new(0x37, "1600x1200", "85", false, 229500, 1600, 1664, 1856, 2160, 1200, 1201, 1204, 1250, "P", "P", false),
        new(0x38, "1600x1200", "120", true, 268250, 1600, 1648, 1680, 1760, 1200, 1203, 1207, 1271, "P", "N", false),
        new(0x39, "1680x1050", "60", true, 119000, 1680, 1728, 1760, 1840, 1050, 1053, 1059, 1080, "P", "N", false),
        new(0x3A, "1680x1050", "60", false, 146250, 1680, 1784, 1960, 2240, 1050, 1053, 1059, 1089, "N", "P", false),
        new(0x3B, "1680x1050", "75", false, 187000, 1680, 1800, 1976, 2272, 1050, 1053, 1059, 1099, "N", "P", false),
        new(0x3C, "1680x1050", "85", false, 214750, 1680, 1808, 1984, 2288, 1050, 1053, 1059, 1105, "N", "P", false),
        new(0x3D, "1680x1050", "120", true, 245500, 1680, 1728, 1760, 1840, 1050, 1053, 1059, 1112, "P", "N", false),
        new(0x3E, "1792x1344", "60", false, 204750, 1792, 1920, 2120, 2448, 1344, 1345, 1348, 1394, "N", "P", false),
        new(0x3F, "1792x1344", "75", false, 261000, 1792, 1888, 2104, 2456, 1344, 1345, 1348, 1417, "N", "P", false),
        new(0x40, "1792x1344", "120", true, 333250, 1792, 1840, 1872, 1952, 1344, 1347, 1351, 1423, "P", "N", false),
        new(0x41, "1856x1392", "60", false, 218250, 1856, 1952, 2176, 2528, 1392, 1393, 1396, 1439, "N", "P", false),
        new(0x42, "1856x1392", "75", false, 288000, 1856, 1984, 2208, 2560, 1392, 1393, 1396, 1500, "N", "P", false),
        new(0x43, "1856x1392", "120", true, 356500, 1856, 1904, 1936, 2016, 1392, 1395, 1399, 1474, "P", "N", false),
        new(0x52, "1920x1080", "60", false, 148500, 1920, 2008, 2052, 2200, 1080, 1084, 1089, 1125, "N", "N", false),
        new(0x44, "1920x1200", "60", true, 154000, 1920, 1968, 2000, 2080, 1200, 1203, 1209, 1235, "P", "N", false),
        new(0x45, "1920x1200", "60", false, 193250, 1920, 2056, 2256, 2592, 1200, 1203, 1209, 1245, "N", "P", false),
        new(0x46, "1920x1200", "75", false, 245250, 1920, 2056, 2264, 2608, 1200, 1203, 1209, 1255, "N", "P", false),
        new(0x47, "1920x1200", "85", false, 281250, 1920, 2064, 2272, 2624, 1200, 1203, 1209, 1262, "N", "P", false),
        new(0x48, "1920x1200", "120", true, 317000, 1920, 1968, 2000, 2080, 1200, 1203, 1209, 1271, "P", "N", false),
        new(0x49, "1920x1440", "60", false, 234000, 1920, 2048, 2256, 2600, 1440, 1441, 1444, 1500, "N", "P", false),
        new(0x4A, "1920x1440", "75", false, 297000, 1920, 2064, 2288, 2640, 1440, 1441, 1444, 1500, "N", "P", false),
        new(0x4B, "1920x1440", "120", true, 380500, 1920, 1968, 2000, 2080, 1440, 1443, 1447, 1525, "P", "N", false),
        new(0x54, "2048x1152", "60", false, 162000, 2048, 2074, 2154, 2250, 1152, 1153, 1156, 1200, "P", "P", false),
        new(0x4C, "2560x1600", "60", true, 268500, 2560, 2608, 2640, 2720, 1600, 1603, 1609, 1646, "P", "N", false),
        new(0x4D, "2560x1600", "60", false, 348500, 2560, 2752, 3032, 3504, 1600, 1603, 1609, 1658, "N", "P", false),
        new(0x4E, "2560x1600", "75", false, 443250, 2560, 2768, 3048, 3536, 1600, 1603, 1609, 1672, "N", "P", false),
        new(0x4F, "2560x1600", "85", false, 505250, 2560, 2768, 3048, 3536, 1600, 1603, 1609, 1682, "N", "P", false),
        new(0x50, "2560x1600", "120", true, 552750, 2560, 2608, 2640, 2720, 1600, 1603, 1609, 1694, "P", "N", false),
        new(0x57, "4096x2160", "60", true, 556744, 4096, 4104, 4136, 4176, 2160, 2208, 2216, 2222, "P", "N", false),
        new(0x58, "4096x2160", "59.94", true, 556188, 4096, 4104, 4136, 4176, 2160, 2208, 2216, 2222, "P", "N", false),
    ];

    /// <summary>EDID 位元組 32–34 的 established timing 位圖：17 條（24 位中 7 位保留未定義，如實不列）。</summary>
    [SpecRef("VESA E-EDID v1.4 §3.8.1（Linux drm_edid.c edid_est_modes[] 對照）")]
    public static IReadOnlyList<DmtEst1Entry> EstablishedTiming1 { get; } =
    [
        new(0, 800, 600, "60", false),
        new(1, 800, 600, "56", false),
        new(2, 640, 480, "75", false),
        new(3, 640, 480, "72", false),
        new(4, 640, 480, "67", false),
        new(5, 640, 480, "60", false),
        new(6, 720, 400, "88", false),
        new(7, 720, 400, "70", false),
        new(8, 1280, 1024, "75", false),
        new(9, 1024, 768, "75", false),
        new(10, 1024, 768, "70", false),
        new(11, 1024, 768, "60", false),
        new(12, 1024, 768, "43", true),
        new(13, 832, 624, "75", false),
        new(14, 800, 600, "75", false),
        new(15, 800, 600, "72", false),
        new(16, 1152, 864, "75", false),
    ];

    /// <summary>Established Timing III 描述元位圖：44 條。</summary>
    [SpecRef("VESA E-EDID Established Timing III（Linux drm_edid.c est3_modes[] 對照）")]
    public static IReadOnlyList<DmtEst3Entry> EstablishedTiming3 { get; } =
    [
        new(0, 640, 350, "85", false),
        new(1, 640, 400, "85", false),
        new(2, 720, 400, "85", false),
        new(3, 640, 480, "85", false),
        new(4, 848, 480, "60", false),
        new(5, 800, 600, "85", false),
        new(6, 1024, 768, "85", false),
        new(7, 1152, 864, "75", false),
        new(8, 1280, 768, "60", true),
        new(9, 1280, 768, "60", false),
        new(10, 1280, 768, "75", false),
        new(11, 1280, 768, "85", false),
        new(12, 1280, 960, "60", false),
        new(13, 1280, 960, "85", false),
        new(14, 1280, 1024, "60", false),
        new(15, 1280, 1024, "85", false),
        new(16, 1360, 768, "60", false),
        new(17, 1440, 900, "60", true),
        new(18, 1440, 900, "60", false),
        new(19, 1440, 900, "75", false),
        new(20, 1440, 900, "85", false),
        new(21, 1400, 1050, "60", true),
        new(22, 1400, 1050, "60", false),
        new(23, 1400, 1050, "75", false),
        new(24, 1400, 1050, "85", false),
        new(25, 1680, 1050, "60", true),
        new(26, 1680, 1050, "60", false),
        new(27, 1680, 1050, "75", false),
        new(28, 1680, 1050, "85", false),
        new(29, 1600, 1200, "60", false),
        new(30, 1600, 1200, "65", false),
        new(31, 1600, 1200, "70", false),
        new(32, 1600, 1200, "75", false),
        new(33, 1600, 1200, "85", false),
        new(34, 1792, 1344, "60", false),
        new(35, 1792, 1344, "75", false),
        new(36, 1856, 1392, "60", false),
        new(37, 1856, 1392, "75", false),
        new(38, 1920, 1200, "60", true),
        new(39, 1920, 1200, "60", false),
        new(40, 1920, 1200, "75", false),
        new(41, 1920, 1200, "85", false),
        new(42, 1920, 1440, "60", false),
        new(43, 1920, 1440, "75", false),
    ];

    /// <summary>知識包版本與來源宣告（VC-0043）：解碼表隨規格修訂更新的通道。</summary>
    [SpecRef("同上三表的出處")]
    public static string Version =>
        "DMT v1.0r12（88 條）＋E-EDID established timing（17＋44 條）——經 Linux drm_edid.c 對照，kernel.org 2026-11-15";

    /// <summary>按代碼查模式；查不到回 <c>null</c>（不猜）。</summary>
    [SpecRef("同 Modes 表出處")]
    public static DmtModeInfo? FindByCode(int code)
    {
        foreach (var m in Modes)
            if (m.Code == code)
                return m;
        return null;
    }

    /// <summary>
    /// 按幾何查模式（EDID detailed timing 對照用）：寬、高、刷新率、RB、交錯五者全對才算命中；
    /// 刷新率以字串比較（59.94 這類非整數原樣比）。
    /// </summary>
    [SpecRef("同 Modes 表出處；「全欄位都對才命中」沿知識包三態規則")]
    public static DmtModeInfo? FindByGeometry(int width, int height, string refreshHz,
        bool reducedBlanking = false, bool interlaced = false)
    {
        foreach (var m in Modes)
            if (m.HActive == width && m.VActive == height && m.RefreshHz == refreshHz
                && m.ReducedBlanking == reducedBlanking && m.Interlaced == interlaced)
                return m;
        return null;
    }

    /// <summary>est3 位圖條目對應的 DMT 代碼；對不到回 <c>null</c>——這是自校的一部分，不是繞過。</summary>
    [SpecRef("同 EstablishedTiming3 表出處")]
    public static int? ResolveEst3(DmtEst3Entry e) =>
        FindByGeometry(e.Width, e.Height, e.RefreshHz, e.ReducedBlanking)?.Code;

    /// <summary>
    /// est1（位元組 32–34）條目對應的 DMT 代碼。<b>對不到是正常且如實的</b>：640x480@67（Apple）、
    /// 832x624@75（Apple）、720x400@88、1024x768@43i 不在 DMT 表裡——那些是 DMT 之前的遺產模式。
    /// </summary>
    [SpecRef("同 EstablishedTiming1 表出處；非 DMT 遺產模式沿主目錄 VC-0021 的碼號定案")]
    public static int? ResolveEst1(DmtEst1Entry e) =>
        FindByGeometry(e.Width, e.Height, e.RefreshHz, reducedBlanking: false, interlaced: e.Interlaced)?.Code;
}
