using LibreHardwareMonitor.Hardware;

namespace XinSpect;

/// <summary>
/// 感測讀值的合理性閘門：把「介面回報了數字，但那個數字不可能是真的」擋在畫面之外。
/// </summary>
/// <remarks>
/// 為什麼需要這一層：LibreHardwareMonitor 忠實轉述晶片回報的原始值，而原始值本身可能是
/// <list type="bullet">
/// <item><c>NaN</c>／<c>±∞</c>——感測器不存在或本輪讀取失敗；</item>
/// <item>哨兵值——常見如 <c>6553.5</c>（0xFFFF 半刻度）、<c>-273</c>、<c>255 °C</c>，
/// 這些是「沒有讀到」的編碼，不是量測結果；</item>
/// <item>匯流排雜訊——偶發的離譜跳點（幾千度、幾萬 MHz）。</item>
/// </list>
/// 這些值一旦流進去，就會同時汙染畫面數字、走勢圖、歷史統計、CSV 記錄、警示判定與 AI 工具回報——
/// 也就是把「沒讀到」偽裝成「量到了」。本閘門的取捨很單純：<b>寧可顯示「—」，也不顯示假數字。</b>
///
/// 各型別的界線取自實機物理上限再留餘裕（例：溫度上界 130 °C 已高過所有消費級 Tjmax），
/// 目的是擋哨兵與雜訊，而不是替硬體「修正」讀值——落在界線內的值一律原樣採用，絕不平滑、不內插、不猜測。
/// 唯一的例外是百分比類：容許 ±0.5 的量化誤差後夾回 0–100，因為那確實是同一個量測值的表示誤差。
/// </remarks>
public static class SensorSanity
{
    /// <summary>溫度上界（°C）。消費級 CPU 的 Tjmax 約 100–115，留餘裕至 130；0 與負值視為未讀到。</summary>
    public const double MaxTemperatureC = 130;
    /// <summary>頻率上界（MHz）。目前世界紀錄約 9 GHz，留餘裕至 12 GHz。</summary>
    public const double MaxClockMHz = 12_000;
    /// <summary>電壓上界（V）。主機板會曝露 +12 V 軌，故上界須高於 12；取 24 V。</summary>
    public const double MaxVoltageV = 24;
    /// <summary>功耗上界（W）。整機層級的極端配置亦不及 2 kW。</summary>
    public const double MaxPowerW = 2_000;
    /// <summary>風扇轉速上界（RPM）。伺服器暴力扇約 15 000，留餘裕至 30 000。</summary>
    public const double MaxFanRpm = 30_000;
    /// <summary>電流上界（A）。</summary>
    public const double MaxCurrentA = 500;

    /// <summary>百分比類讀值容許的量化誤差：先容忍 ±0.5 再夾回 0–100，超出則視為哨兵值。</summary>
    private const double PercentSlack = 0.5;

    /// <summary>
    /// 檢驗單一讀值。通過者原樣回傳（百分比類夾回 0–100），未通過者回 <c>null</c>——
    /// 呼叫端一律把 <c>null</c> 呈現為「—」，不得代換為 0。
    /// </summary>
    public static double? Plausible(SensorType type, float? raw)
    {
        if (raw is not float f || float.IsNaN(f) || float.IsInfinity(f)) return null;
        return Check(type, f);
    }

    // 型別界線的實作。刻意不公開 double 多載：公開多載會與 float? 版本在 float 引數上模稜兩可。
    private static double? Check(SensorType type, double v)
    {
        switch (type)
        {
            // 0 與負值代表未讀到：運轉中的機器不會有 0 °C 的核心、0 MHz 的封裝、0 V 的核心電壓。
            case SensorType.Temperature:
                return v > 0 && v <= MaxTemperatureC ? v : null;
            case SensorType.Clock:
            case SensorType.Frequency:
                return v > 0 && v <= MaxClockMHz ? v : null;
            case SensorType.Voltage:
                return v > 0 && v <= MaxVoltageV ? v : null;

            // 百分比類：0 是有意義的讀值（停轉的風扇、閒置的核心、壽命歸零的磁碟），故容許 0。
            case SensorType.Load:
            case SensorType.Control:
            case SensorType.Level:
                return v >= -PercentSlack && v <= 100 + PercentSlack ? Math.Clamp(v, 0, 100) : null;

            // 以下各型別 0 皆可能為真（零轉速模式、閒置功耗、空的顯示記憶體），僅擋負值與離譜值。
            case SensorType.Power:
                return v >= 0 && v <= MaxPowerW ? v : null;
            case SensorType.Fan:
                return v >= 0 && v <= MaxFanRpm ? v : null;
            case SensorType.Current:
                return v >= 0 && v <= MaxCurrentA ? v : null;
            case SensorType.Data:          // GB
                return v >= 0 && v <= 1 << 20 ? v : null;
            case SensorType.SmallData:     // MB
                return v >= 0 && v <= 1 << 24 ? v : null;
            case SensorType.Throughput:    // B/s
                return v >= 0 ? v : null;

            // 未知型別無從判斷合理範圍，只擋非數值——不會假裝知道它的物理界線。
            default:
                return v;
        }
    }

    /// <summary>是否為可用讀值（<see cref="Plausible(SensorType, float?)"/> 的布林版）。</summary>
    public static bool IsUsable(SensorType type, float? raw) => Plausible(type, raw) is not null;
}
