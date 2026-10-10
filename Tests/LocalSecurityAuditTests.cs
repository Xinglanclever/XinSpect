using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 本地安全審計組（v2.56，工業目錄批次一 SA-001／002／003／005／007）：
/// 全部走注入探測（fixture），不碰真登錄檔。三條誠實規則釘死：
/// 命中＝風險面非判決（合法軟體也會掛）；讀不到＝ReadError 不猜；空集合＝0 條不冒充。
/// </summary>
public class LocalSecurityAuditTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    // ── SA-001 IFEO ──────────────────────────────────────────────────────────

    [Fact]
    public void ifeo_命中逐條列出且標注非判決()
    {
        var facts = LocalSecurityAuditService.CollectIfeo(At,
            () => [new("input.exe", "C:\\ime\\hook.dll", null), new("evil.exe", null, "C:\\x\\gfi.dll")]);
        var scan = Assert.Single(facts, f => f.Key == "sa.ifeo.scan");
        Assert.Equal(2, scan.NumericValue);
        var hit = Assert.Single(facts, f => f.Key == "sa.ifeo.input.exe");
        Assert.Contains("Debugger＝C:\\ime\\hook.dll", hit.Value, StringComparison.Ordinal);
        Assert.Contains("非判決", hit.Source, StringComparison.Ordinal);
        Assert.All(facts, f => Assert.Equal(FactAvailability.Present, f.Availability));
    }

    [Fact]
    public void ifeo_零命中如實說零條()
    {
        var facts = LocalSecurityAuditService.CollectIfeo(At, () => []);
        var scan = Assert.Single(facts, f => f.Key == "sa.ifeo.scan");
        Assert.Contains("0 個映像", scan.Value, StringComparison.Ordinal);
        Assert.Equal(0, scan.NumericValue);
        Assert.Equal(FactAvailability.Present, scan.Availability);   // 「沒有」是量到的 Present 事實
    }

    [Fact]
    public void ifeo_讀不到走ReadError不猜()
    {
        var facts = LocalSecurityAuditService.CollectIfeo(At, () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── SA-002 Winlogon ──────────────────────────────────────────────────────

    [Fact]
    public void winlogon_shell與userinit照原文陳述()
    {
        var facts = LocalSecurityAuditService.CollectWinlogon(At, () =>
            new("explorer.exe", "C:\\Windows\\system32\\userinit.exe,", []));
        Assert.Contains("explorer.exe", Assert.Single(facts, f => f.Key == "sa.winlogon.shell").Value, StringComparison.Ordinal);
        Assert.Contains("userinit.exe", Assert.Single(facts, f => f.Key == "sa.winlogon.userinit").Value, StringComparison.Ordinal);
        var n = Assert.Single(facts, f => f.Key == "sa.winlogon.notify.count");
        Assert.Contains("0 個", n.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void winlogon_notify有掛點時逐名列出()
    {
        var facts = LocalSecurityAuditService.CollectWinlogon(At, () =>
            new(null, null, ["SuspiciousNotify"]));
        var n = Assert.Single(facts, f => f.Key == "sa.winlogon.notify.count");
        Assert.Contains("SuspiciousNotify", n.Value, StringComparison.Ordinal);
        Assert.Equal(1, n.NumericValue);
    }

    // ── SA-003 AppInit_DLLs ──────────────────────────────────────────────────

    [Fact]
    public void appinit_值與開關分列_啟用時明說注入面()
    {
        var facts = LocalSecurityAuditService.CollectAppInit(At, () => (@"C:\evil\x.dll", 1));
        Assert.Contains(@"C:\evil\x.dll", Assert.Single(facts, f => f.Key == "sa.appinit.value").Value, StringComparison.Ordinal);
        var load = Assert.Single(facts, f => f.Key == "sa.appinit.load");
        Assert.Contains("已啟用", load.Value, StringComparison.Ordinal);
        Assert.Equal(1, load.NumericValue);
    }

    [Fact]
    public void appinit_空值或關閉時不算啟用()
    {
        var off = LocalSecurityAuditService.CollectAppInit(At, () => (null, 0));
        Assert.Contains("未生效", Assert.Single(off, f => f.Key == "sa.appinit.load").Value, StringComparison.Ordinal);
        var half = LocalSecurityAuditService.CollectAppInit(At, () => (null, 1));   // 開關開但 DLL 空＝不生效
        Assert.Contains("未生效", Assert.Single(half, f => f.Key == "sa.appinit.load").Value, StringComparison.Ordinal);
    }

    // ── SA-005 輔助功能 ──────────────────────────────────────────────────────

    [Fact]
    public void 輔助功能_六個輸入點逐一陳述_未掛也如實()
    {
        var facts = LocalSecurityAuditService.CollectAccessibility(At, () =>
            [("sethc.exe", "C:\\evil\\cmd.exe"), ("utilman.exe", null), ("osk.exe", null),
             ("magnify.exe", null), ("narrator.exe", null), ("displayswitch.exe", null)]);
        Assert.Equal(6, facts.Count);
        Assert.Contains("C:\\evil\\cmd.exe",
            Assert.Single(facts, f => f.Key == "sa.a11y.sethc.exe").Value, StringComparison.Ordinal);
        Assert.Contains("未掛", Assert.Single(facts, f => f.Key == "sa.a11y.utilman.exe").Value, StringComparison.Ordinal);
    }

    // ── SA-007 代理 ──────────────────────────────────────────────────────────

    [Fact]
    public void 代理_四欄分列且標注代理不等於惡意()
    {
        var facts = LocalSecurityAuditService.CollectProxy(At, () => new(1, "proxy.corp:8080", "internal", false));
        Assert.Contains("proxy.corp:8080",
            Assert.Single(facts, f => f.Key == "sa.proxy.wininet.server").Value, StringComparison.Ordinal);
        Assert.Contains("代理≠惡意",
            Assert.Single(facts, f => f.Key == "sa.proxy.wininet.server").Source, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(facts, f => f.Key == "sa.proxy.wininet.enable").NumericValue);
        var wh = Assert.Single(facts, f => f.Key == "sa.proxy.winhttp.server");
        Assert.Contains("未設定", wh.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void 代理_winhttp有配置但格式未對準時如實標注未解析()
    {
        var facts = LocalSecurityAuditService.CollectProxy(At, () => new(0, null, null, true));
        var wh = Assert.Single(facts, f => f.Key == "sa.proxy.winhttp.server");
        Assert.Contains("未解析", wh.Value, StringComparison.Ordinal);
        Assert.Contains("如實", wh.Value, StringComparison.Ordinal);
    }
}
