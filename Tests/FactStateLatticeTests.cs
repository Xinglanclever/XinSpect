using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 事實可用性六態的格與傳播（純函式）。這一組釘住的是一件不會產生任何錯誤訊息的事：
/// 把「讀不到」當成 0 帶進算式——那是所有誠實工具最常見的塌陷方式。
/// </summary>
public class FactStateLatticeTests
{
    // ── 格本身的形狀 ──────────────────────────────────────────────────────

    [Fact]
    public void 格要含六態且無重複()
    {
        var order = FactStateLattice.LatticeOrder;
        Assert.Equal(6, order.Count);
        Assert.Equal(6, order.Distinct().Count());
        Assert.Contains(FactAvailability.Present, order);
        Assert.Contains(FactAvailability.Unknown, order);
        Assert.Contains(FactAvailability.NotApplicable, order);
        Assert.Contains(FactAvailability.NotSupported, order);
        Assert.Contains(FactAvailability.InsufficientPrivilege, order);
        Assert.Contains(FactAvailability.ReadError, order);
    }

    [Fact]
    public void Present是最強_ReadError是最弱()
    {
        Assert.Equal(0, FactStateLattice.Rank(FactAvailability.Present));
        Assert.Equal(5, FactStateLattice.Rank(FactAvailability.ReadError));

        foreach (var s in FactStateLattice.LatticeOrder)
        {
            if (s == FactAvailability.Present) continue;
            Assert.True(FactStateLattice.Rank(s) > 0, $"{s} 不該與 Present 同級");
        }
    }

    [Fact]
    public void Unknown比Present弱但比不適用強()
    {
        // 「有值但未確認」比「完全沒有」強——它至少代表嘗試過、拿到了東西
        Assert.True(FactStateLattice.Rank(FactAvailability.Unknown)
                  > FactStateLattice.Rank(FactAvailability.Present));
        Assert.True(FactStateLattice.Rank(FactAvailability.Unknown)
                  < FactStateLattice.Rank(FactAvailability.NotApplicable));
    }

    [Fact]
    public void 環境事實比不確定強_因為環境本身是確定的()
    {
        // 「本機沒有 BMC」是確定的環境事實，不該與「讀取失敗」同級
        Assert.True(FactStateLattice.Rank(FactAvailability.NotApplicable)
                  < FactStateLattice.Rank(FactAvailability.InsufficientPrivilege));
        Assert.True(FactStateLattice.Rank(FactAvailability.NotSupported)
                  < FactStateLattice.Rank(FactAvailability.ReadError));
    }

    [Fact]
    public void 未收錄的列舉值_排到最弱不得取得強位置()
    {
        // 新增列舉值而忘記登記時，它不該意外變成「最強」
        var unknownValue = (FactAvailability)999;
        Assert.True(FactStateLattice.Rank(unknownValue) >= FactStateLattice.LatticeOrder.Count);
        Assert.Equal(unknownValue, FactStateLattice.Weakest(FactAvailability.Present, unknownValue));
    }

    // ── 合取傳播 ──────────────────────────────────────────────────────────

    [Fact]
    public void 全部Present才是Present()
    {
        Assert.Equal(FactAvailability.Present, FactStateLattice.Combine(
            FactAvailability.Present, FactAvailability.Present, FactAvailability.Present));
    }

    [Fact]
    public void 有一個讀不到_結論就不能宣稱可用()
    {
        // 微碼一致性同時依賴登錄檔與 MSR：一個讀不到，結論就不能說「一致」
        var combined = FactStateLattice.Combine(
            FactAvailability.Present, FactAvailability.InsufficientPrivilege);
        Assert.Equal(FactAvailability.InsufficientPrivilege, combined);
        Assert.False(FactStateLattice.IsTrustworthy(combined));
    }

    [Fact]
    public void 取最弱者_不是取最強者也不是多數決()
    {
        Assert.Equal(FactAvailability.ReadError, FactStateLattice.Combine(
            FactAvailability.Present, FactAvailability.Present, FactAvailability.ReadError));

        // 多數決會給出 Present——那正是要防的塌陷
        Assert.NotEqual(FactAvailability.Present, FactStateLattice.Combine(
            FactAvailability.Present, FactAvailability.Present, FactAvailability.ReadError));
    }

    [Fact]
    public void 傳播是單調的_加更多來源只會變弱不會變強()
    {
        var one = FactStateLattice.Combine([FactAvailability.Unknown]);
        var two = FactStateLattice.Combine([FactAvailability.Unknown, FactAvailability.Present]);
        var three = FactStateLattice.Combine(
            [FactAvailability.Unknown, FactAvailability.Present, FactAvailability.ReadError]);

        Assert.True(FactStateLattice.Rank(one) <= FactStateLattice.Rank(two));
        Assert.True(FactStateLattice.Rank(two) <= FactStateLattice.Rank(three));
    }

    [Fact]
    public void 空集合回Unknown_不是回讀取失敗()
    {
        // 沒有任何來源的結論不該宣稱可用，也不該說成「試過了」
        var combined = FactStateLattice.Combine([]);
        Assert.Equal(FactAvailability.Unknown, combined);
        Assert.False(FactStateLattice.IsBlocked(combined));
    }

    [Fact]
    public void 單一來源_原樣傳遞()
    {
        foreach (var s in FactStateLattice.LatticeOrder)
            Assert.Equal(s, FactStateLattice.Combine([s]));
    }

    // ── 可信度判斷 ────────────────────────────────────────────────────────

    [Fact]
    public void 只有Present可以拿去下結論()
    {
        Assert.True(FactStateLattice.IsTrustworthy(FactAvailability.Present));
        foreach (var s in FactStateLattice.LatticeOrder.Where(x => x != FactAvailability.Present))
            Assert.False(FactStateLattice.IsTrustworthy(s), $"{s} 不該被當成可信");
    }

    [Theory]
    [InlineData(FactAvailability.Unknown, true)]
    [InlineData(FactAvailability.Present, false)]
    [InlineData(FactAvailability.NotApplicable, false)]
    public void 未確認的辨識(FactAvailability s, bool expected)
        => Assert.Equal(expected, FactStateLattice.IsUnconfirmed(s));

    [Theory]
    [InlineData(FactAvailability.NotApplicable, true)]
    [InlineData(FactAvailability.NotSupported, true)]
    [InlineData(FactAvailability.ReadError, false)]
    [InlineData(FactAvailability.InsufficientPrivilege, false)]
    public void 環境事實的辨識(FactAvailability s, bool expected)
        => Assert.Equal(expected, FactStateLattice.IsEnvironmental(s));

    [Theory]
    [InlineData(FactAvailability.InsufficientPrivilege, true)]
    [InlineData(FactAvailability.ReadError, true)]
    [InlineData(FactAvailability.NotApplicable, false)]
    [InlineData(FactAvailability.Unknown, false)]
    public void 被擋住的辨識(FactAvailability s, bool expected)
        => Assert.Equal(expected, FactStateLattice.IsBlocked(s));

    [Fact]
    public void ReadError不算可恢復_因為連失敗原因都不確定()
    {
        Assert.True(FactStateLattice.IsRecoverable(FactAvailability.InsufficientPrivilege));
        Assert.True(FactStateLattice.IsRecoverable(FactAvailability.NotSupported));
        Assert.False(FactStateLattice.IsRecoverable(FactAvailability.ReadError));
    }

    // ── 說明文字 ──────────────────────────────────────────────────────────

    [Fact]
    public void 每一態都要有說明且不得為空()
    {
        foreach (var s in FactStateLattice.LatticeOrder)
        {
            Assert.False(string.IsNullOrWhiteSpace(FactStateLattice.Describe(s)), $"{s} 缺中文名");
            Assert.False(string.IsNullOrWhiteSpace(FactStateLattice.Explain(s)), $"{s} 缺說明");
        }
    }

    [Fact]
    public void 未確認的說明要明說不要當成已確認()
    {
        string text = FactStateLattice.Explain(FactAvailability.Unknown);
        Assert.Contains("不要當成已確認", text);
    }

    [Fact]
    public void 不適用的說明要明說不是故障()
    {
        Assert.Contains("不是故障", FactStateLattice.Explain(FactAvailability.NotApplicable));
    }

    [Fact]
    public void 不支援的說明要明說不代表硬體沒能力()
    {
        Assert.Contains("不代表硬體沒有這個能力", FactStateLattice.Explain(FactAvailability.NotSupported));
    }

    // ── 事實層的驗證：Unknown 的兩條硬規則 ────────────────────────────────

    // ── 事實層的驗證：Unknown 的兩條硬規則 ────────────────────────────────

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-08T00:00:00+00:00");
    private static readonly string[] Identity = ["test-machine"];

    private static HardwareSnapshotFact Fact(
        FactAvailability availability, double? numeric = null, string? reason = "值來自快取")
        => new()
        {
            Key = "test.unconfirmed",
            Category = "測試",
            Name = "未確認的事實",
            Value = "1234",
            NumericValue = numeric,
            Source = "模擬來源",
            Trust = FactTrustLevel.Reported,
            MeasuredAtUtc = At,
            Availability = availability,
            UnavailableReason = reason,
        };

    private static HardwareSnapshot Build(HardwareSnapshotFact fact)
        => HardwareSnapshotService.Create([fact], Identity, At, "t");

    [Fact]
    public void Unknown狀態_值與原因都在就可通過正規化()
    {
        var stored = Assert.Single(Build(Fact(FactAvailability.Unknown)).Facts);
        Assert.Equal(FactAvailability.Unknown, stored.Availability);
        Assert.Equal("1234", stored.Value);
    }

    [Fact]
    public void Unknown狀態_不得帶numericValue()
    {
        // numericValue 是可計算欄位；允許未確認的值帶數值等於邀請呼叫端拿它去算
        Assert.Throws<ArgumentException>(() => Build(Fact(FactAvailability.Unknown, numeric: 1234)));
    }

    [Fact]
    public void Unknown狀態_不得缺原因()
    {
        Assert.Throws<ArgumentException>(() => Build(Fact(FactAvailability.Unknown, reason: null)));
    }

    [Fact]
    public void Unknown的字串值不得被自動轉成數值()
    {
        // 「未確認」與「可計算」是兩件事。若正規化把 "1234" 轉成 NumericValue，
        // 下游就會拿一個未確認的數字去算，而且不會有任何錯誤訊息。
        Assert.Null(Assert.Single(Build(Fact(FactAvailability.Unknown)).Facts).NumericValue);
    }

    [Fact]
    public void Present的字串值_在UI入口仍會被自動轉成數值()
    {
        // 對照組：確認過的值該轉就轉，否則這條規則會誤傷正常路徑。
        // 走 HardwareFact 入口（UI 相容路徑）——TryNumeric 的自動轉換在那條路上。
        var fact = new HardwareFact("test.present", "測試", "確認的事實",
            "1234", "", "模擬來源", FactTrustLevel.Reported, false, At, null,
            FactAvailability.Present, null);
        var snapshot = HardwareSnapshotService.Create("t", [fact], includeSensitive: false);
        Assert.Equal(1234, Assert.Single(snapshot.Facts).NumericValue);
    }

    [Fact]
    public void Unknown的字串值_在UI入口也不得被自動轉成數值()
    {
        // 同一條入口的對照：Unknown 不轉，即使字串就是一個純數字
        var fact = new HardwareFact("test.unconfirmed", "測試", "未確認的事實",
            "1234", "", "模擬來源", FactTrustLevel.Reported, false, At, null,
            FactAvailability.Unknown, "值來自快取");
        var snapshot = HardwareSnapshotService.Create("t", [fact], includeSensitive: false);
        Assert.Null(Assert.Single(snapshot.Facts).NumericValue);
    }

    [Fact]
    public void ReadError仍不得帶數值_與Unknown同一條界線()
    {
        Assert.Throws<ArgumentException>(() => Build(Fact(FactAvailability.ReadError, numeric: 1)));
    }
}
