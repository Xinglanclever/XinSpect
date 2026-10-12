using System.IO;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 生命週期與報告事實（Vol2 推進第 113–118 項）：EL-002 Office 版本、EL-003 瀏覽器版本、
/// RS-008 機隊批次 CSV、RS-009 機器可讀 diff JSON、RS-011 報告版本比較、PL-010 版本公告草稿。
/// </summary>
/// <remarks>
/// <para><b>版本類只讀登錄</b>：Office／瀏覽器版本來自官方安裝登錄，只報版本字串與更新通道，
/// 不做 EOL 判定（那需要附快照日期的離線表，屬 EL-001／004／005）。</para>
/// <para><b>匯出類零網路</b>：CSV／JSON 都寫到呼叫端指定的路徑；格式由序列化測試釘住；
/// 敏感值依 <see cref="SensitiveValuePolicy"/> 處理。</para>
/// </remarks>
public static class LifecycleAndReportsFactsService
{
    private const string CatEl = "EOL 與壽命資料庫";
    private const string CatRs = "報告輸出";
    private const string CatPl = "平台與包裝";

    public const string OfficeKey = "el.office.version";
    public const string BrowserKey = "el.browser.versions";
    public const string CsvKey = "rs.fleet";
    public const string DiffJsonKey = "rs.diff.json";
    public const string ReportDiffKey = "rs.rdiff";
    public const string ReleaseDraftKey = "pl.releasenote";

    public delegate string? ValueProbe(string path, string valueName);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, ValueProbe? values = null)
    {
        var v = values ?? ReadValue;
        return [OfficeFact(at, v), BrowserFact(at, v), CsvFact(at), DiffJsonFact(at), ReportDiffFact(at), ReleaseDraftFact(at)];
    }

    // ── EL-002 Office 版本 ──────────────────────────────────────────────

    public static HardwareFact OfficeFact(DateTimeOffset at, ValueProbe v)
    {
        string? version;
        string? channel;
        try
        {
            version = v(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "VersionToReport");
            channel = v(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "CDNBaseUrl");
        }
        catch { return new HardwareFact(OfficeKey, CatEl, "Office 版本", "", "", @"HKLM\...\ClickToRun\Configuration",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "Office 登錄讀取失敗——不推測"); }

        if (version is null)
            return new HardwareFact(OfficeKey, CatEl, "Office 版本",
                "未偵測到 Click-to-Run Office（0 是值、不是缺席）——沒安裝或不是 C2R 版本。",
                "", @"HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration", FactTrustLevel.Reported, false, at, null,
                FactAvailability.NotApplicable, "沒有 ClickToRun 安裝記錄（未安裝 Office 不是錯誤）");

        return new HardwareFact(OfficeKey, CatEl, "Office 版本",
            $"{version}" + (channel is null ? "" : $"（更新通道 {channel}）") +
            "。界線：只陳述版本與通道，不做 EOL 判定（要附快照日期的離線表）。",
            "", @"HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration", FactTrustLevel.Reported, false, at);
    }

    // ── EL-003 瀏覽器版本 ───────────────────────────────────────────────

    public static HardwareFact BrowserFact(DateTimeOffset at, ValueProbe v)
    {
        var found = new List<string>();
        try
        {
            string? edge = v(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}", "pv");
            if (edge is not null) found.Add($"Edge {edge}");
            string? chrome = v(@"SOFTWARE\Google\Update\Clients\{8A69D345-D564-463C-AFF1-A69D9E530F96}", "pv");
            if (chrome is not null) found.Add($"Chrome {chrome}");
            string? firefox = v(@"SOFTWARE\Mozilla\Mozilla Firefox", "CurrentVersion");
            if (firefox is not null) found.Add($"Firefox {firefox}");
        }
        catch { return new HardwareFact(BrowserKey, CatEl, "瀏覽器版本", "", "", "瀏覽器更新登錄",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "瀏覽器登錄讀取失敗——不推測"); }

        return new HardwareFact(BrowserKey, CatEl, "瀏覽器版本",
            found.Count == 0
                ? "未偵測到 Edge／Chrome／Firefox 的版本登錄（0 是值、不是缺席）。"
                : string.Join("・", found) + "。界線：只陳述更新器登記的版本，不做 EOL 判定，也不列使用者設定或瀏覽記錄。",
            "個", "Edge／Chrome／Firefox 更新登錄（僅版本）", FactTrustLevel.Reported, false, at, found.Count,
            found.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
            found.Count == 0 ? "沒有可讀的瀏覽器版本記錄" : null);
    }

    // ── RS-008 機隊批次 CSV ─────────────────────────────────────────────

    /// <summary>CSV（RFC 4180 風格）：標頭固定，值含逗號／引號／換行時正確轉義；敏感值依政策。</summary>
    public static string SerializeCsv(IEnumerable<HardwareFact> facts, SensitiveValuePolicy policy = SensitiveValuePolicy.Redact)
    {
        var sb = new StringBuilder();
        sb.Append("key,category,name,value,unit,trust,availability,sensitive,measured_at_utc\r\n");
        foreach (var f in facts ?? [])
        {
            bool redact = policy == SensitiveValuePolicy.Redact && f.Sensitive && !string.IsNullOrEmpty(f.Value);
            sb.Append(Csv(f.Key)).Append(',').Append(Csv(f.Category)).Append(',').Append(Csv(f.Name)).Append(',')
              .Append(Csv(redact ? "（敏感值已遮蔽）" : f.Value)).Append(',').Append(Csv(f.Unit)).Append(',')
              .Append(Csv(f.Trust.ToString())).Append(',').Append(Csv(f.Availability.ToString())).Append(',')
              .Append(f.Sensitive ? "true" : "false").Append(',').Append(f.MeasuredAtUtc.ToString("O"))
              .Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string? s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    public static async Task WriteCsvAsync(string path, IReadOnlyList<HardwareFact> facts,
        SensitiveValuePolicy policy = SensitiveValuePolicy.Redact, CancellationToken ct = default)
        => await File.WriteAllTextAsync(path, SerializeCsv(facts, policy), new UTF8Encoding(false), ct);

    private static HardwareFact CsvFact(DateTimeOffset at)
        => new(CsvKey, CatRs, "機隊批次 CSV",
            "CSV 匯出可用：固定標頭、值含逗號／引號／換行時正確轉義、UTF-8 無 BOM、敏感值依政策遮蔽（預設 Redact）。" +
            "界線：適合試算表／機隊批次；欄位一旦改動要視為格式版本變更。",
            "", "LifecycleAndReportsFactsService.SerializeCsv", FactTrustLevel.Derived, false, at);

    // ── RS-009 機器可讀 diff JSON ───────────────────────────────────────

    public static string SerializeDiffJson(HardwareSnapshotDiff diff)
    {
        var payload = new
        {
            beforeMachineId = diff.BeforeMachineId,
            afterMachineId = diff.AfterMachineId,
            sameMachine = diff.IsSameMachine,
            beforeCapturedAtUtc = diff.BeforeCapturedAtUtc,
            afterCapturedAtUtc = diff.AfterCapturedAtUtc,
            added = diff.Added,
            removed = diff.Removed,
            changed = diff.Changed,
            unchanged = diff.Unchanged,
            changes = diff.Changes.Select(c => new
            {
                key = c.Key,
                kind = c.Kind.ToString(),
                value = c.Current?.Value,
                unit = c.Current?.Unit,
                availability = c.Current?.Availability.ToString(),
                previousValue = c.Previous?.Value,
            }),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static HardwareFact DiffJsonFact(DateTimeOffset at)
        => new(DiffJsonKey, CatRs, "機器可讀 diff JSON",
            "差異可序列化成 JSON（機器可讀）：機器識別、前後時間、added／removed／changed 計數與逐鍵差異。" +
            "界線：只在做過快照比較時有內容；同一份 JSON 不含敏感值原樣（值仍由來源的敏感標記決定）。",
            "", "LifecycleAndReportsFactsService.SerializeDiffJson", FactTrustLevel.Derived, false, at);

    // ── RS-011 報告版本比較 ─────────────────────────────────────────────

    /// <summary>兩份報告的鍵集比較（復用 QueryInsightFactsService.DiffKeySets，不寫第二份）。</summary>
    public static HardwareFact ReportDiffFact(DateTimeOffset at, IReadOnlyCollection<string>? before = null,
        IReadOnlyCollection<string>? after = null)
    {
        if (before is null || after is null)
            return new HardwareFact(ReportDiffKey, CatRs, "報告版本比較",
                "", "", "兩份報告的鍵集（QueryInsightFactsService.DiffKeySets）", FactTrustLevel.Unknown, false, at,
                null, FactAvailability.NotApplicable,
                "沒有兩份報告可比較——版本比較只在有前後兩份時才有意義（工具已就緒、單純沒有資料）");

        var d = QueryInsightFactsService.DiffKeySets(before, after);
        return new HardwareFact(ReportDiffKey, CatRs, "報告版本比較",
            $"前 {before.Count} 鍵／後 {after.Count} 鍵：新增 {d.AddedCount}・消失 {d.RemovedCount}・共通 {d.CommonCount}" +
            (d.Added.Count > 0 ? "；新增：" + string.Join("、", d.Added.Take(8)) : "") +
            (d.Removed.Count > 0 ? "；消失：" + string.Join("、", d.Removed.Take(8)) : "") +
            "。界線：這是鍵集差異，不是值的差異（值的差異看快照比較清單）。",
            "鍵", "QueryInsightFactsService.DiffKeySets", FactTrustLevel.Derived, false, at, d.AddedCount + d.RemovedCount);
    }

    private static HardwareFact ReportDiffFact(DateTimeOffset at)
        => ReportDiffFact(at, null, null);

    // ── PL-010 版本公告草稿 ─────────────────────────────────────────────

    /// <summary>由 ChangelogCatalog 產生繁中 Release 草稿（草稿非發佈；不呼叫任何網路）。</summary>
    public static string BuildReleaseNoteDraft(string version)
    {
        var e = ChangelogCatalog.Entries.FirstOrDefault(x => x.Version == version)
                ?? ChangelogCatalog.Entries.FirstOrDefault();
        if (e is null) return "";
        var sb = new StringBuilder();
        sb.Append("## 曦覽 XinSpect ").Append(e.Version).AppendLine();
        sb.AppendLine();
        sb.Append("**").Append(e.Title).AppendLine("**");
        sb.AppendLine();
        foreach (string item in e.Items) sb.Append("- ").AppendLine(item);
        sb.AppendLine();
        sb.AppendLine("（此為自動草稿：發佈前請人工確認內容與連結，草稿非發佈、不等於已發佈。）");
        return sb.ToString();
    }

    private static HardwareFact ReleaseDraftFact(DateTimeOffset at)
        => new(ReleaseDraftKey, CatPl, "版本公告自動草稿",
            "可由 ChangelogCatalog 產生 Release 草稿（標題＋逐項改動＋「草稿非發佈」聲明）；零網路、不自動發佈。" +
            "界線：草稿是給人改的起點，內容正確性仍由人工確認。",
            "", "LifecycleAndReportsFactsService.BuildReleaseNoteDraft", FactTrustLevel.Derived, false, at);

    private static string? ReadValue(string path, string valueName)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path)
                        ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(valueName)?.ToString();
    }
}
