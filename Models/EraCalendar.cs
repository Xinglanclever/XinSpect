namespace XinSpect;

/// <summary>日期紀年模式（供標題列時鐘切換）。</summary>
public enum EraMode
{
    /// <summary>西元紀年（預設，格里曆）。</summary>
    Gregorian = 0,
    /// <summary>民國紀年：中華民國元年為西元 1912 年，西元年 − 1911。</summary>
    Minguo = 1,
    /// <summary>中華黃帝紀元：以西元前 2698 年為元年，西元年 + 2698。</summary>
    Huangdi = 2,
    /// <summary>宣統紀年〔大清〕：清宣統元年為西元 1909 年，西元年 − 1908。</summary>
    Xuantong = 3,
    /// <summary>哆啦A夢紀元〔惡搞〕：以哆啦A夢誕生年（西元 2112 年，22 世紀）為元年；此前的年份為「哆啦A夢前 ◯ 年」。</summary>
    Doraemon = 4,
}

/// <summary>依所選紀年將時間格式化為時鐘顯示字串。</summary>
public static class EraCalendar
{
    /// <summary>下拉選單顯示名稱（順序須與 <see cref="EraMode"/> 對應）。</summary>
    public static readonly string[] Names = { "西元紀年", "民國紀年", "中華黃帝紀元", "宣統紀年〔大清〕", "哆啦A夢紀元〔惡搞〕" };

    /// <summary>
    /// 1.5.1 以前的舊編號 → 現行 <see cref="EraMode"/> 的遷移。
    /// </summary>
    /// <remarks>
    /// <see cref="EraMode"/> 的數值在 1.5.1 重新排序過（民國提前到 1），而設定檔存的是<b>裸整數</b>，
    /// 不是名稱。若不遷移，舊使用者原本選的「民國」(舊 3) 會靜默變成「宣統」(新 3)——
    /// 這種「設定沒壞但意思變了」的回歸比當掉更難察覺，故必須明確處理。
    ///
    /// 舊編號：0 西元、1 黃帝、2 宣統、3 民國、4 大漢、5 哆啦A夢。
    /// 「大漢紀年」在 1.5.1 已移除；無法照原意還原，退回同屬上古中國連續紀元的黃帝紀元，
    /// 而不是悄悄丟回西元——使用者選的是「某種古代紀年」，這個意圖仍然保得住。
    /// 超出範圍的值一律回西元（預設），不猜。
    /// </remarks>
    public static EraMode MigrateLegacyValue(int legacy) => legacy switch
    {
        0 => EraMode.Gregorian,
        1 => EraMode.Huangdi,
        2 => EraMode.Xuantong,
        3 => EraMode.Minguo,
        4 => EraMode.Huangdi,   // 大漢紀年已移除，退回同類的黃帝紀元
        5 => EraMode.Doraemon,
        _ => EraMode.Gregorian,
    };

    /// <summary>把任意整數夾成合法的 <see cref="EraMode"/>；超出範圍回西元而不是拋例外。</summary>
    public static EraMode Coerce(int value)
        => value >= 0 && value < Names.Length ? (EraMode)value : EraMode.Gregorian;

    /// <summary>格式化時間；全部紀年統一以「◯年◯月◯日 時:分:秒」自然排列呈現。</summary>
    public static string Format(DateTime t, EraMode mode) => mode switch
    {
        EraMode.Minguo   => $"民國 {t.Year - 1911} 年 {t.Month} 月 {t.Day} 日  {t:HH:mm:ss}",
        EraMode.Huangdi  => $"中華黃帝紀元 {t.Year + 2698} 年 {t.Month} 月 {t.Day} 日  {t:HH:mm:ss}",
        EraMode.Xuantong => $"宣統 {t.Year - 1908} 年 {t.Month} 月 {t.Day} 日  {t:HH:mm:ss}",
        // 哆啦A夢誕生於西元 2112 年（22 世紀）＝元年；在此之前為「哆啦A夢前 ◯ 年」（如 2026 年＝哆啦A夢前 86 年）。
        EraMode.Doraemon => (t.Year >= 2112
                                ? (t.Year == 2112 ? "哆啦A夢元年" : $"哆啦A夢 {t.Year - 2111} 年")
                                : $"哆啦A夢前 {2112 - t.Year} 年")
                            + $" {t.Month} 月 {t.Day} 日  {t:HH:mm:ss}",
        _                => $"西元 {t.Year} 年 {t.Month} 月 {t.Day} 日  {t:HH:mm:ss}",
    };
}
