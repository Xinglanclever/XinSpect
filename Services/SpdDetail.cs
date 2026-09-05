namespace XinSpect;

/// <summary>DDR4 SPD 位元組 3 低四位宣告的模組型式。</summary>
public enum SpdModuleType
{
    Unknown,
    Rdimm, Udimm, SoDimm, LrDimm, MiniRdimm, MiniUdimm,
    SoRdimm72Bit, SoUdimm72Bit, SoDimm16Bit, SoDimm32Bit,
}

/// <summary>模組的幾何：容量是<b>算出來</b>的，不是抄哪一格來的。</summary>
/// <param name="CapacityMib">顆粒密度 ÷ 8 × (匯流排寬度 ÷ 顆粒寬度) × rank 數。任一格是保留值就回 0。</param>
/// <param name="NominalMillivolts">位元組 11 宣告的標準電壓；沒宣告就回 0（＝讀不到，不拿 1.2 V 充數）。</param>
public sealed record SpdGeometry(
    SpdModuleType ModuleType,
    int SdramDensityMbits,
    int Ranks,
    int DeviceWidthBits,
    int BusWidthBits,
    int EccBits,
    int CapacityMib,
    int NominalMillivolts);

/// <summary>
/// JEDEC 時序，全部以<b>皮秒</b>存。
/// </summary>
/// <remarks>
/// 用整數皮秒而不是浮點奈秒，是因為原始值本來就是整數（MTB＝125 ps 的倍數加上 1 ps 的細調），
/// 而測試要能做精確比較。把它先轉成 double 再比對，等於自己製造一個沒必要的誤差來源。
/// </remarks>
public sealed record SpdTimings(
    int TckMinPs, int TckMaxPs,
    IReadOnlyList<int> SupportedCas,
    int TaaPs, int TrcdPs, int TrpPs, int TrasPs, int TrcPs,
    int TrrdSPs, int TrrdLPs, int TccdLPs,
    int TwrPs, int TwtrSPs, int TwtrLPs,
    int Trfc1Ps, int Trfc2Ps, int Trfc4Ps, int TfawPs)
{
    /// <summary>把一段時間換算成某個時鐘週期下的時鐘數（向上取整）。</summary>
    public static int ClocksAt(int timePs, int tckPs)
        => tckPs <= 0 ? 0 : (timePs + tckPs - 1) / tckPs;

    /// <summary>DDR4 的標準頻率分級（MT/s）。</summary>
    private static readonly int[] JedecBins = [1600, 1866, 2133, 2400, 2666, 2933, 3200];

    /// <summary>
    /// 最高的標準頻率分級。由 tCKmin 算出實際速率後貼到最近的分級上——差距超過 3% 就不貼，
    /// 直接回算出來的值，免得把一條超規的模組硬說成標準品。
    /// </summary>
    public int MaxJedecDataRate
    {
        get
        {
            if (TckMinPs <= 0) return 0;
            int rate = 1_000_000 / TckMinPs * 2;
            foreach (int bin in JedecBins)
                if (Math.Abs(bin - rate) * 100 <= rate * 3) return bin;
            return rate;
        }
    }
}

/// <summary>一組 XMP profile。</summary>
/// <param name="Index">SPD 裡的第幾組（0 起算）。</param>
/// <param name="RawVdd">電壓那一格的原始位元組。編碼只用本機的 1.40 V 對過 CPU-Z，所以原值留著。</param>
public sealed record SpdXmpProfile(
    int Index, int Millivolts, byte RawVdd,
    int TckMinPs, int TaaPs, int TrcdPs, int TrpPs, int TrasPs, int TrcPs, int TrrdSPs)
{
    /// <summary>資料速率（MT/s）。頻率取整後乘二，與 CPU-Z 的顯示一致（0.555 ns → 1801 MHz → 3602）。</summary>
    public int DataRate => TckMinPs <= 0 ? 0 : 1_000_000 / TckMinPs * 2;

    /// <summary>這組 profile 自己的 CAS 延遲。</summary>
    public int CasLatency => SpdTimings.ClocksAt(TaaPs, TckMinPs);
}
