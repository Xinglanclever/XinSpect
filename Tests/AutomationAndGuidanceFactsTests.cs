using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Vol2 第 133–142 項（自動化、開機／記憶體引導、硬解、Sysmon 對照）的守門。
/// 紅線：遠端查詢明文不做；stdin 批次不啟動新掃描；YAML 不執行檢查；程序記憶摘要不揭露路徑。
/// </summary>
public class AutomationAndGuidanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 0, 30, 0, TimeSpan.Zero);

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinauto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HardwareFact Fact(string key, FactAvailability availability = FactAvailability.Present, string reason = "")
        => new(key, "測試域", key, availability == FactAvailability.Present ? "v" : "", "", "測試",
            FactTrustLevel.Reported, false, At, null, availability, availability == FactAvailability.Present ? null : reason);

    // ── AG-001 headless ─────────────────────────────────────────────────────

    [Fact]
    public void headless模式目錄_從CLI常數產生_涵蓋stdin批次()
    {
        string json = AutomationAndGuidanceFactsService.GenerateHeadlessModesJson();
        var doc = JsonDocument.Parse(json);

        // 8 → 9（2026-10-25 第 272 輪）：--diff-report（U10-PF-0002 報告鍵級對照）進 headless 目錄。
        Assert.Equal(9, doc.RootElement.GetArrayLength());
        Assert.Contains("--json", json, StringComparison.Ordinal);
        Assert.Contains(CliService.StdinBatchArg, json, StringComparison.Ordinal);
        Assert.Contains("零網路", json, StringComparison.Ordinal);
    }

    [Fact]
    public void headless模式目錄_本機命令面_不是遠端API()
    {
        var fact = AutomationAndGuidanceFactsService.Collect(At).Single(f => f.Key == AutomationAndGuidanceFactsService.HeadlessKey);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("不是遠端 API", fact.Value);
    }

    // ── AG-002 YAML 組合 ───────────────────────────────────────────────────

    [Fact]
    public void YAML組合_checks去重_exclude移除_非法項目進錯誤()
    {
        string json = AutomationAndGuidanceFactsService.ComposeChecklists([
            "checks:\n  - id: cpu.topology\n  - id: pcie.aer\n  - id: cpu.topology\nexclude:\n  - id: pcie.aer\n",
            "checks:\n  - notid: bad\n",
        ]);
        var doc = JsonDocument.Parse(json);

        Assert.Equal(["cpu.topology"], doc.RootElement.GetProperty("included").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["cpu.topology"], doc.RootElement.GetProperty("duplicates").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("項目必須是", doc.RootElement.GetProperty("errors")[0].GetString());
        Assert.DoesNotContain("notid", doc.RootElement.GetProperty("included").ToString());
        Assert.Contains("不執行檢查", doc.RootElement.GetProperty("boundary").GetString());
    }

    [Fact]
    public void YAML子集_只接受小寫id_不猜未知語法()
    {
        string json = AutomationAndGuidanceFactsService.ComposeChecklists(["checks:\n  - id: BAD ID\n"]);
        var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("included").EnumerateArray());
        Assert.Contains("非法 id", doc.RootElement.GetProperty("errors")[0].GetString());
    }

    // ── AG-003 退出碼 ───────────────────────────────────────────────────────

    [Fact]
    public void 退出碼對照_0_1_2_三語意清楚()
    {
        string json = AutomationAndGuidanceFactsService.BuildExitCodeMapJson();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Contains("所有輸出事實都是 Present", json);
        Assert.Contains("致命錯誤", json);
        Assert.Contains("部分", json);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── AG-004 stdin batch ──────────────────────────────────────────────────

    [Fact]
    public void stdin批次_每行一個前綴_輸出JSONL並回0()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var stdin = new StringReader("cpu.\nplatform.\n");
        int code = CliService.RunStdinBatch(() =>
        [
            new HardwareFact("cpu.name", "CPU", "CPU", "Test", "", "s", FactTrustLevel.Reported, false, At),
            new HardwareFact("platform.boot", "平台", "boot", "0", "", "s", FactTrustLevel.Reported, false, At),
            Fact("irrelevant"),
        ], stdin, stdout, stderr);

        string[] lines = stdout.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"query\":\"cpu.\"", lines[0]);
        Assert.Contains("\"query\":\"platform.\"", lines[1]);
    }

    [Fact]
    public void stdin批次_沒命中仍輸出空列並回2()
    {
        var stdout = new StringWriter();
        int code = CliService.RunStdinBatch(() => [Fact("cpu.name")], new StringReader("zzz.\n"), stdout, new StringWriter());
        Assert.Equal(CliService.ExitPartial, code);
        Assert.Contains("\"count\":0", stdout.ToString());
    }

    [Fact]
    public void stdin批次_空輸入不假裝成功()
    {
        int code = CliService.RunStdinBatch(() => [Fact("cpu.name")], new StringReader(""), new StringWriter(), new StringWriter());
        Assert.Equal(CliService.ExitPartial, code);
    }

    [Fact]
    public void stdin批次_沒有stdin是致命_不自動開網路()
    {
        var stderr = new StringWriter();
        int code = CliService.RunStdinBatch(() => [], null, new StringWriter(), stderr);
        Assert.Equal(CliService.ExitError, code);
        Assert.Contains("stdin 不可用", stderr.ToString());
    }

    // ── AG-006 輪轉 ─────────────────────────────────────────────────────────

    [Fact]
    public void 輸出輪轉_保留兩份_本次進1前一份進2()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "report.json");
        File.WriteAllText(path + ".2", "old");
        File.WriteAllText(path + ".1", "prev");
        File.WriteAllText(path, "current");
        try
        {
            Assert.True(AutomationAndGuidanceFactsService.RotateOutput(path, keepBackups: 2));
            Assert.Equal("current", File.ReadAllText(path + ".1"));
            Assert.Equal("prev", File.ReadAllText(path + ".2"));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 輸出輪轉_缺檔只是沒有可輪轉_不建立目錄()
    {
        string dir = TempDir();
        string missing = Path.Combine(dir, "nope.json");
        try
        {
            Assert.False(AutomationAndGuidanceFactsService.RotateOutput(missing, 5));
            Assert.False(File.Exists(missing + ".1"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 輸出輪轉_keep小於1直接拒絕()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AutomationAndGuidanceFactsService.RotateOutput("x", 0));
    }

    // ── AG-007 遠端查詢不做 ────────────────────────────────────────────────

    [Fact]
    public void 遠端查詢_明文不做_三態標NotSupported()
    {
        var f = AutomationAndGuidanceFactsService.Collect(At).Single(x => x.Key == AutomationAndGuidanceFactsService.RemoteKey);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("零網路", f.UnavailableReason);
        Assert.Contains("监听端口", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    // ── SG-001 開機引導 ─────────────────────────────────────────────────────

    [Fact]
    public void 開機引導_事件有時間就報秒_啟動項0是值()
    {
        var f = AutomationAndGuidanceFactsService.BootFact(At, () => 12345u, () => 0);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("12.3 秒", f.Value);
        Assert.Contains("約 0 筆", f.Value);
        Assert.Contains("不是診斷", f.Value);
    }

    [Fact]
    public void 開機引導_兩路都讀不到是ReadError不是0秒()
    {
        var f = AutomationAndGuidanceFactsService.BootFact(At, () => throw new InvalidOperationException("x"), () => throw new IOException("y"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("讀不到就是不猜", f.UnavailableReason);
    }

    [Fact]
    public void 開機引導_沒有事件且登錄可讀時標不適用()
    {
        var f = AutomationAndGuidanceFactsService.BootFact(At, () => null, () => 3);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("0.0 秒", f.Value);
        Assert.Contains("約 3 筆", f.Value);
    }

    // ── SG-003 記憶體摘要 ───────────────────────────────────────────────────

    [Fact]
    public void 記憶體摘要_同名程序加總_只列前幾名不列路徑()
    {
        string text = AutomationAndGuidanceFactsService.SummarizeProcessMemory([
            new("Code", 10), new("Code", 5), new("Browser", 100),
        ], top: 2);
        Assert.Contains("Browser 100.0 MiB", text);
        Assert.Contains("Code 15.0 MiB", text);
        Assert.DoesNotContain("C:\\", text);
    }

    [Fact]
    public void 記憶體摘要_零樣本是值不是錯誤()
    {
        var f = AutomationAndGuidanceFactsService.MemoryFact(At, () => []);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有可列舉", f.UnavailableReason);
    }

    [Fact]
    public void 記憶體摘要_列舉失敗ReadError不以0冒充()
    {
        var f = AutomationAndGuidanceFactsService.MemoryFact(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── MU-002 HEVC/AV1 ─────────────────────────────────────────────────────

    [Fact]
    public void 硬解摘要_只計HEVC_AV1_profile且明說非實測速度()
    {
        string text = AutomationAndGuidanceFactsService.BuildHardwareDecodeSummary([
            ("GPU", ["HEVC Main", "AV1 Profile0", "H.264"])
        ]);
        Assert.Contains("HEVC 解碼 profile 1 筆", text);
        Assert.Contains("AV1 解碼 profile 1 筆", text);
        Assert.Contains("不等於實測播放", text);
    }

    [Fact]
    public void 硬解摘要_0筆是值不是缺席()
    {
        string text = AutomationAndGuidanceFactsService.BuildHardwareDecodeSummary([]);
        Assert.Contains("0 筆", text);
    }

    [Fact]
    public void 硬解探測失敗_ReadError不猜()
    {
        var f = AutomationAndGuidanceFactsService.HardwareDecodeFact(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── XT-007 Sysmon ───────────────────────────────────────────────────────

    [Fact]
    public void Sysmon對照_包含常用EventID且未實作不假裝()
    {
        string json = AutomationAndGuidanceFactsService.BuildSysmonEventMap();
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(x => x.GetProperty("sysmonEventId").GetInt32()).ToHashSet();
        Assert.Contains(1, ids);
        Assert.Contains(11, ids);
        Assert.Contains(22, ids);
        Assert.Contains("未實作", json);
    }

    [Fact]
    public void Sysmon對照_不讀Sysmon也不讀跳過清單()
    {
        var f = AutomationAndGuidanceFactsService.Collect(At).Single(x => x.Key == AutomationAndGuidanceFactsService.SysmonMapKey);
        Assert.Contains("不宣稱本工具已安裝或讀取 Sysmon", f.Value);
        Assert.DoesNotContain("MRU", f.Value);
    }

    // ── 整批契約 ────────────────────────────────────────────────────────────

    [Fact]
    public void 本批十把鍵齊且遠端項如實NotSupported()
    {
        var facts = AutomationAndGuidanceFactsService.Collect(At);
        string[] expected =
        [
            AutomationAndGuidanceFactsService.HeadlessKey, AutomationAndGuidanceFactsService.ComposeKey,
            AutomationAndGuidanceFactsService.ExitCodeKey, AutomationAndGuidanceFactsService.StdinKey,
            AutomationAndGuidanceFactsService.RotateKey, AutomationAndGuidanceFactsService.RemoteKey,
            AutomationAndGuidanceFactsService.BootKey, AutomationAndGuidanceFactsService.MemoryKey,
            AutomationAndGuidanceFactsService.HardwareDecodeKey, AutomationAndGuidanceFactsService.SysmonMapKey,
        ];
        foreach (string key in expected) Assert.Contains(facts, f => f.Key == key);
        Assert.Equal(10, facts.Count);
        Assert.Equal(FactAvailability.NotSupported, facts.Single(f => f.Key == AutomationAndGuidanceFactsService.RemoteKey).Availability);
    }
}
