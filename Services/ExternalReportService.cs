using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XinSpect;

/// <summary>
/// 外部報告解析：讀入 GPU-Z sensor log（txt）、AIDA64 XML 報告、HWiNFO CSV，
/// 把讀值轉成 UI 可顯示的列。不啟動任何子行程、不上網、零特權——只讀使用者選的檔案。
/// 「不捆綁，但能讀別人的證據。」
/// </summary>
public static class ExternalReportService
{
    /// <summary>單列呈現：來源名稱、欄位名、值、單位、量測時間（若有）。</summary>
    public sealed record ReportRow(string Tool, string Sensor, string Field, string Value, string Unit,
        DateTimeOffset? MeasuredAt, double? NumericValue);

    /// <summary>解析結果。</summary>
    public sealed record ReportResult(string FileName, string Format, string Status, IReadOnlyList<ReportRow> Rows)
    {
        public int Count => Rows.Count;
    }

    // ---- 自動偵測格式 ----------------------------------------------------------

    public static ReportResult ParseFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var bytes = File.ReadAllBytes(path);
        // BOM 檢測：UTF-8 BOM (EF BB BF)、UTF-16 LE BOM (FF FE)
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else
            text = Encoding.Latin1.GetString(bytes);

        // XML 開頭 → AIDA64
        if (text.TrimStart().StartsWith("<") && text.Contains("aida64", StringComparison.OrdinalIgnoreCase))
            return ParseAida64(path, text);

        // CSV 標頭含 HWiNFO 特徵
        if (ext == ".csv" || text.StartsWith("Date,Time", StringComparison.OrdinalIgnoreCase)
            || text.Contains("HWiNFO", StringComparison.OrdinalIgnoreCase))
            return ParseHwinfoCsv(path, text);

        // 預設嘗試 GPU-Z txt
        return ParseGpuz(path, text);
    }

    // ---- GPU-Z sensor log ---------------------------------------------------

    // GPU-Z 格式：表頭為 "GPU Core Clock, GPU Memory Clock, ..., [MHz] [MHz] [C]..." 
    // 資料行：tab 分隔，第一欄時間 yyyy/M/d HH:mm:ss
    internal static ReportResult ParseGpuz(string path, string text)
    {
        var rows = new List<ReportRow>();
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return new(Path.GetFileName(path), "GPU-Z", "內容不足，無法解析", rows);

        // 第二行是單位列（[MHz] [C] [V] 等），第三行起是資料
        string[] headers = lines[0].Split(',');
        string[] units = lines.Length > 1 ? lines[1].Split(',') : [];
        var unitList = units.Select(u => u.Trim().Trim('[', ']')).ToArray();

        for (int li = 2; li < lines.Length; li++)
        {
            string[] vals = lines[li].Split(',');
            if (vals.Length < 2) continue;
            DateTimeOffset? ts = ParseTimestamp(vals[0].Trim());
            for (int i = 1; i < vals.Length; i++)
            {
                string v = vals[i].Trim();
                if (string.IsNullOrEmpty(v) || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    continue;
                string unit = i-1 < unitList.Length ? unitList[i-1] : "";
                string h = headers[i-1].Trim();
                rows.Add(new ReportRow("GPU-Z", "感測器", h, num.ToString("0.###", CultureInfo.InvariantCulture),
                    unit, ts, num));
            }
        }
        return new(Path.GetFileName(path), "GPU-Z", rows.Count > 0
            ? $"已解析 {rows.Count} 個讀值" : "未找到數值欄位", rows);
    }

    // ---- AIDA64 XML ---------------------------------------------------------

    internal static ReportResult ParseAida64(string path, string text)
    {
        var rows = new List<ReportRow>();
        try
        {
            var doc = XDocument.Parse(text);
            foreach (var el in doc.Descendants().Where(e => e.Attribute("id") is not null && !string.IsNullOrWhiteSpace(e.Value)))
            {
                string id = el.Attribute("id")!.Value;
                string val = el.Value.Trim();
                // AIDA64 XML 通常每個 item 是 <item id="SENSOR_NAME">VALUE</item>，值可能是 "45 °C"
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(val)) continue;

                string unit = "";
                double? num = null;
                // 嘗試從值裡剝出數字和單位
                var match = System.Text.RegularExpressions.Regex.Match(val, @"^(-?[\d.,]+)\s*(.*)$");
                if (match.Success && double.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    num = n;
                    unit = match.Groups[2].Value.Trim();
                }
                string sensor = GetAidaSection(el);
                rows.Add(new ReportRow("AIDA64", sensor, id, val, unit, null, num));
            }
        }
        catch (XmlException ex)
        {
            return new(Path.GetFileName(path), "AIDA64 XML", $"XML 解析失敗：{ex.Message}", rows);
        }
        return new(Path.GetFileName(path), "AIDA64 XML", rows.Count > 0
            ? $"已解析 {rows.Count} 個欄位" : "未找到感測器欄位", rows);
    }

    // 找出 XML 節點的祖區段名（如 Temperatures / Voltages 等）
    private static string GetAidaSection(XElement el)
    {
        var p = el.Parent;
        while (p is not null)
        {
            string n = p.Name.LocalName;
            if (n is "page" or "section") return n;
            p = p.Parent;
        }
        return "報告";
    }

    // ---- HWiNFO CSV ---------------------------------------------------------

    // HWiNFO sensor log CSV：第一列欄名（Date, Time, Sensor #1, ...），第二列單位，第三列起資料
    internal static ReportResult ParseHwinfoCsv(string path, string text)
    {
        var rows = new List<ReportRow>();
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3)
            return new(Path.GetFileName(path), "HWiNFO CSV", "內容不足，無法解析", rows);

        string[] headers = ParseCsvLine(lines[0]);
        string[] units = ParseCsvLine(lines[1]);

        for (int li = 2; li < lines.Length; li++)
        {
            string[] vals = ParseCsvLine(lines[li]);
            if (vals.Length < 3) continue;
            // HWiNFO 第一兩欄是 Date 和 Time
            DateTimeOffset? ts = ParseHwinfoTimestamp(vals[0], vals[1]);
            for (int i = 2; i < vals.Length && i < headers.Length; i++)
            {
                string v = vals[i].Trim();
                if (string.IsNullOrEmpty(v) || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    continue;
                string unit = i < units.Length ? units[i].Trim().Trim('"', '[', ']') : "";
                rows.Add(new ReportRow("HWiNFO", "感測器", headers[i].Trim(), num.ToString("0.###", CultureInfo.InvariantCulture),
                    unit, ts, num));
            }
        }
        return new(Path.GetFileName(path), "HWiNFO CSV", rows.Count > 0
            ? $"已解析 {rows.Count} 個讀值" : "未找到數值欄位", rows);
    }

    // 簡單 CSV 分割（處理引號內的逗號）
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuote = false;
        var sb = new StringBuilder();
        foreach (char c in line)
        {
            if (c == '"') inQuote = !inQuote;
            else if (c == ',' && !inQuote) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static DateTimeOffset? ParseHwinfoTimestamp(string date, string time)
    {
        if (DateTimeOffset.TryParseExact($"{date.Trim()} {time.Trim()}", "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dtoExact))
            return dtoExact;
        if (DateTimeOffset.TryParse($"{date.Trim()} {time.Trim()}", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset dto))
            return dto;
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(string s)
    {
        string[] formats = ["yyyy/M/d HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy/M/d HH:mm", "yyyy-MM-dd HH:mm"];
        foreach (var fmt in formats)
            if (DateTimeOffset.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dtoExact))
                return dtoExact;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto))
            return dto;
        return null;
    }

    // ---- 轉 HardwareFact 給時間膠囊 -----------------------------------------

    /// <summary>把解析結果轉成可存入時間膠囊的事實列（key 由 tool+field 穩定命名）。</summary>
    public static List<HardwareFact> ToFacts(ReportResult r, bool includeSensitive = false)
    {
        var at = DateTimeOffset.UtcNow;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var f = new List<HardwareFact>();
        foreach (var row in r.Rows)
        {
            string key = HardwareSnapshotService.StableKey($"{r.Format}\0{row.Tool}\0{row.Field}");
            int occ = seen.TryGetValue(key, out int s) ? s + 1 : 1;
            seen[key] = occ;
            string k = occ > 1 ? $"{key}.{occ}" : key;
            string category = $"外部報告・{r.Format}";
            f.Add(new HardwareFact(k, category, row.Field,
                row.NumericValue is not null ? row.NumericValue!.Value.ToString("0.###", CultureInfo.InvariantCulture) : row.Value,
                row.Unit, row.Tool, FactTrustLevel.Reported, false,
                row.MeasuredAt ?? at, row.NumericValue));
        }
        return f;
    }
}
