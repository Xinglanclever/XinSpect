using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 商業授權與「專案自身授權敘述」的守門。
/// </summary>
/// <remarks>
/// <para>
/// 這一組擋的是兩件不同的事：
/// </para>
/// <list type="number">
/// <item><b>商業授權那條路要一直在。</b>AGPL 對某些組織是硬牆——採購規則直接禁用 copyleft。
///       沒有另一條路，那些人只能放棄或偷偷用；兩種結果都比付費授權糟。
///       所以檔案存在、三份 README 都指得到、且「不給什麼」寫清楚了（尤其是不含第三方義務）。</item>
/// <item><b>專案自身的授權敘述只有一套。</b>曾經出現過「本專案以前是 X」這種句子；
///       那類句子會讓讀者以為授權有歷史包袱，而正確的敘述只有一句：現在是什麼。
///       第三方元件的 MIT／MPL／LGPL 是<b>別人的</b>授權，必須保留——這條不碰它們。</item>
/// </list>
/// </remarks>
public class CommercialLicenseTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    // ── 商業授權文件 ────────────────────────────────────────────────────

    [Fact]
    public void 商業授權文件存在且把界線寫清楚()
    {
        string doc = Read("COMMERCIAL-LICENSE.md");

        // 它是什麼、給什麼
        Assert.Contains("商業授權", doc);
        Assert.Contains("不觸發 AGPL 的原始碼公開義務", doc);

        // 它「不給」什麼——這一段比「給什麼」重要：買方最容易誤解的就是這裡
        Assert.Contains("不轉移著作權", doc);
        Assert.Contains("不涵蓋第三方元件", doc);
        Assert.Contains("不給商標", doc);
        Assert.Contains("不提供任何擔保", doc);

        // 不得自稱法律意見
        Assert.Contains("不是法律意見", doc);
        // 不得自稱要約（否則會變成對任何人都有效的報價）
        Assert.Contains("不構成要約", doc);
    }

    [Fact]
    public void 商業授權要說清楚AGPL不可撤回這件事()
    {
        // 這是實務上最常被誤解的一點：買了商業授權，不會讓先前依 AGPL 散布的內容回收。
        // 寫清楚比事後爭論便宜。
        string doc = Read("COMMERCIAL-LICENSE.md");
        Assert.Contains("不可撤回", doc);
        Assert.Contains("生效日", doc);
    }

    [Fact]
    public void 商業授權要點名第三方授權仍須自行合規()
    {
        // 賣方不能替買方把 MPL-2.0／LGPL 的義務買掉——那在授權上做不到。
        // 這一段若被刪，買方會以為付錢就一切免責。
        string doc = Read("COMMERCIAL-LICENSE.md");
        Assert.Contains("MPL-2.0", doc);
        Assert.Contains("LGPL", doc);
        Assert.Contains("THIRD-PARTY-NOTICES.md", doc);
    }

    [Fact]
    public void 三份README都指得到商業授權()
    {
        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
        {
            Assert.Contains("COMMERCIAL-LICENSE.md", Read(file));
        }
    }

    [Fact]
    public void 三份README的授權徽章都在而且都指得出商業授權()
    {
        Assert.Contains("license-AGPL--3.0--or--later", Read("README.md"));
        Assert.Contains("license-AGPL--3.0--or--later", Read("README.zh-CN.md"));
        Assert.Contains("license-AGPL--3.0--or--later", Read("README.en.md"));

        // 商業授權那顆徽章：三份都要有，顏色與 AGPL 那顆不同（8B5CF6）。
        foreach (string file in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
            Assert.Contains("8B5CF6", Read(file));
    }

    // ── 專案自身授權敘述只有一套 ────────────────────────────────────────

    /// <summary>
    /// 「本專案以前是別的授權」這類句子不得出現在<b>專案自己的敘述</b>裡。
    /// </summary>
    /// <remarks>
    /// 這個守門是刻意寫成「掃句子」而不是「掃字串 MIT」：第三方元件的 MIT 條款
    /// 是別人的授權義務，必須保留；被禁的是把授權沿革講成本專案自身歷史的那幾種寫法。
    /// </remarks>
    [Fact]
    public void 專案自身的授權敘述不得提過去的授權()
    {
        string[] forbidden =
        [
            "授權改採", "授權改採", "改採 AGPL", "改采 AGPL", "改為 AGPL",
            "MIT 條款", "MIT 条款", "MIT terms",
            "MIT 痕跡", "MIT 痕迹", "MIT 授權", "MIT 授权",
        ];

        var hits = new List<string>();
        foreach (string rel in TrackedDocs())
        {
            string text = Read(rel);
            foreach (string bad in forbidden)
                if (text.Contains(bad, StringComparison.Ordinal))
                    hits.Add($"{rel}：「{bad}」");
        }

        Assert.True(hits.Count == 0,
            "專案自身的授權敘述仍提到過去的授權（只寫現在是什麼就夠了）：\n" + string.Join("\n", hits.Distinct()));
    }

    /// <summary>會被當成「對外授權敘述」的檔案。第三方清單不在此列——它記的是別人的條款。</summary>
    private static IEnumerable<string> TrackedDocs()
    {
        yield return "README.md";
        yield return "README.zh-CN.md";
        yield return "README.en.md";
        yield return "手冊.md";
        yield return "CONTRIBUTING.md";
        yield return "COMMERCIAL-LICENSE.md";
        yield return Path.Combine("Nav", "ChangelogCatalog.cs");
        yield return Path.Combine("Nav", "HelpCatalog.cs");
    }

    [Fact]
    public void 程式內不得再宣稱禁止商業使用()
    {
        // 「嚴禁用於任何商業用途」是 AGPL 之前的說法。AGPL-3.0-or-later 明文允許商業使用，
        // 而 COMMERCIAL-LICENSE.md 賣的正是「不想公開原始碼的閉源整合」——兩句話同時存在，
        // 等於告訴使用者「可以買授權，但你不准用」。這條把它擋在介面與文件之外。
        string about = Read(Path.Combine("Views", "AboutView.xaml"));
        Assert.DoesNotContain("嚴禁用於任何商業用途", about);
        Assert.Contains("AGPL-3.0-or-later", about);
        Assert.Contains("COMMERCIAL-LICENSE.md", about);

        string help = Read(Path.Combine("Nav", "HelpCatalog.cs"));
        Assert.DoesNotContain("嚴禁", help);
        Assert.Contains("COMMERCIAL-LICENSE.md", help);

        // README 的說明目錄是 HelpCatalog 的鏡像：只檢查那一行。沿革條目會引用舊句子
        // （「原本寫……」，那是歷史紀錄，不是現在的宣稱），不能拿整份 README 去掃。
        string readme = Read("README.md");
        string? line = readme.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("- 功能／怎麼讀：本軟體", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.DoesNotContain("嚴禁", line!);
        Assert.Contains("COMMERCIAL-LICENSE.md", line!);
    }

    [Fact]
    public void 免責聲明的英文與中文說同一件事()
    {
        string en = Read(Path.Combine("Services", "EnglishStrings.cs"));
        Assert.DoesNotContain("strictly non-commercial", en);
        Assert.Contains("including commercially", en);
        Assert.Contains("AGPL-3.0-or-later", en);
        Assert.Contains("COMMERCIAL-LICENSE.md", en);

        // 字串表以「中文原文」為鍵：中文改了字而鍵沒跟著改，英文模式會悄悄退回中文，
        // 不會紅、也不會有人發現。所以關於頁那句話必須原樣出現在鍵裡。
        string about = Read(Path.Combine("Views", "AboutView.xaml"));
        var m = System.Text.RegularExpressions.Regex.Match(about, "Text=\"(本軟體以[^\"]*)\"");
        Assert.True(m.Success, "關於頁找不到免責聲明那句（本軟體以…）");
        Assert.Contains("[\"" + m.Groups[1].Value + "\"]", en);
    }

    [Fact]
    public void 第三方清單仍保留別人的授權條款()
    {
        // 反向守門：清掉「本專案曾是 MIT」的痕跡時，**不可以**連帶刪掉別人的 MIT 段落。
        // 那些是授權義務，刪掉就變成侵權。
        string notices = Read("THIRD-PARTY-NOTICES.md");
        Assert.Contains("MIT", notices);
        Assert.Contains("MPL-2.0", notices);
        Assert.Contains("LGPL", notices);

        // 而且第三方表格的列數不能因為這次清理而變少。
        int rows = notices.Split('\n').Count(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| 元件", StringComparison.Ordinal) && !l.StartsWith("|---", StringComparison.Ordinal));
        Assert.True(rows >= 9, $"第三方元件表被刪到只剩 {rows} 列——只許增不許減");
    }
}
