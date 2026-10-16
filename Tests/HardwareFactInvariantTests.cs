using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// <b><c>HardwareFact</c> 的不變式：非 <c>Present</c> 的事實不得帶數字。</b>
///
/// <para>
/// 同一個錯誤在本專案發生過<b>五次</b>，而每次都是把兩個不同的「沒有」壓成一個。
/// 修法不是第六次的注意力，而是把那個不變式寫進型別本身。
/// </para>
/// </summary>
public class HardwareFactInvariantTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Make(double? numeric, FactAvailability availability,
        string? reason = null)
        => new("k", "c", "n", "v", "u", "s", FactTrustLevel.Measured, false, At,
            numeric, availability, reason);

    [Fact]
    public void Present的事實可以帶數字()
    {
        var f = Make(7, FactAvailability.Present);
        Assert.Equal(7.0, f.NumericValue);
    }

    [Fact]
    public void Present的事實也可以不帶數字()
    {
        var f = Make(null, FactAvailability.Present);
        Assert.Null(f.NumericValue);
    }

    /// <summary>這一條就是那個犯了五次的錯誤。</summary>
    [Theory]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.ReadError)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.Unknown)]
    public void 非Present的事實不得帶數字(FactAvailability availability)
    {
        var f = Make(0, availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void 非Present的數字被收斂時要留下痕跡而不是靜默修正()
    {
        var f = Make(0, FactAvailability.NotApplicable, "原本的理由");

        Assert.Null(f.NumericValue);
        Assert.Contains("原本的理由", f.UnavailableReason);
        Assert.Contains("本筆原本帶了數字 0", f.UnavailableReason);
        Assert.Contains("非 Present 的事實不得帶數字，已如實收斂為空", f.UnavailableReason);
    }

    [Fact]
    public void Present的事實不得被加上收斂痕跡()
    {
        // **這一條原本斷言 `Assert.Equal("理由", f.UnavailableReason)`——
        // 而那假設了「Present 時理由原封不動留在理由欄」。**
        //
        // 加上第二條不變式之後（Present ⇒ 理由併進 Value、理由欄留空），
        // 那個假設不成立了。**而原本的意圖仍然成立**：
        // 「Present 的事實不該出現『已如實收斂為空』那句痕跡」——
        // 因為痕跡是給非 Present 的數字矛盾用的。
        //
        // 所以斷言改成「那段話還在（在 Value 裡）、而痕跡不在（兩邊都不在）」。
        var f = Make(3, FactAvailability.Present, "理由");

        Assert.Null(f.UnavailableReason);
        Assert.Contains("理由", f.Value);
        Assert.DoesNotContain("收斂", f.Value);
        Assert.DoesNotContain("非 Present 的事實不得帶數字", f.Value);
    }

    /// <summary>
    /// <b>第二條不變式：Present 時，寫在理由欄的說明併進 Value。</b>
    /// 那些話不是垃圾——它們是「這個值是什麼意思」，而那正是 Value 該說的事。
    /// </summary>
    [Fact]
    public void Present時寫在理由欄的說明要併進值裡()
    {
        var f = Make(3, FactAvailability.Present, "這個數字是這樣算的");

        Assert.Equal("v。這個數字是這樣算的", f.Value);
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void Present而沒有理由時值不得被加上多餘的標點()
    {
        var f = Make(3, FactAvailability.Present, null);

        Assert.Equal("v", f.Value);
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 非Present但不帶數字時理由要原樣保留()
    {
        var f = Make(null, FactAvailability.ReadError, "讀取失敗");
        Assert.Equal("讀取失敗", f.UnavailableReason);
    }

    [Fact]
    public void 五次歷史錯誤的實際形狀都要被擋住()
    {
        // ① MeasuredBootFacts：讀不到 PCR 卻傳 0
        Assert.Null(Make(0, FactAvailability.InsufficientPrivilege).NumericValue);
        // ② TrustRootFacts：讀不到信任根卻傳 0
        Assert.Null(Make(0, FactAvailability.InsufficientPrivilege).NumericValue);
        // ③ UefiSectionTreeFacts.TreeKey：沒有 FV
        Assert.Null(Make(0, FactAvailability.NotApplicable).NumericValue);
        // ④ UefiSectionTreeFacts.DepthKey：沒有樹可以數
        Assert.Null(Make(0, FactAvailability.NotApplicable).NumericValue);
        // ⑤ EfiVariableFacts.KeysKey：四個變數都讀不到
        Assert.Null(Make(0, FactAvailability.NotApplicable).NumericValue);
    }
}
