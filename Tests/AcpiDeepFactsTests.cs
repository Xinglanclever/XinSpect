using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 AC-004/005/011/012/013/018 項（ACPI 深化二）的事實服務測試。</summary>
public class AcpiDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 20, 0, 0, TimeSpan.Zero);

    // ── AC-004 _PRx ─────────────────────────────────────────────────

    [Fact]
    public void 電源資源_無評估層如實說不假裝解出來()
    {
        var f = AcpiDeepFactsService.PowerResourceFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有評估層就沒有物件可數", f.UnavailableReason);
        Assert.Contains("不假裝解出來", f.UnavailableReason);
    }

    [Fact]
    public void 電源資源_單一資源為不適用()
    {
        var f = AcpiDeepFactsService.PowerResourceFact(At,
            () => new AcpiDeepFactsService.PowerResourceState(1, 1, 2));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒什麼好匯總的", f.UnavailableReason);
    }

    [Fact]
    public void 電源資源_標明只到存在性與計數()
    {
        var f = AcpiDeepFactsService.PowerResourceFact(At,
            () => new AcpiDeepFactsService.PowerResourceState(12, 5, 4));
        Assert.Contains("只到存在性與計數", f.Value);
        Assert.Contains("不是「電源管理的實際行為」", f.Value);
        Assert.Equal(12.0, f.NumericValue);
    }

    // ── AC-005 _DSM ─────────────────────────────────────────────────

    [Fact]
    public void DSM_無表宣告為不適用()
    {
        var f = AcpiDeepFactsService.DsmFact(At,
            () => new AcpiDeepFactsService.DsmState(0, 0, true));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void DSM_標明不代查GUID用途()
    {
        var f = AcpiDeepFactsService.DsmFact(At,
            () => new AcpiDeepFactsService.DsmState(6, 14, true));
        Assert.Contains("不代查、不猜測 GUID 的用途", f.Value);
        Assert.Contains("不含機器或使用者資訊", f.Value);
        Assert.Equal(14.0, f.NumericValue);
    }

    [Fact]
    public void DSM_GUID長度異常要標示()
    {
        var f = AcpiDeepFactsService.DsmFact(At,
            () => new AcpiDeepFactsService.DsmState(6, 14, false));
        Assert.Contains("有 GUID 長度不合 16 位元組", f.Value);
        Assert.Contains("值得留意", f.Value);
    }

    // ── AC-011 BGRT ─────────────────────────────────────────────────

    [Fact]
    public void BGRT_無表為不適用並說明不是問題()
    {
        var f = AcpiDeepFactsService.BgrtFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不代表有問題", f.UnavailableReason);
    }

    [Fact]
    public void BGRT_有表不等於功能可用()
    {
        var f = AcpiDeepFactsService.BgrtFact(At,
            () => new AcpiDeepFactsService.BgrtState(true, 1, 0, true, true));
        Assert.Contains("有表不等於功能可用", f.Value);
        Assert.Contains("點陣圖", f.Value);
        Assert.Contains("校驗和正確", f.Value);
    }

    [Fact]
    public void BGRT_映像位址為零與校驗和錯要標示()
    {
        var f = AcpiDeepFactsService.BgrtFact(At,
            () => new AcpiDeepFactsService.BgrtState(true, 1, 0, false, false));
        Assert.Contains("為零", f.Value);
        Assert.Contains("不正確", f.Value);
    }

    // ── AC-012 SPCR ─────────────────────────────────────────────────

    [Fact]
    public void SPCR_無表為不適用並說明平台性質()
    {
        var f = AcpiDeepFactsService.SpcrFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("主要給伺服器與嵌入式", f.UnavailableReason);
    }

    [Fact]
    public void SPCR_明確聲明不開序列埠()
    {
        var f = AcpiDeepFactsService.SpcrFact(At,
            () => new AcpiDeepFactsService.SpcrState(true, 0x0E, "APIC", true, 1));
        Assert.Contains("不開啟任何序列埠、不送任何字元", f.Value);
        Assert.Equal(1.0, f.NumericValue);
    }

    [Fact]
    public void SPCR_鮑率未設定要標示()
    {
        var f = AcpiDeepFactsService.SpcrFact(At,
            () => new AcpiDeepFactsService.SpcrState(true, 0x03, "雙 8259", false, 2));
        Assert.Contains("未設定", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    // ── AC-013 DBG2 ─────────────────────────────────────────────────

    [Fact]
    public void DBG2_無表為不適用()
    {
        var f = AcpiDeepFactsService.Dbg2Fact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void DBG2_明確聲明不探測除錯埠()
    {
        var f = AcpiDeepFactsService.Dbg2Fact(At,
            () => new AcpiDeepFactsService.Dbg2State(true, 2, "序列埠", 2));
        Assert.Contains("不開啟、不寫入、不探測", f.Value);
        Assert.Contains("全部有非零位址", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    [Fact]
    public void DBG2_部分位址為零如實報比例()
    {
        var f = AcpiDeepFactsService.Dbg2Fact(At,
            () => new AcpiDeepFactsService.Dbg2State(true, 3, "混合", 1));
        Assert.Contains("1/3 個有非零位址", f.Value);
    }

    // ── AC-018 表匯總 ───────────────────────────────────────────────

    [Fact]
    public void 表匯總_讀不到為讀取錯誤()
    {
        var f = AcpiDeepFactsService.TablesSummaryFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 表匯總_零校驗和錯誤是值()
    {
        var f = AcpiDeepFactsService.TablesSummaryFact(At,
            () => new AcpiDeepFactsService.TablesSummaryState(24, 15, 24, 0, "DSDT v2"));
        Assert.Contains("0 是值", f.Value);
        Assert.Equal(24.0, f.NumericValue);
    }

    [Fact]
    public void 表匯總_校驗和錯誤要標示()
    {
        var f = AcpiDeepFactsService.TablesSummaryFact(At,
            () => new AcpiDeepFactsService.TablesSummaryState(24, 15, 22, 2, "DSDT v2"));
        Assert.Contains("錯誤 2", f.Value);
        Assert.Contains("值得留意", f.Value);
    }

    [Fact]
    public void 表匯總_標明與逐表分工不重複列()
    {
        var f = AcpiDeepFactsService.TablesSummaryFact(At,
            () => new AcpiDeepFactsService.TablesSummaryState(24, 15, 24, 0, "DSDT v2"));
        Assert.Contains("與逐表列舉分工", f.Value);
        Assert.Contains("不是某一張表的內容", f.Value);
    }

    // ── 整批 ────────────────────────────────────────────────────────

    [Fact]
    public void 整批_六把鍵齊且不重複()
    {
        var facts = AcpiDeepFactsService.Collect(At,
            () => new AcpiDeepFactsService.PowerResourceState(12, 5, 4),
            () => new AcpiDeepFactsService.DsmState(6, 14, true),
            () => new AcpiDeepFactsService.BgrtState(true, 1, 0, true, true),
            () => new AcpiDeepFactsService.SpcrState(true, 0x0E, "APIC", true, 1),
            () => new AcpiDeepFactsService.Dbg2State(true, 2, "序列埠", 2),
            () => new AcpiDeepFactsService.TablesSummaryState(24, 15, 24, 0, "DSDT v2"));

        Assert.Equal(6, facts.Count);
        Assert.Equal(6, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            AcpiDeepFactsService.PowerResourceKey, AcpiDeepFactsService.DsmKey,
            AcpiDeepFactsService.BgrtKey, AcpiDeepFactsService.SpcrKey,
            AcpiDeepFactsService.Dbg2Key, AcpiDeepFactsService.TablesKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
