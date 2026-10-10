using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 能力矩陣彙總層（v2.52，docs/PROGRAM-ULTIMATE-2026-10-10.md §5.11）：
/// cap.* 只是既有來源鍵的彙總，不重新解釋環境——
/// 來源 Present→可用；來源三態→原樣搬運；來源缺席→Unknown。
/// 缺席不讀成「不支援」，也不假「可用」，這與 v2.36 六態守恆同一條線。
/// </summary>
public class CapabilityMatrixTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] CapKeys =
        ["cap.iop", "cap.mmio", "cap.msr", "cap.pci", "cap.pmu", "cap.smbus", "cap.tpm", "cap.uefi.variables", "cap.wmi"];

    private static HardwareFact Src(string key, FactAvailability avail,
        double? num = null, string? reason = null)
        => new(key, "來源", key, num?.ToString() ?? "", "", "測試來源",
            FactTrustLevel.Measured, false, At, num, avail, reason);

    [Fact]
    public void 九列全在且鍵名固定()
    {
        var rows = CapabilityMatrixService.Collect([], At);
        Assert.Equal(CapKeys.Length, rows.Count);
        Assert.Equal(CapKeys.OrderBy(k => k, StringComparer.Ordinal),
            rows.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(rows, f => Assert.Equal("能力矩陣", f.Category));
    }

    [Fact]
    public void 來源Present時cap可用並繼承數值()
    {
        var rows = CapabilityMatrixService.Collect([Src("backend.msr", FactAvailability.Present, num: 42)], At);
        var msr = rows.Single(f => f.Key == "cap.msr");
        Assert.Equal(FactAvailability.Present, msr.Availability);
        Assert.Equal("可用", msr.Value);
        Assert.Equal(FactTrustLevel.Derived, msr.Trust);
        Assert.Equal(42, msr.NumericValue);
        Assert.Contains("backend.msr＝Present", msr.Source, StringComparison.Ordinal);
        Assert.Null(msr.UnavailableReason);
        // 其他八鍵沒有來源輸入→一律缺席判定，不得跟隨唯一那筆 Present
        Assert.All(rows.Where(f => f.Key != "cap.msr"),
            f => Assert.Equal(FactAvailability.Unknown, f.Availability));
    }

    [Fact]
    public void 來源三態時cap原樣搬運可用性與原因()
    {
        var rows = CapabilityMatrixService.Collect(
            [Src("tpm.present", FactAvailability.NotApplicable, reason: "TBS 通道不存在")], At);
        var tpm = rows.Single(f => f.Key == "cap.tpm");
        Assert.Equal(FactAvailability.NotApplicable, tpm.Availability);
        Assert.Equal("TBS 通道不存在", tpm.UnavailableReason);
        Assert.Contains("tpm.present＝NotApplicable", tpm.Source, StringComparison.Ordinal);
        Assert.NotEqual("可用", tpm.Value);
    }

    [Fact]
    public void 來源鍵缺席說無法判定而不是不支援()
    {
        var rows = CapabilityMatrixService.Collect([], At);
        Assert.All(rows, f =>
        {
            Assert.Equal(FactAvailability.Unknown, f.Availability);
            Assert.Contains("無法判定", f.UnavailableReason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 九鍵都已登記進目錄()
    {
        foreach (string key in CapKeys)
            Assert.Contains(key, FactKeyCatalog.Keys);
    }

    [Fact]
    public void 矩陣已併入AllFacts且输入不含cap行免迴圈()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        string lab = File.ReadAllText(Path.Combine(dir!.FullName, "Services", "EvidenceLabService.cs"));
        Assert.Contains("CapabilityMatrixService.Collect(baseFacts", lab, StringComparison.Ordinal);
        // 輸入是 baseFacts（來源群），不是 AllFacts 本身——cap 行不會餵回來源對照
        Assert.DoesNotContain("CapabilityMatrixService.Collect(AllFacts", lab, StringComparison.Ordinal);
    }

    /// <summary>畫面投影（v2.56）：尚未載入任何來源時，九行全在且一律「無法判定」——缺席不冒充不支援。</summary>
    [Fact]
    public void 畫面投影九行全在且未載來源時一律無法判定()
    {
        var lab = new EvidenceLabService();
        var rows = lab.CapabilityMatrixRows;
        Assert.Equal(9, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(r.IsUnavailable);
            Assert.Contains("無法判定", r.ValueText, StringComparison.Ordinal);
        });
    }

    /// <summary>守門（v2.56）：卡片與投影屬性不得拆除——拆掉後九條彙總又沈回長清單裡沒人看得見。</summary>
    [Fact]
    public void 能力矩陣卡片與投影接線不得拆除()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "Views", "FirmwareSecurityView.xaml"));
        Assert.Contains("firmware-security/能力矩陣", xaml, StringComparison.Ordinal);
        Assert.Contains("EvidenceLab.CapabilityMatrixRows", xaml, StringComparison.Ordinal);
    }
}
