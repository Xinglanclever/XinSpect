using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-056（金標／往返守門）、P1-058（完整性網守門）、
/// P1-059（性質測試守門）、P1-060（正對照實驗程序）的守門。
///
/// <para>
/// <b>這一組與既有守門的關係是「補上缺的宣告與正對照」，不是重寫。</b>
/// 接線守門、目錄守門、掃描守門都已經存在且有牙；它們缺的是
/// ①沒有集中的清冊可以回答「整體守得住什麼」②部分掃描類守門沒有正對照。
/// </para>
/// </summary>
public class GuardRegistryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── P1-055 清冊完整性 ──────────────────────────────────────────────

    [Fact]
    public void 清冊_每一條守門的宣告都要完備()
    {
        var problems = GuardRegistry.ValidateAll();
        Assert.True(problems.Count == 0,
            "守門清冊有宣告不完整的條目：\n" + string.Join("\n", problems.Select(p => "  • " + p)));
    }

    [Fact]
    public void 清冊_不得有重複名稱()
    {
        var names = GuardRegistry.Guards.Select(g => g.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 清冊_要有內容而且涵蓋多種模式()
    {
        Assert.True(GuardRegistry.Guards.Count >= 5, "清冊太少——是不是沒登記？");
        var byMode = GuardRegistry.CountByMode();
        Assert.True(byMode.Count >= 3, "守門模式少於三種——防線太單薄");
    }

    // ── P1-060 正對照實驗程序 ──────────────────────────────────────────

    [Fact]
    public void 正對照_沒有正對照的掃描類守門必須登記理由()
    {
        var missing = GuardPolicy.UnexcusedGuards(GuardRegistry.Guards);

        Assert.True(missing.Count == 0,
            "以下掃描類／政策類守門沒有正對照，也沒有登記豁免理由——"
            + "沒有『該紅會紅』的證明，它可能因為樣式失效而永遠是綠的：\n"
            + string.Join("\n", missing.Select(m => "  • " + m)));
    }

    [Fact]
    public void 正對照_豁免理由必須跟著守門本體而不是散在測試裡()
    {
        // 理由放在測試檔裡，改守門的人不會看到它——所以理由必須是守門宣告的一部分。
        var excused = GuardRegistry.Guards
            .Where(g => !g.HasPositiveControl && g.PositiveControlExemption is not null)
            .ToList();

        Assert.NotEmpty(excused);
        foreach (var g in excused)
            Assert.True(g.PositiveControlExemption!.Trim().Length >= 30,
                $"{g.Name}：免除理由太短");
    }

    [Fact]
    public void 正對照_已附正對照的守門要真的找得到對應的測試()
    {
        // 宣告「有正對照」不能只是宣告——對照的測試名稱要能在 Tests/ 裡找到。
        string tests = Path.Combine(RepoRoot(), "Tests");
        var files = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToList();

        foreach (var guard in GuardRegistry.Guards.Where(g => g.HasPositiveControl))
        {
            bool found = files.Any(t => t.Contains("正對照", StringComparison.Ordinal));
            Assert.True(found, "整個 Tests/ 找不到任何『正對照』——宣告為有正對照的守門就沒有依據");
            break;   // 只要整體存在即可；逐條對應由下面的具名檢查負責
        }
    }

    [Fact]
    public void 正對照_具名守門的對照測試必須存在()
    {
        string tests = Path.Combine(RepoRoot(), "Tests");
        var texts = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);

        // 這三條是清冊裡宣告「有正對照」的掃描類守門，它們的對照測試名要對得上。
        Assert.Contains(texts.Keys, f => texts[f].Contains("正對照_寫入API的假掃描器必須紅燈"));
        Assert.Contains(texts.Keys, f => texts[f].Contains("正對照_含出網呼叫的假來源必須被抓到"));
        Assert.Contains(texts.Keys, f => texts[f].Contains("禁項掃描_正對照_已知會外洩的清單必須紅"));
    }

    // ── P1-058 完整性網 ────────────────────────────────────────────────

    [Fact]
    public void 完整性網_三層對帳都要有對應的守門存在()
    {
        // 接線（有沒有被呼叫）、孤兒（有沒有沒被呼叫的）、對帳（兩邊說法一致）
        Assert.Contains(GuardRegistry.Guards, g => g.Name.Contains("接線"));
        Assert.Contains(GuardRegistry.Guards, g => g.Mode == GuardPolicy.GuardMode.Reconciliation);
        Assert.Contains(GuardRegistry.Guards, g => g.Name.Contains("目錄"));
    }

    // ── P1-059 性質測試 ────────────────────────────────────────────────

    [Fact]
    public void 性質_格的傳播對任意輸入都單調()
    {
        // 不變式：加進更多來源只會讓結果變弱，不會變強。
        var states = FactStateLattice.LatticeOrder;
        foreach (var a in states)
        foreach (var b in states)
        {
            var combined = FactStateLattice.Combine([a, b]);
            Assert.True(FactStateLattice.Rank(combined) >= FactStateLattice.Rank(a));
            Assert.True(FactStateLattice.Rank(combined) >= FactStateLattice.Rank(b));
        }
    }

    [Fact]
    public void 性質_格的傳播對任意輸入都可交換且具等冪()
    {
        var states = FactStateLattice.LatticeOrder;
        foreach (var a in states)
        foreach (var b in states)
        {
            Assert.Equal(FactStateLattice.Combine([a, b]), FactStateLattice.Combine([b, a]));
            Assert.Equal(a, FactStateLattice.Combine([a, a]));
        }
    }

    [Fact]
    public void 性質_敏感度分級對任意事實都恰好落在一級()
    {
        foreach (string key in new[] { "cpu.cores", "system.name", "secret.token", "tpm.ek" })
        foreach (bool sensitive in new[] { true, false })
        {
            var fact = new HardwareFact(key, "測試", key, "值", "", "來源", FactTrustLevel.Reported,
                sensitive, DateTimeOffset.UnixEpoch, null, FactAvailability.Present, null);
            var level = SensitivityPolicy.Classify(fact);
            Assert.True(Enum.IsDefined(level));
        }
    }

    // ── P1-056 金標／往返 ──────────────────────────────────────────────

    [Fact]
    public void 金標_既有的解碼金標測試仍存在()
    {
        // 這一項不新增金標，只確認既有金標機制還在——它的守門是 SpecRefCoverageTests
        // 與各解碼器的 *Tests。若哪天被刪掉，這裡會紅。
        string tests = Path.Combine(RepoRoot(), "Tests");
        int goldenish = Directory.EnumerateFiles(tests, "*Tests.cs", SearchOption.AllDirectories)
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        Assert.True(goldenish >= 50, $"解碼器測試檔只剩 {goldenish} 個——金標機制是不是被移除了？");
    }

    [Fact]
    public void 金標_往返性質_事實鍵目錄排序等同於自身排序()
    {
        var keys = FactKeyCatalog.Keys;
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
    }
}
