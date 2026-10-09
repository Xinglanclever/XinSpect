using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 覆蓋申報的 UI 服務：把「事實鍵的對帳覆蓋」與「知識表收錄率」兩份申報攤到設定頁。
/// </summary>
/// <remarks>
/// <para>
/// 這兩份申報的共同點是：它們申報的都是<b>「我們知道自己不知道什麼」</b>。
/// 這種東西不會產生任何錯誤訊息，所以特別需要一個看得見的地方。
/// </para>
/// <para>
/// <b>資料來源是編譯期固定的目錄，不是執行期掃原始碼。</b>第一版做成掃 <c>Services/</c>，
/// 那在開發機上可行（往上找得到倉庫根），但發佈版是單一 exe、目錄裡只有 <c>Assets/</c> 與執行檔——
/// 掃不到時回報「0 個鍵」，而覆蓋申報把 0 讀成「全部都覆蓋了」。
/// 那是「掃不到」冒充「沒有問題」。現在改用 <see cref="FactKeyCatalog"/>（編譯期固定，
/// 由 <c>FactKeyCatalogTests</c> 與原始碼逐鍵比對）。
/// </para>
/// <para>
/// <b>規則來源是執行期真正在跑的那一份。</b>原本讀 <c>Rules/builtin.json</c>——
/// 那個檔沒有隨程式出貨（csproj 完全沒提到它），生產路徑跑的是內建的
/// <see cref="FactRelationRules.All"/>。讀一個不存在的檔會得到「0 條規則」，
/// 於是申報會顯示「所有鍵都已覆蓋」。
/// </para>
/// </remarks>
public sealed class CoverageService : ObservableObject
{
    private bool _loaded;

    /// <summary>事實鍵對帳覆蓋的申報（一行結論）。</summary>
    public string RuleCoverageHeadline { get; private set; } = "尚未計算。";

    /// <summary>事實鍵對帳覆蓋的細節：未覆蓋的鍵與它們代表什麼。</summary>
    public string RuleCoverageDetail { get; private set; } = "";

    /// <summary>知識表收錄率的整體申報。</summary>
    public string KnowledgeSummary { get; private set; } = "尚未計算。";

    /// <summary>逐表申報。</summary>
    public ObservableCollection<string> KnowledgeTables { get; } = [];

    /// <summary>對帳規則來源的申報（內建 vs 外部形式）。</summary>
    public string RuleSourceNote { get; private set; } = "尚未計算。";

    /// <summary>第一次進頁時計算一次。純記憶體運算，不碰硬體、不需權限、不讀檔案。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    /// <summary>重新計算。</summary>
    public void Refresh()
    {
        try
        {
            var rules = RuntimeRules();
            var summary = FactCoverageReport.Report(FactKeyCatalog.Keys, rules);

            RuleCoverageHeadline = summary.Headline;
            RuleCoverageDetail = BuildDetail(summary);
            KnowledgeSummary = KnowledgeCoverage.Summary();
            KnowledgeTables.Clear();
            foreach (var t in KnowledgeCoverage.Tables) KnowledgeTables.Add(t.Describe());
            RuleSourceNote = RuleSourceReport.Describe();
        }
        catch (Exception ex)
        {
            Diag.Swallow("覆蓋申報計算", ex, "申報維持無法計算");
            RuleCoverageHeadline = "無法計算（" + ex.Message + "）";
            RuleCoverageDetail = "";
        }

        OnPropertyChanged(nameof(RuleCoverageHeadline));
        OnPropertyChanged(nameof(RuleCoverageDetail));
        OnPropertyChanged(nameof(KnowledgeSummary));
        OnPropertyChanged(nameof(RuleSourceNote));
    }

    /// <summary>
    /// 覆蓋申報的細節。<b>沒有任何事實鍵或規則時如實說「無法申報」</b>——
    /// 這是第一版的缺陷所在：0 個鍵被讀成「全部都覆蓋了」。
    /// </summary>
    internal static string BuildDetail(FactCoverageReport.Summary summary)
    {
        if (summary.Total == 0)
            // 措辭刻意不在畫面上重述第一版那句假結論——那句話若出現，使用者掃過去
            // 只會看到肯定的字樣，正是要防的誤讀。版本沿革記在 ChangelogCatalog。
            return "讀不到事實鍵目錄——這一版沒有把目錄編進去，或目錄是空的。"
                 + "這一項無法申報：沒有鍵可以檢查，不代表沒有未覆蓋的鍵。";

        if (summary.RuleCount == 0)
            return $"讀到 {summary.Total} 個事實鍵，但一條對帳規則都沒有——"
                 + "這一項無法申報，不代表沒有未覆蓋的鍵。";

        if (summary.Uncovered == 0)
            return $"所有 {summary.Total} 個事實鍵都已被 {summary.RuleCount} 條規則考慮或明文豁免。";

        return $"尚未被任何規則碰到的 {summary.Uncovered} 個鍵（前 12 個）："
             + string.Join("、", summary.UncoveredKeys.Take(12))
             + (summary.Uncovered > 12 ? "…" : "")
             + "。未覆蓋不是錯誤——它只代表那個數字目前沒有第二個來源可以交叉；"
             + "要嘛補一條對帳規則，要嘛加進 FactCoverageReport.Exemptions 並寫明理由。";
    }

    /// <summary>
    /// 執行期真正在跑的規則（<see cref="FactRelationRules.All"/>）。
    /// <b>刻意不讀 <c>Rules/builtin.json</c></b>：那個檔沒有隨程式出貨，
    /// 而生產路徑用的是內建規則；讀一個不存在的檔會讓申報顯示「0 條規則」，
    /// 進而把「所有鍵都已覆蓋」這種假結論端出來。
    /// </summary>
    internal static List<(string Id, IReadOnlyList<string> Inputs)> RuntimeRules()
        => FactRelationRules.All.Select(r => (r.Id, r.InputKeys)).ToList();

    /// <summary>
    /// 從原始碼掃事實鍵。<b>只給測試用</b>——用來驗證 <see cref="FactKeyCatalog"/> 與原始碼一致。
    /// 執行期不使用它：發佈版沒有 <c>Services/</c> 目錄，掃不到會回空集合。
    /// </summary>
    internal static HashSet<string> ScanFactKeysFromSource(string servicesDirectory)
    {
        var patterns = new[]
        {
            new Regex(@"new HardwareFact\(\s*\$?""([a-z][a-z0-9_.\[\]-]*)"""),
            new Regex(@"new\(\s*\$?""([a-z][a-z0-9_.\[\]-]*)""\s*,\s*(?:Category|\$?_?cat)"),
            new Regex(@"(?:const|static readonly)\s+string\s+\w*[Kk]ey\w*\s*=\s*""([a-z][a-z0-9_.\[\]-]*)"""),
            new Regex(@"FactKey\s*=\s*""([a-z][a-z0-9_.\[\]-]*)"""),
            // 服務層的不可得 helper 常把鍵以字面值當參數傳入（第一個參數，或第二個——at 在前）。
            // 這兩個形狀原本掃不到，於是「已生產但目錄沒登記」的鍵會靜默漏掉
            // （實測：net.offload 與 amd.sev_es／sev_snp／mem_enc 三個）。
            // 只認「至少含一個點」的字面值，避免把一般字串也當成事實鍵。
            new Regex(@"Unavailable\(\s*(?:at\s*,\s*)?""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
            new Regex(@"Unavailable\([^,)]*,\s*""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)""\s*,"),
            // 2026-10-10 執行期對帳（FactKeyRuntimeReconcileTests）抓出的下一批生產形狀：
            // 鍵以字面值傳給 helper 再建事实——SupportedBit（AMD 能力位）、BuildFact（MSR 事实）、
            // CollectSigList（簽章資料庫）、MSR 元組表（0x60D, "cpu.pkg_c2_us", …）。
            // 掃描器追寫法有上限；真正的收口是執行期對帳，這裡補樣式讓「目錄≡掃描」繼續成立。
            // 只認「含點的全小寫字面值」，且限呼叫位址——不抓 `string key = $"…{動態}"` 那類前綴。
            new Regex(@"SupportedBit\(\s*""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
            new Regex(@"BuildFact\([^""]*?""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
            new Regex(@"CollectSigList\(\s*\w+\s*,\s*""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
            new Regex(@"\(\s*0x[0-9A-Fa-f]{2,}\s*,\s*""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
            new Regex(@"Bool\(\s*""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
            new Regex(@"ServiceFact\(\s*""([a-z][a-z0-9_]*(\.[a-z0-9_]+)+)"""),
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(servicesDirectory)) return keys;
        foreach (string file in Directory.EnumerateFiles(servicesDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                    keys.Add(m.Groups[1].Value.TrimEnd('.'));
        }
        return keys;
    }
}

/// <summary>
/// 對帳規則的來源申報：<b>內建規則與外部 JSON 的關係必須說清楚</b>。
/// </summary>
/// <remarks>
/// <para>
/// 現況：對帳規則有兩份實作——內建的 <see cref="FactRelationRules.All"/>（26 條，生產路徑用這一份）
/// 與 <c>Rules/builtin.json</c>（同樣 26 條，可分享的外部形式）。
/// <c>RuleEngineTests</c> 保證兩者的 Id 集合一致、行為等價。
/// </para>
/// <para>
/// <b>但外部 JSON 沒有隨程式出貨</b>（csproj 未收錄 <c>Rules/</c>），
/// 而說明文件寫著「規則可分享：Rules/builtin.json 為可編輯的外部形式」——
/// 使用者去找會找不到，找到了編輯也不會生效。
/// </para>
/// <para>
/// <b>本類別如實申報這件事</b>，而不是讓說明文字繼續承諾一個做不到的事。
/// 兩條出路（讓它出貨、或把措辭降級）是產品決定，不由本類別代選。
/// </para>
/// </remarks>
public static class RuleSourceReport
{
    /// <summary>生產路徑實際使用的規則數。</summary>
    public static int RuntimeRuleCount => FactRelationRules.All.Count;

    /// <summary>外部規則檔的預期路徑。</summary>
    public static string ExternalRulePath => Path.Combine(AppContext.BaseDirectory, "Rules", "builtin.json");

    /// <summary>外部規則檔是否隨程式出貨（本機檢查；發佈版為 false）。</summary>
    public static bool ExternalRuleFilePresent => File.Exists(ExternalRulePath);

    /// <summary>一行申報：哪一份在跑、外部形式在不在。</summary>
    public static string Describe() =>
        $"對帳規則：內建 {RuntimeRuleCount} 條（生產路徑實際使用的是這一份）。"
        + (ExternalRuleFilePresent
           ? $"外部形式（可編輯、可分享）位於 {ExternalRulePath}；與內建規則的行為等價由測試保證。"
           : "外部形式（Rules/builtin.json）沒有隨程式出貨——發佈版裡沒有這個檔。"
             + "內建規則仍完整運作；但說明文件寫的「可編輯的外部形式」目前只在原始碼倉庫裡成立，"
             + "不是發佈版的功能。");
}
