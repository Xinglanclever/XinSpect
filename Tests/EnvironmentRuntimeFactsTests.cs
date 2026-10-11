using System;
using System.Collections.Generic;
using System.Linq;
using System;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 執行環境與治理事實組（Vol2 推進第 42–51 項）的守門：全部走注入假件——
/// 真實通路碰 token／登錄／powercfg 行程／GC 與 ThreadPool 計數，
/// 在單元測試裡讀真的會讓結果取決於跑測試的機器。
/// </summary>
public class EnvironmentRuntimeFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Collect(
        EnvironmentRuntimeFactsService.RegistryProbe? registry = null,
        EnvironmentRuntimeFactsService.CommandRunner? wake = null,
        EnvironmentRuntimeFactsService.PoolProbe? pool = null,
        EnvironmentRuntimeFactsService.GcProbe? gc = null,
        IReadOnlyList<string>? visited = null,
        string? symbolEnv = null)
        => EnvironmentRuntimeFactsService.Collect(At, registry, wake, pool, gc, visited, symbolEnv is null ? (Func<string?>)(() => null) : () => symbolEnv);

    // ── IN-005 提權最小化審計（衍生項，吃前面各組事實）────────────────

    [Fact]
    public void 提權審計_無缺權限鍵_如實說沒有()
    {
        var facts = new List<HardwareFact>
        {
            new("a", "測試", "a", "v", "", "s", FactTrustLevel.Reported, false, At),
        };
        var f = Fact(EnvironmentRuntimeFactsService.CollectDerived(At, facts), EnvironmentRuntimeFactsService.ElevationKey);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("沒有任何來源因權限不足", f.Value);
        Assert.Contains("不自行提權", f.Value);
    }

    [Fact]
    public void 提權審計_缺權限鍵逐名列出()
    {
        var facts = new List<HardwareFact>
        {
            new("spi.x", "測試", "x", "", "", "s", FactTrustLevel.Unknown, false, At, null,
                FactAvailability.InsufficientPrivilege, "缺 ring0"),
            new("msr.y", "測試", "y", "", "", "s", FactTrustLevel.Unknown, false, At, null,
                FactAvailability.InsufficientPrivilege, "缺 ring0"),
            new("ok.z", "測試", "z", "v", "", "s", FactTrustLevel.Reported, false, At),
        };
        var f = Fact(EnvironmentRuntimeFactsService.CollectDerived(At, facts), EnvironmentRuntimeFactsService.ElevationKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("msr.y", f.Value);
        Assert.Contains("spi.x", f.Value);
    }

    // ── IN-006 自我更新完整性 ───────────────────────────────────────────

    [Fact]
    public void 自我更新_沒有通道就說沒有_不冒充驗證通過()
    {
        var f = Fact(Collect(), EnvironmentRuntimeFactsService.UpdateSigKey);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("沒有自我更新通道", f.UnavailableReason);
        Assert.Contains("不等於「驗證通過」", f.UnavailableReason);
    }

    // ── NG-005 導覽歷史 ─────────────────────────────────────────────────

    [Fact]
    public void 導覽歷史_空軌跡_標不適用且不落盤()
    {
        var f = Fact(Collect(visited: []), EnvironmentRuntimeFactsService.HistoryKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不落盤", f.UnavailableReason);
    }

    [Fact]
    public void 導覽歷史_軌跡如實且標敏感_重複站不重複記()
    {
        EnvironmentRuntimeFactsService.ResetVisitedForTests();
        EnvironmentRuntimeFactsService.NotePageVisited("cpu", "處理器");
        EnvironmentRuntimeFactsService.NotePageVisited("cpu", "處理器");   // 同一頁連按
        EnvironmentRuntimeFactsService.NotePageVisited("gpu", "顯示卡");

        var snap = EnvironmentRuntimeFactsService.SnapshotVisited();
        Assert.Equal(2, snap.Count);

        var f = Fact(Collect(visited: snap), EnvironmentRuntimeFactsService.HistoryKey);
        Assert.Equal(2, f.NumericValue);
        Assert.True(f.Sensitive, "使用者行為軌跡屬 L1——必須標敏感讓匯出遮蔽");
        Assert.Contains("cpu／處理器", f.Value);
        Assert.Contains("不進快照", f.Value);
        EnvironmentRuntimeFactsService.ResetVisitedForTests();
    }

    // ── DL-003 符號路徑 ─────────────────────────────────────────────────

    [Fact]
    public void 符號路徑_未設定_標不適用說清是設定狀態()
    {
        var f = Fact(Collect(symbolEnv: null), EnvironmentRuntimeFactsService.SymbolPathKey);

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("設定狀態", f.UnavailableReason);
    }

    // ── DL-008 線程池 ───────────────────────────────────────────────────

    [Fact]
    public void 線程池_讀取失敗_兩把鍵都ReadError()
    {
        var facts = Collect(pool: () => null);
        foreach (string key in new[] { EnvironmentRuntimeFactsService.PoolCountKey, EnvironmentRuntimeFactsService.PoolSummaryKey })
        {
            var f = Fact(facts, key);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
        }
    }

    [Fact]
    public void 線程池_數值如實_界線講清楚是本行程()
    {
        var facts = Collect(pool: () => (12, 300, 900, 42, 3));
        var count = Fact(facts, EnvironmentRuntimeFactsService.PoolCountKey);
        Assert.Equal(12, count.NumericValue);
        var s = Fact(facts, EnvironmentRuntimeFactsService.PoolSummaryKey);
        Assert.Contains("執行緒 12 條", s.Value);
        Assert.Contains("本工具自己這個行程", s.Value);
    }

    // ── DL-009 GC ───────────────────────────────────────────────────────

    [Fact]
    public void GC_模式與計數如實_不當洩漏判決()
    {
        var facts = Collect(gc: () => (true, "Interactive", 5, 3, 1, 100 * 1024 * 1024));
        var mode = Fact(facts, EnvironmentRuntimeFactsService.GcModeKey);
        Assert.Contains("Server GC", mode.Value);
        Assert.Contains("Interactive", mode.Value);
        var summary = Fact(facts, EnvironmentRuntimeFactsService.GcSummaryKey);
        Assert.Equal(1, summary.NumericValue);
        Assert.Contains("不是洩漏判決", summary.Value);
    }

    // ── UF-007 更新通道 ─────────────────────────────────────────────────

    [Fact]
    public void 更新通道_政策釘版本_說被釘而不是自己選的()
    {
        var f = Fact(Collect(registry: name => name switch
        {
            "TargetReleaseVersion" => 1,
            "TargetReleaseVersionInfo" => "24H2",
            "DisplayVersion" => "24H2",
            "CurrentBuildNumber" => "26100",
            _ => null,
        }), EnvironmentRuntimeFactsService.ChannelKey);

        Assert.Contains("目前 24H2／build 26100", f.Value);
        Assert.Contains("被釘在這個版本", f.Value);
        Assert.Contains("不講「該不該升」", f.Value);
    }

    // ── UF-010 更新健康儀表 ─────────────────────────────────────────────

    [Fact]
    public void 更新儀表_三把鍵都不在場_標Unknown不是正常()
    {
        var f = Fact(EnvironmentRuntimeFactsService.CollectDerived(At, []), EnvironmentRuntimeFactsService.DashKey);

        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("沒有東西可以湊", f.UnavailableReason);
    }

    [Fact]
    public void 更新儀表_部分在場_如實計數()
    {
        var facts = new List<HardwareFact>
        {
            new("uf.source", "更新深化", "更新來源", "Windows Update（公用服務）", "", "s", FactTrustLevel.Reported, false, At),
            new("uf.servicing", "更新深化", "服務堆疊版本", "CBS 服務堆疊版本 10.0.26100.32692。界線：只陳述版本。", "", "s", FactTrustLevel.Measured, false, At),
            // uf.channel 缺席
        };
        var f = Fact(EnvironmentRuntimeFactsService.CollectDerived(At, facts), EnvironmentRuntimeFactsService.DashKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("已知 2 項", f.Value);
        Assert.Contains("尚未讀取", f.Value);
    }

    // ── HM-004 喚醒記錄 ─────────────────────────────────────────────────

    [Fact]
    public void 喚醒記錄_命令不可用_標ReadError()
    {
        var f = Fact(Collect(wake: _ => null), EnvironmentRuntimeFactsService.WakeKey);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("讀不到就是不猜", f.UnavailableReason);
    }

    [Fact]
    public void 喚醒記錄_原樣輸出_不解析在地化文字()
    {
        var f = Fact(Collect(wake: _ => "REALTEK USB GbE Family Controller\r\nIntel(R) Wi-Fi 6 AX201 160MHz"),
            EnvironmentRuntimeFactsService.WakeKey);

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("REALTEK USB GbE Family Controller", f.Value);
        Assert.Contains("不解析在地化文字", f.Value);
    }

    // ── FS-013 隱私禁項 ─────────────────────────────────────────────────

    [Fact]
    public void 隱私禁項_清單逐類列出_禁令講明是測試釘住()
    {
        var f = Fact(Collect(), EnvironmentRuntimeFactsService.PrivacyKey);

        Assert.Equal(EnvironmentRuntimeFactsService.ForbiddenSourcePatterns.Count, f.NumericValue);
        Assert.Contains("Jump lists", f.Value);
        Assert.Contains("thumbcache", f.Value);
        Assert.Contains("不是靠記性", f.Value);
    }

    [Fact]
    public void 隱私禁項_禁的是使用者行為資料_不是硬體來源()
    {
        foreach (var pattern in EnvironmentRuntimeFactsService.ForbiddenSourcePatterns)
        {
            bool isUserBehavior =
                pattern.Contains("Recent", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("thumbcache", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("UrlCache", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("UserAssist", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("MRU", StringComparison.OrdinalIgnoreCase);
            Assert.True(isUserBehavior, $"禁項 {pattern} 看起來不像使用者行為來源——禁令清單要重新審視");
        }
    }

    // ── 整組契約 ────────────────────────────────────────────────────────

    [Fact]
    public void 整組_非Present都帶原因_敏感值都標記()
    {
        var facts = Collect(
            registry: name => name switch
            {
                "TargetReleaseVersion" => 1,
                "TargetReleaseVersionInfo" => "24H2",
                "DisplayVersion" => "24H2",
                "CurrentBuildNumber" => "26100",
                _ => null,
            },
            wake: _ => "Xxx Device",
            pool: () => null,          // 讀取失敗路徑
            gc: () => null,            // 讀取失敗路徑
            visited: ["overview／總覽"],
            symbolEnv: "SRV*C:\\MySymbols\\cache");

        foreach (var f in facts)
        {
            if (f.Availability != FactAvailability.Present)
                Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺三態原因");
        }
        // 導覽歷史與符號路徑是 L1，必須標敏感
        Assert.True(Fact(facts, EnvironmentRuntimeFactsService.HistoryKey).Sensitive);
        var sym = Fact(facts, EnvironmentRuntimeFactsService.SymbolPathKey);
        Assert.True(sym.Sensitive, "符號路徑可能含使用者名稱——必須標敏感");
        Assert.Contains("SRV*C:", sym.Value);
    }
}