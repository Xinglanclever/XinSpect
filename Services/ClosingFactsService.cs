using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>
/// 收尾事實組（Vol2 推進第 52–55 項）：IN-003 執行環境自我聲明、QS-002 來源信任分布、
/// DL-010 模組衝突、NG-001 診斷樹導航。
/// </summary>
/// <remarks>
/// <para><b>界線</b>：自我聲明只陳述「可讀到的事實」，不做反 VM／反沙箱；來源信任只列分布，
/// 不做分數也不背書（分數≠正確性）；模組衝突是「同名不同路徑」的觀察，不是惡意判決；
/// 導覽樹是導航，不是診斷背書。</para>
/// </remarks>
public static class ClosingFactsService
{
    private const string CatSelf = "自我完整性";
    private const string CatNav = "診斷導航";
    private const string CatDl = "開發者診斷";

    public const string EnvKey = "in.env";
    public const string TrustKey = "qs.trust";
    public const string ConflictCountKey = "dl.modconflict.count";
    public const string ConflictSummaryKey = "dl.modconflict.summary";
    public const string TreeKey = "ng.tree";

    /// <summary>機器環境（真實走 WMI Win32_ComputerSystem；測試注入）。</summary>
    public sealed record MachineEnvironment(string Manufacturer, string Model, bool? HypervisorPresent, string SystemType)
    {
        public bool IsVirtualMachine => HypervisorPresent == true;
    }

    public delegate MachineEnvironment? EnvProbe();
    public delegate IReadOnlyList<string> ModulesProbe();
    public delegate IReadOnlyList<(string Key, string Title, string Group)>? PagesProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        EnvProbe? env = null,
        ModulesProbe? modules = null,
        PagesProbe? pages = null)
    {
        var facts = new List<HardwareFact>(4)
        {
            EnvFact(at, (env ?? ReadEnv)()),
            TreeFact(at, (pages ?? ReadDiagnosticPages)()),
        };
        facts.AddRange(ConflictFacts(at, (modules ?? SelfIntegrityInputs.ProcessModulePaths)()));
        return facts;
    }

    /// <summary>QS-002 來源信任分布：吃前面各組事實（尾端呼叫，輸入不含自己）。</summary>
    public static HardwareFact TrustFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        int total = facts.Count;
        if (total == 0)
            return new HardwareFact(TrustKey, CatNav, "來源信任分布",
                "", "", "各事實的 Trust 標示", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "事實集是空的——沒有東西可以統計（不是「全部可信」）");

        var byTrust = facts.GroupBy(f => f.Trust).OrderBy(g => g.Key).ToList();
        int pending = facts.Count(f => f.Availability != FactAvailability.Present);
        string dist = string.Join("、", byTrust.Select(g => $"{g.Key} {g.Count()}"));
        return new HardwareFact(TrustKey, CatNav, "來源信任分布",
            $"{total} 項事實的信任標示：{dist}（其中 {pending} 項不是 Present）。" +
            "界線：這裡只列「來源自己宣稱的可信程度」的分布，<b>不產生分數、不背書</b>——分數看起來像結論，但可信度不是正確性。",
            "項", "各事實的 Trust 標示", FactTrustLevel.Derived, false, at, total);
    }

    // ── IN-003 執行環境自我聲明 ─────────────────────────────────────────

    private static HardwareFact EnvFact(DateTimeOffset at, MachineEnvironment? env)
    {
        if (env is null)
            return new HardwareFact(EnvKey, CatSelf, "執行環境自我聲明",
                "", "", "WMI Win32_ComputerSystem", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "機器環境讀不到——不推測是不是虛擬機");

        string hyper = env.HypervisorPresent switch
        {
            true => "Hypervisor 存在（執行在虛擬化環境；可能是 VM、也可能是啟用 Hyper-V 的宿主）",
            false => "Hypervisor 不存在（裸機或未揭露的虛擬化）",
            null => "Hypervisor 欄位未回報——不猜",
        };
        return new HardwareFact(EnvKey, CatSelf, "執行環境自我聲明",
            $"製造商 {env.Manufacturer}・型號 {env.Model}・系統類型 {env.SystemType}・{hyper}。" +
            "界線：這是自我聲明不是反 VM——本工具不隱藏、不逃避、不偵測使用者是不是在分析它。",
            "", "WMI Win32_ComputerSystem（Manufacturer／Model／HypervisorPresent／SystemType）",
            FactTrustLevel.Reported, false, at);
    }

    private static MachineEnvironment? ReadEnv()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model, HypervisorPresent, SystemType FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    bool? hv = o["HypervisorPresent"] is bool b ? b : null;
                    return new MachineEnvironment(
                        o["Manufacturer"] as string ?? "(未回報)",
                        o["Model"] as string ?? "(未回報)",
                        hv,
                        o["SystemType"] as string ?? "(未回報)");
                }
            }
            return null;
        }
        catch { return null; }
    }

    // ── DL-010 模組衝突偵測 ─────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> ConflictFacts(DateTimeOffset at, IReadOnlyList<string> modules)
    {
        if (modules.Count == 0)
            return
            [
                new HardwareFact(ConflictCountKey, CatDl, "同名模組衝突數",
                    "", "", "Process.Modules", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "模組清單為空——讀不到就是不猜"),
                new HardwareFact(ConflictSummaryKey, CatDl, "同名模組衝突",
                    "", "", "Process.Modules", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "模組清單為空——讀不到就是不猜"),
            ];

        var groups = modules.Where(m => !string.IsNullOrWhiteSpace(m))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string detail = groups.Count == 0
            ? "沒有同名不同路徑的模組（0 是值，不是缺席）"
            : string.Join("；", groups.Take(5).Select(g =>
                $"{g.Key}：{string.Join("、", g.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))}"))
              + (groups.Count > 5 ? $"；等 {groups.Count - 5} 組" : "");
        return
        [
            new HardwareFact(ConflictCountKey, CatDl, "同名模組衝突數",
                groups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "組",
                "Process.Modules（檔名分組）", FactTrustLevel.Measured, false, at, groups.Count),
            new HardwareFact(ConflictSummaryKey, CatDl, "同名模組衝突",
                detail + "。界線：同名不同路徑是觀察，不是惡意判決（版本目錄、AppContainer、外掛載入都會這樣）；" +
                "哪一個真的被載入、載入順序，要另外看，不在本項宣稱範圍。",
                "", "Process.Modules（檔名分組）", FactTrustLevel.Measured, false, at, groups.Count),
        ];
    }

    // ── NG-001 診斷樹導航 ───────────────────────────────────────────────

    private static HardwareFact TreeFact(DateTimeOffset at, IReadOnlyList<(string Key, string Title, string Group)>? pages)
    {
        if (pages is null || pages.Count == 0)
            return new HardwareFact(TreeKey, CatNav, "診斷樹導航",
                "", "", "PageRegistry（診斷相關頁）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "頁面清單讀不到——導覽樹不憑空生");

        var byGroup = pages.GroupBy(p => p.Group).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        string text = string.Join("；", byGroup.Select(g => $"{g.Key}：" + string.Join("、", g.Select(p => p.Title))));
        return new HardwareFact(TreeKey, CatNav, "診斷樹導航",
            $"{pages.Count} 個診斷入口：{text}。界線：樹是導航（告訴你哪裡可以看），不是診斷背書——" +
            "每個入口的事實與三態在各自的頁面與事實鍵裡。",
            "個", "PageRegistry 的診斷相關頁", FactTrustLevel.Reported, false, at, pages.Count);
    }

    /// <summary>診斷相關頁＝標題或關鍵字含診斷／排查／鑑識（其餘頁面不在診斷樹內）。</summary>
    internal static IReadOnlyList<(string Key, string Title, string Group)> ReadDiagnosticPages()
        => PageRegistry.Pages
            .Where(p => p.Keywords.Any(k => k.Contains("診斷", StringComparison.Ordinal)
                                        || k.Contains("排查", StringComparison.Ordinal)
                                        || k.Contains("鑑識", StringComparison.Ordinal))
                     || p.Title.Contains("診斷", StringComparison.Ordinal)
                     || p.Title.Contains("排查", StringComparison.Ordinal))
            .Select(p => (p.Key, p.Title, p.Group))
            .ToList();
}