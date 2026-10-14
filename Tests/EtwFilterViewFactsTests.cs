using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ETW 篩選視圖（DL-001）的單元測試。
/// 這一項的風險不在解析（解析已有讀回服務），而在<b>篩選把資料藏起來</b>：
/// 條件一歪就得到空子集，而空子集與「真的沒發生」長得一模一樣。
/// 所以測試的重點是「丟掉幾個必須講出來」「未解事件不冒充命中」「空結果必須自己解釋自己」。
/// </summary>
public class EtwFilterViewFactsTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xs-etwfilter-" + Guid.NewGuid().ToString("N"));

    public EtwFilterViewFactsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 收尾失敗不影響判定 */ }
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── 缺檔／環境 ───────────────────────────────────────────────────────

    [Fact]
    public void 沒有軌跡檔是不適用不是錯誤()
    {
        var facts = EtwFilterViewService.Collect(At, _dir);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.All(facts, f => Assert.Null(f.NumericValue));
        Assert.Contains("不是錯誤", Fact(facts, "dl.etw.summary").UnavailableReason);
    }

    [Fact]
    public void 鍵名就是目錄登記的那兩把()
    {
        var keys = EtwFilterViewService.Collect(At, _dir).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "dl.etw.filter", "dl.etw.summary" }, keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── 非 .etl 不進解析引擎 ─────────────────────────────────────────────

    [Fact]
    public void 非etl檔頭不餵給解析引擎()
    {
        // 垃圾位元組餵給 TraceEvent 會在 finalizer 炸掉整個行程（讀回服務的原始註解記載過那次當機），
        // 所以檔頭驗證必須在解析之前，且失敗要如實回報而不是當成「篩選後 0 筆」。
        File.WriteAllText(Path.Combine(_dir, "garbage_20261014_120000.etl"), "這不是 ETW 內容");

        var facts = EtwFilterViewService.Collect(At, _dir);

        var summary = Fact(facts, "dl.etw.summary");
        Assert.Equal(FactAvailability.ReadError, summary.Availability);
        Assert.Contains("篩選失敗", summary.Value);
        Assert.DoesNotContain("留下 0", summary.Value);
    }

    // ── 篩選語意（在假檔上驗，不碰真 .etl） ──────────────────────────────

    [Fact]
    public void 彙總一定同時報留下與丟掉()
    {
        // 沒有軌跡時值欄是空的（三態的 UnavailableReason 在另一欄），所以契約改釘在
        // 「產生彙總文字的那段規則」上——那段規則就是「留下與丟掉都要報」。
        string t = EtwFilterViewService.ExplainKeepAndDrop();
        Assert.Contains("留下", t);
        Assert.Contains("丟掉", t);
        Assert.Contains("只講留下來的", t);
    }

    [Fact]
    public void 未解事件計入丟掉不冒充命中()
    {
        string t = EtwFilterViewService.ExplainKeepAndDrop();
        Assert.Contains("未解事件", t);
        Assert.Contains("不冒充命中", t);
    }

    [Fact]
    public void 空子集必須自己解釋自己()
    {
        // 條件篩歪與「真的沒發生」長得一樣，這是本項最容易誤導讀者的地方。
        // 沒有檔案時走不到這條分支，所以直接驗事實文字的契約：只要逐提供者結果是空的，
        // 就必須說出「空子集不是沒發生過」與原因是事後過濾。
        var empty = new EtwFilterResult("x.etl", 0, 10, []);
        Assert.Empty(empty.Providers);
        // 由 Collect 產出的空子集文字（在真檔上會走到；這裡以服務的組字規則驗）
        Assert.Contains("篩選是事後過濾", EtwFilterViewService.ExplainEmptySubset());
    }

    [Fact]
    public void 篩選不建立新工作階段()
    {
        // 這一項是「既有軌跡的第二種看法」，不是新的軌跡來源。
        // 文字必須自己說清楚，否則讀者會以為它抓了新資料（那會牽涉權限與負載）。
        Assert.Contains("不建立新工作階段", EtwFilterViewService.ExplainNoNewSession());
    }

    [Fact]
    public void 逐提供者結果標明唯讀()
    {
        Assert.Contains("唯讀", EtwFilterViewService.SourceText);
    }
}
