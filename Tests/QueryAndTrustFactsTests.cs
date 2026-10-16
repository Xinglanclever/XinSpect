using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 查詢智慧層與信任報告批次（Vol2 推進第 57–66 項）的守門：全部注入假件——
/// 真實通路碰 HelpCatalog／PageRegistry／pipe 目錄／審計日誌／登錄，
/// 在單元測試裡讀真的會讓結果取決於跑測試的機器。
/// </summary>
public class QueryAndTrustFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static HardwareFact Fact(string key, string value, double? numeric = null, string unit = "",
        FactAvailability availability = FactAvailability.Present, string category = "測試域")
        => new(key, category, key, value, unit, "測試", FactTrustLevel.Measured, false, At, numeric, availability,
            availability == FactAvailability.Present ? null : "測試原因");

    private static IReadOnlyList<HardwareFact> Trust(
        TrustOpsFactsService.PipeProbe? pipes = null,
        TrustOpsFactsService.AuditProbe? audit = null,
        bool webauthn = true,
        string? build = "26100",
        bool consent = false,
        HardwareSnapshotDiff? diff = null)
        => TrustOpsFactsService.Collect(At, pipes ?? (() => []), audit ?? (() => null),
            _ => webauthn, _ => build, _ => consent, diff);

    // ── QS-004 單位轉換層 ───────────────────────────────────────────────

    [Fact]
    public void 單位_有數值未標單位_標Unknown並逐項列出()
    {
        var f = QueryInsightFactsService.UnitFact(At, [
            Fact("cpu.temp", "65", numeric: 65),
            Fact("mem.size", "32", numeric: 32, unit: "GiB"),
            Fact("odd.unit", "7", numeric: 7, unit: "knob"),
        ]);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：1 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(f.NumericValue);
        Assert.Contains("原本帶了數字 1", f.UnavailableReason);
        Assert.Contains("cpu.temp", f.Value);
        Assert.Contains("GiB×1", f.Value);
        Assert.Contains("可換算單位 1 種", f.Value);
        Assert.Contains("未定義換算 knob", f.Value);   // 換算層只做有定義的單位，未知的不猜
    }

    [Fact]
    public void 單位_全部有標_標Present()
    {
        var f = QueryInsightFactsService.UnitFact(At, [Fact("a.b", "1", numeric: 1, unit: "個")]);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("未標單位 0", f.Value);
    }

    [Fact]
    public void 單位_空事實集_標Unknown不是通過()
    {
        var f = QueryInsightFactsService.UnitFact(At, []);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不是「全部都有標」", f.UnavailableReason);
    }

    // ── QS-006 查詢索引 ─────────────────────────────────────────────────

    [Fact]
    public void 索引_按域前綴統計_最大域排前面()
    {
        var f = QueryInsightFactsService.IndexFact(At, [
            Fact("cpu.a", "1"), Fact("cpu.b", "2"), Fact("mem.c", "3"),
        ]);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("cpu×2", f.Value);
        Assert.Contains("不是新的資料來源", f.Value);
    }

    [Fact]
    public void 索引_空事實集_標Unknown()
    {
        var f = QueryInsightFactsService.IndexFact(At, []);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
    }

    // ── SE-014 安全摘要分級 ─────────────────────────────────────────────

    [Fact]
    public void 分級_紅黃灰分開計數_有矛盾即標Unknown待人工()
    {
        var f = QueryInsightFactsService.GradeFact(At, [
            Fact("a.b", "v"),
            Fact("reconcile.x", "矛盾：兩源不一致", category: "交叉對帳"),
            Fact("c.d", "", availability: FactAvailability.ReadError),
            Fact("e.f", "", availability: FactAvailability.NotSupported),
        ]);

        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：1 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(f.NumericValue);
        Assert.Contains("原本帶了數字 1", f.UnavailableReason);
        Assert.Contains("紅（對帳矛盾）1 條", f.Value);
        Assert.Contains("黃（不支援／缺權限／不適用）1 條", f.Value);
        Assert.Contains("灰（未知／讀取失敗）1 條", f.Value);
        Assert.Contains("不是安全評分", f.Value);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
    }

    [Fact]
    public void 分級_無矛盾_仍聲明不是背書()
    {
        var f = QueryInsightFactsService.GradeFact(At, [Fact("a.b", "v")]);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("不是背書", f.Value);
    }

    // ── QS-005 懸浮說明覆蓋 ─────────────────────────────────────────────

    [Fact]
    public void 說明覆蓋_缺說明的頁面要列出來()
    {
        var f = QueryInsightFactsService.HelpFact(At,
            helpKeys: () => new List<string> { "overview/系統總覽", "cpu/處理器" },
            pageKeys: () => new List<string> { "overview", "cpu", "gpu" });

        Assert.Equal(1, f.NumericValue);
        Assert.Contains("gpu", f.Value);
        Assert.Contains("不講文案寫得好不好", f.Value);
    }

    [Fact]
    public void 說明覆蓋_目錄讀不到_標ReadError()
    {
        var f = QueryInsightFactsService.HelpFact(At, helpKeys: () => throw new InvalidOperationException("壞了"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 說明覆蓋_真實目錄與頁面都非空()
    {
        var f = QueryInsightFactsService.HelpFact(At);

        Assert.True(f.NumericValue is null || f.NumericValue >= 0);
        Assert.Contains("說明條目", f.Value);
    }

    // ── QS-009 鍵集 diff ────────────────────────────────────────────────

    [Fact]
    public void 鍵集diff_新增移除共通都排序可重現()
    {
        var d = QueryInsightFactsService.DiffKeySets(new[] { "b", "a" }, new[] { "c", "b" });

        Assert.Equal(new[] { "c" }, d.Added);
        Assert.Equal(new[] { "a" }, d.Removed);
        Assert.Equal(new[] { "b" }, d.Common);
    }

    [Fact]
    public void 鍵集diff_空對空_不誤報差異()
    {
        var d = QueryInsightFactsService.DiffKeySets(Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(d.Added);
        Assert.Empty(d.Removed);
    }

    [Fact]
    public void 鍵集diff_沒有上一份_標NotApplicable說工具已就緒()
    {
        var f = QueryInsightFactsService.KeyDiffFact(At, null, new[] { "a" });

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("做過時間膠囊比較", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 鍵集diff_上一份是空集合_標Unknown不假裝比過()
    {
        var f = QueryInsightFactsService.KeyDiffFact(At, Array.Empty<string>(), new[] { "a" });
        Assert.Equal(FactAvailability.Unknown, f.Availability);
    }

    [Fact]
    public void 鍵集diff_兩份都給出_真的算出新增與消失()
    {
        var f = QueryInsightFactsService.KeyDiffFact(At,
            new[] { "cpu.a", "mem.b", "old.c" }, new[] { "cpu.a", "mem.b", "new.d" });

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("新增 1", f.Value);
        Assert.Contains("消失 1", f.Value);
        Assert.Contains("new.d", f.Value);
        Assert.Contains("old.c", f.Value);
        Assert.Contains("不是硬體本身變了", f.Value);
    }

    // ── SE-004 Named pipe ───────────────────────────────────────────────

    [Fact]
    public void pipe_列舉如實_清單標敏感且不開啟任何pipe()
    {
        var facts = Trust(pipes: () => new List<string> { "lsass", "eventlog" });

        Assert.Equal(2, F(facts, TrustOpsFactsService.PipeCountKey).NumericValue);
        var names = F(facts, TrustOpsFactsService.PipeSummaryKey);
        Assert.True(names.Sensitive, "pipe 名可能含使用者／工作階段識別（L1），必須交給遮蔽政策");
        Assert.Contains("只列名，不開啟", names.Source);
    }

    [Fact]
    public void pipe_零條_0是值不是缺席()
    {
        var facts = Trust(pipes: () => []);
        Assert.Contains("0 是值", F(facts, TrustOpsFactsService.PipeSummaryKey).Value);
    }

    [Fact]
    public void pipe_列舉失敗_ReadError不讀成沒有開放()
    {
        var facts = Trust(pipes: () => null);
        var f = F(facts, TrustOpsFactsService.PipeCountKey);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不讀成「沒有開放的 pipe」", f.UnavailableReason);
    }

    // ── RS-010 報告簽章 ─────────────────────────────────────────────────

    [Fact]
    public void 報告簽章_鏈有效_講明雜湊鏈不是數位簽章()
    {
        var facts = Trust(audit: () => new TrustOpsFactsService.AuditChainState(true, true, 7, "ABCDEF0123456789ZZZZ"));
        var f = F(facts, TrustOpsFactsService.ReportSigKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(7, f.NumericValue);
        Assert.Contains("鏈頭 ABCDEF0123456789…", f.Value);
        Assert.Contains("不是數位簽章", f.Value);
    }

    [Fact]
    public void 報告簽章_驗證失敗_標Unknown不是正常()
    {
        var facts = Trust(audit: () => new TrustOpsFactsService.AuditChainState(true, false, 3, "HASH"));
        var f = F(facts, TrustOpsFactsService.ReportSigKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("需人工檢視", f.UnavailableReason);
    }

    [Fact]
    public void 報告簽章_沒有日誌與讀不到分開()
    {
        var missing = F(Trust(audit: () => new TrustOpsFactsService.AuditChainState(false, false, 0, null)),
            TrustOpsFactsService.ReportSigKey);
        var unreadable = F(Trust(audit: () => null), TrustOpsFactsService.ReportSigKey);

        Assert.Equal(FactAvailability.NotApplicable, missing.Availability);
        Assert.Equal(FactAvailability.ReadError, unreadable.Availability);
    }

    // ── HM-007 硬體變更時間軸 ───────────────────────────────────────────

    private static HardwareSnapshotDiff Diff(bool sameMachine, int added, int removed, int changedCount, int unchanged) => new()
    {
        BeforeMachineId = sameMachine ? "M" : "A",
        AfterMachineId = sameMachine ? "M" : "B",
        BeforeCapturedAtUtc = At.AddMonths(-1),
        AfterCapturedAtUtc = At,
        Changes =
        [
            .. Enumerable.Range(0, added).Select(i => new HardwareFactChange { Key = $"new.{i}", Kind = SnapshotChangeKind.Added }),
            .. Enumerable.Range(0, removed).Select(i => new HardwareFactChange { Key = $"gone.{i}", Kind = SnapshotChangeKind.Removed }),
            .. Enumerable.Range(0, changedCount).Select(i => new HardwareFactChange { Key = $"chg.{i}", Kind = SnapshotChangeKind.Changed }),
            .. Enumerable.Range(0, unchanged).Select(i => new HardwareFactChange { Key = $"same.{i}", Kind = SnapshotChangeKind.Unchanged }),
        ],
    };

    [Fact]
    public void 硬體變更_沒比較過_不拿零冒充沒變()
    {
        var facts = Trust();
        var f = F(facts, TrustOpsFactsService.HwDiffKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是「0 項變更」", f.UnavailableReason);
    }

    [Fact]
    public void 硬體變更_跨機器比對_不構成本機變更史()
    {
        var f = F(Trust(diff: Diff(sameMachine: false, 1, 0, 0, 0)), TrustOpsFactsService.HwDiffKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不同機器", f.UnavailableReason);
    }

    [Fact]
    public void 硬體變更_差異如實且聲明不是連續事件史()
    {
        var f = F(Trust(diff: Diff(true, 2, 1, 3, 40)), TrustOpsFactsService.HwDiffKey);

        Assert.Equal(6, f.NumericValue);
        Assert.Contains("新增 2", f.Value);
        Assert.Contains("消失 1", f.Value);
        Assert.Contains("改變 3", f.Value);
        Assert.Contains("未變 40", f.Value);
        Assert.Contains("不是連續事件史", f.Value);
    }

    // ── ZR-005 Passkey ──────────────────────────────────────────────────

    [Fact]
    public void Passkey_環境存在且版本達標_講明支援不等於已設定()
    {
        var f = F(Trust(), TrustOpsFactsService.PasskeyKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("支援不等於已設定", f.Value);
        Assert.Contains("26100", f.Value);
    }

    [Fact]
    public void Passkey_沒有webauthn環境_標NotSupported()
    {
        var f = F(Trust(webauthn: false), TrustOpsFactsService.PasskeyKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    [Fact]
    public void Passkey_組建號讀不到_不猜版本門檻()
    {
        var f = F(Trust(build: null), TrustOpsFactsService.PasskeyKey);

        Assert.Contains("無法確認版本門檻", f.Value);
        Assert.DoesNotContain("達 Windows 11", f.Value);
    }

    [Fact]
    public void Passkey_版本低於門檻_直說不在支援範圍()
    {
        var f = F(Trust(build: "19045"), TrustOpsFactsService.PasskeyKey);
        Assert.Contains("低於 22621", f.Value);
    }

    // ── DL-002 行程傾摘同意閘門 ─────────────────────────────────────────

    [Fact]
    public void 傾摘_未同意_永不執行且不留值()
    {
        var f = F(Trust(), TrustOpsFactsService.DumpKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Equal("", f.Value);
        Assert.Contains("未同意則永不執行", f.UnavailableReason);
    }

    [Fact]
    public void 傾摘_已同意但無實作_不假裝做過()
    {
        var f = F(Trust(consent: true), TrustOpsFactsService.DumpKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("沒有傾印實作", f.UnavailableReason);
    }

    [Fact]
    public void 傾摘_同意查詢拋例外_一律按未同意處理()
    {
        var facts = TrustOpsFactsService.Collect(At, () => [], () => null, _ => true, _ => "26100",
            _ => throw new InvalidOperationException("閘門壞了"), null);

        Assert.Equal(FactAvailability.NotApplicable, F(facts, TrustOpsFactsService.DumpKey).Availability);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 整組_非Present都帶三態原因()
    {
        var facts = Trust(pipes: () => null, audit: () => null, webauthn: false, build: null);
        foreach (var f in facts.Where(f => f.Availability != FactAvailability.Present))
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺三態原因");
    }

    [Fact]
    public void 整組_衍生項一次交齊三把鍵()
    {
        var derived = QueryInsightFactsService.CollectDerived(At, [Fact("cpu.a", "1", numeric: 1)]);

        Assert.Equal(
            new[] { QueryInsightFactsService.UnitKey, QueryInsightFactsService.IndexKey, QueryInsightFactsService.GradeKey },
            derived.Select(d => d.Key).ToArray());
    }
}