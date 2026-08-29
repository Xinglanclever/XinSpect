namespace XinSpect;

/// <summary>
/// 命令面板用的模糊比對評分器。純函式、無相依，可獨立測試。
/// 評分分四級（高→低）：前綴命中 → 子字串命中 → 字元子序列命中 → 不命中(0)。
/// 同級之內較短的目標分數較高，讓「處理器」勝過「處理器超頻進階設定」這類長標題。
/// </summary>
public static class FuzzyMatch
{
    /// <summary>命中即回傳正分，數字越大越相關；不命中回傳 0。空查詢視為全部命中（回傳 1）。</summary>
    public static int Score(string query, string? text)
    {
        if (string.IsNullOrEmpty(query)) return 1;
        if (string.IsNullOrEmpty(text)) return 0;

        // 以不分大小寫的方式比對；中文不受影響，英文別名可大小寫混打
        int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx == 0) return 10_000 - Math.Min(text.Length, 200);
        if (idx > 0) return 7_000 - idx * 8 - Math.Min(text.Length, 200);

        return SubsequenceScore(query, text);
    }

    /// <summary>
    /// 對多個欄位評分並取最高分。<paramref name="weights"/> 為各欄位的百分比權重
    /// （標題 100、關鍵字 80、說明 55 之類），讓標題命中永遠勝過說明命中。
    /// </summary>
    public static int Best(string query, IReadOnlyList<(string? Text, int Weight)> fields)
    {
        int best = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            int s = Score(query, fields[i].Text);
            if (s <= 0) continue;
            s = s * fields[i].Weight / 100;
            if (s > best) best = s;
        }
        return best;
    }

    // 字元子序列：query 的每個字元須依序在 text 中找到（可跳字）。
    // 分數扣除「跳過的字元數」，故 "cpu" 對 "c…p…u" 這種鬆散命中分數低於緊湊命中。
    private static int SubsequenceScore(string query, string text)
    {
        int qi = 0, gaps = 0, lastHit = -1;
        for (int ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(text[ti]) != char.ToLowerInvariant(query[qi])) continue;
            if (lastHit >= 0) gaps += ti - lastHit - 1;
            lastHit = ti;
            qi++;
        }
        if (qi < query.Length) return 0;                       // 有字元找不到 → 不命中
        return Math.Max(1, 4_000 - gaps * 20 - Math.Min(text.Length, 200));
    }
}
