using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// BCD 開機順序（U10 第三圈 BD-0001）與選單等待秒數（BD-0003），第 315 輪。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要這一組：</b>`boot.*` 既有的五把鍵談的是「上次開機多久、快速開機開不開」，
/// 不含「**先開誰**」。多系統機器上，`displayorder` 就是使用者每天看到的那份清單順序，
/// 而它只存在 BCD 裡——沒有第二個地方可以查。
/// </para>
/// <para>
/// <b>純解析。</b>輸入是 <see cref="BootOrderReader"/> 取回的文字，這支不開行程、不讀磁碟，
/// 所以測試可以餵任意輸出（包含壞掉的）重跑。
/// </para>
/// <para>
/// <b>不判斷合不合理：</b>只寫「清單長什麼樣、預設是哪一個、預設在不在清單裡」。
/// 「這個順序對不對」「該不該用 30 秒」不是本程式能回答的。
/// </para>
/// </remarks>
public static class BootOrderFactsService
{
    /// <summary>開機順序總覽（靜態鍵）。</summary>
    public const string SummaryKey = "boot.order.summary";

    /// <summary>預設開機項（靜態鍵）。</summary>
    public const string DefaultEntryKey = "boot.order.default";

    /// <summary>選單等待秒數（靜態鍵）。</summary>
    public const string TimeoutKey = "boot.order.timeout";

    /// <summary>逐項清單家族：<c>boot.order.item.{序號}</c>（成員＝清單上有幾項）。</summary>
    public const string ItemPrefix = "boot.order.item.";

    private const string Category = "開機";
    private const string Source = "bcdedit /enum（唯讀）";

    /// <summary>`欄位名` 與值：欄位名是 ASCII 識別字，值至少兩個空白之後。</summary>
    private static readonly Regex FieldLine = new(@"^([A-Za-z][A-Za-z0-9]*)[ \t]{2,}(\S.*)$", RegexOptions.Compiled);

    /// <summary>把 bcdedit /enum 的文字轉成事實；<paramref name="output"/> 為 null 表示沒讀到。</summary>
    public static IReadOnlyList<HardwareFact> Collect(string? output, string? readFailureReason, DateTimeOffset at)
    {
        if (output is null)
        {
            string reason = string.IsNullOrWhiteSpace(readFailureReason)
                ? "沒有讀到 bcdedit /enum 的輸出。"
                : readFailureReason!;

            return
            [
                new HardwareFact(SummaryKey, Category, "開機順序", "", "項", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason),
                new HardwareFact(DefaultEntryKey, Category, "預設開機項", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason),
                new HardwareFact(TimeoutKey, Category, "選單等待", "", "秒", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason),
            ];
        }

        var blocks = ParseBlocks(output);
        var bootMgr = blocks.FirstOrDefault(b => Identifiers(b).Any(id => id.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase)));

        if (bootMgr is null)
        {
            const string reason = "bcdedit /enum 的輸出裡找不到 {bootmgr} 區塊——輸出可能被截斷，或這台機器的系統存放區不是預設那一個。";
            return
            [
                new HardwareFact(SummaryKey, Category, "開機順序", "", "項", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason),
                new HardwareFact(DefaultEntryKey, Category, "預設開機項", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason),
            ];
        }

        var descriptions = DescribeAll(blocks);
        var order = Values(bootMgr, "displayorder");

        var facts = new List<HardwareFact>(order.Count + 3);
        for (int i = 0; i < order.Count; i++)
        {
            string id = order[i];
            int position = i + 1;
            string value = descriptions.TryGetValue(id, out string? desc)
                ? $"第 {position} 位：{id} → 「{desc}」"
                : $"第 {position} 位：{id}（這個識別碼沒有出現在 /enum 的其他區塊，描述不在輸出裡）";

            facts.Add(new HardwareFact($"{ItemPrefix}{position}", Category, $"開機選單第 {position} 位", value, "",
                Source, FactTrustLevel.Reported, false, at, position, FactAvailability.Present, null));
        }

        string? defaultId = Values(bootMgr, "default").FirstOrDefault();
        facts.Add(DefaultFact(defaultId, order, descriptions, at));

        string? timeoutText = Values(bootMgr, "timeout").FirstOrDefault();
        facts.Add(TimeoutFact(timeoutText, at));

        facts.Add(SummaryFact(order, defaultId, descriptions, timeoutText, at));
        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static HardwareFact DefaultFact(string? defaultId, List<string> order,
        Dictionary<string, string> descriptions, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(defaultId))
        {
            return new HardwareFact(DefaultEntryKey, Category, "預設開機項",
                "BCD 的開機管理程式區塊沒有 default 欄位——本程式不推測逾時後會開哪一個", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
        }

        string label = descriptions.TryGetValue(defaultId, out string? desc) ? $"「{desc}」（{defaultId}）" : defaultId;
        int position = order.FindIndex(id => id.Equals(defaultId, StringComparison.OrdinalIgnoreCase));
        string where = position >= 0
            ? $"在 displayorder 的第 {position + 1} 位"
            : "**不在 displayorder 清單裡**——清單上的項目與預設項目不一致";

        return new HardwareFact(DefaultEntryKey, Category, "預設開機項", $"{label}；{where}", "", Source,
            FactTrustLevel.Reported, false, at, position >= 0 ? position + 1 : null, FactAvailability.Present, null);
    }

    private static HardwareFact TimeoutFact(string? timeoutText, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(timeoutText))
        {
            return new HardwareFact(TimeoutKey, Category, "選單等待",
                "BCD 的開機管理程式區塊沒有 timeout 欄位——等待秒數由其他層決定（例如韌體），本機沒有讀到", "秒",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
        }

        bool numeric = int.TryParse(timeoutText, out int seconds);
        return new HardwareFact(TimeoutKey, Category, "選單等待",
            numeric ? $"{seconds} 秒" : timeoutText, "秒", Source,
            FactTrustLevel.Reported, false, at, numeric ? seconds : null, FactAvailability.Present, null);
    }

    private static HardwareFact SummaryFact(List<string> order, string? defaultId,
        Dictionary<string, string> descriptions, string? timeoutText, DateTimeOffset at)
    {
        var parts = new List<string>
        {
            order.Count == 0
                ? "displayorder 欄位不存在或為空——BCD 裡沒有列出開機選單的順序"
                : $"{order.Count} 項依序：{string.Join("、", order.Select((id, i) => $"{i + 1}.{descriptions.GetValueOrDefault(id, id)}"))}",
            string.IsNullOrWhiteSpace(defaultId) ? "預設：沒有 default 欄位" : $"預設：{defaultId}",
            string.IsNullOrWhiteSpace(timeoutText) ? "等待：沒有 timeout 欄位" : $"等待：{timeoutText} 秒",
            "只讀 BCD 陳述現況，不判斷這個順序合不合理",
        };

        return new HardwareFact(SummaryKey, Category, "開機順序", string.Join("；", parts), "項", Source,
            FactTrustLevel.Reported, false, at, order.Count, FactAvailability.Present, null);
    }

    // ── 解析 ─────────────────────────────────────────────────────────

    private static List<Dictionary<string, List<string>>> ParseBlocks(string text)
    {
        var blocks = new List<Dictionary<string, List<string>>>();
        Dictionary<string, List<string>>? current = null;
        string? currentField = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { current = null; currentField = null; continue; }
            if (IsRule(line)) continue;

            var match = FieldLine.Match(line);
            if (match.Success)
            {
                if (current is null)
                {
                    current = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    blocks.Add(current);
                }

                string name = match.Groups[1].Value;
                if (!current.TryGetValue(name, out var values))
                    current[name] = values = [];
                values.Add(match.Groups[2].Value.Trim());
                currentField = name;
                continue;
            }

            if (current is not null && currentField is not null && char.IsWhiteSpace(line[0]))
            {
                current[currentField].Add(line.Trim());
                continue;
            }

            // 其餘都是區塊標題（各語言不同，內容不參與解析）——當成邊界，下一個欄位行開新區塊。
            current = null;
            currentField = null;
        }

        return blocks;
    }

    private static bool IsRule(string line)
    {
        string t = line.Trim();
        return t.Length >= 3 && t.All(c => c == '-');
    }

    private static List<string> Identifiers(Dictionary<string, List<string>> block)
        => Values(block, "identifier");

    /// <summary>欄位值：多行清單（displayorder）要在空白處切開。</summary>
    private static List<string> Values(Dictionary<string, List<string>> block, string name)
        => block.TryGetValue(name, out var raw)
            ? raw.SelectMany(v => v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList()
            : [];

    private static Dictionary<string, string> DescribeAll(List<Dictionary<string, List<string>>> blocks)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            string? id = Identifiers(block).FirstOrDefault();
            if (id is null) continue;
            if (block.TryGetValue("description", out var desc) && desc.Count > 0)
                map[id] = desc[0];
        }
        return map;
    }
}
