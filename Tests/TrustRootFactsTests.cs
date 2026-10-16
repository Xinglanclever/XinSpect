using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 信任根來源陳述的守門（ULTIMATE-1000 階段二 P2-058）。
///
/// <para>
/// <b>核心是「看得到證據」與「根已生效」之間的距離</b>，
/// 以及「沒有證據」與「讀不到」的分離。
/// </para>
/// </summary>
public class TrustRootFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static TrustRootFactsService.Evidence E(
        bool? bg = null, bool? measured = null, bool? sb = null)
        => new(bg, measured, sb);

    // ── 三態 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有證據來源時要標不支援並說出不自己去讀()
    {
        var facts = TrustRootFactsService.Collect(At, null);
        var r = facts.Single(f => f.Key == TrustRootFactsService.RootKey);

        Assert.Equal(FactAvailability.NotSupported, r.Availability);
        Assert.Contains("不自己去讀", r.UnavailableReason);
        Assert.Contains("各有自己的可及性事實", r.UnavailableReason);
    }

    [Fact]
    public void 三者都讀不到要說那與沒有信任根不同()
    {
        var facts = TrustRootFactsService.Collect(At, E());
        var r = facts.Single(f => f.Key == TrustRootFactsService.RootKey);

        Assert.Equal(FactAvailability.InsufficientPrivilege, r.Availability);
        Assert.Contains("「讀不到」與「沒有」是不同的斷言", r.UnavailableReason);
        Assert.Contains("這台機器沒有硬體信任根", r.UnavailableReason);
    }

    // ── 根的種類 ────────────────────────────────────────────────────────

    [Fact]
    public void 三種根可以並存()
    {
        var facts = TrustRootFactsService.Collect(At, E(bg: true, measured: true, sb: true));
        var r = facts.Single(f => f.Key == TrustRootFactsService.RootKey);

        Assert.Equal(3.0, r.NumericValue);
        Assert.Contains("Boot Guard", r.Value);
        Assert.Contains("TPM 度量開機", r.Value);
        Assert.Contains("Secure Boot", r.Value);
        // **Present 的事實不得帶理由欄**（HardwareFact 的不變式）：說明被併進了 Value，
        // 所以這一節的斷言讀 f.Value，不是 f.UnavailableReason。
        Assert.Contains("**三種根不互斥**", r.Value);
    }

    [Fact]
    public void 類型鍵要報出錨點與本機證據()
    {
        var facts = TrustRootFactsService.Collect(At, E(bg: true, measured: true));
        var t = facts.Single(f => f.Key == TrustRootFactsService.TypesKey);

        Assert.Contains("Boot Guard→CPU 熔絲裡的 OEM 金鑰雜湊", t.Value);
        Assert.Contains("FIT 目錄裡有 ACM／啟動模組條目", t.Value);
        Assert.Contains("TPM 度量開機→TPM 的背書金鑰（EK）", t.Value);
        Assert.Contains("關鍵 PCR 已被延伸", t.Value);
    }

    [Fact]
    public void 要說出錨點本身看不到()
    {
        var facts = TrustRootFactsService.Collect(At, E(bg: true));
        var t = facts.Single(f => f.Key == TrustRootFactsService.TypesKey);

        Assert.Contains("錨點本身看不到", t.Value);
        Assert.Contains("CPU 熔絲與 TPM 的 EK 都不在使用者模式的範圍內", t.Value);
    }

    // ── 界線：證據與生效的距離 ──────────────────────────────────────────

    [Fact]
    public void 要說出看得到證據與根已生效之間有距離()
    {
        var facts = TrustRootFactsService.Collect(At, E(bg: true));
        var r = facts.Single(f => f.Key == TrustRootFactsService.RootKey);

        Assert.Contains("「看得到證據」與「根已生效」之間有距離", r.Value);
        Assert.Contains("不代表 Boot Guard 已啟用", r.Value);
    }

    [Fact]
    public void 要說出不宣告這台機器的根是哪一種()
    {
        var facts = TrustRootFactsService.Collect(At, E(bg: true));
        var r = facts.Single(f => f.Key == TrustRootFactsService.RootKey);

        Assert.Contains("不宣告「這台機器的根是 X」", r.Value);
    }

    // ── 沒有 vs 讀不到 ──────────────────────────────────────────────────

    [Fact]
    public void 沒有證據與讀不到要分開列()
    {
        // Boot Guard 讀不到、TPM 讀得到但沒有、Secure Boot 讀不到。
        var facts = TrustRootFactsService.Collect(At, E(bg: null, measured: false, sb: null));
        var t = facts.Single(f => f.Key == TrustRootFactsService.TypesKey);

        // **TypesKey 是 Present**，所以不變式把說明併進了 Value（理由欄留空）；
        // 而「沒有證據」與「讀不到」這兩句是服務組出來的（名字是內插的），所以逐字比對 Value。
        Assert.Contains("沒有證據的：TPM 度量開機", t.Value);
        Assert.Contains("讀不到的：Boot Guard、Secure Boot", t.Value);
        Assert.Contains("把兩者混在一起會讓一個讀不到的正常機器看起來像沒有信任根", t.Value);
    }

    [Fact]
    public void 三者都讀得到但都沒看到根是一個有意義的結果()
    {
        var facts = TrustRootFactsService.Collect(At, E(bg: false, measured: false, sb: false));
        var r = facts.Single(f => f.Key == TrustRootFactsService.RootKey);

        Assert.Equal(FactAvailability.NotApplicable, r.Availability);
        // **非 Present 的事實不得帶數字**（HardwareFact 的不變式）：0 已被收斂成 null，
        // 而「原本是多少」留在理由欄的痕跡裡（不是靜默修正）。
        Assert.Null(r.NumericValue);
        Assert.Contains("原本帶了數字 0", r.UnavailableReason);
        Assert.Contains("那是一個有意義的結果", r.UnavailableReason);
        Assert.Contains("排除了「讀不到所以看不到」這個解釋", r.UnavailableReason);
    }

    // ── FromFacts ───────────────────────────────────────────────────────

    [Fact]
    public void 從事實取值時讀不到不得當成沒有()
    {
        var facts = new List<HardwareFact>
        {
            new("fw.fit.count", "韌體", "FIT 條目數", "", "條", "s",
                FactTrustLevel.Measured, false, At, 3, FactAvailability.Present),
            new(MeasuredBootFactsService.MeasuredKey, "信賴根", "量測開機", "", "", "s",
                FactTrustLevel.Unknown, false, At, null, FactAvailability.InsufficientPrivilege),
        };

        var ev = TrustRootFactsService.FromFacts(facts);

        Assert.NotNull(ev);
        Assert.True(ev!.BootGuardAcmInFit);
        Assert.Null(ev.MeasuredBoot);   // 讀不到 → null，不是 false
        Assert.Null(ev.SecureBootEnabled);
    }

    [Fact]
    public void 沒有相關事實時回null()
    {
        Assert.Null(TrustRootFactsService.FromFacts([]));
        Assert.Null(TrustRootFactsService.FromFacts(
        [
            new("unrelated.key", "x", "y", "", "", "s",
                FactTrustLevel.Measured, false, At, 1, FactAvailability.Present),
        ]));
    }

    // ── 鍵集合與目錄 ────────────────────────────────────────────────────

    [Fact]
    public void 失敗路徑與成功路徑的鍵集合必須相同()
    {
        var ok = TrustRootFactsService.Collect(At, E(bg: true))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var none = TrustRootFactsService.Collect(At, null)
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(ok, none);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var facts = TrustRootFactsService.Collect(At, E(bg: true, measured: true));
        var unknown = facts.Select(f => f.Key).Where(k => !catalog.Contains(k)).ToList();

        Assert.True(unknown.Count == 0,
            "信任根事實產出了目錄裡沒有的鍵：\n"
            + string.Join("\n", unknown.Select(u => "  • " + u)));
    }
}
