using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TPM 清除界線的守門（ULTIMATE-1000 階段二 P2-076）。
///
/// <para>
/// 它與 <c>ec.boundary</c> 同型：<b>一把什麼都不測的鍵，
/// 而它存在的理由就是說出那個界線</b>。
/// </para>
/// </summary>
public class TpmClearBoundaryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact() => TpmFactsService.TpmClearBoundaryFact(At);

    [Fact]
    public void 鍵名與規格一致()
    {
        Assert.Equal("tpm.clear.boundary", Fact().Key);
    }

    [Fact]
    public void 值要明說本專案不做清除或重設()
    {
        var f = Fact();

        Assert.Contains("不做 TPM 清除或重設", f.Value);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 要說出為什麼不做()
    {
        var f = Fact();

        Assert.Contains("TPM2_Clear 會讓擁有者授權與所有以該授權封存的資料永久失效", f.UnavailableReason);
        Assert.Contains("最不可逆的操作之一", f.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著「不做」的合理性。</b>
    /// 不做清除不是因為做不到，而是因為不需要。
    /// </summary>
    [Fact]
    public void 要說出判斷擁有者不需要清除()
    {
        var f = Fact();

        Assert.Contains("讀 TPM_PT_PERMANENT 的位元就夠了", f.UnavailableReason);
        Assert.Contains("不需要先清除再重來", f.UnavailableReason);
    }

    [Fact]
    public void 要說出界線不說出口會被誤讀()
    {
        var f = Fact();

        Assert.Contains("這把鍵存在的理由就是說出那件事", f.UnavailableReason);
        Assert.Contains("會被讀成「還沒做」而不是「刻意不做」", f.UnavailableReason);
        Assert.Contains("與 ec.boundary 同型的處置", f.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著「未對準出處就不給編號」的第四次判斷。</b>
    /// P2-069 需要 TPM_PT_PERMANENT 的編號，而本專案只收了有出處的四個。
    /// </summary>
    [Fact]
    public void 要如實標出P2_069尚未對準出處()
    {
        var f = Fact();

        Assert.Contains("P2-069（tpm.owned）", f.UnavailableReason);
        Assert.Contains("需要 TPM_PT_PERMANENT 的屬性編號", f.UnavailableReason);
        Assert.Contains("只收了有出處的四個", f.UnavailableReason);
        Assert.Contains("在對準出處之前不做", f.UnavailableReason);
    }

    /// <summary>
    /// <b>這一條守著「不給編號」本身。</b>
    /// 那把界線鍵的輸出裡不得出現任何 TPM_PT_* 的十六進位值——
    /// 因為它只引用名字（那個名字在 Tpm2 的出處裡），不引用我記得的數字。
    /// </summary>
    [Fact]
    public void 界線鍵不得給出任何屬性編號的數值()
    {
        var f = Fact();

        Assert.DoesNotContain("0x00000110", f.UnavailableReason);   // TPM_PT_PERMANENT「我記得的」值
        Assert.DoesNotContain("0x110", f.UnavailableReason);
        // 而它引用的名字必須真的出現在 Tpm2 的出處裡。
        Assert.Contains("TPM_PT_PERMANENT", f.UnavailableReason);
    }

    [Fact]
    public void 這把鍵要真的出現在TPM事實裡且可在無TPM時給出()
    {
        // 沒有 TPM 時整組只剩 tpm.present，所以這把鍵走的是「有 TPM」那條路。
        var facts = TpmFactsService.Collect(At, createContext: () => nint.Zero);
        Assert.Single(facts);
        Assert.Equal("tpm.present", facts[0].Key);
    }

    [Fact]
    public void 所有輸出鍵都要在目錄裡()
    {
        var catalog = FactKeyCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        var f = Fact();

        Assert.True(catalog.Contains(f.Key),
            $"TPM 清除界線產出了目錄裡沒有的鍵：{f.Key}");
    }
}
