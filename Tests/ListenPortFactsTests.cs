using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 監聽埠三方對照（P0-03／SA-008）三態契約：表讀不到、PID 競態 Unknown、簽章三態、
/// 「取樣瞬間」「不主動連線」「未通過＝待查證不是中毒判決」界線要寫進值裡。
/// </summary>
public class ListenPortFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static PortRow Row(string proto, int port, int pid, string state = "LISTENING") =>
        new() { Protocol = proto, LocalPort = port, Pid = pid, State = state, ProcessName = "" };

    [Fact]
    public void 正常_三方對照逐欄與界線()
    {
        var facts = ListenPortFactsService.Collect(At,
            tableProbe: () => [Row("TCP", 445, 4), Row("UDP", 5353, 1234)],
            imageProbe: pid => pid == 4 ? null : @"C:\Windows\System32\svchost.exe",
            signProbe: path => (true, "微軟簽章"));

        var count = facts.Single(f => f.Key == "net.listen.count");
        Assert.Equal(2, count.NumericValue);
        Assert.Contains("取樣瞬間", count.Value);
        Assert.Contains("不主動連線", count.Value);
        Assert.Contains("不是中毒判決", count.Value);

        var entries = facts.Single(f => f.Key == "net.listen.entries");
        Assert.True(entries.Sensitive); // 映像路徑含系統路徑——敏感預設遮蔽
        Assert.Contains("TCP:445", entries.Value);
        Assert.Contains("映像讀不到", entries.Value);
        Assert.Contains("Unknown，不是未簽章", entries.Value);
        Assert.Contains("UDP:5353", entries.Value);
        Assert.Contains("簽章通過", entries.Value);

        var unverified = facts.Single(f => f.Key == "net.listen.unverified");
        Assert.Equal(1, unverified.NumericValue);

        Assert.Contains(facts, f => f.Key == "net.listen.tcp.445");
        Assert.Contains(facts, f => f.Key == "net.listen.udp.5353");
    }

    [Fact]
    public void 簽章未通過_待查證不是已中招()
    {
        var facts = ListenPortFactsService.Collect(At,
            tableProbe: () => [Row("TCP", 8080, 42)],
            imageProbe: _ => @"C:\Users\me\app.exe",
            signProbe: _ => (false, "無簽章"));
        var unverified = facts.Single(f => f.Key == "net.listen.unverified");
        Assert.Equal(1, unverified.NumericValue);
        Assert.Contains("待查證不是已中招", unverified.Value);
        var row = facts.Single(f => f.Key == "net.listen.tcp.8080");
        Assert.Contains("簽章未通過（待查證）", row.Value);
    }

    [Fact]
    public void 系統持有無PID_映像簽章不適用()
    {
        var facts = ListenPortFactsService.Collect(At,
            tableProbe: () => [Row("TCP", 135, 0)],
            imageProbe: _ => null,
            signProbe: _ => (true, ""));
        var row = facts.Single(f => f.Key == "net.listen.tcp.135");
        Assert.Contains("不適用", row.Value);
    }

    [Fact]
    public void 表讀不到_標讀取錯誤不冒充沒有()
    {
        var facts = ListenPortFactsService.Collect(At, tableProbe: () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("讀不到不等於沒有", f.UnavailableReason);
    }

    [Fact]
    public void 非監聽的TCP連線不列入_UDP整表都算()
    {
        var facts = ListenPortFactsService.Collect(At,
            tableProbe: () => [Row("TCP", 50000, 9, state: "ESTABLISHED"), Row("UDP", 137, 4)],
            imageProbe: _ => @"C:\a.exe",
            signProbe: _ => (true, ""));
        Assert.DoesNotContain(facts, f => f.Key == "net.listen.tcp.50000");
        Assert.Contains(facts, f => f.Key == "net.listen.udp.137");
        var count = facts.Single(f => f.Key == "net.listen.count");
        Assert.Equal(1, count.NumericValue);
    }

    [Fact]
    public void 列舉丟例外_如實標讀取錯誤()
    {
        var facts = ListenPortFactsService.Collect(At,
            tableProbe: () => throw new System.ComponentModel.Win32Exception(5));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("Win32Exception", f.UnavailableReason);
    }
}
