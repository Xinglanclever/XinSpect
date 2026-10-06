namespace XinSpect;

/// <summary>
/// PMBus（PMBus Power System Management Protocol Specification, Rev 1.3）資料格式的純解碼器。
/// READ_* 命令回 16-bit 補數：LINEAR11（指數 5-bit＋尾數 11-bit）或 LINEAR16（VOUT 搭配 VOUT_MODE 的指數）。
/// </summary>
public static class PmbusDecoder
{
    /// <summary>
    /// LINEAR11：bits[15:11]＝指數（5-bit 二補數）、bits[10:0]＝尾數（11-bit 二補數），值＝尾數 × 2^指數。
    /// </summary>
    [SpecRef("PMBus Specification Rev 1.3 Part II §8.4（Linear11 data format）；READ_VIN/READ_IIN/READ_POUT/READ_TEMPERATURE_1 預設格式")]
    public static double? DecodeLinear11(ushort raw)
    {
        unchecked
        {
            int exponent = (raw >> 11) & 0x1F;
            if ((exponent & 0x10) != 0) exponent -= 32;   // 5-bit 二補數
            int mantissa = (int)(raw & 0x7FF);
            if ((mantissa & 0x400) != 0) mantissa -= 2048; // 11-bit 二補數
            return mantissa * Math.Pow(2, exponent);
        }
    }

    /// <summary>LINEAR16：值＝16-bit 二補數尾數 × 2^exponent（指數來自 VOUT_MODE）。尾數 0xFFFF 语义依命令而定，呼叫端自判。</summary>
    [SpecRef("PMBus Specification Rev 1.3 Part II §8.5（Linear16 data format）；READ_VOUT 搭配 VOUT_MODE（0x20）")]
    public static double? DecodeLinear16(ushort raw, int exponent)
    {
        unchecked
        {
            short mantissa = (short)raw;
            return mantissa * Math.Pow(2, exponent);
        }
    }

    /// <summary>VOUT_MODE（0x20）：bits[7:5]＝模式（000＝LINEAR16）、bits[4:0]＝指數（5-bit 二補數）。其餘模式（Direct／VID）不解，回 null。</summary>
    [SpecRef("PMBus Specification Rev 1.3 Part II §8.5＋§20.4（VOUT_MODE）")]
    public static int? DecodeVoutModeExponent(byte voutMode)
    {
        if (((voutMode >> 5) & 0x07) != 0x00) return null; // 只有 LINEAR16 模式有出處；Direct/VID 不在本解碼器
        int exponent = voutMode & 0x1F;
        return (exponent & 0x10) != 0 ? exponent - 32 : exponent;
    }

    // ── PMBus 命令碼（Part II § 註冊表）──
    public const byte CmdPage = 0x00;
    public const byte CmdVoutMode = 0x20;
    public const byte CmdReadVin = 0x88;
    public const byte CmdReadIin = 0x89;
    public const byte CmdReadVout = 0x8B;
    public const byte CmdReadIout = 0x8C;
    public const byte CmdReadTemperature1 = 0x8D;
    public const byte CmdReadPout = 0x96;
    public const byte CmdMfrId = 0x99;
}
