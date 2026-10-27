namespace XinSpect;

/// <summary>
/// 一次請求的上下文預算結果。<b>裁掉什麼必須說出來</b>——模型若以為自己看到的是完整對話，
/// 就會說「你剛才提到…」而實際上那一段已經被丟掉了，讀者拿到的是看起來合理的錯誤結論。
/// </summary>
/// <param name="System">（可能被截斷的）系統提示。</param>
/// <param name="History">依原順序保留的歷史訊息（可能被截斷）。</param>
/// <param name="DroppedMessages">因預算不足而整則丟棄的歷史訊息數（永遠不會丟掉最後一則使用者訊息）。</param>
/// <param name="ClippedMessages">被截斷內容的訊息數（含系統提示）。</param>
/// <param name="TotalChars">送出內容的總字元數（含系統提示）。</param>
public sealed record BudgetedRequest(
    string System,
    IReadOnlyList<string> History,
    int DroppedMessages,
    int ClippedMessages,
    int TotalChars)
{
    /// <summary>要附加在系統提示末尾的說明；沒有裁任何東西時是空字串。</summary>
    public string Notice
    {
        get
        {
            var parts = new List<string>();
            if (DroppedMessages > 0)
                parts.Add($"較早的 {DroppedMessages} 則對話因長度限制已省略");
            if (ClippedMessages > 0)
                parts.Add($"有 {ClippedMessages} 段內容過長已截斷（末尾標示原長度）");
            return parts.Count == 0
                ? ""
                : "\n\n（註：為控制請求長度，" + string.Join("、", parts)
                  + "。你看到的不一定是完整對話，不要宣稱「使用者剛才說過」未出現在下方的內容。）";
        }
    }
}

/// <summary>
/// 請求長度的硬上限：<see cref="AiService"/> 原本只按「則數」裁切歷史（<c>AiHistoryTurns</c>），
/// 但一則貼上的長檔案或一段三萬字的回覆就能把整包撐爆——端點直接回 400，
/// 而使用者看到的是一句「呼叫失敗」，看不出真正的原因是「這次送太多了」。
/// 這裡把預算改成<b>字元數</b>，並且先截斷單則過長訊息、再從最舊的整則丟棄，
/// 最後一則（這次要問的問題）永遠保留。
/// </summary>
public static class AiRequestBudget
{
    /// <summary>預設上限（字元）。約等於三萬個中文字，多數模型的上下文都吃得下。</summary>
    public const int DefaultChars = 60_000;

    /// <summary>使用者可設定範圍的下限：再小就連硬體快照都放不進去。</summary>
    public const int MinChars = 8_000;

    /// <summary>上限：超過這個數字通常已經是端點的問題，不是預算的問題。</summary>
    public const int MaxChars = 400_000;

    /// <summary>單則訊息最多佔預算的幾分之一（留給系統提示與其他訊息）。</summary>
    private const int PerMessageDivisor = 4;

    /// <summary>截斷時至少留這麼多字元，否則「截斷」等於什麼都沒送。</summary>
    private const int MinPerMessage = 2_000;

    /// <summary>系統提示（含硬體快照）最多佔預算的一半——它再重要，也不能把對話擠成零。</summary>
    private const int SystemDivisor = 2;

    public static BudgetedRequest Apply(string system, IReadOnlyList<string> history, int budgetChars)
    {
        system ??= "";
        history ??= Array.Empty<string>();
        int budget = Math.Clamp(budgetChars <= 0 ? DefaultChars : budgetChars, MinChars, MaxChars);

        int clipped = 0;

        // ① 系統提示先截到一半預算：快照是「背景資料」，不是這一問的主體。
        string sys = system;
        int sysLimit = Math.Max(MinPerMessage, budget / SystemDivisor);
        if (sys.Length > sysLimit)
        {
            sys = Clip(sys, sysLimit);
            clipped++;
        }

        // ② 單則過長先截斷（保留開頭，那通常是摘要與結論），再談丟整則。
        int perLimit = Math.Max(MinPerMessage, budget / PerMessageDivisor);
        var kept = new List<string>(history.Count);
        foreach (string raw in history)
        {
            string text = raw ?? "";
            if (text.Length > perLimit)
            {
                text = Clip(text, perLimit);
                clipped++;
            }
            kept.Add(text);
        }

        // ③ 總長仍超預算 → 從最舊的整則丟棄，最後一則永遠保留。
        int total = sys.Length + kept.Sum(t => t.Length);
        int dropped = 0;
        while (total > budget && kept.Count > 1)
        {
            total -= kept[0].Length;
            kept.RemoveAt(0);
            dropped++;
        }

        return new BudgetedRequest(sys, kept, dropped, clipped, total);
    }

    /// <summary>截斷並在末尾寫出原長度——讀的人（含模型）要看得出這裡少了一段。</summary>
    private static string Clip(string text, int limit)
        => text[..limit] + $"\n…（此段原長 {text.Length:N0} 字元，已截斷至此處）";
}
