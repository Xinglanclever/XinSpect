using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 「我們到底有哪些知識表」這個問題在專案裡有兩份答案：<see cref="BuiltinKnowledgeInventory"/>
/// （第 283 輪的內部盤點，逐表報條數／擔保／快照日）與 <see cref="KnowledgeCoverage"/>
/// （第 286 輪之前的收錄率申報，給使用者看的）。
///
/// <para>
/// <b>第 286 輪之前兩份答案不一樣：</b>收錄率那邊列 8 列、漏掉 SMART 屬性、
/// BugCheck 停止代碼、BugCheck 符號名三張——而這三張的值都會出現在畫面上。
/// 更糟的是它把 Super I/O 報成「5 個家族」（實作分類），而問題本來是
/// 「查得到幾個名字」（55 顆晶片）。
/// </para>
///
/// <para>
/// <b>這一支做雙向對帳：</b>清冊的每一張表都要在收錄率申報裡有對應的列，
/// 申報的每一列也都要被某張表認領；條數必須相等（單位不同時可一對多，但要加得起來）。
/// 兩邊任何一邊補表而另一邊沒補，紅燈會指名。
/// </para>
/// </summary>
public class KnowledgeCoverageInventoryTests
{
    /// <summary>
    /// 清冊的鍵末段 → 收錄率申報裡對應的那幾列。
    /// 一對多只發生在「清冊合併、申報分開」的兩處（PCI 類別碼、PCI 登錄檔），
    /// 以及單位一致的其餘各表。
    /// </summary>
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        ["pci-class"] = ["PCI 基底類別碼", "PCI 子類別碼"],
        ["pci-vendor-curated"] = ["PCI 廠商 ID"],
        ["pci-registry"] = ["PCI 廠商 ID（登錄檔快照）", "PCI 裝置 ID（登錄檔快照）"],
        ["superio"] = ["Super I/O 晶片名稱"],
        ["oui"] = ["MAC OUI 廠商"],
        ["cta-vic"] = ["CTA VIC 時序"],
        ["smart"] = ["SMART 屬性（六家）"],
        ["bugcheck-code"] = ["BugCheck 停止代碼"],
        ["bugcheck-symbol"] = ["BugCheck 符號名"],
    };

    private static IReadOnlyList<BuiltinKnowledgeInventory.Entry> Inventory =>
        BuiltinKnowledgeInventory.Entries;

    private static KnowledgeCoverage.Table Row(string name)
    {
        var row = KnowledgeCoverage.Tables.SingleOrDefault(t => t.Name == name);
        Assert.True(row.Name is not null, $"收錄率申報裡沒有「{name}」這一列");
        return row;
    }

    [Fact]
    public void 清冊裡的每一張表都要在收錄率申報裡有對應的列()
    {
        var problems = new List<string>();
        foreach (var entry in Inventory)
        {
            if (!Map.TryGetValue(entry.Slug, out var rows))
            {
                problems.Add($"清冊有「{entry.Name}」({entry.Slug})，但測試的對照表沒有說它對應到哪一列");
                continue;
            }
            foreach (string row in rows)
            {
                if (!KnowledgeCoverage.Tables.Any(t => t.Name == row))
                    problems.Add($"「{entry.Name}」對應的列「{row}」不存在於收錄率申報裡");
            }
        }

        Assert.True(problems.Count == 0,
            "內建知識表清冊與收錄率申報不一致（使用者看到的申報會少掉整張表）：\n"
            + string.Join("\n", problems));
    }

    [Fact]
    public void 收錄率申報裡沒有清冊不認得的列()
    {
        // 反方向：申報多一列而清冊沒有，代表使用者看到一張「盤點裡不存在」的表。
        var claimed = Map.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
        var orphans = KnowledgeCoverage.Tables.Select(t => t.Name)
            .Where(n => !claimed.Contains(n)).ToList();

        Assert.True(orphans.Count == 0,
            "以下列在收錄率申報裡出現，但沒有任何一張內建知識表認領："
            + string.Join("、", orphans));
    }

    [Fact]
    public void 對應的條數要相等()
    {
        var problems = new List<string>();
        foreach (var entry in Inventory)
        {
            if (!Map.TryGetValue(entry.Slug, out var rows)) continue;
            int declared = rows.Sum(r => KnowledgeCoverage.Tables.Single(t => t.Name == r).Entries);
            if (declared != entry.Entries)
                problems.Add($"{entry.Name}：清冊 {entry.Entries} 條，收錄率申報合計 {declared} 條");
        }

        Assert.True(problems.Count == 0,
            "同一張表在兩處的條數不同（其中一處一定過期了）：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void 對照表涵蓋清冊的每一張表_沒有漏寫()
    {
        // 清冊新增一張表而這裡沒補，前一條會過（因為 continue），所以再單獨釘一次。
        var missing = Inventory.Select(e => e.Slug)
            .Where(s => !Map.ContainsKey(s)).ToList();

        Assert.True(missing.Count == 0,
            $"清冊新增了沒有對應收錄率列的知識表：{string.Join("、", missing)}"
            + "——請在收錄率申報裡補列，並更新這一支的對照表");
    }

    [Fact]
    public void Super_IO報的是查得到幾個名字_不是幾個家族()
    {
        // 先前報「5 個家族」——那是實作分類。這一節要回答的是「查得到幾個名字」。
        Assert.DoesNotContain(KnowledgeCoverage.Tables, t => t.Name == "Super I/O 晶片家族");
        Assert.Equal(SuperIoKnowledge.Count, Row("Super I/O 晶片名稱").Entries);
        Assert.Contains("四家族", Row("Super I/O 晶片名稱").Scope);
    }

    [Fact]
    public void 摘要的張數與總條數都是算出來的()
    {
        string s = KnowledgeCoverage.Summary();
        int total = KnowledgeCoverage.Tables.Sum(t => t.Entries);

        Assert.Contains($"{total} 條", s);
        Assert.Contains($"{KnowledgeCoverage.Tables.Count} 張表", s);
        // 張數寫死過一次（「目前六張」對上 8 列）——這裡順便釘住「寫的是實際張數」。
        Assert.Contains($"{total} 條（{KnowledgeCoverage.Tables.Count} 張表）", s);
    }

    [Fact]
    public void 摘要不得宣稱每一張都是手工子集()
    {
        // pci.ids 快照、SMART、Super I/O 都是上游資料；一句「都是手工子集」會替社群資料背書。
        string s = KnowledgeCoverage.Summary();
        Assert.DoesNotContain("這些都是手工子集", s);
        Assert.Contains("擔保等級不同", s);
        Assert.Contains("錯誤率不是零", s);
    }
}
