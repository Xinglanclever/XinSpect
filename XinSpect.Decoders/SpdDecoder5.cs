namespace XinSpect;

/// <summary>
/// DDR5 SPD 的純函式解碼器（JEDEC JESD400-5 DDR5 SPD Contents）。
/// </summary>
/// <remarks>
/// <para>
/// 與 <see cref="SpdDecoder"/>（DDR4）同一條紀律：純函式、只吃 <c>byte[]</c>、
/// 位移只在這裡出現一次、不合法就回 null 不給替代值。
/// 佈局與編碼規則逐欄對照過開源參照（The-Open-Memory-Initiative 的 spdr JESD400-5 解碼器，
/// 與 Linux 核心 drivers/hwmon/spd5118.c 的暫存器定義），2026-10-07 抓取。
/// </para>
/// <para>
/// <b>未在本機驗證</b>：本機是 DDR4 平台，沒有 DDR5 模組可以拿真實位元組對帳。
/// 金標向量是從編碼規則手算的合成資料；第一條實機 DDR5 到手時應以真實 dump 重建基準檔。
/// </para>
/// </remarks>
public static class SpdDecoder5
{
    // ── 位移：JESD400-5（DDR5 SPD Contents）；整份程式只有這裡有這些數字 ──
    /// <summary>DDR5 的 key type＝0x12（JESD400-5）。</summary>
    public const byte Ddr5TypeCode = 0x12;

    private const int OffSpdSize = 0;          // bits[6:4]＝SPD 裝置總大小
    private const int OffRevision = 1;         // SPD revision（兩個純 nibble，非 BCD）
    private const int OffDeviceType = 2;       // Key type＝0x12（DDR5）
    private const int OffModuleType = 3;       // bits[3:0] 模組型別、bit7 hybrid
    private const int OffDensityPackage = 4;   // bits[4:0] 每晶粒密度、bits[7:5] 封裝
    private const int OffAddressing = 5;       // bits[4:0] 列位址（基 16）、bits[7:5] 行位址（基 10）
    private const int OffIoWidth = 6;          // bits[7:5] I/O 寬度
    private const int OffBankGroups = 7;       // bits[7:5] bank group 數、bits[2:0] 每組 bank 數
    private const int OffTckAvgMin = 20;       // 以下時序皆 16-bit LE，1 ps 粒度（DDR5 無 MTB/FTB）
    private const int OffTckAvgMax = 22;
    private const int OffCasLatencies = 24;    // 5 bytes＝40-bit 遮罩；bit i → CL 20+2i
    private const int OffTaa = 30, OffTrcd = 32, OffTrp = 34, OffTras = 36, OffTrc = 38, OffTwr = 40;
    private const int OffTrfc1 = 42, OffTrfc2 = 44, OffTrfcSb = 46; // tRFC 族：16-bit LE，1 ns 粒度
    // bank-group 類時序：[ps u16][nCK u8] 三元組
    private const int OffTRrdL = 70, OffTCcdL = 73, OffTCcdLWr = 76, OffTCcdLWr2 = 79,
                      OffTFaw = 82, OffTWtrL = 85, OffTWtrS = 88, OffTRtp = 91;
    private const int OffModuleOrganization = 234; // bits[5:3] 每通道 rank（基 1）、bit6 非對稱
    private const int OffMemoryChannelBusWidth = 235; // bits[2:0] 每通道匯流排寬、bits[7:5] 每模組通道數
    // 製造區（DDR5 放在 512 起，與 DDR4 的 320 起不同）
    private const int OffModuleMfrId = 512, OffMfrLocation = 514, OffMfrYear = 515, OffMfrWeek = 516;
    private const int OffSerial = 517, OffPartNumber = 521, OffPartNumberLength = 30;
    private const int OffModuleRevision = 551, OffDramMfrId = 552, OffDramStepping = 554;
    private const int OffBaseCrc = 510;        // 基本段 CRC 涵蓋 0–509
    private const int BaseCrcCoveredEnd = 509; // （含）
    private const int MinContentSize = 555;    // 進製造區的最小長度

    /// <summary>解不出來就回 null（長度不足、不是 DDR5）。</summary>
    public static Ddr5SpdSnapshot? Decode(byte[] raw)
    {
        if (raw.Length < MinContentSize) return null;
        if (raw[OffDeviceType] != Ddr5TypeCode) return null;

        var date = DecodeDate(raw[OffMfrYear], raw[OffMfrWeek]);

        return new Ddr5SpdSnapshot(
            SpdSizeBytes: DecodeSpdSize(raw[OffSpdSize]),
            SpdRevision: $"{raw[OffRevision] >> 4:X}.{raw[OffRevision] & 0x0F:X}",
            ModuleType: DecodeModuleType(raw[OffModuleType], out bool hybrid),
            Hybrid: hybrid,
            DensityGigabitsPerDie: DecodeDensity(raw[OffDensityPackage]),
            Package: DecodePackage(raw[OffDensityPackage], out int dies),
            DieCount: dies,
            RowAddressBits: (byte)(16 + (raw[OffAddressing] & 0x1F)),
            ColumnAddressBits: (byte)(10 + ((raw[OffAddressing] >> 5) & 0x07)),
            IoWidth: DecodeIoWidth(raw[OffIoWidth]),
            BankGroups: DecodeBankGroups(raw[OffBankGroups]),
            BanksPerBankGroup: DecodeBanksPerGroup(raw[OffBankGroups]),
            RanksPerChannel: (byte)(((raw[OffModuleOrganization] >> 3) & 0x07) + 1),
            RankMixAsymmetric: (raw[OffModuleOrganization] & 0x40) != 0,
            ChannelsPerDimm: DecodeChannels(raw[OffMemoryChannelBusWidth]),
            PrimaryBusWidthBits: DecodeBusWidth(raw[OffMemoryChannelBusWidth]),
            TckAvgMinPs: ReadLe16(raw, OffTckAvgMin),
            TckAvgMaxPs: ReadLe16(raw, OffTckAvgMax),
            CasLatencies: DecodeCasLatencies(raw, OffCasLatencies),
            TaaPs: ReadLe16(raw, OffTaa),
            TrcdPs: ReadLe16(raw, OffTrcd),
            TrpPs: ReadLe16(raw, OffTrp),
            TrasPs: ReadLe16(raw, OffTras),
            TrcPs: ReadLe16(raw, OffTrc),
            TwrPs: ReadLe16(raw, OffTwr),
            Trfc1Ns: ReadLe16(raw, OffTrfc1),
            Trfc2Ns: ReadLe16(raw, OffTrfc2),
            TrfcSbNs: ReadLe16(raw, OffTrfcSb),
            TRrdL: DecodePair(raw, OffTRrdL),
            TCcdL: DecodePair(raw, OffTCcdL),
            TCcdLWr: DecodePair(raw, OffTCcdLWr),
            TCcdLWr2: DecodePair(raw, OffTCcdLWr2),
            TFaw: DecodePair(raw, OffTFaw),
            TWtrL: DecodePair(raw, OffTWtrL),
            TWtrS: DecodePair(raw, OffTWtrS),
            TRtp: DecodePair(raw, OffTRtp),
            ModuleManufacturer: DecodeManufacturer(raw, OffModuleMfrId),
            DramManufacturer: DecodeManufacturer(raw, OffDramMfrId),            ManufacturingLocation: raw[OffMfrLocation],
            ManufactureYear: date.Year,
            ManufactureWeek: date.Week,
            SerialHex: Convert.ToHexString(raw, OffSerial, 4),
            PartNumber: DecodePartNumber(raw),
            ModuleRevision: raw[OffModuleRevision],
            DramStepping: raw[OffDramStepping],
            BaseCrc: new Ddr5Crc(ReadLe16(raw, OffBaseCrc), Crc16(raw.AsSpan(0, BaseCrcCoveredEnd + 1)), BaseCrcCoveredEnd + 1));
    }

    /// <summary>
    /// JEDEC SPD 用的 CRC-16／XMODEM（多項式 0x1021、初值 0、不反轉、不做最終 XOR）——
    /// 與 DDR3／DDR4／DDR5 共通。檢查值："123456789" → 0x31C3。
    /// </summary>
    /// <remarks>演算法與 <see cref="SpdDecoder.Crc16"/> 相同；兩個專案各留一份，避免 Decoders 反向依賴主程式。</remarks>
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

    private static ushort ReadLe16(byte[] raw, int offset) => (ushort)(raw[offset] | (raw[offset + 1] << 8));

    /// <summary>byte 0 bits[6:4]：SPD 裝置總大小（1→256B、2→512B、3→1024B、4→2048B）。</summary>
    private static int DecodeSpdSize(byte b) => ((b >> 4) & 0x07) switch
    {
        1 => 256, 2 => 512, 3 => 1024, 4 => 2048,
        _ => 0,
    };

    /// <summary>byte 3 bits[3:0]：模組型別。未收錄的編碼回「未知（0x_）」字串——不猜。</summary>
    private static string DecodeModuleType(byte b, out bool hybrid)
    {
        hybrid = (b & 0x80) != 0;
        return (b & 0x0F) switch
        {
            0x01 => "RDIMM",
            0x02 => "UDIMM",
            0x03 => "SODIMM",
            0x04 => "LRDIMM",
            0x05 => "CUDIMM",
            0x06 => "CSOUDIMM",
            0x07 => "MRDIMM",
            0x08 => "CAMM2",
            0x0A => "DDIMM",
            0x0B => "solder down",
            var v => $"未知（0x{v:X}）",
        };
    }

    /// <summary>byte 4 bits[4:0]：每晶粒密度（Gb）。未收錄回 0。</summary>
    private static int DecodeDensity(byte b) => (b & 0x1F) switch
    {
        0x01 => 4, 0x02 => 8, 0x03 => 12, 0x04 => 16,
        0x05 => 24, 0x06 => 32, 0x07 => 48, 0x08 => 64,
        _ => 0,
    };

    private static string DecodePackage(byte b, out int dieCount)
    {
        switch ((b >> 5) & 0x07)
        {
            case 0: dieCount = 1; return "Monolithic";
            case 1: dieCount = 2; return "DDP";
            case 2: dieCount = 2; return "3DS";
            case 3: dieCount = 4; return "3DS";
            case 4: dieCount = 8; return "3DS";
            case 5: dieCount = 16; return "3DS";
            default: dieCount = 0; return "未知";
        }
    }

    private static int DecodeIoWidth(byte b) => ((b >> 5) & 0x07) switch
    {
        0 => 4, 1 => 8, 2 => 16, 3 => 32, _ => 0,
    };

    private static int DecodeBankGroups(byte b) => ((b >> 5) & 0x07) switch
    {
        0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 0,
    };

    private static int DecodeBanksPerGroup(byte b) => (b & 0x07) switch
    {
        0 => 1, 1 => 2, 2 => 4, _ => 0,
    };

    private static int DecodeChannels(byte b) => ((b >> 5) & 0x07) switch
    {
        0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 0,
    };

    private static int DecodeBusWidth(byte b) => (b & 0x07) switch
    {
        0 => 8, 1 => 16, 2 => 32, 3 => 64, _ => 0,
    };

    /// <summary>CAS latencies：5-byte LE 40-bit 遮罩，bit i → CL 20+2i。</summary>
    private static IReadOnlyList<int> DecodeCasLatencies(byte[] raw, int offset)
    {
        var list = new List<int>();
        for (int byteIndex = 0; byteIndex < 5; byteIndex++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                if ((raw[offset + byteIndex] & (1 << bit)) != 0)
                    list.Add(20 + 2 * (byteIndex * 8 + bit));
            }
        }
        return list;
    }

    private static Ddr5TimingPair DecodePair(byte[] raw, int offset) =>
        new(ReadLe16(raw, offset), raw[offset + 2]);

    private static Ddr5Manufacturer DecodeManufacturer(byte[] raw, int offset)
    {
        byte hi = raw[offset], lo = raw[offset + 1];
        int bank = (hi & 0x7F) + 1;
        byte code = (byte)(lo & 0x7F);
        bool parityOk = System.Numerics.BitOperations.PopCount(hi) % 2 == 1
                     && System.Numerics.BitOperations.PopCount(lo) % 2 == 1;
        string name = KnownManufacturer(bank, code)
            ?? $"未知（bank {bank}，代碼 0x{code:X2}）";
        return new Ddr5Manufacturer((ushort)((hi << 8) | lo), bank, code, parityOk, name);
    }

    /// <summary>
    /// 已驗證的 JEP106 廠商代碼（與 <see cref="SpdDecoder"/> 同一份原則：只收確認過的——
    /// 該表在主程式，Decoders 庫不反向依賴，故此處獨立收錄）。
    /// </summary>
    private static readonly Dictionary<(int Bank, byte Code), string> Names = new()
    {
        [(1, 0x2C)] = "Micron",
        [(1, 0x2D)] = "SK Hynix",
        [(1, 0x4E)] = "Samsung",
        [(1, 0x0B)] = "Nanya",
    };

    private static string? KnownManufacturer(int bank, byte code) =>
        Names.TryGetValue((bank, code), out var name) ? name : null;

    /// <summary>製造年／週是 BCD。任一邊不合法就兩邊都回 null——半個日期不是日期（與 DDR4 同規則）。</summary>
    private static (int? Year, int? Week) DecodeDate(byte year, byte week)
    {
        int? Year() { int? v = FromBcd(year); return v is { } x and > 0 ? 2000 + x : null; }
        int? Week() { int? v = FromBcd(week); return v is { } x and >= 1 and <= 53 ? x : null; }
        var y = Year();
        var w = Week();
        if (y is null || w is null) return (null, null);
        return (y, w);
    }

    private static int? FromBcd(byte b)
    {
        if ((b & 0x0F) > 9 || (b >> 4) > 9) return null;
        return (b >> 4) * 10 + (b & 0x0F);
    }

    /// <summary>料號是 30 位元組 ASCII（DDR4 是 20）。出現不可列印字元就當作沒有料號，不硬轉。</summary>
    private static string DecodePartNumber(byte[] raw)
    {
        var chars = new char[OffPartNumberLength];
        int n = 0;
        for (int i = 0; i < OffPartNumberLength; i++)
        {
            byte b = raw[OffPartNumber + i];
            if (b == 0x00) break;
            if (b is < 0x20 or > 0x7E) return "";
            chars[n++] = (char)b;
        }
        return new string(chars, 0, n).TrimEnd();
    }
}

/// <summary>SPD 裡的 JEDEC 廠商識別（JEP106：continuation 數與代碼，各帶奇同位）。</summary>
public sealed record Ddr5Manufacturer(ushort Raw, int Bank, byte Code, bool ParityOk, string Name);

/// <summary>SPD 一段 CRC。<see cref="Valid"/> 為 false 就是「這段被改過而沒有重算校驗」。</summary>
public sealed record Ddr5Crc(ushort Stored, ushort Computed, int SpanBytes)
{
    public bool Valid => Stored == Computed;
}

/// <summary>bank-group 類時序：皮秒下限＋時鐘數下限，控制器取兩者的較大值。</summary>
/// <param name="TimePs">絕對時間下限（皮秒）。</param>
/// <param name="ClocksNck">時鐘數下限（nCK）。</param>
public sealed record Ddr5TimingPair(int TimePs, byte ClocksNck);

/// <summary>一條 DDR5 模組 SPD 的型別化快照。所有位移只在 <see cref="SpdDecoder5"/> 出現一次。</summary>
/// <remarks>容量推算：density × dieCount × ranksPerChannel × channelsPerDimm × (busWidth / ioWidth) / 8 ＝ GB。</remarks>
public sealed record Ddr5SpdSnapshot(
    int SpdSizeBytes,
    string SpdRevision,
    string ModuleType,
    bool Hybrid,
    int DensityGigabitsPerDie,
    string Package,
    int DieCount,
    byte RowAddressBits,
    byte ColumnAddressBits,
    int IoWidth,
    int BankGroups,
    int BanksPerBankGroup,
    byte RanksPerChannel,
    bool RankMixAsymmetric,
    int ChannelsPerDimm,
    int PrimaryBusWidthBits,
    int TckAvgMinPs,
    int TckAvgMaxPs,
    IReadOnlyList<int> CasLatencies,
    int TaaPs,
    int TrcdPs,
    int TrpPs,
    int TrasPs,
    int TrcPs,
    int TwrPs,
    int Trfc1Ns,
    int Trfc2Ns,
    int TrfcSbNs,
    Ddr5TimingPair TRrdL,
    Ddr5TimingPair TCcdL,
    Ddr5TimingPair TCcdLWr,
    Ddr5TimingPair TCcdLWr2,
    Ddr5TimingPair TFaw,
    Ddr5TimingPair TWtrL,
    Ddr5TimingPair TWtrS,
    Ddr5TimingPair TRtp,
    Ddr5Manufacturer ModuleManufacturer,
    Ddr5Manufacturer DramManufacturer,
    byte ManufacturingLocation,
    int? ManufactureYear,
    int? ManufactureWeek,
    string SerialHex,
    string PartNumber,
    byte ModuleRevision,
    byte DramStepping,
    Ddr5Crc BaseCrc)
{
    /// <summary>基準資料率（MT/s）：2,000,000 / tCKmin，四捨五入到 100。</summary>
    public int BaseDataRateMtS => TckAvgMinPs == 0 ? 0 : (2_000_000 / TckAvgMinPs + 50) / 100 * 100;

    /// <summary>模組容量推算（MiB）：density × dieCount × ranksPerChannel × channelsPerDimm × (busWidth / ioWidth) × 128（1 Gb＝128 MiB）。各欄位有未收錄編碼（0 值）時回 0——不猜。</summary>
    public long CapacityMib
        => DensityGigabitsPerDie > 0 && DieCount > 0 && IoWidth > 0 && PrimaryBusWidthBits > 0 && ChannelsPerDimm > 0
            ? (long)DensityGigabitsPerDie * DieCount * RanksPerChannel * ChannelsPerDimm
              * PrimaryBusWidthBits / IoWidth * 128
            : 0;
}
