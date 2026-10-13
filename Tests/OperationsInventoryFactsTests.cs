using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 173–182 項守門：全部唯讀、三態、0 是值；不列路徑／名稱／MAC／IP，不啟停服務或記錄器。
/// </summary>
public class OperationsInventoryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);

    // ── NW-006 虛擬網卡 ─────────────────────────────────────────────────────

    [Fact]
    public void 虛擬網卡_分類計數不列名稱()
    {
        var f = OperationsInventoryFactsService.VirtualAdapterFact(At,
            () => ["Hyper-V Virtual Ethernet Adapter", "TAP-Windows Adapter V9", "Intel Ethernet"]);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("3 個", f.Value);
        Assert.Contains("Hyper-V 1", f.Value);
        Assert.Contains("不列網卡名稱、MAC 或 IP", f.Value);
        Assert.DoesNotContain("TAP-Windows", f.Value);
    }

    [Fact]
    public void 虛擬網卡_零個是值()
    {
        var f = OperationsInventoryFactsService.VirtualAdapterFact(At, () => []);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 個", f.Value);
    }

    [Fact]
    public void 虛擬網卡_列舉失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.VirtualAdapterFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── NW-022 LPT／COM ────────────────────────────────────────────────────

    [Fact]
    public void 舊式埠_只報類別計數不列裝置()
    {
        var f = OperationsInventoryFactsService.LegacyPortFact(At,
            () => new Dictionary<string, string> { ["Device1"] = "COM1", ["Device2"] = "LPT1" });
        Assert.Contains("舊式連接埠 2 個", f.Value);
        Assert.Contains("COM 1", f.Value);
        Assert.Contains("LPT 1", f.Value);
        Assert.DoesNotContain("Device1", f.Value);
    }

    [Fact]
    public void 舊式埠_沒有登錄是NotApplicable()
    {
        var f = OperationsInventoryFactsService.LegacyPortFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 舊式埠_空對映_0是值()
    {
        var f = OperationsInventoryFactsService.LegacyPortFact(At, () => new Dictionary<string, string>());
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 個", f.Value);
    }

    // ── WS-002 服務依賴圖 ─────────────────────────────────────────────────
    [Fact]
    public void 服務依賴_計算層數與未對應依賴()
    {
        var f = OperationsInventoryFactsService.ServiceGraphFact(At,
            () =>
            [
                new("a", "Running", ["b"]),
                new("b", "Stopped", ["missing"]),
            ]);
        Assert.Contains("服務 2 個", f.Value);
        Assert.Contains("依賴邊 2 條", f.Value);
        Assert.Contains("最深鏈 2 層", f.Value);
        Assert.Contains("未對應依賴 1 個", f.Value);
        Assert.DoesNotContain("a", f.Value);
    }

    [Fact]
    public void 服務依賴_循環不無限遞迴()
    {
        var f = OperationsInventoryFactsService.ServiceGraphFact(At,
            () => [new("a", "Running", ["b"]), new("b", "Running", ["a"])]);
        Assert.Contains("最深鏈 2 層", f.Value);
    }

    [Fact]
    public void 服務依賴_列舉失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.ServiceGraphFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── WS-007 傾印設定 ───────────────────────────────────────────────────

    [Fact]
    public void 傾印設定_只審計且不揭露路徑()
    {
        var f = OperationsInventoryFactsService.DumpConfigFact(At, () => new(true, 2, true, 1));
        Assert.Contains("核心傾印", f.Value);
        Assert.Contains("自動重啟 是", f.Value);
        Assert.Contains("只審計不調整", f.Value);
        Assert.DoesNotContain(":\\", f.Value);
    }

    [Fact]
    public void 傾印設定_缺鍵_NotApplicable不代填()
    {
        var f = OperationsInventoryFactsService.DumpConfigFact(At, () => new(false, null, null, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不代填 Windows 預設", f.UnavailableReason);
    }

    [Fact]
    public void 傾印設定_未知原始值不猜()
    {
        var f = OperationsInventoryFactsService.DumpConfigFact(At, () => new(true, 99, null, 0));
        Assert.Contains("未知", f.Value);
        Assert.Contains("原始值 99", f.Value);
    }

    // ── MS-012 NTFS ───────────────────────────────────────────────────────

    [Fact]
    public void NTFS特性_只報設定原值()
    {
        var f = OperationsInventoryFactsService.NtfsAuditFact(At, () => new(3, 1, 2));
        Assert.Contains("NTFS 卷冊 3 個", f.Value);
        Assert.Contains("8.3 短檔名設定 1", f.Value);
        Assert.Contains("不做效能或安全判決", f.Value);
    }

    [Fact]
    public void NTFS特性_零卷_NotApplicable()
    {
        var f = OperationsInventoryFactsService.NtfsAuditFact(At, () => new(0, null, null));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void NTFS特性_讀取失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.NtfsAuditFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }
    // ── ST-016 實體磁碟交叉 ───────────────────────────────────────────────

    [Fact]
    public void 磁碟交叉_對上時明說對上()
    {
        var f = OperationsInventoryFactsService.PhysicalDiskCrossFact(At,
            () => new([1000L, 2000L, 2000L], [2000L, 1000L, 2000L]));
        Assert.Contains("容量匹配 3", f.Value);
        Assert.Contains("完全對上", f.Value);
        Assert.Contains("不自動判定", f.Value);
    }

    [Fact]
    public void 磁碟交叉_差集不判誰正確()
    {
        var f = OperationsInventoryFactsService.PhysicalDiskCrossFact(At,
            () => new([1000L, 2000L], [1000L, 3000L]));
        Assert.Contains("只在 Win32 1", f.Value);
        Assert.Contains("只在 Storage 1", f.Value);
        Assert.Contains("不自動判定哪個來源正確", f.Value);
    }

    [Fact]
    public void 磁碟交叉_列舉失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.PhysicalDiskCrossFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── ST-019 舊式儲存 ───────────────────────────────────────────────────

    [Fact]
    public void 舊式儲存_只報類別數量()
    {
        var f = OperationsInventoryFactsService.LegacyStorageFact(At, () => new(1, 2));
        Assert.Contains("光碟機 1 台", f.Value);
        Assert.Contains("軟碟機 2 台", f.Value);
        Assert.Contains("不讀光碟內容", f.Value);
    }

    [Fact]
    public void 舊式儲存_零台是值()
    {
        var f = OperationsInventoryFactsService.LegacyStorageFact(At, () => new(0, 0));
        Assert.Contains("0 是值", f.Value);
    }

    [Fact]
    public void 舊式儲存_列舉失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.LegacyStorageFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── PV-001 遙測層級 ───────────────────────────────────────────────────

    [Fact]
    public void 遙測_只報政策字面值()
    {
        var f = OperationsInventoryFactsService.TelemetryFact(At, () => 1);
        Assert.Contains("基本（1）", f.Value);
        Assert.Contains("不判隱私好壞", f.Value);
    }

    [Fact]
    public void 遙測_未設定_NotApplicable不代填預設()
    {
        var f = OperationsInventoryFactsService.TelemetryFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不代填", f.UnavailableReason);
    }

    [Fact]
    public void 遙測_未知值不猜()
    {
        var f = OperationsInventoryFactsService.TelemetryFact(At, () => 42);
        Assert.Contains("未知", f.Value);
        Assert.Contains("42", f.Value);
    }

    // ── TM-004 時區／DST ──────────────────────────────────────────────────

    [Fact]
    public void 時區_不冒充tzdb版本()
    {
        var f = OperationsInventoryFactsService.TimeZoneFact(At,
            () => new("Taipei Standard Time", "台北", TimeSpan.FromHours(8), false, "10.0.26100.1"));
        Assert.Contains("Taipei Standard Time", f.Value);
        Assert.Contains("UTC+08:00", f.Value);
        Assert.Contains("不冒充 tzdb 版本", f.Value);
    }

    [Fact]
    public void 時區_tzres讀不到如實說()
    {
        var f = OperationsInventoryFactsService.TimeZoneFact(At,
            () => new("UTC", "UTC", TimeSpan.Zero, false, null));
        Assert.Contains("未回報", f.Value);
    }

    [Fact]
    public void 時區_讀取失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.TimeZoneFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── PV-003 診斷記錄器 ─────────────────────────────────────────────────

    [Fact]
    public void 診斷記錄器_只讀狀態不啟停()
    {
        var f = OperationsInventoryFactsService.DiagnosticViewerFact(At, () => new(10, 3, 2));
        Assert.Contains("自動記錄器 10 個", f.Value);
        Assert.Contains("啟用 3", f.Value);
        Assert.Contains("不啟停記錄器", f.Value);
    }

    [Fact]
    public void 診斷記錄器_零個_NotApplicable()
    {
        var f = OperationsInventoryFactsService.DiagnosticViewerFact(At, () => new(0, 0, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    [Fact]
    public void 診斷記錄器_讀取失敗_ReadError()
    {
        var f = OperationsInventoryFactsService.DiagnosticViewerFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 整組契約 ──────────────────────────────────────────────────────────

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = OperationsInventoryFactsService.Collect(At);
        string[] expected =
        [
            OperationsInventoryFactsService.VirtualAdapterKey,
            OperationsInventoryFactsService.LegacyPortKey,
            OperationsInventoryFactsService.ServiceGraphKey,
            OperationsInventoryFactsService.DumpConfigKey,
            OperationsInventoryFactsService.NtfsAuditKey,
            OperationsInventoryFactsService.PhysicalDiskCrossKey,
            OperationsInventoryFactsService.LegacyStorageKey,
            OperationsInventoryFactsService.TelemetryKey,
            OperationsInventoryFactsService.TimeZoneKey,
            OperationsInventoryFactsService.DiagnosticViewerKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
        Assert.Equal(10, facts.Count);
    }
}