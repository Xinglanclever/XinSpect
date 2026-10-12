using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 153–162 項：環境／顯示／診斷補充的守門。
/// 紅線：讀不到不冒充 0；0 是值；不列完整本機路徑；不開啟裝置、不啟停服務、零網路。
/// </summary>
public class EnvironmentDiagnosticsFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);
    private static PowerPolicyRow Row(string name, string value) => new() { Name = name, Value = value };

    private static HardwareFact Fact(string key, FactAvailability availability = FactAvailability.Present, string reason = "")
        => new(key, "測試域", key, availability == FactAvailability.Present ? "v" : "", "", "測試",
            FactTrustLevel.Reported, false, At, null, availability, availability == FactAvailability.Present ? null : reason);

    // ── PW-003 睡眠能力 ─────────────────────────────────────────────────────

    [Fact]
    public void 睡眠能力_三態可用_說明現代待命界線()
    {
        var f = EnvironmentDiagnosticsFactsService.SleepFact(At, () =>
        [
            Row("S1（掛起，時脈停止）", "支援"),
            Row("S3（待命，記憶體保持）", "不支援"),
            Row("S4（休眠，寫入磁碟）", "支援"),
        ]);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("S1 支援", f.Value);
        Assert.Contains("S3 不支援", f.Value);
        Assert.Contains("S4 支援", f.Value);
        Assert.Contains("現代待命", f.Value);
    }

    [Fact]
    public void 睡眠能力_通路失敗_ReadError()
    {
        var f = EnvironmentDiagnosticsFactsService.SleepFact(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 睡眠能力_沒有回報列_NotSupported()
    {
        var f = EnvironmentDiagnosticsFactsService.SleepFact(At, () => []);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
    }

    // ── DV-001 WSL ──────────────────────────────────────────────────────────

    [Fact]
    public void WSL_一個發行版是值()
    {
        var f = EnvironmentDiagnosticsFactsService.WslFact(At, () => 1);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("1 個", f.Value);
        Assert.Contains("不列發行版名稱或路徑", f.Value);
    }

    [Fact]
    public void WSL_零個也是值()
    {
        var f = EnvironmentDiagnosticsFactsService.WslFact(At, () => 0);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0 是值", f.Value);
    }

    [Fact]
    public void WSL_沒有登錄記錄_不讀成未安裝()
    {
        var f = EnvironmentDiagnosticsFactsService.WslFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不把未安裝當成 0", f.UnavailableReason);
    }

    // ── DV-004 PATH ─────────────────────────────────────────────────────────

    [Fact]
    public void PATH_重複只報計數不列路徑()
    {
        var f = EnvironmentDiagnosticsFactsService.PathFact(At, name => name == "PATH" ? @"C:\x;C:\y" : @"C:\y;C:\z");
        Assert.Contains("4 段", f.Value);
        Assert.Contains("重複", f.Value);
        Assert.DoesNotContain("C:\\x", f.Value);
    }

    [Fact]
    public void PATH_缺字串是讀不到不是0()
    {
        var f = EnvironmentDiagnosticsFactsService.PathFact(At, _ => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DV-007 容器 ────────────────────────────────────────────────────────

    [Fact]
    public void 容器_服務狀態分開陳述()
    {
        var f = EnvironmentDiagnosticsFactsService.ContainerFact(At, () =>
        [
            new("docker", "Running"), new("containerd", "Stopped"),
        ]);
        Assert.Contains("2 個", f.Value);
        Assert.Contains("1 個執行中", f.Value);
        Assert.Contains("docker:Running", f.Value);
    }

    [Fact]
    public void 容器_零個服務也是值()
    {
        var f = EnvironmentDiagnosticsFactsService.ContainerFact(At, () => []);
        Assert.Contains("0 個", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
    }

    [Fact]
    public void 容器_列舉失敗_ReadError()
    {
        var f = EnvironmentDiagnosticsFactsService.ContainerFact(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── DV-008 工具鏈 ──────────────────────────────────────────────────────

    [Fact]
    public void 工具鏈_找到的版本逐一列出()
    {
        var f = EnvironmentDiagnosticsFactsService.ToolchainFact(At, () =>
        [
            new("dotnet", "10.0.1"), new("git", "2.50.0"),
        ]);
        Assert.Contains("dotnet 10.0.1", f.Value);
        Assert.Contains("git 2.50.0", f.Value);
        Assert.Contains("不代表專案使用該版本", f.Value);
    }

    [Fact]
    public void 工具鏈_零個是值()
    {
        var f = EnvironmentDiagnosticsFactsService.ToolchainFact(At, () => []);
        Assert.Contains("0 是值", f.Value);
        Assert.Equal(FactAvailability.Present, f.Availability);
    }

    [Fact]
    public void 工具鏈_通路失敗_ReadError()
    {
        var f = EnvironmentDiagnosticsFactsService.ToolchainFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── GD-005 著色器快取 ─────────────────────────────────────────────────

    [Fact]
    public void 著色器快取_有目錄時報數量與MiB()
    {
        var f = EnvironmentDiagnosticsFactsService.ShaderCacheFact(At, () => new(2, 3, 1024 * 1024 * 2));
        Assert.Contains("2 個", f.Value);
        Assert.Contains("3 個檔案", f.Value);
        Assert.Contains("2.0 MiB", f.Value);
    }

    [Fact]
    public void 著色器快取_沒有目錄也是值()
    {
        var f = EnvironmentDiagnosticsFactsService.ShaderCacheFact(At, () => new(0, 0, 0));
        Assert.Contains("沒有找到", f.Value);
    }

    [Fact]
    public void 著色器快取_失敗_ReadError()
    {
        var f = EnvironmentDiagnosticsFactsService.ShaderCacheFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── GD-006 HDR ─────────────────────────────────────────────────────────

    [Fact]
    public void HDR_能力與開關合併呈現但不判畫質()
    {
        var f = EnvironmentDiagnosticsFactsService.HdrFact(At, () =>
        [
            new("Windows HDR", "已啟用", "正常"),
            new("HDR 支援", "是", "正常"),
        ]);
        Assert.Contains("Windows HDR=已啟用", f.Value);
        Assert.Contains("HDR 支援=是", f.Value);
        Assert.Contains("不代為判定畫質", f.Value);
    }

    [Fact]
    public void HDR_沒有列_ReadError()
    {
        var f = EnvironmentDiagnosticsFactsService.HdrFact(At, () => []);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── GD-008 繪圖板 ──────────────────────────────────────────────────────

    [Fact]
    public void 繪圖板_0個是不適用且講明原因()
    {
        var f = EnvironmentDiagnosticsFactsService.TabletFact(At, () => 0);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 個", f.Value);
        Assert.Contains("0 是值", f.UnavailableReason);
    }

    [Fact]
    public void 繪圖板_2個是Present()
    {
        var f = EnvironmentDiagnosticsFactsService.TabletFact(At, () => 2);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("2 個", f.Value);
    }

    [Fact]
    public void 繪圖板_列舉失敗_ReadError()
    {
        var f = EnvironmentDiagnosticsFactsService.TabletFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── SC-007 uptime ──────────────────────────────────────────────────────

    [Fact]
    public void Uptime_以天數彙總且不宣稱更新已套用()
    {
        var f = EnvironmentDiagnosticsFactsService.UptimeFact(At, () => 3L * 24 * 60 * 60 * 1000 + 2L * 60 * 60 * 1000);
        Assert.Contains("3 天", f.Value);
        Assert.Contains("2 小時", f.Value);
        Assert.Contains("不等於更新是否已套用", f.Value);
    }

    [Fact]
    public void Uptime_負值是讀不到_不是剛開機()
    {
        var f = EnvironmentDiagnosticsFactsService.UptimeFact(At, () => -1);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── SC-009 網路事件 ─────────────────────────────────────────────────────

    [Fact]
    public void 網路中斷_只計事件不做根因判決()
    {
        var f = EnvironmentDiagnosticsFactsService.NetDropFact(At, () =>
        [
            new(At.AddHours(-1), 10001),
            new(At.AddHours(-2), 4202),
        ]);
        Assert.Contains("2 筆", f.Value);
        Assert.Contains("不等於根因", f.Value);
    }

    [Fact]
    public void 網路中斷_事件零筆也是值()
    {
        var f = EnvironmentDiagnosticsFactsService.NetDropFact(At, () => []);
        Assert.Contains("0 筆", f.Value);
    }

    [Fact]
    public void 網路中斷_頻道不可用_NotSupported()
    {
        var f = EnvironmentDiagnosticsFactsService.NetDropFact(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不猜", f.UnavailableReason);
    }

    // ── 整組契約 ────────────────────────────────────────────────────────────

    [Fact]
    public void 本批十把鍵齊()
    {
        var facts = EnvironmentDiagnosticsFactsService.Collect(At);
        string[] expected =
        [
            EnvironmentDiagnosticsFactsService.SleepKey, EnvironmentDiagnosticsFactsService.WslKey,
            EnvironmentDiagnosticsFactsService.PathKey, EnvironmentDiagnosticsFactsService.ContainerKey,
            EnvironmentDiagnosticsFactsService.ToolchainKey, EnvironmentDiagnosticsFactsService.ShaderCacheKey,
            EnvironmentDiagnosticsFactsService.HdrKey, EnvironmentDiagnosticsFactsService.TabletKey,
            EnvironmentDiagnosticsFactsService.UptimeKey, EnvironmentDiagnosticsFactsService.NetDropKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
        Assert.Equal(10, facts.Count);
    }
}
