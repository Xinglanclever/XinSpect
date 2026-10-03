using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 審計日誌（A47）的契約：雜湊鏈追加、<b>改中間一筆驗證必失敗</b>、序號連續、
/// 重放（存檔→載入→驗證通過）、機器識別為不可逆雜湊（無序號原文）、只記中繼資料。
/// </summary>
public class AuditLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xinaudit-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 追加兩筆_鏈有效_前後雜湊串接()
    {
        var log = new List<AuditEntry>();
        log.Add(AuditLogService.Append(log, "user", "machine-hash", "建立時間膠囊", "時間膠囊", "12 項事實", "hash-a", At));
        log.Add(AuditLogService.Append(log, "user", "machine-hash", "比較快照", "時間膠囊", "變更 3", "hash-b", At.AddMinutes(1)));

        Assert.Equal(2, log.Count);
        Assert.Equal(AuditEntry.Genesis, log[0].PreviousHash);
        Assert.Equal(log[0].EntryHash, log[1].PreviousHash);

        var verdict = AuditVerifier.Verify(log);
        Assert.True(verdict.Valid);
        Assert.Equal(2, verdict.CheckedCount);
    }

    [Fact]
    public void 改中間一筆_驗證必失敗_後續鏈斷裂()
    {
        var log = new List<AuditEntry>();
        for (int i = 0; i < 3; i++)
            log.Add(AuditLogService.Append(log, "user", "m", $"動作{i}", "範圍", "摘要", $"hash-{i}", At.AddMinutes(i)));

        var tampered = log.Select(e => e with { }).ToList();
        tampered[1] = tampered[1] with { Action = "被改成別的動作" }; // 竄改中間一筆的內容

        var verdict = AuditVerifier.Verify(tampered);
        Assert.False(verdict.Valid);
        Assert.Contains("竄改", verdict.FailureReason);
    }

    [Fact]
    public void 重算竄改筆雜湊_後續PreviousHash仍對不上()
    {
        var log = new List<AuditEntry>();
        for (int i = 0; i < 3; i++)
            log.Add(AuditLogService.Append(log, "user", "m", $"動作{i}", "範圍", "摘要", $"hash-{i}", At.AddMinutes(i)));

        // 攻擊者改內容並重算該筆雜湊——後續筆的 PreviousHash 必然對不上
        var tampered = log.Select(e => e with { }).ToList();
        var rewritten = tampered[1] with { Action = "偽造" };
        tampered[1] = rewritten with { EntryHash = AuditEntry.ComputeHash(rewritten) };

        var verdict = AuditVerifier.Verify(tampered);
        Assert.False(verdict.Valid);
        Assert.Contains("PreviousHash", verdict.FailureReason);
    }

    [Fact]
    public void 重放_存檔載入驗證通過_格式損毀拒載()
    {
        var log = new List<AuditEntry>();
        log.Add(AuditLogService.Append(log, "user", "m", "掃描", "evidence", "2816 項事實", "hash", At));
        AuditLogService.Save(_path, log);

        var loaded = AuditLogService.Load(_path);
        Assert.True(AuditVerifier.Verify(loaded).Valid);

        File.WriteAllText(_path, "不是日誌");
        var broken = AuditVerifier.VerifyFile(_path);
        Assert.False(broken.Valid);
        Assert.Contains("無法解析", broken.FailureReason);
    }

    [Fact]
    public void 機器識別_不可逆雜湊_不含原文()
    {
        string hash = AuditIdentity.MachineHash(["ROG RAMPAGE VI EXTREME OMEGA", "SERIAL-12345"]);
        Assert.NotEqual(string.Empty, hash);
        Assert.DoesNotContain("RAMPAGE", hash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL-12345", hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hash, AuditIdentity.MachineHash(["ROG RAMPAGE VI EXTREME OMEGA", "SERIAL-12345"])); // 決定性
        Assert.NotEqual(hash, AuditIdentity.MachineHash(["另一台", "SERIAL-99999"]));
    }

    [Fact]
    public void 檔案不存在_空日誌有效()
    {
        var verdict = AuditVerifier.VerifyFile(_path);
        Assert.True(verdict.Valid);
        Assert.Equal(0, verdict.CheckedCount);
    }
}
