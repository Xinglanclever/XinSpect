namespace XinSpect;

/// <summary>SPD 裡的 JEDEC 廠商識別（兩個位元組：continuation 數與代碼，各帶奇同位）。</summary>
/// <param name="Raw">原樣的兩個位元組（高位＝continuation）。</param>
/// <param name="ParityOk">兩個位元組的奇同位是否都正確。不正確代表這一格沒照規格寫——本身就是線索。</param>
public sealed record SpdManufacturer(ushort Raw, int Bank, byte Code, bool ParityOk, string Name);

/// <summary>SPD 的一段 CRC。<see cref="Valid"/> 為 false 就是「這段被改過而沒有重算校驗」。</summary>
public sealed record SpdCrc(ushort Stored, ushort Computed, int SpanBytes)
{
    public bool Valid => Stored == Computed;
}

/// <param name="Revision">以位元組 387 的高低半位元組表示，例如 0x20 → "2.0"。</param>
/// <param name="ProfileCount">位元組 386 低兩位的啟用位圖裡有幾個 1。</param>
public sealed record SpdXmp(string Revision, int ProfileCount, byte RawFlags, byte RawRevision);

/// <summary>一條 DDR4 模組 SPD 的型別化快照。所有位移只在 <see cref="SpdDecoder"/> 出現一次。</summary>
public sealed record SpdSnapshot(
    byte TypeCode,
    SpdCrc BaseCrc,
    SpdCrc ModuleCrc,
    SpdManufacturer ModuleManufacturer,
    SpdManufacturer DramManufacturer,
    byte ManufacturingLocation,
    int? ManufactureYear,
    int? ManufactureWeek,
    string SerialHex,
    string PartNumber,
    byte ModuleRevision,
    byte DramStepping,
    SpdXmp? Xmp,
    SpdGeometry Geometry,
    SpdTimings Timings,
    IReadOnlyList<SpdXmpProfile> XmpProfiles);

/// <summary>
/// DDR4 SPD 的純函式解碼器（JEDEC SPD Annex L）。
/// </summary>
/// <remarks>
/// <para>
/// 純函式、只吃 <c>byte[]</c>，因此可以用實機的真實位元組驗證位移——基準檔在
/// <c>Tests/Fixtures/spd-ddr4-real-dimm{1,2,3}.bin</c>，而同一份來源裡還有 CPU-Z 對同一串
/// 位元組的獨立解讀可以對帳。這條分界是 1.9.1-B1 的教訓：位移只能出現一次，而且要有
/// 獨立來源可比對，否則測試會照著錯誤的實作去合成資料，把 bug 一起鎖死。
/// </para>
/// <para>
/// <b>不合法就回 null，不給替代值。</b>製造日期是 BCD，欄位沒燒時是 0x00——把它當有效值
/// 會解出「2000 年第 0 週」，那是個看起來很像真的假結論，而驗機的誤判代價是不對稱的。
/// </para>
/// </remarks>
public static class SpdDecoder
{
    // 位移：整份程式只有這裡有這些數字
    private const int OffCrcCoverage = 0, OffTypeCode = 2;
    private const int OffBaseCrc = 126, OffModuleCrc = 254;
    private const int OffModuleMfr = 320, OffLocation = 322, OffYear = 323, OffWeek = 324;
    private const int OffSerial = 325, OffPartNumber = 329, OffModuleRevision = 349;
    private const int OffDramMfr = 350, OffDramStepping = 352;
    private const int OffXmp = 384;

    private const int PartNumberLength = 20, SerialLength = 4;
    private const ushort XmpMagic = 0x0C4A;

    /// <summary>解不出來就回 null（長度不足、不是 DDR4）。</summary>
    public static SpdSnapshot? Decode(byte[] raw)
    {
        if (raw.Length < SpdReader.Ddr4Size) return null;
        if (raw[OffTypeCode] != SpdReader.Ddr4TypeCode) return null;

        // 位元組 0 的 bit 7 宣告基本段 CRC 涵蓋到哪：0＝0–125、1＝0–116
        int baseSpan = (raw[OffCrcCoverage] & 0x80) != 0 ? 117 : 126;

        var (year, week) = DecodeDate(raw[OffYear], raw[OffWeek]);

        return new SpdSnapshot(
            TypeCode: raw[OffTypeCode],
            BaseCrc: new SpdCrc(ReadLe16(raw, OffBaseCrc), Crc16(raw.AsSpan(0, baseSpan)), baseSpan),
            ModuleCrc: new SpdCrc(ReadLe16(raw, OffModuleCrc), Crc16(raw.AsSpan(128, 126)), 126),
            ModuleManufacturer: DecodeManufacturer(raw, OffModuleMfr),
            DramManufacturer: DecodeManufacturer(raw, OffDramMfr),
            ManufacturingLocation: raw[OffLocation],
            ManufactureYear: year,
            ManufactureWeek: week,
            SerialHex: Convert.ToHexString(raw, OffSerial, SerialLength),
            PartNumber: DecodePartNumber(raw),
            ModuleRevision: raw[OffModuleRevision],
            DramStepping: raw[OffDramStepping],
            Xmp: DecodeXmp(raw),
            Geometry: SpdDetailDecoder.Geometry(raw),
            Timings: SpdDetailDecoder.Timings(raw),
            XmpProfiles: SpdDetailDecoder.XmpProfiles(raw));
    }

    /// <summary>
    /// JEDEC SPD 用的 CRC-16／XMODEM（多項式 0x1021、初值 0、不反轉、不做最終 XOR）。
    /// </summary>
    /// <remarks>這個實作用三條實機模組的兩段共六個值對過，全部相符。</remarks>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
        }
        return (ushort)crc;
    }

    /// <summary>
    /// 已驗證的 JEP106 廠商代碼。<b>刻意只收確認過的。</b>
    /// </summary>
    /// <remarks>
    /// 驗機工具給出錯的廠名，比誠實地說「未知」糟得多——使用者會拿那個名字去跟賣家對質。
    /// 新增一列的條件是：手上有那家廠的實機模組，或有可對帳的獨立解讀（例如 CPU-Z 報告）。
    /// SK Hynix 這一列就是這樣來的：本機三條模組讀到 0x80AD，CPU-Z 對同一串位元組也報 SK Hynix。
    /// </remarks>
    private static readonly Dictionary<(int Bank, byte Code), string> Names = new()
    {
        [(1, 0x2C)] = "Micron",
        [(1, 0x2D)] = "SK Hynix",
        [(1, 0x4E)] = "Samsung",
        [(1, 0x0B)] = "Nanya",
    };

    private static SpdManufacturer DecodeManufacturer(byte[] raw, int offset)
    {
        byte hi = raw[offset], lo = raw[offset + 1];
        int bank = (hi & 0x7F) + 1;
        byte code = (byte)(lo & 0x7F);
        bool parityOk = HasOddParity(hi) && HasOddParity(lo);
        string name = Names.TryGetValue((bank, code), out var n)
            ? n
            : $"未知（bank {bank}，代碼 0x{code:X2}）";
        return new SpdManufacturer((ushort)((hi << 8) | lo), bank, code, parityOk, name);
    }

    private static bool HasOddParity(byte b) => System.Numerics.BitOperations.PopCount(b) % 2 == 1;

    /// <summary>製造年／週是 BCD。任一邊不合法就兩邊都回 null——半個日期不是日期。</summary>
    private static (int? Year, int? Week) DecodeDate(byte year, byte week)
    {
        if (!IsBcd(year) || !IsBcd(week)) return (null, null);
        int y = FromBcd(year), w = FromBcd(week);
        if (y == 0 || w is < 1 or > 53) return (null, null);
        return (2000 + y, w);
    }

    private static bool IsBcd(byte b) => (b & 0x0F) <= 9 && (b >> 4) <= 9;
    private static int FromBcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

    /// <summary>料號是 20 位元組 ASCII。出現不可列印字元就當作沒有料號，不硬轉。</summary>
    private static string DecodePartNumber(byte[] raw)
    {
        var chars = new char[PartNumberLength];
        int n = 0;
        for (int i = 0; i < PartNumberLength; i++)
        {
            byte b = raw[OffPartNumber + i];
            if (b == 0x00) break;                       // 有些模組用 0 補位而不是空白
            if (b is < 0x20 or > 0x7E) return "";
            chars[n++] = (char)b;
        }
        return new string(chars, 0, n).TrimEnd();
    }

    private static SpdXmp? DecodeXmp(byte[] raw)
    {
        if (((raw[OffXmp] << 8) | raw[OffXmp + 1]) != XmpMagic) return null;
        byte flags = raw[OffXmp + 2], rev = raw[OffXmp + 3];
        // 低兩位是啟用位圖（bit0＝第一組、bit1＝第二組）。這個讀法對過 CPU-Z：
        // 本機第一條的 0x17 → 兩組（CPU-Z 列出 XMP-3602 與 XMP-4000），第二條的 0x05 → 一組。
        int profiles = System.Numerics.BitOperations.PopCount((uint)(flags & 0x03));
        return new SpdXmp($"{rev >> 4}.{rev & 0x0F}", profiles, flags, rev);
    }

    private static ushort ReadLe16(byte[] raw, int offset) => (ushort)(raw[offset] | (raw[offset + 1] << 8));
}
