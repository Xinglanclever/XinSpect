using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

/// <summary>
/// HTML 報告產生器（V7 WP7／M5）：把事實列渲染成<b>自足單檔</b> HTML——無外部資源（無 CDN、無字型、無圖片），
/// 離線可開、可列印。完整性：報告尾端嵌 SHA-256（對不含該行的內容計算），<see cref="Verify"/>
/// 可在沒有本程式的機器上用任何工具重算驗證——「交得出可驗證的報告」的最低實現。
/// 誠實界線：讀不到的列如實顯示原因，警示列（裁決不利）以樣式標記；報告不自動下「好／壞」總結。
/// </summary>
public static class HtmlReportService
{
    public const string HashMarkerStart = "<!-- SHA256:";
    public const string HashMarkerEnd = " -->";

    public static string Build(IReadOnlyList<EvidenceFactRow> rows, string title, DateTimeOffset generatedAtUtc)
    {
        var body = new StringBuilder();
        body.Append("<!DOCTYPE html>\n<html lang=\"zh-Hant\">\n<head>\n<meta charset=\"utf-8\">\n");
        body.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        body.Append("<title>").Append(Escape(title)).Append("</title>\n");
        body.Append(BuildCss());
        body.Append("</head>\n<body>\n");
        body.Append("<h1>").Append(Escape(title)).Append("</h1>\n");
        body.Append("<p class=\"meta\">產生時間（UTC）：").Append(Escape(generatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'")))
            .Append(" ・ 本檔自足無外部資源；尾端 SHA-256 可離線重算驗證。</p>\n");

        foreach (var group in rows.GroupBy(r => r.Category, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            body.Append("<h2>").Append(Escape(group.Key)).Append("</h2>\n<table>\n<thead><tr><th>項目</th><th>值</th><th>來源</th><th>可信度</th></tr></thead>\n<tbody>\n");
            foreach (var row in group.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                string css = row.IsUnavailable ? " class=\"unavail\"" : row.IsWarning ? " class=\"warn\"" : "";
                body.Append("<tr").Append(css).Append("><td>").Append(Escape(row.Name))
                    .Append("</td><td>").Append(Escape(row.ValueText))
                    .Append("</td><td>").Append(Escape(row.Source))
                    .Append("</td><td>").Append(Escape(row.Trust)).Append("</td></tr>\n");
            }
            body.Append("</tbody>\n</table>\n");
        }

        body.Append("<h2>誠實聲明</h2>\n<p class=\"meta\">「讀取失敗／不支援」列代表<b>該項當時讀不到</b>，");
        body.Append("不以 0 或典型值頂替；警示底色列為裁決對使用者不利的項目。本報告不自動下「好／壞」總結，判讀請逐項對照來源。</p>\n");

        // 完整性：雜湊蓋「標記值挖空的完整文件」——驗證時把嵌入值清空重算即可，無需知道邊界。
        string head = body.ToString();
        string FullDocument(string hashValue) => head + HashMarkerStart + hashValue + HashMarkerEnd + "\n</body>\n</html>\n";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FullDocument(""))));
        return FullDocument(hash);
    }

    /// <summary>離線核驗：把嵌入值清空後重算全文雜湊並比對。檔案被改動或截斷會現形。</summary>
    public static bool Verify(string html)
    {
        int start = html.IndexOf(HashMarkerStart, StringComparison.Ordinal);
        if (start < 0) return false;
        int end = html.IndexOf(HashMarkerEnd, start, StringComparison.Ordinal);
        if (end < 0) return false;
        string embedded = html[(start + HashMarkerStart.Length)..end];

        string withoutHash = html.Replace(HashMarkerStart + embedded + HashMarkerEnd, HashMarkerStart + HashMarkerEnd);
        string recomputed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(withoutHash)));
        return string.Equals(embedded, recomputed, StringComparison.Ordinal);
    }

    /// <summary>所有進 HTML 的文字一律過這裡。刻意只轉義五個特殊字元——中文、單位符號（°、μ）等原樣保留，
    /// 讓報告原始碼可讀可比對；WebUtility.HtmlEncode 會把 Latin-1 範圍（如 °）轉成數字實體，破壞可讀性。</summary>
    public static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string BuildCss() => """
        <style>
        body{font-family:"Microsoft JhengHei",sans-serif;margin:24px;max-width:1100px;color:#111;background:#fff}
        h1{font-size:22px;border-bottom:2px solid #333;padding-bottom:6px}
        h2{font-size:16px;margin-top:28px}
        table{border-collapse:collapse;width:100%;font-size:13px}
        th,td{border:1px solid #bbb;padding:5px 8px;text-align:left;vertical-align:top}
        th{background:#eee}
        td:nth-child(2){font-family:Consolas,monospace;word-break:break-all}
        tr.warn td:nth-child(2){background:#fde8e8;font-weight:bold}
        tr.unavail td:nth-child(2){background:#f4f4f4;color:#666}
        .meta{color:#555;font-size:12px}
        @media print{body{margin:8px}h2{page-break-after:avoid}table{page-break-inside:auto}}
        </style>
        """;
}
