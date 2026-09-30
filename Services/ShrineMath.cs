namespace XinSpect;

/// <summary>
/// 「拜神」的純核心（娛樂功能）：乖度評分、開機時長文字與工程師祈福語。
/// </summary>
/// <remarks>
/// 這個功能的存在理由是把「笑話」與「數據」切乾淨——演算法是玩笑（自己也這麼說），
/// 但輸入數字全部來自真實量測：藍屏數來自 Minidump 資料夾的傾印檔時間戳。
/// 讀不到就回 null，由 UI 顯示「—」，絕不把「沒讀到」當成「零」——那是全站的誠實紅線。
/// 祈福語刻意排除任何診斷或命令字眼（清灰、重灌、換電源……），願望可以許，
/// 「你該做什麼」不可以說，那是量測工具不該越過的線。測試會守住這條邊界。
/// </remarks>
public static class ShrineMath
{
    /// <summary>近 7 天每筆藍屏扣的分數。演算法本身是玩笑，這個數字不必認真。</summary>
    private const int BsodPenalty = 25;

    /// <summary>工程師祈福語池：全部是「願……」的許願句式，不含任何診斷或指示。</summary>
    public static readonly string[] Blessings =
    [
        "願你的幀時間平直如尺，1% Low 與平均只差毫釐",
        "願溫度牆只出現在規格書裡",
        "願記憶體訓練一次收斂，時序不再自己跳舞",
        "願每次睡眠都算得出耗電，每次喚醒都不遲到",
        "願重配置扇區一輩子為零，SMART 永遠全綠",
        "願 WHEA 日誌長草，MCA 銀行一塵不染",
        "願 PCIe 永遠跑滿 x16，訓練一次到位不降速",
        "願電供波紋安靜如深夜，coil whine 只出現在別人家",
        "願藍屏代碼只出現在維基百科裡",
        "願電池循環增加得比歲月還慢",
    ];

    /// <summary>
    /// 乖度評分（娛樂指標）。藍屏傾印讀不到時回 <c>null</c>——「從未藍屏」與
    /// 「未啟用傾印」分不清的日子，寧可不評分也不猜。
    /// </summary>
    public static (int? Score, string Verdict, string Detail) Obedience(int? bsod7d, long uptimeSeconds)
    {
        string up = $"本次開機已乖 {UptimeText(uptimeSeconds)}";
        if (bsod7d is null)
            return (null, "—", "藍屏傾印讀不到（分不清「從未藍屏」與「未啟用傾印」），拒絕亂猜");
        int score = Math.Clamp(100 - bsod7d.Value * BsodPenalty, 0, 100);
        return (score, VerdictOf(score), $"近 7 天藍屏 {bsod7d} 筆 ・ {up}");
    }

    /// <summary>乖度判級文字。門檻純屬玩笑，不必認真。</summary>
    public static string VerdictOf(int score) => score switch
    {
        >= 90 => "超級乖",
        >= 70 => "大致乖",
        >= 40 => "有點叛逆",
        _ => "該補貨了",
    };

    /// <summary>把開機秒數換成人話；只進位到分鐘，不虛報秒。</summary>
    public static string UptimeText(long uptimeSeconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));
        if (t.Days > 0) return $"{t.Days} 天 {t.Hours} 小時";
        if (t.Hours > 0) return $"{t.Hours} 小時 {t.Minutes} 分鐘";
        return $"{t.Minutes} 分鐘";
    }

    /// <summary>照種子挑一句祈福語；同一顆種子必得同一句（可測試）。</summary>
    public static string PickBlessing(int seed)
    {
        // 不用 Math.Abs：int.MinValue 的絕對值超出 int 範圍會炸。先取模再校正負數。
        int index = seed % Blessings.Length;
        if (index < 0) index += Blessings.Length;
        return Blessings[index];
    }
}