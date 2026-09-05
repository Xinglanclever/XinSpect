namespace XinSpect;

/// <summary>
/// SPD 的幾何、JEDEC 時序與 XMP 逐組 profile 解碼。
/// </summary>
/// <remarks>
/// <para>
/// 這一層讓記憶體頁不必再依賴 CPU-Z。<c>ViewModels/StartupSequence.cs</c> 目前是呼叫
/// <c>CpuzReportService.ReadAsync()</c> 解析報告文字取得這些欄位的，也就是要使用者先裝 CPU-Z
/// 並跑過一次；而它們全都在我們自己讀得到的 512 位元組裡。
/// </para>
/// <para>
/// 時間單位：MTB（Medium Timebase）＝125 ps，FTB（Fine Timebase）＝1 ps 的<b>有號</b>細調。
/// 實際值＝MTB 倍數 × 125 ＋ FTB。所有輸出都是整數皮秒，不轉浮點——原始值本來就是整數。
/// </para>
/// <para>
/// <b>XMP 的細調只套用在 tCKmin 上。</b>那一組 9 個 FTB 位元組的排列是照基本段（位元組 117–125）
/// 的順序推出來的，但只有 tCKmin 那一格有獨立來源可對（本機三組 profile 分別算出 0.555／0.500／
/// 0.500 ns，與 CPU-Z 及料號都相符）。其餘位置若套錯，會產出一個<i>看起來對</i>的錯數字；
/// 不套的話最多粗 125 ps。驗機工具寧可粗而正確。
/// </para>
/// </remarks>
public static class SpdDetailDecoder
{
    private const int Mtb = 125;                    // 皮秒

    // 基本段
    private const int OffModuleType = 3, OffDensity = 4, OffNominalVdd = 11, OffOrganization = 12, OffBusWidth = 13;
    private const int OffTckMin = 18, OffTckMax = 19, OffCasFirst = 20;
    private const int OffTaa = 24, OffTrcd = 25, OffTrp = 26, OffTrasTrcMsn = 27, OffTras = 28, OffTrc = 29;
    private const int OffTrfc1 = 30, OffTrfc2 = 32, OffTrfc4 = 34, OffTfawMsn = 36, OffTfaw = 37;
    private const int OffTrrdS = 38, OffTrrdL = 39, OffTccdL = 40;
    private const int OffTwrMsn = 41, OffTwr = 42, OffTwtrMsn = 43, OffTwtrS = 44, OffTwtrL = 45;

    // 基本段的 FTB（位元組 117–125，順序與上面各項對應）
    private const int FtbTccdL = 117, FtbTrrdL = 118, FtbTrrdS = 119, FtbTrc = 120;
    private const int FtbTrp = 121, FtbTrcd = 122, FtbTaa = 123, FtbTckMax = 124, FtbTckMin = 125;

    // XMP
    private const int OffXmpMagic = 384, OffXmpEnable = 386;
    private const int XmpMagic = 0x0C4A;
    private const int XmpProfileBase = 393, XmpProfileStride = 47, XmpMaxProfiles = 2;
    private const int PVdd = 0, PTckMin = 3, PTaa = 8, PTrcd = 9, PTrp = 10;
    private const int PTrasTrcMsn = 11, PTras = 12, PTrc = 13, PTrrdS = 22, PFtbTckMin = 38;

    /// <summary>顆粒密度（位元組 4 低四位）→ Mbit。0xF 之類的保留值回 0。</summary>
    private static int DensityMbits(byte b) => (b & 0x0F) switch
    {
        0 => 256, 1 => 512, 2 => 1024, 3 => 2048, 4 => 4096,
        5 => 8192, 6 => 16384, 7 => 32768, 8 => 12288, 9 => 24576,
        _ => 0,
    };

    public static SpdGeometry Geometry(byte[] raw)
    {
        var type = (raw[OffModuleType] & 0x0F) switch
        {
            0x01 => SpdModuleType.Rdimm,
            0x02 => SpdModuleType.Udimm,
            0x03 => SpdModuleType.SoDimm,
            0x04 => SpdModuleType.LrDimm,
            0x05 => SpdModuleType.MiniRdimm,
            0x06 => SpdModuleType.MiniUdimm,
            0x08 => SpdModuleType.SoRdimm72Bit,
            0x09 => SpdModuleType.SoUdimm72Bit,
            0x0C => SpdModuleType.SoDimm16Bit,
            0x0D => SpdModuleType.SoDimm32Bit,
            _ => SpdModuleType.Unknown,
        };

        int density = DensityMbits(raw[OffDensity]);
        int deviceWidth = (raw[OffOrganization] & 0x07) switch { 0 => 4, 1 => 8, 2 => 16, 3 => 32, _ => 0 };
        int ranks = ((raw[OffOrganization] >> 3) & 0x07) + 1;
        int busWidth = (raw[OffBusWidth] & 0x07) switch { 0 => 8, 1 => 16, 2 => 32, 3 => 64, _ => 0 };
        int ecc = ((raw[OffBusWidth] >> 3) & 0x03) == 1 ? 8 : 0;

        // 任一格是保留值就算不出容量——回 0 讓上層說「讀不到」，不拿看起來合理的數字充數
        int capacity = density > 0 && deviceWidth > 0 && busWidth > 0
            ? density / 8 * (busWidth / deviceWidth) * ranks
            : 0;

        // 位元組 11 的 bit 0＝「1.2 V 可運作」。沒宣告就回 0，不因為「DDR4 本來就是 1.2 V」而補值。
        int millivolts = (raw[OffNominalVdd] & 0x01) != 0 ? 1200 : 0;

        return new SpdGeometry(type, density, ranks, deviceWidth, busWidth, ecc, capacity, millivolts);
    }

    public static SpdTimings Timings(byte[] raw)
    {
        int Fine(int mtbOffset, int ftbOffset) => raw[mtbOffset] * Mtb + (sbyte)raw[ftbOffset];
        int Wide(int msnOffset, int lsbOffset, bool upperNibble = false)
        {
            int msn = upperNibble ? (raw[msnOffset] >> 4) & 0x0F : raw[msnOffset] & 0x0F;
            return ((msn << 8) | raw[lsbOffset]) * Mtb;
        }

        var cas = new List<int>(24);
        for (int i = 0; i < 32; i++)
            if ((raw[OffCasFirst + i / 8] & (1 << (i % 8))) != 0) cas.Add(7 + i);

        return new SpdTimings(
            TckMinPs: Fine(OffTckMin, FtbTckMin),
            TckMaxPs: Fine(OffTckMax, FtbTckMax),
            SupportedCas: cas,
            TaaPs: Fine(OffTaa, FtbTaa),
            TrcdPs: Fine(OffTrcd, FtbTrcd),
            TrpPs: Fine(OffTrp, FtbTrp),
            TrasPs: Wide(OffTrasTrcMsn, OffTras),
            TrcPs: Wide(OffTrasTrcMsn, OffTrc, upperNibble: true) + (sbyte)raw[FtbTrc],
            TrrdSPs: Fine(OffTrrdS, FtbTrrdS),
            TrrdLPs: Fine(OffTrrdL, FtbTrrdL),
            TccdLPs: Fine(OffTccdL, FtbTccdL),
            TwrPs: Wide(OffTwrMsn, OffTwr),
            TwtrSPs: Wide(OffTwtrMsn, OffTwtrS),
            TwtrLPs: Wide(OffTwtrMsn, OffTwtrL, upperNibble: true),
            Trfc1Ps: (raw[OffTrfc1 + 1] << 8 | raw[OffTrfc1]) * Mtb,
            Trfc2Ps: (raw[OffTrfc2 + 1] << 8 | raw[OffTrfc2]) * Mtb,
            Trfc4Ps: (raw[OffTrfc4 + 1] << 8 | raw[OffTrfc4]) * Mtb,
            TfawPs: Wide(OffTfawMsn, OffTfaw));
    }

    /// <summary>解出啟用的 XMP profile。沒有 XMP 或那一組看起來無效時就不列出來。</summary>
    public static List<SpdXmpProfile> XmpProfiles(byte[] raw)
    {
        var list = new List<SpdXmpProfile>(XmpMaxProfiles);
        // 沒有魔術數就沒有 XMP：那時候位元組 386 是別的東西，拿它當啟用位圖會憑空生出 profile
        if (((raw[OffXmpMagic] << 8) | raw[OffXmpMagic + 1]) != XmpMagic) return list;
        byte enable = raw[OffXmpEnable];

        for (int i = 0; i < XmpMaxProfiles; i++)
        {
            if ((enable & (1 << i)) == 0) continue;
            int p = XmpProfileBase + i * XmpProfileStride;

            int tck = raw[p + PTckMin] * Mtb + (sbyte)raw[p + PFtbTckMin];
            if (tck <= 0) continue;                       // 旗標說有，內容卻是空的——不編出一組來

            int msn = raw[p + PTrasTrcMsn];
            list.Add(new SpdXmpProfile(
                Index: i,
                Millivolts: 1000 + (raw[p + PVdd] & 0x7F) * 10,
                RawVdd: raw[p + PVdd],
                TckMinPs: tck,
                TaaPs: raw[p + PTaa] * Mtb,
                TrcdPs: raw[p + PTrcd] * Mtb,
                TrpPs: raw[p + PTrp] * Mtb,
                TrasPs: (((msn & 0x0F) << 8) | raw[p + PTras]) * Mtb,
                TrcPs: ((((msn >> 4) & 0x0F) << 8) | raw[p + PTrc]) * Mtb,
                TrrdSPs: raw[p + PTrrdS] * Mtb));
        }
        return list;
    }
}
