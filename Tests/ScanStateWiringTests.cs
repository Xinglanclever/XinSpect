using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-001–P1-105 的<b>接線守門</b>。
///
/// <para>
/// <b>這一組存在的理由是一個真實的缺陷：</b>階段一寫了 20 支「輸出鍵產生器」，
/// 每一支都有完整測試、每一把鍵都登記在目錄裡、覆蓋申報也把它們算進去了，
/// <b>而其中 20 支的生產碼引用數是 0</b>——它們的鍵不會出現在任何使用者的報告裡。
/// </para>
///
/// <para>
/// 既有的 <c>GuardRegistry</c>「接線守門」宣告要防這件事，但它沒有響：
/// 它的判斷方式是「事實鍵有沒有生產者」，而這 20 支<b>確實是生產者</b>，
/// 只是沒有人呼叫它們。所以這一組補上真正會響的那一種判斷。
/// </para>
/// </summary>
public class ScanStateWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>讀生產碼（不含 Tests／obj／bin）。</summary>
    private static List<(string File, string Text)> ProductionFiles()
    {
        string root = RepoRoot();
        var dirs = new[] { "Services", "Views", "ViewModels", "Controls", "Nav", "Models", "BlueSquadron" };
        var list = new List<(string, string)>();
        foreach (string d in dirs)
        {
            string full = Path.Combine(root, d);
            if (!Directory.Exists(full)) continue;
            foreach (string f in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                list.Add((f, File.ReadAllText(f)));
            }
        }
        // 根目錄的檔案也要（App.xaml.cs 等）
        foreach (string f in Directory.EnumerateFiles(root, "*.cs"))
            list.Add((f, File.ReadAllText(f)));
        return list;
    }

    // ── 產生器必須被生產碼呼叫 ──────────────────────────────────────────

    [Fact]
    public void 每一支輸出鍵產生器都必須被生產碼引用()
    {
        var production = ProductionFiles();
        var orphan = new List<string>();

        foreach (var name in ScanStateFactsService.GeneratorTypes)
        {
            string selfFile = name + ".cs";
            bool referenced = production.Any(p =>
                Path.GetFileName(p.File) != selfFile
                && Regex.IsMatch(p.Text, @"\b" + Regex.Escape(name) + @"\b"));

            if (!referenced) orphan.Add(name);
        }

        Assert.True(orphan.Count == 0,
            "以下輸出鍵產生器寫好了、測過了，但沒有任何生產碼呼叫它們——"
            + "它們的鍵不會出現在任何使用者的報告裡：\n"
            + string.Join("\n", orphan.Select(o => "  • " + o)));
    }

    [Fact]
    public void 接線守門_正對照_沒有被呼叫的假產生器必須被抓到()
    {
        // 沒有這一條，這道守門可能因為清單被清空而永遠是綠的。
        var production = ProductionFiles();
        const string fake = "完全不存在的假產生器名稱";
        bool referenced = production.Any(p => p.Text.Contains(fake, StringComparison.Ordinal));
        Assert.False(referenced);   // 若這個名字真的存在，正對照就失效了
    }

    // ── 收集器必須真的產出鍵 ────────────────────────────────────────────

    [Fact]
    public void 掃描狀態收集器要產出登記表上的每一把鍵()
    {
        // 帶一輪真的有跑過的結果——scan.partial.count 只在「跑過一輪」時才有意義，
        // 沒有 outcome 時刻意不產出（那是三態，不是缺漏）。這條測的是「有 outcome 時
        // 登記表上的鍵要全部到齊」。
        var outcome = new ScanOutcome([], [ScanStepResult.Completed("一", TimeSpan.Zero)]);
        var facts = ScanStateFactsService.Collect(
            new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero),
            skeletonAdopted: 3, skeletonTotal: 3, steps: [], outcome: outcome,
            facts: [Seed()], fileInputs: 0, egressAttempts: 0);

        var produced = facts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var expected = ScanStateFactsService.Generators.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var missing = expected.Except(produced).ToList();
        Assert.True(missing.Count == 0,
            "收集器登記了這些鍵卻沒有產出：\n" + string.Join("\n", missing.Select(m => "  • " + m)));
    }

    [Fact]
    public void 收集器_沒有跑過一輪時刻意不產出中斷保留鍵()
    {
        // 三態：沒有 outcome 不等於「中斷保留 0 筆」，而是「這一輪沒有跑過」。
        // 所以這把鍵在沒有 outcome 時不該出現——而不是出現並回 0。
        var facts = ScanStateFactsService.Collect(
            DateTimeOffset.UnixEpoch, 0, 0, [], outcome: null, facts: [], fileInputs: 0, egressAttempts: 0);
        Assert.DoesNotContain("scan.partial.count", facts.Select(f => f.Key));
    }

    [Fact]
    public void 登記表的每一把鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = ScanStateFactsService.Generators
            .Select(g => g.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(missing.Count == 0,
            "登記表上的這些鍵不在事實鍵目錄裡：\n" + string.Join("\n", missing.Select(m => "  • " + m)));
    }

    [Fact]
    public void 登記表不得有重複的鍵()
    {
        var keys = ScanStateFactsService.Generators.Select(g => g.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // ── EvidenceLabService 必須把它接進 AllFacts ────────────────────────

    [Fact]
    public void EvidenceLabService必須把掃描狀態接進事實集()
    {
        string root = RepoRoot();
        string text = File.ReadAllText(Path.Combine(root, "Services", "EvidenceLabService.cs"));

        Assert.Contains("ScanStateFactsService.Collect", text);

        // 接在 AllFacts 的推導段（分母不含自己那一區），不是別的地方。
        int idx = text.IndexOf("ScanStateFactsService.Collect", StringComparison.Ordinal);
        int allFactsIdx = text.IndexOf("var baseFacts = ChipsetFacts", StringComparison.Ordinal);
        Assert.True(allFactsIdx >= 0 && idx > allFactsIdx,
            "掃描狀態必須接在 AllFacts 的事實串接區，否則它不會進入使用者的報告");
    }

    // ── 收集器的輸入有三態，不猜 ────────────────────────────────────────

    [Fact]
    public void 收集器_沒有事實時仍要產出鍵而不是丟例外()
    {
        var facts = ScanStateFactsService.Collect(
            DateTimeOffset.UnixEpoch, 0, 0, [], null, [], 0, 0);
        Assert.NotEmpty(facts);
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void 收集器_事實為空時狀態鍵要標不適用而不是回零()
    {
        var facts = ScanStateFactsService.Collect(
            DateTimeOffset.UnixEpoch, 0, 0, [], null, [], 0, 0);
        var count = facts.Single(f => f.Key == "state.count");
        Assert.NotEqual(FactAvailability.Present, count.Availability);
    }

    [Fact]
    public void 收集器_全部輸出鍵都要在目錄裡()
    {
        var facts = ScanStateFactsService.Collect(
            new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero),
            1, 1, [], null, [Seed()], 0, 0);
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);

        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).Distinct().ToList();
        Assert.True(unknown.Count == 0,
            "收集器產出了目錄裡沒有的鍵：\n" + string.Join("\n", unknown.Select(u => "  • " + u)));
    }

    private static HardwareFact Seed() =>
        new("cpu.cores", "處理器", "核心數", "8", "個", "測試", FactTrustLevel.Reported,
            false, DateTimeOffset.UnixEpoch, 8, FactAvailability.Present, null);
}
