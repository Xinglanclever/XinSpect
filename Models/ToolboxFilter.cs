namespace XinSpect;

/// <summary>
/// 工具箱搜尋的純函式核心（不碰 UI、不碰檔案系統，故可完整測試）。
/// 規則刻意做成「全部詞彙都要命中」而非模糊比對：工具箱是一份要找特定工具的目錄，
/// 使用者打「ssd 測試」時期望同時含這兩個概念的項目，而不是任一命中就都列出來。
/// </summary>
public static class ToolboxFilter
{
    /// <summary>把查詢字串切成詞彙；空白（含全角空白）為分隔。</summary>
    public static string[] Tokenize(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>單一詞彙是否命中任一欄位（不分大小寫；中文為子字串比對）。</summary>
    public static bool MatchesToken(string token, params string?[] fields)
    {
        foreach (var f in fields)
            if (!string.IsNullOrEmpty(f) && f.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>全部詞彙皆命中才算符合；查詢為空時一律符合。</summary>
    public static bool Matches(string? query, params string?[] fields)
    {
        var tokens = Tokenize(query);
        for (int i = 0; i < tokens.Length; i++)
            if (!MatchesToken(tokens[i], fields)) return false;
        return true;
    }

    /// <summary>搜尋結果的狀態列文字；筆數為 0 時明說沒有命中而不是留白。</summary>
    public static string Summarize(string? query, bool onlyBuiltin, int matched, int total)
    {
        string scope = onlyBuiltin ? "（僅列曦覽自己做得到的項目）" : "";
        if (string.IsNullOrWhiteSpace(query) && !onlyBuiltin)
            return $"共 {total} 項工具。第三方工具一律導向官方下載，本程式不內含任何外部執行檔。";
        if (matched == 0)
            return $"沒有符合「{query}」的項目{scope}；共 {total} 項可搜尋。";
        return $"符合 {matched} / {total} 項{scope}。";
    }
}
