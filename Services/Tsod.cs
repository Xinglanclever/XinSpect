namespace XinSpect;

/// <summary>
/// TSOD／TSE2004 記憶體溫度感測器的純解碼器。溫度暫存器（0x05，word 讀）：
/// bits[15:4] 為 12 位元二補數、單位 1/16°C，bits[3:0] 為旗號不參與溫度；
/// 製造商 ID 暫存器（0x06，word 讀）的欄位佈局各世代文件表述不一——只報原始值不解碼。
/// </summary>
public static class Tsod
{
    /// <summary>解溫度（°C，1/16 解析度）。全 F／全 0 以外的任何 16 位元值都有定義的溫度意義。</summary>
    [SpecRef("JEDEC TSE2004av（TSOD）溫度暫存器 0x05：bits[15:4] 為 12 位元二補數、LSB＝1/16°C，bits[3:0] 為旗號")]
    public static double? TemperatureC(ushort raw)
    {
        if (raw is 0xFFFF or 0x0000) return null; // 0xFFFF＝讀取失敗的常見殘值；0x0000 無從與未初始化區分
        return ((short)(raw & 0xFFF0) >> 4) / 16.0;
    }
}
