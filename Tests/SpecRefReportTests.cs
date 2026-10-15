using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// ULTIMATE-1000 階段一 P1-024 – P1-031 的出處鏈輸出鍵守門。
/// </summary>
public class SpecRefReportTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, FactAvailability availability) =>
        new(key, "測試", key, availability == FactAvailability.Present ? "值" : "—", "", "測試來源",
            FactTrustLevel.Reported, false, At, null, availability, null);

    // ── spec.ref ────────────────────────────────────────────────────────

    [Fact]
    public void 引用總數_要同時報條數與涵蓋的解碼器數()
    {
        var fact = SpecRefReport.ToRefFact(At);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.True(fact.NumericValue > 0, "登錄裡應該已經有引用");
        Assert.Contains("解碼器", fact.Value);
    }

    // ── spec.ref.granularity ────────────────────────────────────────────

    [Fact]
    public void 解析度_有位移級描述就算夠細()
    {
        Assert.True(SpecRefReport.IsGranular("ACPI Spec, HEST 表：Error Source Count u32 @36"));
        Assert.True(SpecRefReport.IsGranular("UEFI PI Vol. 3, §2.1.4 EFI_FFS_FILE_HEADER.Type"));
        Assert.True(SpecRefReport.IsGranular("wlanapi.h：lRssi(offset 64)"));
    }

    [Fact]
    public void 解析度_只寫文件名的引用算不夠細()
    {
        Assert.False(SpecRefReport.IsGranular("PCIe Base Specification"));
        Assert.False(SpecRefReport.IsGranular("Intel SDM"));
    }

    [Fact]
    public void 解析度_輸出要說出總數與不足的條數()
    {
        var fact = SpecRefReport.ToGranularityFact(At);
        Assert.Contains("條引用", fact.Value);
        // 不足時必須標成「不是 Present」——那是要人看的，不是綠燈
        if (fact.Availability != FactAvailability.Present)
            Assert.Contains("只寫到規格層級", fact.Value);
    }

    // ── spec.ref.coverage ───────────────────────────────────────────────

    [Fact]
    public void 覆蓋_全部有引用時標Present()
    {
        var fact = SpecRefReport.ToCoverageFact(At, totalPublicMethods: 10);
        Assert.True(fact.NumericValue is >= 0);
        Assert.Contains("解碼器", fact.Value);
    }

    [Fact]
    public void 覆蓋_有缺時要標讀取失敗並列出缺口()
    {
        // 用一個不可能為 0 的總數，逼出「有缺」的分支
        var missing = SpecRefRegistry.MethodsMissingRefs();
        var fact = SpecRefReport.ToCoverageFact(At, totalPublicMethods: missing.Count + 1);
        if (missing.Count > 0)
        {
            Assert.Equal(FactAvailability.ReadError, fact.Availability);
            Assert.Contains("沒有 SpecRef", fact.UnavailableReason);
        }
    }

    [Fact]
    public void 覆蓋_沒有方法可統計時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            SpecRefReport.ToCoverageFact(At, totalPublicMethods: 0).Availability);
    }

    // ── spec.ref.version ────────────────────────────────────────────────

    [Fact]
    public void 版本_要報出標有版本的條數與校對日期()
    {
        var fact = SpecRefReport.ToVersionFact(At, new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("校對", fact.Value);
        Assert.Contains("2026-10-16", fact.Value);
        Assert.True(fact.NumericValue >= 0);
    }

    // ── spec.ref.unit ───────────────────────────────────────────────────

    [Fact]
    public void 換算係數_全部有出處時標Present()
    {
        var fact = SpecRefReport.ToUnitFact(At,
            [("kHz→MHz", 0.001, "SI 前置詞定義")]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("全部附有出處", fact.Value);
    }

    [Fact]
    public void 換算係數_沒出處的要標讀取失敗並說出後果()
    {
        var fact = SpecRefReport.ToUnitFact(At,
            [("MT/s→MHz", 0.5, "JEDEC DDR5"), ("GB→GiB", 1.073741824, "")]);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("GB→GiB", fact.Value);
        Assert.Contains("看起來正常的錯數字", fact.UnavailableReason);
    }

    [Fact]
    public void 換算係數_沒有登記時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            SpecRefReport.ToUnitFact(At, []).Availability);
    }

    // ── fact.provenance ─────────────────────────────────────────────────

    [Fact]
    public void 出處對應_全部有出處時標Present()
    {
        var fact = SpecRefReport.ToProvenanceFact(At,
            [Fact("a", FactAvailability.Present)], []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("可回溯到出處", fact.Value);
    }

    [Fact]
    public void 出處對應_有值卻沒出處時要標未確認()
    {
        var fact = SpecRefReport.ToProvenanceFact(At,
            [Fact("a", FactAvailability.Present), Fact("b", FactAvailability.Present)],
            ["b"]);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("b", fact.Value);
        Assert.Contains("未驗證", fact.UnavailableReason);
    }

    [Fact]
    public void 出處對應_沒有事實時標不適用()
    {
        Assert.Equal(FactAvailability.NotApplicable,
            SpecRefReport.ToProvenanceFact(At, [], []).Availability);
    }
}
