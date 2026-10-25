using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace XinSpect;

/// <summary>
/// 第三方授權清單（U10-UI-0034）：把倉庫根的 <c>THIRD-PARTY-NOTICES.md</c> 解析成
/// 「元件 × 版本 × 授權」清單，供關於頁呈現。
///
/// <para>
/// 讀取順序是「資源優先、檔案其次、皆無＝如實標缺」：csproj 把清單內嵌為資源
/// （單檔發佈版走這條——根目錄實檔不隨 exe 走）；開發機上資源不在時退回磁碟檔
/// （exe 旁，或沿父目錄找到倉庫根）；兩者皆無時回 <see cref="ThirdPartyNoticesCatalog.LicensesReport.Available"/>
/// ＝ false 並帶原因——<b>不用空清單冒充、也不丟例外</b>（倉庫的誠實慣例：讀不到就說讀不到，
/// 讀到了但抽不出內容也如實說，不拿 0 筆充數）。
/// </para>
///
/// <para>清單內容本身由 <c>Tests/ThirdPartyNoticesTests</c> 守著與 csproj 對帳（加套件沒寫進
/// 清單＝紅燈）；本類只負責讀與解析，不改寫任何內容。</para>
/// </summary>
public static class ThirdPartyNoticesCatalog
{
    /// <summary>csproj 內嵌時用的資源邏輯名（與 <c>&lt;EmbeddedResource LogicalName&gt;</c> 一致）。</summary>
    public const string ResourceName = "XinSpect.THIRD-PARTY-NOTICES.md";

    /// <summary>清單檔名（磁碟退路與倉庫根查找都用它）。</summary>
    public const string FileName = "THIRD-PARTY-NOTICES.md";

    /// <summary>整檔讀不到時的固定原因字串（已收錄進 EnglishStrings，顯示前過 T()）。</summary>
    public const string MissingReason = "授權清單檔讀不到";

    /// <summary>一條授權條目：元件名、版本、授權（清單原文照抄，不在這裡改寫）。</summary>
    public sealed record LicenseEntry(string Component, string Version, string License);

    /// <summary>
    /// 清單讀取結果。<c>Available = false</c> 時 <see cref="UnavailableReason"/> 說明原因
    /// （繁中原文；UI 顯示前過 <c>LanguageService.T</c>），此時 <see cref="Entries"/> 為空——
    /// 但「空 Entries」只在「讀不到／解析不出」時出現，不會拿它冒充「本程式沒有用任何第三方元件」。
    /// </summary>
    public sealed record LicensesReport(
        bool Available,
        string? UnavailableReason,
        IReadOnlyList<LicenseEntry> Entries,
        string Source);

    public static LicensesReport Load() => Load(
        () => ReadResource(Assembly.GetExecutingAssembly(), ResourceName),
        path => File.Exists(path) ? File.ReadAllText(path) : null);

    /// <summary>測試注入點：資源讀取與磁碟讀取都可替換（模擬「皆無」不必動真檔案）。</summary>
    internal static LicensesReport Load(Func<string?> readResource, Func<string, string?> readFile)
    {
        string? embedded = readResource();
        if (embedded is not null)
            return Report(embedded, "embedded:" + ResourceName);

        // 磁碟退路一：exe／輸出目錄旁。
        string? beside = readFile(Path.Combine(AppContext.BaseDirectory, FileName));
        if (beside is not null)
            return Report(beside, FileName);

        // 磁碟退路二：開發機的倉庫根（沿父目錄找 XinSpect.csproj，與各測試的 RepoRoot 同一口徑）。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj"))) continue;
            string? repoFile = readFile(Path.Combine(dir.FullName, FileName));
            return repoFile is not null ? Report(repoFile, FileName) : Missing();
        }

        return Missing();
    }

    /// <summary>
    /// 解析清單裡的元件表（執行期表與建置期表，欄序都是「元件｜版本｜授權｜…」）。
    /// 表頭與分隔列跳過；候選鍵那種「資源優先」的正文敘述不帶管線列，天然不會被吃進來。
    /// </summary>
    public static List<LicenseEntry> Parse(string markdown)
    {
        var entries = new List<LicenseEntry>();
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith('|')) continue;

            // cells[0] 是首管線前的空字串；cells[1]=元件、cells[2]=版本、cells[3]=授權。
            string[] cells = line.Split('|');
            if (cells.Length < 4) continue;

            string component = cells[1].Trim();
            if (component.Length == 0 || component == "元件") continue;          // 表頭
            if (component.Trim('-', ':', ' ').Length == 0) continue;             // 分隔列 |---|

            string license = cells[3].Trim();
            if (license.Length == 0) continue;

            entries.Add(new LicenseEntry(component, cells[2].Trim(), license));
        }
        return entries;
    }

    private static LicensesReport Report(string markdown, string source)
    {
        var entries = Parse(markdown);
        if (entries.Count == 0)
        {
            return new LicensesReport(false,
                "授權清單讀到了，但抽不出元件表——檔案格式可能變了",
                Array.Empty<LicenseEntry>(), source);
        }
        return new LicensesReport(true, null, entries, source);
    }

    private static string? ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static LicensesReport Missing() =>
        new(false, MissingReason, Array.Empty<LicenseEntry>(), "");
}
