using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol2 第 454–468 項（韌體深化）的事實服務測試。</summary>
public class FirmwareDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 微碼CVE_空表不代填()
    {
        var f = FirmwareDeepFactsService.UcodeCveFact(At, () => (0, ""));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("空表不是「沒漏洞」", f.UnavailableReason);
    }

    [Fact]
    public void 微碼CVE_快照日期必印()
    {
        var f = FirmwareDeepFactsService.UcodeCveFact(At, () => (12, "2026-09-01"));
        Assert.Contains("快照日期必印", f.Value);
        Assert.Equal(12.0, f.NumericValue);
    }

    [Fact]
    public void CSME_未取得傾印標未施測()
    {
        var f = FirmwareDeepFactsService.MeFact(At, () => ("", false));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("未施測聲明", f.UnavailableReason);
    }

    [Fact]
    public void CSME_版本是足跡非判決()
    {
        var f = FirmwareDeepFactsService.MeFact(At, () => ("16.1.30", true));
        Assert.Contains("足跡", f.Value);
        Assert.Contains("不是安全判決", f.Value);
    }

    [Fact]
    public void PSP_未取得傾印標未施測()
    {
        var f = FirmwareDeepFactsService.PspFact(At, () => ("", false));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    [Fact]
    public void manifest_未取得傾印標未施測()
    {
        var f = FirmwareDeepFactsService.MeManifestFact(At, () => (0, ""));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("未取得", f.UnavailableReason);
    }

    [Fact]
    public void manifest_SVN是防降級()
    {
        var f = FirmwareDeepFactsService.MeManifestFact(At, () => (8, "3"));
        Assert.Contains("防降級", f.Value);
    }

    [Fact]
    public void PSP目錄_標未施測不以推測填位址()
    {
        var f = FirmwareDeepFactsService.PspDirFact(At, () => (5, 2));
        Assert.Contains("標未施測", f.Value);
        Assert.Contains("不以推測填位址", f.Value);
    }

    [Fact]
    public void 變數空間_異常需人判讀不修復()
    {
        var f = FirmwareDeepFactsService.VarStoreFact(At, () => (50, false));
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("不修復", f.Value);
    }

    [Fact]
    public void 變數空間_不刪不改()
    {
        var f = FirmwareDeepFactsService.VarStoreFact(At, () => (50, true));
        Assert.Contains("不刪不改", f.Value);
    }

    [Fact]
    public void Runtime_無表N_A如實()
    {
        var f = FirmwareDeepFactsService.RtFact(At, () => (0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("N/A 如實", f.UnavailableReason);
    }

    [Fact]
    public void Runtime_指標落點異常是訊號()
    {
        var f = FirmwareDeepFactsService.RtFact(At, () => (10, false));
        Assert.Contains("值得留意的訊號", f.Value);
    }

    [Fact]
    public void TCG階段_邊界誤判率聲明()
    {
        var f = FirmwareDeepFactsService.TcgPhaseFact(At, () => (5, 20, 30));
        Assert.Contains("邊界誤判率聲明", f.Value);
        Assert.Contains("近似", f.Value);
        Assert.Equal(55.0, f.NumericValue);
    }

    [Fact]
    public void FV_統計非安全值()
    {
        var f = FirmwareDeepFactsService.FvFact(At, () => (3, 42.5));
        Assert.Contains("統計非安全值", f.Value);
    }

    [Fact]
    public void 微碼鏈_缺一源即明說()
    {
        var f = FirmwareDeepFactsService.UcodeWinnerFact(At, () => ("", false));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("缺一不判", f.UnavailableReason);
    }

    [Fact]
    public void 微碼鏈_齊全才宣稱勝出()
    {
        var f = FirmwareDeepFactsService.UcodeWinnerFact(At, () => ("OS update", true));
        Assert.Contains("缺一源即明說", f.Value);
    }

    [Fact]
    public void BootGuardACM_不可讀是檢索級聲明()
    {
        var f = FirmwareDeepFactsService.BgAcmFact(At, () => ("", false));
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("檢索級聲明", f.UnavailableReason);
    }

    [Fact]
    public void ACPI對照_應有需SpecRef級源()
    {
        var f = FirmwareDeepFactsService.AcpiMissingFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("SpecRef 級來源", f.UnavailableReason);
    }

    [Fact]
    public void FPDT_缺席N_A()
    {
        var f = FirmwareDeepFactsService.FpdtFact(At, () => (0, false));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("缺席＝N/A", f.UnavailableReason);
    }

    [Fact]
    public void 變數屬性_與FW017互補不重複()
    {
        var f = FirmwareDeepFactsService.VarAttrFact(At, () => (30, 5, 2));
        Assert.Contains("互補不重複", f.Value);
        Assert.Equal(37.0, f.NumericValue);
    }

    [Fact]
    public void SBAT_表過期明說()
    {
        var f = FirmwareDeepFactsService.DbxSbatFact(At, () => ("202401", true));
        Assert.Contains("表過期明說", f.Value);
    }

    [Fact]
    public void WDAC層級_判讀不執行()
    {
        var f = FirmwareDeepFactsService.WdacLayerFact(At, () => ("Firmware", 3));
        Assert.Contains("判讀不執行", f.Value);
        Assert.Contains("不套用或修改政策", f.Value);
    }

    [Fact]
    public void 本批十六把鍵齊()
    {
        var facts = FirmwareDeepFactsService.Collect(At);
        string[] keys =
        [
            FirmwareDeepFactsService.UcodeCveKey, FirmwareDeepFactsService.MeKey, FirmwareDeepFactsService.PspKey,
            FirmwareDeepFactsService.MeManifestKey, FirmwareDeepFactsService.PspDirKey, FirmwareDeepFactsService.VarStoreKey,
            FirmwareDeepFactsService.RtKey, FirmwareDeepFactsService.TcgPhaseKey, FirmwareDeepFactsService.FvKey,
            FirmwareDeepFactsService.UcodeWinnerKey, FirmwareDeepFactsService.BgAcmKey, FirmwareDeepFactsService.AcpiMissingKey,
            FirmwareDeepFactsService.FpdtKey, FirmwareDeepFactsService.VarAttrKey, FirmwareDeepFactsService.DbxSbatKey,
            FirmwareDeepFactsService.WdacLayerKey,
        ];
        foreach (string k in keys) Assert.Contains(facts, f => f.Key == k);
        Assert.Equal(16, facts.Count);
        Assert.Equal(16, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
    }
}
