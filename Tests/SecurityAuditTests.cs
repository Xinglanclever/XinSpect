using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 安全鑑識組的契約（backlog「本機曾被塞 9 條 Defender 排除」的真實痛點）：
/// ① Defender 排除清單（MSFT_MpPreference，usermode WMI）——排除就是「掃毒永遠不看這裡」，
///    每一條都要攤開；② 事件記錄清除偵測（Security log 1102，需提權）；③ 非微軟根憑證
///    （本機信任根裡的 MITM／監控憑證風險面）；④ USBSTOR 使用痕跡（唯讀登錄檔）。
/// 通路層全部注入探測，三態分離照既有哲學。
/// </summary>
public class SecurityAuditTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defender排除_逐條攤開_零排除也是答()
    {
        var facts = XinSpect.SecurityAuditFactsService.CollectDefenderExclusions(At,
            probe: () => [@"C:\Users\me\crack", @"proc.exe", "*.tmp"]);
        var count = Assert.Single(facts, f => f.Key == "defender.exclusions.count");
        Assert.Equal(3u, count.NumericValue);
        var first = Assert.Single(facts, f => f.Key == "defender.exclusion.0");
        Assert.Contains("crack", first.Value);

        var empty = XinSpect.SecurityAuditFactsService.CollectDefenderExclusions(At, probe: () => []);
        var zero = Assert.Single(empty, f => f.Key == "defender.exclusions.count");
        Assert.Equal(FactAvailability.Present, zero.Availability);
        Assert.Contains("沒有", zero.Value);
    }

    [Fact]
    public void Defender排除_讀不到三態()
    {
        var facts = XinSpect.SecurityAuditFactsService.CollectDefenderExclusions(At, probe: () => null);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("MpPreference", Assert.Single(facts, f => f.Key == "defender.exclusions.count").UnavailableReason);
    }

    [Fact]
    public void 記錄清除偵測_有事件帶時間_無事件與讀不到分清()
    {
        var found = XinSpect.SecurityAuditFactsService.CollectLogClearEvents(At,
            probe: () => [new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 28, 3, 0, 0, TimeSpan.Zero)]);
        var count = Assert.Single(found, f => f.Key == "audit.logclear.count");
        Assert.Equal(2u, count.NumericValue);
        Assert.Contains("2026-10-01", Assert.Single(found, f => f.Key == "audit.logclear.last").Value);

        var none = XinSpect.SecurityAuditFactsService.CollectLogClearEvents(At, probe: () => []);
        Assert.Equal(FactAvailability.Present,
            Assert.Single(none, f => f.Key == "audit.logclear.count").Availability);
        Assert.Contains("沒有", Assert.Single(none, f => f.Key == "audit.logclear.count").Value);

        var fail = XinSpect.SecurityAuditFactsService.CollectLogClearEvents(At, probe: () => null);
        Assert.Equal(FactAvailability.InsufficientPrivilege,
            Assert.Single(fail, f => f.Key == "audit.logclear.count").Availability);
    }

    [Fact]
    public void 非微軟根憑證_逐條攤開_微軟根不算命中()
    {
        var facts = XinSpect.SecurityAuditFactsService.CollectForeignRootCerts(At, probe: () =>
        [
            ("CN=Microsoft Root Certificate Authority", DateTime.Parse("2030-01-01")),
            ("CN=Superfish Inc", DateTime.Parse("2027-05-05")),
            ("CN=SomeProxy CA", DateTime.Parse("2026-12-31")),
        ]);
        var count = Assert.Single(facts, f => f.Key == "cert.foreign_roots");
        Assert.Equal(2u, count.NumericValue);
        var first = Assert.Single(facts, f => f.Key == "cert.foreign_root.0");
        Assert.Contains("Superfish", first.Value);
        Assert.Contains("2027-05-05", first.Value);

        var clean = XinSpect.SecurityAuditFactsService.CollectForeignRootCerts(At, probe: () =>
            [("CN=Microsoft Root Certificate Authority", DateTime.Parse("2030-01-01"))]);
        Assert.Equal(0u, Assert.Single(clean, f => f.Key == "cert.foreign_roots").NumericValue);
    }

    [Fact]
    public void USBSTOR痕跡_逐裝置攤開()
    {
        var facts = XinSpect.SecurityAuditFactsService.CollectUsbstor(At,
            probe: () => ["Disk&Ven_Kingston&Prod_DT_101_G2", "Disk&Ven_SanDisk&Prod_Cruzer"]);
        Assert.Equal(2u, Assert.Single(facts, f => f.Key == "usbstor.count").NumericValue);
        Assert.Contains("Kingston", Assert.Single(facts, f => f.Key == "usbstor.0").Value);

        var empty = XinSpect.SecurityAuditFactsService.CollectUsbstor(At, probe: () => []);
        Assert.Contains("沒有", Assert.Single(empty, f => f.Key == "usbstor.count").Value);
    }
}
