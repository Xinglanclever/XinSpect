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
