using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>遮蔽結果：換過字的文字、換了幾個地方、換的是哪幾類（給人看的摘要）。</summary>
public sealed record RedactionResult(string Text, int Count, IReadOnlyList<string> Kinds)
{
    public static readonly RedactionResult None = new("", 0, Array.Empty<string>());

    public string Summary => Count == 0
        ? "沒有偵測到可識別資訊"
        : $"已遮蔽 {Count} 處（{string.Join("、", Kinds)}）";
}

/// <summary>
/// 把「可以指到這台機器或這個人」的字串換掉，其餘硬體讀值<b>一字不動</b>。
///
/// <para>
/// 送給 AI 的內容會離開這台電腦；對本專案而言，序號與帳號名稱不是「規格」，
/// 是「是誰的機器」。這裡刻意<b>只遮有明確標籤或固定格式的東西</b>：
/// UUID、MAC、標了 IP／位址的 IPv4、<c>C:\Users\名字</c> 路徑、標了序號的英數值、標了主機名稱的值。
/// </para>
/// <para>
/// 為什麼不用「看到像 IPv4 就遮」：<c>1.0.0.0</c>、<c>1.0.0.1</c> 這種版本號會被一起吃掉，
/// 而版本號正是判斷問題需要的資料。遮錯比不遮更糟——它讓回答看起來正常，卻少了關鍵事實。
/// 型號、容量、時脈、溫度、電壓等一律不動。
/// </para>
/// </summary>
public static class AiRedactor
{
    private static readonly (string Kind, Regex Pattern, string Replacement)[] Rules =
    [
        // UUID／GUID：磁碟分割、SMBios UUID、介面 GUID 都是這個形狀。
        ("UUID",
         new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
                   RegexOptions.Compiled),
         "（已遮蔽：UUID）"),

        // MAC：冒號或連字號分隔，六組兩位十六進位。
        ("MAC",
         new Regex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled),
         "（已遮蔽：MAC）"),

        // IPv4：只遮「有標籤」的那種，避免把版本號當成位址吃掉（見類別說明）。
        ("IP 位址",
         new Regex(@"(?<label>(?:IP(?:\s*(?:位址|Address))?|位址|Address)\s*[：:=]\s*)" +
                   @"(?<ip>\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b)",
                   RegexOptions.Compiled | RegexOptions.IgnoreCase),
         "${label}（已遮蔽：IP）"),

        // 使用者設定檔路徑：C:\Users\<名字>\… 直接帶著帳號名稱。
        ("使用者路徑",
         new Regex(@"(?<drive>[A-Za-z]:\\Users\\)[^\\\s""']+", RegexOptions.Compiled),
         "${drive}（已遮蔽：使用者）"),

        // 有標籤的序號（S/N、Serial Number、序號…）：只遮值，標籤留著，讀者仍看得出這裡有欄位。
        // 分隔符除了冒號與等號，也接受「序號是 SN-…／序號為 …」——那是人真的會寫的說法。
        ("序號",
         new Regex(@"(?<label>(?:產品序號|序號碼|序號|Serial\s*(?:Number|No\.?|#)?|S/N)\s*(?:[：:=]|是|為)\s*)" +
                   @"[A-Za-z0-9][A-Za-z0-9._-]{3,}",
                   RegexOptions.Compiled | RegexOptions.IgnoreCase),
         "${label}（已遮蔽：序號）"),

        // 有標籤的主機／電腦名稱。
        ("主機名稱",
         new Regex(@"(?<label>(?:主機名稱|電腦名稱|Computer\s*Name|Host\s*Name)\s*[：:=]\s*)\S+",
                   RegexOptions.Compiled | RegexOptions.IgnoreCase),
         "${label}（已遮蔽：主機名稱）"),
    ];

    /// <summary>遮蔽文字。<paramref name="text"/> 為 null 或空時原樣回傳、計數為 0。</summary>
    public static RedactionResult Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new RedactionResult(text ?? "", 0, Array.Empty<string>());

        string result = text;
        int count = 0;
        var kinds = new List<string>();

        foreach (var (kind, pattern, replacement) in Rules)
        {
            bool hit = false;
            result = pattern.Replace(result, m =>
            {
                hit = true;
                count++;
                return replacement.Replace("${label}", m.Groups["label"].Value)
                                  .Replace("${drive}", m.Groups["drive"].Value);
            });
            if (hit) kinds.Add(kind);
        }

        return new RedactionResult(result, count, kinds);
    }

    /// <summary>把多段文字一起遮，回報總計與類別（同一類只列一次）。</summary>
    public static RedactionResult ApplyAll(IEnumerable<string?> texts)
    {
        var kinds = new List<string>();
        int count = 0;
        var output = new List<string>();
        foreach (string? t in texts)
        {
            var r = Apply(t);
            output.Add(r.Text);
            count += r.Count;
            foreach (string k in r.Kinds)
                if (!kinds.Contains(k)) kinds.Add(k);
        }
        return new RedactionResult(string.Join("\n", output), count, kinds);
    }
}
