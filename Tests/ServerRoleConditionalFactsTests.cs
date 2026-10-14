using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 伺服器角色條件式事實（Vol2 G4 第二波）：Hyper-V 交換器（SV-009）、IIS 應用池（SV-002）、DNS 區域（SV-004）。
/// 最關鍵的契約是<b>「角色不在（探針回 null）＝不適用」與「讀取失敗（擲例外）＝ReadError」不得混為一談</b>。
/// </summary>
public class ServerRoleConditionalFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    // ── SV-009：Hyper-V 虛擬交換器 ────────────────────────────────────────

    [Fact]
    public void HyperV交換器_列出名稱與型別()
    {
        var probe = new[]
        {
            new ServerRoleConditionalFactsService.VSwitch("外部網卡", "外部"),
            new ServerRoleConditionalFactsService.VSwitch("內部測試", "內部"),
        };
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, switchesProbe: () => probe),
            "sv.hyperv.switches");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("外部網卡", f.Value);
        Assert.Contains("內部測試", f.Value);
    }

    [Fact]
    public void HyperV交換器_已安裝但零交換器是如實的零()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, switchesProbe: () => []),
            "sv.hyperv.switches");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("沒有", f.Value);
    }

    [Fact]
    public void HyperV交換器_角色不在標不適用而非錯誤()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, switchesProbe: () => null),
            "sv.hyperv.switches");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未安裝", f.UnavailableReason);
    }

    [Fact]
    public void HyperV交換器_查詢失敗標ReadError()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            switchesProbe: () => throw new InvalidOperationException("存取被拒")), "sv.hyperv.switches");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("存取被拒", f.UnavailableReason);
    }

    [Fact]
    public void HyperV查詢_必須用SELECT星號不得投影()
    {
        // Hyper-V WMI 提供者對投影式 WQL（SELECT 欄位清單）回「查詢無效」，本機實測；必須 SELECT *。
        Assert.StartsWith("SELECT * ", ServerRoleConditionalFactsService.HyperVSwitchQuery);
        Assert.DoesNotContain("ElementName", ServerRoleConditionalFactsService.HyperVSwitchQuery);
    }

    [Theory]
    [InlineData(0, "未設定")]
    [InlineData(1, "內部")]
    [InlineData(2, "私人")]
    [InlineData(3, "外部")]
    public void 交換器型別逐值對照(int type, string expected)
        => Assert.Equal(expected, ServerRoleConditionalFactsService.SwitchTypeName(type));

    [Fact]
    public void 交換器型別_未收錄如實標未收錄不猜()
        => Assert.Contains("未收錄", ServerRoleConditionalFactsService.SwitchTypeName(9));

    // ── SV-002：IIS 應用程式池 ───────────────────────────────────────────

    [Fact]
    public void IIS應用池_列出定義的池與管線模式()
    {
        var probe = new[]
        {
            new ServerRoleConditionalFactsService.AppPool("DefaultAppPool", "Integrated"),
            new ServerRoleConditionalFactsService.AppPool("Legacy", "Classic"),
        };
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, appPoolsProbe: () => probe),
            "sv.iis.apppools");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("DefaultAppPool", f.Value);
        Assert.Contains("Classic", f.Value);
    }

    [Fact]
    public void IIS應用池_未裝標不適用()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, appPoolsProbe: () => null),
            "sv.iis.apppools");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("未安裝", f.UnavailableReason);
    }

    [Fact]
    public void IIS應用池_設定檔解析失敗標ReadError()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            appPoolsProbe: () => throw new System.Xml.XmlException("根項目不符")), "sv.iis.apppools");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("根項目不符", f.UnavailableReason);
    }

    // ── SV-004：DNS 區域 ─────────────────────────────────────────────────

    [Fact]
    public void DNS區域_列出區域名稱()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At,
            dnsZonesProbe: () => new[] { "contoso.local", "1.in-addr.arpa" }), "sv.dns.zones");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(2, f.NumericValue);
        Assert.Contains("contoso.local", f.Value);
    }

    [Fact]
    public void DNS區域_未裝標不適用()
    {
        var f = Fact(ServerRoleConditionalFactsService.Collect(At, dnsZonesProbe: () => null),
            "sv.dns.zones");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用", f.UnavailableReason);
    }

    // ── 契約 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 單一來源失敗或不在都不影響其他來源()
    {
        var facts = ServerRoleConditionalFactsService.Collect(At,
            switchesProbe: () => null,                                    // 角色不在
            appPoolsProbe: () => throw new InvalidOperationException("壞"), // 讀取失敗
            dnsZonesProbe: () => new[] { "corp.local" });                  // 正常

        Assert.Equal(FactAvailability.NotApplicable, Fact(facts, "sv.hyperv.switches").Availability);
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "sv.iis.apppools").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "sv.dns.zones").Availability);
    }

    [Fact]
    public void 契約_七個鍵齊全且排序無關()
    {
        // 原本是三個鍵（Hyper-V／IIS／DNS）；第 524–525 項在同一收集點補上 AD DS／DHCP／RDS，
        // 所以這裡的期望值由 3 → 6——刻意改成逐一比對鍵集，之後再加項時紅燈會直接指出缺哪一把。
        var facts = ServerRoleConditionalFactsService.Collect(At);
        Assert.Equal(
            new[] { "sv.adds", "sv.cluster.state", "sv.dhcp", "sv.dns.zones", "sv.hyperv.switches", "sv.iis.apppools", "sv.rds" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 契約_不適用與讀取失敗都帶原因且值為空()
    {
        var facts = ServerRoleConditionalFactsService.Collect(At,
            switchesProbe: () => null,
            appPoolsProbe: () => null,
            dnsZonesProbe: () => null,
            addsProbe: () => null,
            dhcpProbe: () => null,
            rdsProbe: () => null);

        foreach (var f in facts)
        {
            Assert.NotEqual(FactAvailability.Present, f.Availability);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺原因");
            Assert.Equal("", f.Value);
            Assert.Equal(FactTrustLevel.Unknown, f.Trust);
        }
    }
}
