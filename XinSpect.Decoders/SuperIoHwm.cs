namespace XinSpect;

/// <summary>
/// SuperIO HWM（Hardware Monitor）的純解碼器——三家家族環境控制器的公開公式：
/// 風扇 RPM、溫度（8-bit 二補數 °C）、電壓。
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

    // ── Fintek F718xx（lm-sensors f71882fg 驅動交叉核對）──

    /// <summary>
    /// Fintek 風扇轉速：RPM = 1,500,000 / count（<b>無除數</b>，與 ITE 不同）。
    /// count 0＝停轉；count 超過 4,098（< 366 RPM，驅動的 FAN_MIN_DETECT 下限）視為無訊號——都回 null，不回 0 RPM。
    /// </summary>
    [SpecRef("lm-sensors f71882fg.c：fan_from_reg = reg ? (1500000 / reg) : 0；FAN_MIN_DETECT 366 為最低可偵測轉速（1,500,000/366 ≈ 4098 counts）。交接規格寫 1,500,000/(divisor×count) 帶除數，驅動源碼無除數，從驅動")]
    public static uint? DecodeFintekFanRpm(ushort count)
        => count == 0 || count > 4098 ? null : (uint)(1_500_000u / count);

    /// <summary>Fintek 溫度：8-bit 二補數 °C（F71858FG 的 16-bit 0.125°C 佈局除外，本表不涵蓋該型）。</summary>
    [SpecRef("lm-sensors f71882fg.c：show_temp 非 f71858fg 路徑 = ((s8)temp) * 1000；暫存器 0x70 + 2·nr")]
    public static int DecodeFintekTemperature(byte raw) => (sbyte)raw;

    /// <summary>
    /// Fintek 電壓：LSB 8 mV（晶片端讀值）。
    /// <b>交接規格寫 12 mV（F71882 datasheet），lm-sensors 驅動 show_in 是 in×8——驅動為可驗證源碼，從驅動並如實標注差異</b>。
    /// </summary>
    [SpecRef("lm-sensors f71882fg.c：show_in = data->in[nr] * 8（mV）；暫存器 0x20 + nr。與 F71882 datasheet 的 12 mV 說法不一致，採驅動實作")]
    public static uint DecodeFintekVoltageMv(byte raw) => (uint)raw * 8;

    // ── Nuvoton／Winbond NCT67xx（lm-sensors nct6775-core 驅動交叉核對）──

    /// <summary>NCT 風扇轉速：RPM = 1,350,000 / (count << divreg)；count 0／0xFFFF 回 null。divreg 預設 0（NCT67xx 大多不用除數）。</summary>
    [SpecRef("lm-sensors nct6775-core.c：fan_from_reg16 = 1350000U / (reg << divreg)，reg 0/0xFFFF 回 0；NCT6775_REG_FAN = { 0x630, 0x632, ... }（bank 6、16-bit LSB first）")]
    public static uint? DecodeNctFanRpm(ushort count, uint divreg = 0)
        => count is 0 or 0xFFFF ? null : (uint)(1_350_000u / (count << (int)divreg));

    /// <summary>NCT 溫度：8-bit 二補數 °C（byte 暫存器讀法；TEMP1＝bank 0 暫存器 0x27）。</summary>
    [SpecRef("lm-sensors nct6775-core.c：NCT6775_REG_TEMP[0] = 0x27；byte 暫存器讀後 <<8 再 (s16)/128×500 m°C ≡ (sbyte)raw °C")]
    public static int DecodeNctTemperature(byte raw) => (sbyte)raw;

    /// <summary>NCT 電壓：mV = raw × scale / 100（scale 取自驅動 scale_in 表：in0/in1/in4-in6/in8…＝800 → 8 mV/LSB，in2/in3/in7…＝1600 → 16 mV/LSB），四捨五入。</summary>
    [SpecRef("lm-sensors nct6775-core.c：in_from_reg = DIV_ROUND_CLOSEST(reg × scales[nr], 100)；scale_in = { 800, 800, 1600, 1600, 800, ... }（單位 0.01 mV/LSB）；IN 暫存器 bank 0 0x20 起")]
    public static uint DecodeNctVoltageMv(byte raw, uint scaleCentiMv)
        => ((uint)raw * scaleCentiMv + 50) / 100;
}
