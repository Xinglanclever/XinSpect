using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 端點回報的 token 用量。三個數字<b>都是端點說的</b>：本程式不估算 token，
/// 因為估算要假設 tokenizer，而不同模型的分詞方式不同——用猜的數字去做成本判斷，
/// 比沒有數字更糟（它看起來很精確）。
/// </summary>
public readonly record struct AiUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static readonly AiUsage None = default;

    /// <summary>端點有沒有回報任何用量（三者皆 0 視為沒有回報）。</summary>
    public bool Reported => PromptTokens > 0 || CompletionTokens > 0 || TotalTokens > 0;

    /// <summary>
    /// 從回應（或 SSE 最後一塊）的根物件取 <c>usage</c>。缺欄位就是 0，<b>不推算</b>；
    /// 只有「兩個分項都有、總量缺」時才做加法，因為那不是估算是定義。
    /// </summary>
    public static AiUsage Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return None;
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return None;

        int prompt = ReadInt(u, "prompt_tokens");
        int completion = ReadInt(u, "completion_tokens");
        int total = ReadInt(u, "total_tokens");
        if (total == 0 && prompt > 0 && completion > 0) total = prompt + completion;
        return new AiUsage(prompt, completion, total);
    }

    private static int ReadInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out int n) && n > 0 ? n : 0;

    /// <summary>單次請求的用量說明；沒有回報時說「端點沒有回報」而不是顯示 0。</summary>
    public string Describe()
    {
        if (!Reported) return "端點沒有回報用量";
        var parts = new List<string>();
        if (PromptTokens > 0) parts.Add($"輸入 {PromptTokens:N0}");
        if (CompletionTokens > 0) parts.Add($"輸出 {CompletionTokens:N0}");
        if (TotalTokens > 0) parts.Add($"合計 {TotalTokens:N0}");
        return string.Join(" ・ ", parts) + " tok";
    }
}

/// <summary>
/// 這個工作階段的累計用量。計數只在端點真的回報時累加，回報次數與請求次數分開記——
/// 「10 次請求但只有 3 次回報用量」是一個要看得到的狀態，不能平均成一個漂亮的總數。
/// </summary>
public sealed class AiUsageCounter : ObservableObject
{
    private int _requests;
    private int _reported;

    public int Requests => _requests;
    public int Reported => _reported;
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public long TotalTokens { get; private set; }

    /// <summary>給畫面綁的一句話；沒有請求時是空字串（不佔版面）。</summary>
    public string Summary
    {
        get
        {
            if (_requests == 0) return "";
            if (_reported == 0)
                return $"本次工作階段 ・ {_requests} 次請求 ・ 端點沒有回報用量";
            string basis = _reported == _requests
                ? $"{_requests} 次請求"
                : $"{_requests} 次請求（其中 {_reported} 次有回報）";
            return $"本次工作階段 ・ {basis} ・ 輸入 {PromptTokens:N0}／輸出 {CompletionTokens:N0}"
                   + (TotalTokens > 0 ? $"／合計 {TotalTokens:N0}" : "") + " tok（端點回報）";
        }
    }

    public void Add(AiUsage usage)
    {
        _requests++;
        if (!usage.Reported) { RaiseSummary(); return; }
        _reported++;
        PromptTokens += usage.PromptTokens;
        CompletionTokens += usage.CompletionTokens;
        TotalTokens += usage.TotalTokens;
        RaiseSummary();
    }

    public void Reset()
    {
        _requests = 0;
        _reported = 0;
        PromptTokens = 0;
        CompletionTokens = 0;
        TotalTokens = 0;
        RaiseSummary();
    }

    private void RaiseSummary() => OnPropertyChanged(nameof(Summary));
}
