namespace XinSpect;

/// <summary>
/// 驗機規則的門檻值。全部集中在這裡，理由有二：一是測試要引用同一個值，
/// 二是這些數字每一個都是**取捨**，得寫得出理由。
/// </summary>
/// <remarks>
/// 取值原則：**寧可漏判，不可誤判**。誤判的代價不對稱——一條寫太鬆的規則會讓正常機器
/// 冒出「矛盾」，使用者可能因此殺掉一筆正當交易；漏判只是少給一條線索。
/// </remarks>
public static class VerifyThresholds
{
    /// <summary>
    /// 全生命週期的平均寫入速率上限（GiB／小時）。120 GiB/h ≈ 34 MB/s **連續不斷寫滿整個通電期間**
    /// ——消費級用途幾乎到不了，故超過即視為「通電小時被歸零」的訊號。真正的高負載機器
    /// （影音錄製、虛擬機主機、監控錄影）有可能合理超過，所以規則要附上這個正當成因。
    /// </summary>
    public const double MaxPlausibleGiBPerHour = 120;

    /// <summary>
    /// 已用壽命仍為 0% 卻已寫入這麼多（GiB），代表壽命計數被歸零。100 TiB 對消費級 TLC 碟已是
    /// 標示 TBW 的數倍（常見型號 300–600 TBW），走到這個量還顯示 0% 只有兩種可能：計數被重設，
    /// 或韌體根本不更新這個欄位。
    /// </summary>
    public const double ZeroWearImplausibleGiB = 100_000;

    /// <summary>
    /// 容量誤差容許值。廠商的十進位 GB 與作業系統的 GiB 差約 7%，再加 over-provisioning 與保留區，
    /// 10% 是安全邊界；超出這個範圍才算矛盾。
    /// </summary>
    public const double CapacityTolerance = 0.10;

    /// <summary>電池滿充／設計容量低於此比例即視為明顯衰退。</summary>
    public const double BatteryWornRatio = 0.80;

    /// <summary>低於此比例則衰退嚴重（判定升級為 <see cref="Severity.Serious"/>）。</summary>
    public const double BatteryBadlyWornRatio = 0.50;
}
