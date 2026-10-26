using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-033 – P1-038 的知識包宣告輸出鍵守門。
/// </summary>
public class KnowledgePackReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static KnowledgePackLoad Builtin => new(null, "沒有外掛知識包（檔不存在）——內建表繼續服役", null);

    private static KnowledgePackLoad WithPack => new(
        KnowledgePackService.Build(3, "2026-10-01T00:00:00Z",
        [
            new KnowledgeTable("oui", "2026-09-15", new Dictionary<string, string> { ["001122"] = "Acme" }),
            new KnowledgeTable("pci", "2026-09-20", new Dictionary<string, string> { ["8086"] = "Intel" }),
        ]), null, "sha256:abc");

    // ── kp.version ──────────────────────────────────────────────────────

    [Fact]
    public void 版次_有包時報出版次與生成時間()
    {
        var fact = KnowledgePackReport.ToVersionFact(At, WithPack);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(3.0, fact.NumericValue);
        Assert.Contains("第 3 版", fact.Value);
    }

    [Fact]
    public void 版次_使用內建表時標不支援並說出理由()
    {
        var fact = KnowledgePackReport.ToVersionFact(At, Builtin);
        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("內建表", fact.Value);
    }

    // ── kp.digest ───────────────────────────────────────────────────────

    [Fact]
    public void 完整性_要給完整雜湊不得截斷()
    {
        // 用「真的推導出來的那個序列號」，不是自填的假值——
        // 這一條測的是「有沒有被截斷」，不是「有沒有照抄 DeclaredSerial」。
        var pack = WithPack.Pack!;
        var load = new KnowledgePackLoad(pack, null, pack.SerialNumber);
        var fact = KnowledgePackReport.ToDigestFact(At, load);

        // 說明被不變式併進了 Value（Present 的事實不得帶理由欄），所以比前綴。
        Assert.StartsWith(pack.SerialNumber, fact.Value);         // 完整雜湊，一字不截
        Assert.True(pack.SerialNumber.Length > 18, "序列號本身要夠長，才測得出有沒有被截斷");
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("不是簽章", fact.Value);                  // 界線說明，不是缺陷
    }

    [Fact]
    public void 完整性_被拒收的包仍要顯示它宣告過的雜湊()
    {
        // 拒收的包也宣告過一個雜湊，而那個值正是查證時要比對的東西——
        // 只說「拒收」而不給值，使用者就無法回答「我手上這份到底是不是同一份」。
        var load = new KnowledgePackLoad(null, "serialNumber 不符", "sha256:deadbeef");
        var fact = KnowledgePackReport.ToDigestFact(At, load);

        Assert.Equal("sha256:deadbeef", fact.Value);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
    }

    [Fact]
    public void 完整性_內建表時標不適用並說明由執行檔涵蓋()
    {
        var fact = KnowledgePackReport.ToDigestFact(At, Builtin);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("執行檔本身", fact.UnavailableReason);
    }

    // ── kp.snapshot_date ────────────────────────────────────────────────

    [Fact]
    public void 快照日期_逐表列出()
    {
        var fact = KnowledgePackReport.ToSnapshotFact(At, WithPack);
        Assert.Contains("oui 2026-09-15", fact.Value);
        Assert.Contains("pci 2026-09-20", fact.Value);
    }

    [Fact]
    public void 快照日期_使用內建表時要說出內建快照的時點()
    {
        var fact = KnowledgePackReport.ToSnapshotFact(At, Builtin);
        Assert.Equal(FactAvailability.NotSupported, fact.Availability);

        // 第 283 輪之前這裡斷言的是 "v2.66"——那句手寫字串從 v2.66 起就沒人更新，
        // 內建表長到九張、兩萬多條時它還寫著「OuiKnowledge／bugcheck 子集」。
        // 現在理由由清冊推導，所以測的是「推導出來的結果」而不是某一句話。
        Assert.Equal(BuiltinKnowledgeInventory.SnapshotStatement(), fact.UnavailableReason);
        Assert.Contains($"{BuiltinKnowledgeInventory.Entries.Count} 張知識表", fact.UnavailableReason);
        Assert.Contains(BuiltinKnowledgeInventory.OldestSnapshot()!.Value.ToString("yyyy-MM-dd"),
            fact.UnavailableReason);
    }

    // ── kp.update_channel ───────────────────────────────────────────────

    [Fact]
    public void 更新通道_沒有通道要是確定的陳述而非缺失()
    {
        var fact = KnowledgePackReport.ToChannelFact(At);
        Assert.Equal(FactAvailability.Present, fact.Availability);   // 設計如此，不是缺陷
        Assert.Null(fact.UnavailableReason);
        Assert.Contains("沒有更新通道", fact.Value);
        Assert.Contains("使用者手動換檔", fact.Value);
        Assert.Contains("不會自己去拿新資料", fact.Value);
    }

    // ── kp.swap.audit ───────────────────────────────────────────────────

    [Fact]
    public void 換包審計_沒換過包時標不適用而不是回零()
    {
        var fact = KnowledgePackReport.ToSwapAuditFact(At, swaps: 0, recorded: 0, lastReason: null);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("沒有換包動作", fact.Value);
    }

    [Fact]
    public void 換包審計_全部入鏈時標Present()
    {
        var fact = KnowledgePackReport.ToSwapAuditFact(At, swaps: 2, recorded: 2, lastReason: null);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("append-only", fact.Value);
    }

    [Fact]
    public void 換包審計_有換包沒記錄要標讀取失敗()
    {
        var fact = KnowledgePackReport.ToSwapAuditFact(At, swaps: 3, recorded: 1, lastReason: null);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("沒有紀錄的換包等於沒發生過", fact.UnavailableReason);
    }

    // ── kp.miss ─────────────────────────────────────────────────────────

    [Fact]
    public void 未收錄_沒有查詢時標不適用()
    {
        var fact = KnowledgePackReport.ToMissFact(At, lookups: 0, misses: 0, missedKeys: []);
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
    }

    [Fact]
    public void 未收錄_全部命中時標Present()
    {
        var fact = KnowledgePackReport.ToMissFact(At, lookups: 10, misses: 0, missedKeys: []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
    }

    [Fact]
    public void 未收錄_查不到時顯示未收錄不猜()
    {
        var fact = KnowledgePackReport.ToMissFact(At, lookups: 5, misses: 2,
            missedKeys: ["1a2b3c", "fffffe"]);

        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Contains("未收錄", fact.Value);
        Assert.Contains("1a2b3c", fact.Value);
        Assert.Contains("不猜名稱", fact.UnavailableReason);
        // 「未收錄」不是讀取失敗——它是一個確定的事實。
        Assert.NotEqual(FactAvailability.ReadError, fact.Availability);
    }
}
