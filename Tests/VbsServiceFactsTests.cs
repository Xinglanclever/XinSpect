using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// VBS 安全服務逐服務清單的守門（U10 第三圈 SE-0004，第 320 輪）。
///
/// <para>
/// <b>這一支盯四件事：</b>①代碼 0 是哨兵不是服務（不得出現 <c>sec.vbs.svc.0</c>）；
/// ②成員是兩張清單的聯集（只設定沒跑、只跑沒設定都要列出來，且各自有措辭）；
/// ③某一張清單讀不到時只出讀得到的那一半，並在每一筆上寫明另一半讀不到——
/// <b>不拿一半的資料拼一張看起來完整的表</b>；④執行清單讀不到時總覽不給數字。
/// </para>
/// </summary>
public class VbsServiceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static VbsStateFactsService.VbsSnapshot Snap(
        uint[]? configured = null, uint[]? running = null,
        uint[]? required = null, uint[]? available = null)
        => new(2, required ?? [], available ?? [], configured, running);

    private static IReadOnlyList<HardwareFact> Run(
        VbsStateFactsService.VbsSnapshot? snapshot,
        FactAvailability availability = FactAvailability.Present,
        string? reason = null)
        => VbsServiceFactsService.Collect(snapshot, availability, reason, At);

    private static HardwareFact Summary(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == VbsServiceFactsService.SummaryKey);

    private static IEnumerable<HardwareFact> Services(IReadOnlyList<HardwareFact> facts)
        => facts.Where(f => f.Key.StartsWith(VbsServiceFactsService.ServicePrefix, StringComparison.Ordinal));

    [Fact]
    public void 執行面不可用時沿用上游三態_不自己重新分類()
    {
        var facts = Run(null, FactAvailability.InsufficientPrivilege, "缺 ring0");

        var summary = Summary(facts);
        Assert.Equal(FactAvailability.InsufficientPrivilege, summary.Availability);
        Assert.Null(summary.NumericValue);
        Assert.Contains("Win32_DeviceGuard 不可用", summary.UnavailableReason);
        Assert.Contains("缺 ring0", summary.UnavailableReason);
        Assert.Empty(Services(facts));
    }

    [Fact]
    public void 命名空間不存在時上游是NotApplicable_這裡也照樣()
    {
        var facts = Run(null, FactAvailability.NotApplicable, "本機沒有該命名空間");
        Assert.Equal(FactAvailability.NotApplicable, Summary(facts).Availability);
    }

    [Fact]
    public void 逐服務的鍵要一個服務一把_且成員是兩張清單的聯集()
    {
        // 設定 1、2；執行 2、3 → 聯集 {1,2,3}，其中 1 是「設定但沒跑」、3 是「跑但沒設定」。
        var facts = Run(Snap(configured: [1, 2], running: [2, 3]));

        var keys = Services(facts).Select(f => f.Key).ToList();
        Assert.Equal(["sec.vbs.svc.1", "sec.vbs.svc.2", "sec.vbs.svc.3"], keys);
    }

    [Fact]
    public void 設定但沒在跑要寫成待查_不是已啟用()
    {
        var fact = Assert.Single(Services(Run(Snap(configured: [1], running: [2]))),
            f => f.Key == "sec.vbs.svc.1");

        Assert.Contains("設定：是", fact.Value);
        Assert.Contains("執行：否", fact.Value);
        Assert.Contains("已設定但沒在跑", fact.Value);
        Assert.Contains("不是「已啟用」", fact.Value);
    }

    [Fact]
    public void 在跑但設定清單沒有要並列不仲裁()
    {
        var fact = Assert.Single(Services(Run(Snap(configured: [2], running: [1]))),
            f => f.Key == "sec.vbs.svc.1");

        Assert.Contains("在跑但設定清單裡沒有", fact.Value);
        Assert.Contains("並列不仲裁", fact.Value);
    }

    [Fact]
    public void 代碼0是哨兵_不得出現逐服務鍵()
    {
        // 這是本服務最容易犯的錯：Win32_DeviceGuard 用 0 表示「無服務」，
        // 照字面出鍵會得到一把 sec.vbs.svc.0——那是在替「沒有」造一個東西。
        var facts = Run(Snap(configured: [0], running: [0]));

        Assert.Empty(Services(facts));
        var summary = Summary(facts);
        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Equal(0, summary.NumericValue);
        Assert.Contains("哨兵", summary.Value);
        Assert.Contains("無服務", summary.Value);
    }

    [Fact]
    public void 空清單是空清單_不是讀不到()
    {
        var summary = Summary(Run(Snap(configured: [], running: [])));

        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Equal(0, summary.NumericValue);
        Assert.Contains("（空清單）", summary.Value);
        Assert.Contains("不是讀不到", summary.Value);
        Assert.Null(summary.UnavailableReason);
    }

    [Fact]
    public void 設定清單讀不到時_執行面照樣逐服務列出並寫明另一半讀不到()
    {
        // 不因為少一張清單就整組不出：SE-0004 問的是「實際在跑哪幾個」，
        // 執行面讀得到就該回答；只是每一筆都要寫明「設定：讀不到」。
        var facts = Run(Snap(configured: null, running: [1, 2]));

        var keys = Services(facts).Select(f => f.Key).ToList();
        Assert.Equal(["sec.vbs.svc.1", "sec.vbs.svc.2"], keys);
        Assert.All(Services(facts), f => Assert.Contains("設定：讀不到（欄位缺席）", f.Value));

        var summary = Summary(facts);
        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Equal(2, summary.NumericValue);
        Assert.Contains("設定讀不到", summary.Value);
    }

    [Fact]
    public void 執行清單讀不到時_總覽不給數字且轉未知()
    {
        var facts = Run(Snap(configured: [1], running: null));

        var summary = Summary(facts);
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.Null(summary.NumericValue);              // 非 Present 不得帶數字
        Assert.Contains("不知道在跑哪幾個", summary.UnavailableReason);
        Assert.Contains("不給執行數", summary.UnavailableReason);

        // 設定面仍然讀得到，所以逐服務鍵照樣出，但執行那一欄寫讀不到。
        var svc = Assert.Single(Services(facts));
        Assert.Equal("sec.vbs.svc.1", svc.Key);
        Assert.Contains("設定：是", svc.Value);
        Assert.Contains("執行：讀不到（欄位缺席）", svc.Value);
    }

    [Fact]
    public void 兩張清單都缺席時不逐服務出鍵()
    {
        var facts = Run(Snap(configured: null, running: null));

        Assert.Empty(Services(facts));
        var summary = Summary(facts);
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.Contains("兩個欄位都缺席", summary.UnavailableReason);
    }

    [Fact]
    public void 總覽的執行數要等於逐服務裡執行是的個數()
    {
        var facts = Run(Snap(configured: [1, 2, 3], running: [2, 3]));
        var summary = Summary(facts);

        int runningYes = Services(facts).Count(f => f.Value.Contains("執行：是", StringComparison.Ordinal));
        Assert.Equal(runningYes, summary.NumericValue);
    }

    [Fact]
    public void 服務代碼要照值表解碼_未收錄帶原碼()
    {
        var facts = Run(Snap(configured: [], running: [2, 42]));

        Assert.Contains("2（記憶體完整性／HVCI）", facts.Single(f => f.Key == "sec.vbs.svc.2").Value);
        Assert.Contains("0x2A（值表未收錄）", facts.Single(f => f.Key == "sec.vbs.svc.42").Value);
    }

    [Fact]
    public void 逐服務的鍵要帶可信度與來源_不是空白事實()
    {
        var facts = Run(Snap(configured: [1], running: [1]));
        var svc = Assert.Single(Services(facts));

        Assert.Equal(FactTrustLevel.Reported, svc.Trust);
        Assert.Equal(FactAvailability.Present, svc.Availability);
        Assert.Contains("Win32_DeviceGuard", svc.Source);
        Assert.Null(svc.UnavailableReason);             // Present 不得帶理由
    }

    [Fact]
    public void 家族前綴必須在動態家族登記裡()
    {
        // 執行期產生的鍵要嘛在靜態目錄、要嘛命中家族前綴，否則對帳紅燈。
        Assert.True(FactKeyDynamicCatalog.Families.ContainsKey(VbsServiceFactsService.ServicePrefix),
            $"動態家族沒有登記 {VbsServiceFactsService.ServicePrefix}");
        Assert.Contains(VbsServiceFactsService.SummaryKey, FactKeyCatalog.Keys);
    }
}
