using System.Globalization;
using System.Text;

namespace XinSpect;

/// <summary>
/// 把 AI 對話匯出成 Markdown。與「複製整段對話」的差別是：匯出會<b>自己寫下這份檔案是怎麼來的</b>
/// ——什麼時候、用哪個模型與供應商、有沒有遮蔽、遮蔽了幾處、以及有沒有內容是遮蔽不了的。
///
/// <para>
/// 貼給別人看的一段對話，最容易被誤讀的就是「裡面有沒有我的東西」。所以標頭不是裝飾：
/// 它把「遮蔽已開啟／已關閉」與「圖片附件無法遮蔽」寫在同一個位置，讀的人不必猜。
/// </para>
/// </summary>
public static class AiTranscript
{
    /// <summary>建議檔名：固定前綴＋時間戳，洗掉不能當檔名的字元（呼叫端再用 BrowserPolicy 那套規則檢查一次也不衝突）。</summary>
    public static string SuggestFileName(DateTimeOffset now)
        => "xinspect-ai-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".md";

    /// <summary>
    /// 產生 Markdown 全文。<paramref name="redact"/> 為 true 時逐則套用 <see cref="AiRedactor"/>，
    /// 並在標頭寫出遮蔽了幾處、哪幾類。
    /// </summary>
    public static string Build(
        IEnumerable<AiMessage> messages,
        DateTimeOffset now,
        string modelLabel,
        string providerLabel,
        bool redact,
        AiUsage sessionUsage = default)
    {
        var list = (messages ?? Array.Empty<AiMessage>()).ToList();
        var sb = new StringBuilder();

        sb.AppendLine("# 曦覽 XinSpect ・ AI 對話紀錄");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 內容 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 匯出時間 | {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| 模型 | {Clean(modelLabel)} |");
        sb.AppendLine($"| 供應商 | {Clean(providerLabel)} |");
        if (sessionUsage.Reported)
            sb.AppendLine($"| 本次工作階段用量 | {Clean(sessionUsage.Describe())} |");
        sb.AppendLine();

        int total = 0;
        var kinds = new List<string>();
        var rendered = new List<(AiMessage Message, string Text)>();

        foreach (var m in list)
        {
            if (m.Text.Length == 0) continue;
            string text = m.Text;
            if (redact)
            {
                var r = AiRedactor.Apply(text);
                text = r.Text;
                total += r.Count;
                foreach (string k in r.Kinds)
                    if (!kinds.Contains(k)) kinds.Add(k);
            }
            rendered.Add((m, text));
        }

        sb.AppendLine(redact
            ? $"**遮蔽**：已開啟（共 {total} 處"
              + (kinds.Count > 0 ? "：" + string.Join("、", kinds) : "") + "）。"
              + "型號、容量、時脈、溫度、電壓等判斷用的讀值一律保留。"
            : "**遮蔽**：已關閉——這份檔案可能含有序號、MAC 或使用者路徑原文。");
        sb.AppendLine();
        sb.AppendLine("> 注意：圖片與檔案的內容不在這份文字檔裡；圖片中的文字與序號無法自動遮蔽。");
        sb.AppendLine();

        if (rendered.Count == 0)
        {
            sb.AppendLine("（沒有對話內容）");
            return sb.ToString();
        }

        foreach (var (message, text) in rendered)
        {
            if (message.IsTool)
            {
                // 工具查詢列用引用塊：它是有脈絡的旁註，不是對話的一部分。
                foreach (string line in text.Split('\n'))
                    sb.AppendLine("> " + line);
                sb.AppendLine();
                continue;
            }

            sb.AppendLine(message.IsUser ? "## 你" : "## AI");
            sb.AppendLine();
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>表格儲存格裡不能有直線與換行，否則整張表會歪掉。</summary>
    private static string Clean(string? value)
    {
        string s = (value ?? "").Replace("|", "／").Replace("\r", " ").Replace("\n", " ");
        return s.Trim().Length == 0 ? "（未提供）" : s.Trim();
    }
}
