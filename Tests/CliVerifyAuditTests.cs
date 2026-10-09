using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CLI 審計驗證入口（v2.48）：能產日誌也要能在 App 外驗日誌——杂湊鏈的價值在第三方可驗。
/// </summary>
public class CliVerifyAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "XinSpectAuditCli_" + Guid.NewGuid().ToString("N"));

    public CliVerifyAuditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static AuditEntry Chained(int seq, string prevHash, string action)
    {
        var draft = new AuditEntry
        {
            Sequence = seq,
            TimestampUtc = new DateTimeOffset(2026, 10, 10, 0, seq, 0, TimeSpan.Zero),
            Operator = "tester",
            MachineHash = new string('a', 64),
            Action = action,
            Scope = "evidence",
            ResultSummary = "ok",
            ResultHash = new string('b', 64),
            PreviousHash = prevHash,
            EntryHash = "",
        };
        return draft with { EntryHash = AuditEntry.ComputeHash(draft) };
    }

    private static (int Exit, string Out) Run(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = CliService.Run(args, () => [], stdout, stderr);
        return (exit, stdout.ToString());
    }

    [Fact]
    public void 完整鏈回零且如實筆數()
    {
        string path = Path.Combine(_dir, "audit.json");
        var first = Chained(1, AuditEntry.Genesis, "snapshot.create");
        var second = Chained(2, first.EntryHash, "snapshot.compare");
        AuditLogService.Save(path, [first, second]);

        var (exit, output) = Run(["--verify-audit", path]);

        Assert.Equal(CliService.ExitOk, exit);
        Assert.Contains("\"fileExists\": true", output);
        Assert.Contains("\"chainValid\": true", output);
        Assert.Contains("\"checkedEntries\": 2", output);
    }

    [Fact]
    public void 改中間一筆必抓出斷點並回二()
    {
        string path = Path.Combine(_dir, "audit.json");
        var first = Chained(1, AuditEntry.Genesis, "snapshot.create");
        var second = Chained(2, first.EntryHash, "snapshot.compare");
        var third = Chained(3, second.EntryHash, "snapshot.create");
        AuditLogService.Save(path, [first, second, third]);

        // 篡改第二筆的動作文字，但保留原 EntryHash——重算該筆雜湊會不同、鏈在此斷
        AuditLogService.Save(path, [first, second with { Action = "tampered" }, third]);

        var (exit, output) = Run(["--verify-audit", path]);

        // 中文在 JSON 輸出會被跳脫成 \uXXXX，斷言只用結構欄位：斷點位置（checkedEntries＝第幾筆通過）
        // 與「failureReason 非空」才是機器可依賴的承諾。
        Assert.Equal(CliService.ExitPartial, exit);
        Assert.Contains("\"chainValid\": false", output);
        Assert.Contains("\"checkedEntries\": 1", output);   // 第 1 筆好，斷在第 2 筆
        Assert.DoesNotContain("\"failureReason\": null", output);
    }

    [Fact]
    public void 日誌不存在時說沒有日誌不假稱通過()
    {
        var (exit, output) = Run(["--verify-audit", Path.Combine(_dir, "missing.json")]);

        Assert.Equal(CliService.ExitOk, exit);
        Assert.Contains("\"fileExists\": false", output);
        Assert.Contains("\"checkedEntries\": 0", output);
        // 關鍵界線：不能只回一個乾癟的 valid:true 就假稱驗證通過
        Assert.Contains("note", output);
    }

    [Fact]
    public void 損毀檔案標為無效並回二()
    {
        string path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "這不是一段合法的審計日誌 JSON");

        var (exit, output) = Run(["--verify-audit", path]);

        Assert.Equal(CliService.ExitPartial, exit);
        Assert.Contains("\"chainValid\": false", output);
        Assert.Contains("\"checkedEntries\": 0", output);
        Assert.DoesNotContain("\"failureReason\": null", output);
    }
}
