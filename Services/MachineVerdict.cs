namespace XinSpect;

/// <summary>一條驗機結論,連同它屬於哪個範圍(整機／某顆碟)。</summary>
public sealed record VerdictLine(string Scope, VerifyFinding Finding);

/// <summary>
/// 整機驗機的總結。把記憶體／儲存／電池等所有規則的判定收攏成一份可分享的報告——
/// **但仍守三態(相符／矛盾／讀不到),不給分數、不下「正品／翻新」結論**。
/// </summary>
/// <remarks>
/// 存在的理由:單條規則各自散在證據實驗室裡,買二手機的人要的是「一次看完、能存證、能傳給賣家」
/// 的一張單子。這裡把它們收攏並排序(矛盾在最前、讀不到次之、相符最後),但不把數字加總成一個
/// 「健康分數」——分數會讓人停止思考,而每一條矛盾都可能有正當成因。
/// </remarks>
public sealed record MachineVerdict(
    DateTime GeneratedAt,
    int Match,
    int Conflict,
    int Unread,
    IReadOnlyList<VerdictLine> Lines)
{
    public int Total => Match + Conflict + Unread;

    /// <summary>一句話總結。刻意不含分數、不含「正品／翻新」字眼。</summary>
    public string Summary
    {
        get
        {
            if (Total == 0) return "沒有可對帳的事實——硬體資訊都還沒讀到。";
            string head = $"共 {Total} 條檢查:矛盾 {Conflict}、讀不到 {Unread}、相符 {Match}。";
            if (Conflict == 0)
                return Unread > 0
                    ? head + "沒有發現矛盾;有些項目讀不到(多為權限或此機不支援),那些沒被檢查到。"
                    : head + "沒有發現矛盾。這不代表機器一定沒問題——只代表讀得到的事實彼此對得上。";
            var parts = Lines.Where(l => l.Finding.Verdict == VerifyVerdict.Conflict)
                             .Select(l => l.Finding.Part).Distinct();
            return head + "矛盾集中在:" + string.Join("、", parts) + "。每條矛盾都附了可能的正當成因,判斷留給你。";
        }
    }
}

/// <summary>把規則判定收攏成 <see cref="MachineVerdict"/> 並產出純文字報告。純函式,零硬體相依。</summary>
public static class MachineVerdictBuilder
{
    /// <summary>排序:矛盾(0)→讀不到(1)→相符(2);同級內維持輸入順序(穩定)。</summary>
    private static int Order(VerifyVerdict v) => v switch
    {
        VerifyVerdict.Conflict => 0, VerifyVerdict.Unread => 1, _ => 2,
    };

    public static MachineVerdict Build(DateTime now, IEnumerable<VerdictLine> lines)
    {
        var ordered = lines.OrderBy(l => Order(l.Finding.Verdict)).ToList();
        int conflict = ordered.Count(l => l.Finding.Verdict == VerifyVerdict.Conflict);
        int unread = ordered.Count(l => l.Finding.Verdict == VerifyVerdict.Unread);
        int match = ordered.Count(l => l.Finding.Verdict == VerifyVerdict.Match);
        return new MachineVerdict(now, match, conflict, unread, ordered);
    }

    private static string Mark(VerifyVerdict v) => v switch
    {
        VerifyVerdict.Conflict => "✗ 矛盾", VerifyVerdict.Unread => "— 讀不到", _ => "✓ 相符",
    };

    /// <summary>純文字報告:一段總結 + 逐條(標記／範圍／編號／標題／說明／證據來源／正當成因)。</summary>
    public static string ToPlainText(MachineVerdict v)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("曦覽 XinSpect ・ 整機驗機報告");
        sb.AppendLine($"產生時間:{v.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(v.Summary);
        sb.AppendLine(new string('─', 40));

        foreach (var line in v.Lines)
        {
            var f = line.Finding;
            sb.AppendLine($"{Mark(f.Verdict)}  [{f.Id}] {line.Scope} ・ {f.Title}");
            sb.AppendLine($"    {f.Explanation}");
            if (f.Evidence.Length > 0)
                sb.AppendLine("    證據:" + string.Join("；", f.Evidence.Select(e => $"{e.Label}={e.Value}（{e.Method}）")));
            if (!string.IsNullOrWhiteSpace(f.BenignCause))
                sb.AppendLine($"    可能的正當成因:{f.BenignCause}");
        }
        sb.AppendLine(new string('─', 40));
        sb.AppendLine("本報告只列出讀得到的事實與彼此的對帳結果;不給分數,不下「正品／翻新」結論。讀不到的項目即未檢查。");
        return sb.ToString();
    }
}
