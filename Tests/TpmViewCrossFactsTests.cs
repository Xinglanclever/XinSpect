using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TPM 兩視圖對照（TBS × Win32_Tpm）的守門（U10-OS-0061／U10-FW-0051 的對照鍵）。
///
/// <para>
/// 守的是四件事：<b>①</b>一致＝Present；<b>②</b>矛盾也是 Present，但必須具名列雙方、
/// <b>不仲裁</b>；<b>③</b>一側讀不到＝無從對照（缺哪側、另一側的值如實附上）；
/// <b>④</b>沒有可比對的欄位時不硬湊結論。
/// </para>
/// </summary>
public class TpmViewCrossFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static TpmViewCrossFactsService.TbsView TbsV(string? family = "2.0",
        string? revision = "1.16", string? manufacturer = "IFX")
        => new(family, revision, manufacturer);

    private static TpmFactsService.TpmWindowsView WmiV(string? specVersion = "2.0, 0, 1.16",
        string? manufacturerIdTxt = "IFX", string? manufacturerVersion = "1.16")
        => new(specVersion, manufacturerIdTxt, manufacturerVersion, true, true, false);

    private static TpmViewCrossFactsService.TbsView Boom()
        => throw new InvalidOperationException();

    // ── 兩側都有 ────────────────────────────────────────────────────────

    [Fact]
    public void 兩側一致時要帶雙方原始值並標一致()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(), () => WmiV()).Single();

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("一致", f.Value);
        Assert.Contains("family 2.0、revision 1.16、廠商 IFX", f.Value);
        Assert.Contains("SpecVersion「2.0, 0, 1.16」", f.Value);
        Assert.Contains("ManufacturerIdTxt「IFX」", f.Value);
    }

    [Fact]
    public void family矛盾時要具名列雙方且不仲裁()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(family: "1.2"), () => WmiV())
            .Single();

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0.0, f.NumericValue);
        Assert.Contains("矛盾", f.Value);
        Assert.Contains("family（規格家族）：TBS 說「1.2」、WMI 說「2.0」", f.Value);
        Assert.Contains("不仲裁", f.Value);
    }

    [Fact]
    public void revision矛盾時也要具名列雙方()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(revision: "1.2"), () => WmiV())
            .Single();

        Assert.Equal(0.0, f.NumericValue);
        Assert.Contains("revision（規格版號）：TBS 說「1.2」、WMI 說「1.16」", f.Value);
    }

    [Fact]
    public void 廠商碼比對不分大小寫()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(manufacturer: "ifx"),
            () => WmiV(manufacturerIdTxt: "IFX")).Single();

        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("manufacturer（廠商碼）", f.Value);
    }

    [Fact]
    public void 廠商碼不同是矛盾不是一致()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(manufacturer: "STM"),
            () => WmiV(manufacturerIdTxt: "IFX")).Single();

        Assert.Equal(0.0, f.NumericValue);
        Assert.Contains("manufacturer（廠商碼）：TBS 說「STM」、WMI 說「IFX」", f.Value);
    }

    [Fact]
    public void 矛盾時一致的欄位也要一併說出()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(family: "1.2"), () => WmiV())
            .Single();

        // family 矛盾、revision 與 manufacturer 一致——兩種結果並列，不因矛盾抹掉一致的部分
        Assert.Contains("revision（規格版號）", f.Value);
        Assert.Contains("manufacturer（廠商碼）", f.Value);
    }

    // ── 沒有可比對的欄位 ────────────────────────────────────────────────

    [Fact]
    public void WMI無可比欄位時要不硬湊結論()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(),
            () => WmiV(specVersion: null, manufacturerIdTxt: null)).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("沒有可比對的欄位", f.UnavailableReason);
        Assert.Contains("不硬湊結論", f.UnavailableReason);
    }

    [Fact]
    public void SpecVersion解析不出三段時revision欄標無法比對而不是矛盾()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(), () => WmiV(specVersion: "2.0"))
            .Single();

        Assert.Equal(1.0, f.NumericValue);
        Assert.Contains("無法比對的欄位（一側未回報）", f.Value);
        Assert.Contains("revision（規格版號）（TBS＝1.16、WMI＝未回報）", f.Value);
        Assert.DoesNotContain("**矛盾**", f.Value);
    }

    // ── 一側讀不到 ──────────────────────────────────────────────────────

    [Fact]
    public void TBS側讀不到時要說缺哪側並附上WMI的值()
    {
        var f = TpmViewCrossFactsService.Collect(At, tbs: () => null, wmi: () => WmiV()).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("TBS 那一側讀不到", f.UnavailableReason);
        Assert.Contains("無從對照", f.UnavailableReason);
        Assert.Contains("SpecVersion「2.0, 0, 1.16」", f.UnavailableReason);
        Assert.Contains("「讀不到一側」與「兩側矛盾」是不同的斷言", f.UnavailableReason);
    }

    [Fact]
    public void WMI側讀不到時要說缺哪側並附上TBS的值()
    {
        var f = TpmViewCrossFactsService.Collect(At, tbs: () => TbsV(), wmi: () => null).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("Win32_Tpm 讀不到", f.UnavailableReason);
        Assert.Contains("family 2.0、revision 1.16、廠商 IFX", f.UnavailableReason);
    }

    [Fact]
    public void 兩側都讀不到時只陳述兩條路都不通()
    {
        var f = TpmViewCrossFactsService.Collect(At, tbs: () => null, wmi: () => null).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("兩個視圖都讀不到", f.UnavailableReason);
        Assert.Contains("不推測哪一側該負責", f.UnavailableReason);
    }

    // ── 通路界線 ────────────────────────────────────────────────────────

    /// <summary>探針丟例外＝那條路讀不到，不得炸掉整個對照。</summary>
    [Fact]
    public void 探針丟例外時要當成讀不到而不是紅燈()
    {
        var f = TpmViewCrossFactsService.Collect(At, Boom, () => WmiV()).Single();

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("TBS 那一側讀不到", f.UnavailableReason);
    }

    /// <summary>界線要成文：WMI 是 Windows 的視圖、不是同一次讀取、矛盾不仲裁。</summary>
    [Fact]
    public void 界線宣告要說出兩側不是同一次讀取()
    {
        var f = TpmViewCrossFactsService.Collect(At, () => TbsV(), () => WmiV()).Single();

        Assert.Contains("兩者不是同一次讀取", f.Value);
        Assert.Contains("一致只說「兩個視圖此刻說了同一件事」", f.Value);
    }
}
