using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 163–172 項的守門：唯讀、三態、0 是值；不讀序號／UUID／路徑，不開音訊或顯示裝置。
/// </summary>
public class AdvancedSupplementFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);

    // ── CM-005 快取共享拓撲 ─────────────────────────────────────────────────

    [Fact]
    public void 快取共享_回報共享上界且不冒充核心數()
    {
        var f = AdvancedSupplementFactsService.CacheShareFact(At, () =>
        [
            new("L1 資料", 48 * 1024, 2, false),
            new("L3 統一", 32L * 1024 * 1024, 64, true),
        ]);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("L1 資料", f.Value);
        Assert.Contains("最多 64 邏輯處理器", f.Value);
        Assert.Contains("不是實際核心數", f.Value);
    }

    [Fact]
    public void 快取共享_零子葉是不適用_不是讀錯()
    {
        var f = AdvancedSupplementFactsService.CacheShareFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 是值", f.UnavailableReason);
    }

    [Fact]
    public void 快取共享_CPUID不可用_NotSupported()
    {
        var f = AdvancedSupplementFactsService.CacheShareFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    // ── DV-002 巢狀虛擬化能力 ─────────────────────────────────────────────

    [Fact]
    public void 巢狀虛擬化_保留未回報而不推算()
    {
        var f = AdvancedSupplementFactsService.NestedVirtualizationFact(At,
            () => new(true, false, "未回報", -1));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("Hypervisor 存在=是", f.Value);
        Assert.Contains("韌體虛擬化=未啟用", f.Value);
        Assert.Contains("不宣稱來賓 VM 一定能巢狀執行", f.Value);
    }

    [Fact]
    public void 巢狀虛擬化_查詢失敗_ReadError()
    {
        var f = AdvancedSupplementFactsService.NestedVirtualizationFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DV-003 .NET／VC++ 執行階段 ────────────────────────────────────────

    [Fact]
    public void 執行階段_顯示版本但不宣稱專案使用()
    {
        var f = AdvancedSupplementFactsService.RuntimeStackFact(At,
            () => new Dictionary<string, string> { ["CLR"] = ".NET 10.0", [".NET Framework 4.x"] = "4.8.1" });
        Assert.Contains("CLR=.NET 10.0", f.Value);
        Assert.Contains("不代表專案", f.Value);
    }

    [Fact]
    public void 執行階段_空登錄是不適用_0是值()
    {
        var f = AdvancedSupplementFactsService.RuntimeStackFact(At, () => new Dictionary<string, string>());
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 是值", f.UnavailableReason);
    }

    [Fact]
    public void 執行階段_讀取失敗_ReadError()
    {
        var f = AdvancedSupplementFactsService.RuntimeStackFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── PW-008 排程喚醒對照 ───────────────────────────────────────────────

    [Fact]
    public void 排程喚醒_只計WakeToRun且不列名稱()
    {
        const string xml = "<Tasks><Task><Triggers><WakeToRun>true</WakeToRun></Triggers><Actions><Exec><Command>C:\\secret.exe</Command></Exec></Actions></Task><Task><Triggers><WakeToRun>false</WakeToRun></Triggers></Task></Tasks>";
        var f = AdvancedSupplementFactsService.WakeTaskFact(At, () => xml);
        Assert.Contains("WakeToRun=true 的排程工作 1 個", f.Value);
        Assert.Contains("不列出任務名稱或命令", f.Value);
        Assert.DoesNotContain("secret.exe", f.Value);
    }

    [Fact]
    public void 排程喚醒_沒有喚醒任務_0是值()
    {
        var f = AdvancedSupplementFactsService.WakeTaskFact(At, () => "<Tasks />");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 個", f.Value);
    }

    [Fact]
    public void 排程喚醒_XML壞掉_不把解析失敗當成零()
    {
        var f = AdvancedSupplementFactsService.WakeTaskFact(At, () => "<Tasks");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("解析失敗", f.UnavailableReason);
    }

    // ── GD-004 多螢幕拓撲快照 ─────────────────────────────────────────────

    [Fact]
    public void 多螢幕拓撲_記排列與DPI不記序號()
    {
        var f = AdvancedSupplementFactsService.MonitorTopologyFact(At,
            () => [new(0, 0, 2560, 1440, 96, 96, true), new(2560, 0, 1920, 1080, 144, 144, false)]);
        Assert.Contains("2 個顯示器", f.Value);
        Assert.Contains("#1主 2560×1440@(0,0) 96/96 DPI", f.Value);
        Assert.Contains("不記序號", f.Value);
    }

    [Fact]
    public void 多螢幕拓撲_零個顯示器_NotApplicable()
    {
        var f = AdvancedSupplementFactsService.MonitorTopologyFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 多螢幕拓撲_列舉失敗_ReadError()
    {
        var f = AdvancedSupplementFactsService.MonitorTopologyFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DK-005 匿名 ID 穩定性 ─────────────────────────────────────────────

    [Fact]
    public void 匿名ID_大小寫與空白不造成漂移()
    {
        var a = AdvancedSupplementFactsService.AnonymousIdFact(At, () => "Acme | Model X | CPU 1");
        var b = AdvancedSupplementFactsService.AnonymousIdFact(At, () => "acme|model x|cpu 1");
        Assert.Equal(a.Value, b.Value);
        Assert.Contains("不用序號、UUID、產品金鑰或路徑", a.Value);
    }

    [Fact]
    public void 匿名ID_不得把原始識別字串放進值()
    {
        var f = AdvancedSupplementFactsService.AnonymousIdFact(At, () => "Acme|Model X|CPU 1");
        Assert.DoesNotContain("Acme", f.Value);
        Assert.DoesNotContain("Model X", f.Value);
    }

    [Fact]
    public void 匿名ID_來源不可用_ReadError()
    {
        var f = AdvancedSupplementFactsService.AnonymousIdFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DK-009 匯出／匯入往返 ─────────────────────────────────────────────

    [Fact]
    public void 往返_成功只留數量與digest()
    {
        var f = AdvancedSupplementFactsService.RoundTripFact(At,
            () => new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        Assert.Contains("往返 2 個鍵成功", f.Value);
        Assert.Contains("digest", f.Value);
        Assert.DoesNotContain("a=1", f.Value);
    }

    [Fact]
    public void 往返_空集合也是成功的值()
    {
        var f = AdvancedSupplementFactsService.RoundTripFact(At, () => new Dictionary<string, string>());
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 個鍵", f.Value);
    }

    [Fact]
    public void 往返_來源失敗_ReadError()
    {
        var f = AdvancedSupplementFactsService.RoundTripFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── SC-004 誰在動硬碟 ─────────────────────────────────────────────────

    [Fact]
    public void 磁碟IO_聚合計數且不判根因()
    {
        var f = AdvancedSupplementFactsService.DiskIoFact(At,
            () => [new("a", 1024, 2048), new("b", 4096, 0)]);
        Assert.Contains("2 筆", f.Value);
        Assert.Contains("累計讀", f.Value);
        Assert.Contains("不把單一程序當根因", f.Value);
    }

    [Fact]
    public void 磁碟IO_空樣本_0是值()
    {
        var f = AdvancedSupplementFactsService.DiskIoFact(At, () => []);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 筆", f.Value);
    }

    [Fact]
    public void 磁碟IO_計數器不可用_ReadError()
    {
        var f = AdvancedSupplementFactsService.DiskIoFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── MU-003 DRM 就讀 ───────────────────────────────────────────────────

    [Fact]
    public void DRM_已登錄只陳述元件存在()
    {
        var f = AdvancedSupplementFactsService.DrmFact(At, () => new(true, "4.6"));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("PlayReady 已登錄", f.Value);
        Assert.Contains("不讀授權或內容金鑰", f.Value);
    }

    [Fact]
    public void DRM_未登錄_NotApplicable且0是值()
    {
        var f = AdvancedSupplementFactsService.DrmFact(At, () => new(false, null));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 是值", f.UnavailableReason);
    }

    [Fact]
    public void DRM_讀取失敗_ReadError()
    {
        var f = AdvancedSupplementFactsService.DrmFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── MU-006 空間音訊 ───────────────────────────────────────────────────

    [Fact]
    public void 空間音訊_只讀端點宣告()
    {
        var f = AdvancedSupplementFactsService.SpatialAudioFact(At, () => new(3, 1));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("端點 3 個", f.Value);
        Assert.Contains("不開音訊裝置", f.Value);
    }

    [Fact]
    public void 空間音訊_沒有端點_NotApplicable()
    {
        var f = AdvancedSupplementFactsService.SpatialAudioFact(At, () => new(0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 是值", f.UnavailableReason);
    }

    [Fact]
    public void 空間音訊_讀取失敗_ReadError()
    {
        var f = AdvancedSupplementFactsService.SpatialAudioFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 整組契約 ──────────────────────────────────────────────────────────

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = AdvancedSupplementFactsService.Collect(At);
        string[] expected =
        [
            AdvancedSupplementFactsService.CacheShareKey,
            AdvancedSupplementFactsService.NestedVirtualizationKey,
            AdvancedSupplementFactsService.RuntimeStackKey,
            AdvancedSupplementFactsService.WakeTaskKey,
            AdvancedSupplementFactsService.MonitorTopologyKey,
            AdvancedSupplementFactsService.AnonymousIdKey,
            AdvancedSupplementFactsService.RoundTripKey,
            AdvancedSupplementFactsService.DiskIoKey,
            AdvancedSupplementFactsService.SpatialAudioKey,
            AdvancedSupplementFactsService.DrmKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
        Assert.Equal(10, facts.Count);
    }
}