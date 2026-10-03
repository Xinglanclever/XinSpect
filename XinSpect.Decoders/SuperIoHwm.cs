namespace XinSpect;

/// <summary>
/// SuperIO HWM（Hardware Monitor）的純解碼器——ITE 家族環境控制器的公開公式：
/// 風扇 RPM、溫度（8-bit 二補數 °C）、電壓（LSB 16 mV）。
/// <b>電壓是晶片端讀值，未經主機板的分壓電阻校準</b>——呼叫方必須在呈現時註明，不能冒充實際電壓。
/// </summary>
public static class SuperIoHwmDecoder
{
    /// <summary>風扇轉速：count 為 16-bit 轉速計數（LSB first），divisor 預設 2。count 0／0xFFFF＝無效（停轉或未接），回 null——不回 0 RPM。</summary>
    [SpecRef("ITE IT8728F 環境控制器 datasheet：FAN_TACH = 1,350,000 / (Fan Divisor × Count)；Count 0 與 0xFFFF 為無效值（lm-sensors it87 驅動同一公式交叉核對）")]
    public static uint? DecodeFanRpm(ushort count, uint divisor = 2)
        => count is 0 or 0xFFFF ? null : (uint)(1_350_000u / (divisor * count));

    /// <summary>溫度：8-bit 二補數，單位 °C。</summary>
    [SpecRef("ITE IT8728F datasheet：TMPIN 為 8-bit 二補數（°C）；lm-sensors it87 同一語意交叉核對")]
    public static int DecodeTemperature(byte raw) => (sbyte)raw;

    /// <summary>電壓：LSB 16 mV（晶片端讀值；主機板分壓比例另計）。</summary>
    [SpecRef("ITE IT8728F datasheet：VIN LSB = 16 mV；lm-sensors it87 的 in_scale 同一口徑交叉核對")]
    public static uint DecodeVoltageMv(byte raw) => (uint)raw * 16;
}
