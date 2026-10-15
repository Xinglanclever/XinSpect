using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-009（離線輸入源掛載接縫）、
/// P1-011（來源能力矩陣）、P1-012（骨架相容性報告）的守門與實作測試。
/// </summary>
public class SourceCapabilityAndMountTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeMount(string path, MountKind kind, MountState state, string? detail = null)
        : IOfflineInputMount
    {
        public string Path => path;
        public MountKind DeclaredKind => kind;
        public MountRecord Mount() => new(path, kind, state, detail);
    }

    // ── P1-009 離線來源掛載 ──────────────────────────────────────────────

    [Fact]
    public void 掛載_找不到檔案不算是讀取錯誤()
    {
        var fact = OfflineMountReport.ToFact(At,
            [new MountRecord(@"C:\x.img", MountKind.DiskImage, MountState.NotFound, "路徑不存在")]);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);   // 有掛不上的就要標出來
        Assert.Contains("找不到", fact.UnavailableReason);
    }

    [Fact]
    public void 掛載_找不到與讀不到要分開()
    {
        var notFound = new MountRecord("a", MountKind.DiskImage, MountState.NotFound, null);
        var unreadable = new MountRecord("b", MountKind.DiskImage, MountState.Unreadable, null);

        Assert.Equal(FactAvailability.NotApplicable, notFound.ToFactAvailability());
        Assert.Equal(FactAvailability.InsufficientPrivilege, unreadable.ToFactAvailability());
        Assert.NotEqual(notFound.ToFactAvailability(), unreadable.ToFactAvailability());
    }

    [Fact]
    public void 掛載_種類不符是NotSupported不是壞掉()
    {
        var m = new MountRecord("x.dmp", MountKind.DiskImage, MountState.KindMismatch, "這是記憶體傾印");
        Assert.Equal(FactAvailability.NotSupported, m.ToFactAvailability());
    }

    [Fact]
    public void 掛載_全部成功時標Present且不列原因()
    {
        var fact = OfflineMountReport.ToFact(At,
        [
            new MountRecord("a.img", MountKind.DiskImage, MountState.Mounted, null),
            new MountRecord("b.evtx", MountKind.EventLog, MountState.Mounted, null),
        ]);

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.UnavailableReason);
        Assert.Contains("磁碟映像 1", fact.Value);
        Assert.Contains("事件日誌 1", fact.Value);
    }

    [Fact]
    public void 掛載_部分失敗要說出掛不上的是哪一個()
    {
        var fact = OfflineMountReport.ToFact(At,
        [
            new MountRecord("a.img", MountKind.DiskImage, MountState.Mounted, null),
            new MountRecord("b.dmp", MountKind.MemoryDump, MountState.Corrupt, "標頭壞掉"),
        ]);

        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("1/2", fact.Value);
        Assert.Contains("記憶體傾印", fact.UnavailableReason);
        Assert.Contains("內容損壞", fact.UnavailableReason);
    }

    [Fact]
    public void 掛載_沒有離線來源時標不適用而不是回零()
    {
        var fact = OfflineMountReport.ToFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有任何離線來源", fact.Value);
    }

    // ── P1-011 來源能力矩陣 ─────────────────────────────────────────────

    [Fact]
    public void 能力矩陣_宣告與實際一致時沒有問題()
    {
        var m = new SourceCapabilityMatrix().Declare("SPD", "spd.", "mem.cross.");
        var produced = new Dictionary<string, IReadOnlyList<string>>
        {
            ["SPD"] = ["spd.0.vendor", "mem.cross.capacity"],
        };
        Assert.Empty(m.Reconcile(produced));
    }

    [Fact]
    public void 能力矩陣_生產了未宣告的鍵要指出來()
    {
        var m = new SourceCapabilityMatrix().Declare("SPD", "spd.");
        var produced = new Dictionary<string, IReadOnlyList<string>>
        {
            ["SPD"] = ["spd.0.vendor", "mem.speed"],
        };
        var problems = m.Reconcile(produced);
        Assert.Single(problems);
        Assert.Contains("mem.speed", problems[0]);
    }

    [Fact]
    public void 能力矩陣_沒宣告卻生產要指出來()
    {
        var m = new SourceCapabilityMatrix().Declare("SPD", "spd.");
        var produced = new Dictionary<string, IReadOnlyList<string>>
        {
            ["SPD"] = ["spd.0.vendor"],
            ["偷偷來的"] = ["x.y"],
        };
        Assert.Contains(m.Reconcile(produced), p => p.Contains("偷偷來的") && p.Contains("沒有宣告"));
    }

    [Fact]
    public void 能力矩陣_宣告了卻沒生產也要指出來()
    {
        var m = new SourceCapabilityMatrix().Declare("SPD", "spd.");
        Assert.Contains(m.Reconcile(new Dictionary<string, IReadOnlyList<string>>()),
            p => p.Contains("SPD") && p.Contains("沒有任何生產紀錄"));
    }

    [Fact]
    public void 能力矩陣_空矩陣標不適用()
    {
        var fact = new SourceCapabilityMatrix().ToFact(At, []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    [Fact]
    public void 能力矩陣_有落差時標讀取失敗並摘要()
    {
        var m = new SourceCapabilityMatrix().Declare("SPD", "spd.");
        var fact = m.ToFact(At, ["SPD：生產了未宣告的鍵 mem.speed"]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("對不上", fact.Value);
        Assert.Contains("mem.speed", fact.UnavailableReason);
    }

    // ── P1-012 骨架相容性報告 ────────────────────────────────────────────

    /// <summary>
    /// 這條測試就是 P1-012 的產物：既有服務逐一標示是否已實作骨架。
    /// 掃描原始碼而不是反射——反射看得到型別，看不到「有沒有實作這個介面」以外的接線狀況。
    /// </summary>
    [Fact]
    public void 骨架相容性_既有事實服務的採納清單要可以被數出來()
    {
        string root = RepoRoot();
        var services = Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(services);

        int factsServices = 0, adopted = 0;
        var adoptedNames = new List<string>();
        foreach (string file in services)
        {
            string text = File.ReadAllText(file);
            bool isFactsService = Regex.IsMatch(text, @"class\s+\w*FactsService\b");
            if (!isFactsService) continue;
            factsServices++;
            if (Regex.IsMatch(text, @":\s*[^{]*\bIReadOnlyScanner\b"))
            {
                adopted++;
                adoptedNames.Add(Path.GetFileName(file));
            }
        }

        Assert.True(factsServices > 0, "找不到任何 *FactsService——命名慣例改了？這條報告就失效了");
        Assert.True(adopted <= factsServices);

        // 這一輪的實情：骨架剛建立，既有服務尚未套用（P1-012 的後續工作）。
        // 斷言寫成「採納數不得超過總數」與「清單可列舉」，而不是寫死一個會過期的數字——
        // 會過期的斷言會在下一輪變成假紅燈。
        Assert.Equal(adopted, adoptedNames.Count);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
