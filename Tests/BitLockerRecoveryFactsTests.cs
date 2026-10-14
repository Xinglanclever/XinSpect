using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// BitLocker 復原金鑰輪廓（SA-016）單元測試。
/// 這一組最重要的不是功能，而是界線：<b>金鑰值永不讀</b>，且
/// 「沒有復原保護器」與「有但沒備份」與「備份狀態讀不到」三者不得互相冒充。
/// </summary>
public class BitLockerRecoveryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // ── 保護器分類 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(3)]   // 數值密碼
    [InlineData(8)]   // 復原密碼
    public void 數值與復原密碼算復原類(int type)
        => Assert.True(BitLockerRecoveryFactsService.IsRecoveryProtector(type));

    [Theory]
    [InlineData(1)]   // TPM
    [InlineData(4)]   // TPM＋PIN
    [InlineData(5)]   // TPM＋啟動金鑰
    [InlineData(6)]   // TPM＋PIN＋啟動金鑰
    [InlineData(7)]   // 僅 TPM
    public void TPM家族算TPM類(int type)
        => Assert.True(BitLockerRecoveryFactsService.IsTpmProtector(type));

    [Theory]
    [InlineData(2)]   // 外部金鑰
    [InlineData(9)]   // 憑證
    public void 外部金鑰與憑證兩類都不算(int type)
    {
        // 外部金鑰是隨身碟上的檔案、憑證是智慧卡——「抄在紙上那種」與「綁機器那種」都不是。
        Assert.False(BitLockerRecoveryFactsService.IsRecoveryProtector(type));
        Assert.False(BitLockerRecoveryFactsService.IsTpmProtector(type));
    }

    // ── 判讀核心 ─────────────────────────────────────────────────────────

    [Fact]
    public void 只有TPM保護器時備份狀態是不適用()
    {
        var p = BitLockerRecoveryFactsService.Summarize([[1]]);
        Assert.Equal(0, p.RecoveryProtectors);
        Assert.Equal(1, p.TpmProtectors);
        Assert.Equal(BitLockerRecoveryFactsService.BackupState.NoRecoveryProtector, p.Backup);
    }

    [Fact]
    public void 有復原保護器但沒備份()
    {
        var p = BitLockerRecoveryFactsService.Summarize([[1, 3]], backupReadable: true, backupReported: false);
        Assert.Equal(1, p.RecoveryProtectors);
        Assert.Equal(BitLockerRecoveryFactsService.BackupState.NotBackedUp, p.Backup);
    }

    [Fact]
    public void 有復原保護器且已備份()
    {
        var p = BitLockerRecoveryFactsService.Summarize([[1, 8]], backupReadable: true, backupReported: true);
        Assert.Equal(BitLockerRecoveryFactsService.BackupState.BackedUp, p.Backup);
    }

    [Fact]
    public void 備份狀態讀不到不冒充沒備份()
    {
        var p = BitLockerRecoveryFactsService.Summarize([[3]], backupReadable: false);
        // 這一條是本項的第二紅線：讀不到就是 Unknown，不能落到 NotBackedUp。
        Assert.Equal(BitLockerRecoveryFactsService.BackupState.Unknown, p.Backup);
    }

    [Fact]
    public void 跨卷累加且只計有保護器的卷()
    {
        var p = BitLockerRecoveryFactsService.Summarize([[1], [1, 3, 8], []]);
        Assert.Equal(2, p.Volumes);          // 第三個卷沒有任何保護器，不計
        Assert.Equal(2, p.RecoveryProtectors);
        Assert.Equal(2, p.TpmProtectors);
    }

    [Fact]
    public void 暫停保護的卷數原樣帶出()
    {
        var p = BitLockerRecoveryFactsService.Summarize([[1]], suspendedVolumes: 2);
        Assert.Equal(2, p.SuspendedVolumes);
    }

    // ── 事實值 ───────────────────────────────────────────────────────────

    private static HardwareFact Fact(BitLockerRecoveryFactsService.RecoveryProfile? p)
        => BitLockerRecoveryFactsService.Collect(At, () => p);

    [Fact]
    public void 未備份的結論要說出後果但不自行處理()
    {
        var f = Fact(BitLockerRecoveryFactsService.Summarize([[1, 3]]));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("未備份", f.Value);
        Assert.Contains("換主機板", f.Value);
        Assert.Contains("由人決定", f.Value);
    }

    [Fact]
    public void 沒有復原保護器不是缺陷()
    {
        var f = Fact(BitLockerRecoveryFactsService.Summarize([[1]]));
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("不是缺陷", f.Value);
        Assert.Equal(0d, f.NumericValue);
    }

    [Fact]
    public void 值裡永遠聲明金鑰不讀不輸出()
    {
        var f = Fact(BitLockerRecoveryFactsService.Summarize([[1, 3]]));
        Assert.Contains("金鑰值永不讀取、永不輸出、永不落地", f.Value);
        Assert.Contains("NumericalPassword", f.Value);
    }

    [Fact]
    public void 暫停保護會出現在值裡()
    {
        var f = Fact(BitLockerRecoveryFactsService.Summarize([[1, 3]], suspendedVolumes: 1));
        Assert.Contains("保護暫停中的卷 1 個", f.Value);
    }

    [Fact]
    public void 完全沒有任何保護器是不適用()
    {
        var f = Fact(new BitLockerRecoveryFactsService.RecoveryProfile(
            0, 0, 0, BitLockerRecoveryFactsService.BackupState.NoRecoveryProtector, 0));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("這不是缺陷", f.UnavailableReason);
    }

    [Fact]
    public void 讀取失敗是ReadError不當成沒有金鑰()
    {
        var f = Fact(null);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不把讀不到當成沒有金鑰", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 探針擲例外也標ReadError()
    {
        var f = BitLockerRecoveryFactsService.Collect(At, () => throw new InvalidOperationException("x"));
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    [Fact]
    public void 常數值符合Win32列舉()
    {
        // 保護器型別是 Win32 API 的公開契約，寫成常數就要能對得上來源。
        Assert.Equal(1, BitLockerRecoveryFactsService.ProtectorTpm);
        Assert.Equal(3, BitLockerRecoveryFactsService.ProtectorNumericalPassword);
        Assert.Equal(8, BitLockerRecoveryFactsService.ProtectorRecoveryPassword);
    }
}
