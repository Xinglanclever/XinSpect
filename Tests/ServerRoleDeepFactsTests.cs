using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 伺服器角色深層事實（Vol2 G4 第一波）：角色服務（SV-013）、授權啟用（SV-012）、
/// 檔案／列印共用（SV-006／SV-007）的三態契約與欄位。以注入探針合成，不碰真的 WMI。
/// </summary>
public class ServerRoleDeepFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static ServerRoleDeepFactsService.RoleServiceState Svc(string name, bool running,
        string display = "") => new(name, display.Length == 0 ? name : display, running);

    // ── SV-013：角色服務 ─────────────────────────────────────────────────

    [Fact]
    public void 角色服務_只認固定清單且計數與執行數正確()
    {
        var probe = new[]
        {
            Svc("LanmanServer", true),
            Svc("Spooler", true),
            Svc("vmms", false),
            Svc("NotARole", true),          // 不在固定清單 → 不列入
            Svc("wuauserv", true),
        };
        var f = Fact(ServerRoleDeepFactsService.Collect(At, servicesProbe: () => probe), "sv.roles.services");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(3, f.NumericValue);                 // 三個在清單內
        Assert.Contains("執行中", f.Value);
        Assert.Contains("檔案伺服器", f.Value);
        Assert.Contains("Hyper-V", f.Value);
        Assert.DoesNotContain("NotARole", f.Value);
        Assert.Contains("2 個執行中", f.Value);
    }

    [Fact]
    public void 角色服務_空清單是如實的零而不是讀取失敗()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At, servicesProbe: () => []), "sv.roles.services");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(0, f.NumericValue);
        Assert.Contains("一個都沒偵測到", f.Value);
    }

    [Fact]
    public void 角色服務_讀取失敗標ReadError且不畫成零()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At,
            servicesProbe: () => throw new InvalidOperationException("WMI 掛了")), "sv.roles.services");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("WMI 掛了", f.UnavailableReason);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 角色服務_清單大小寫不敏感()
    {
        // Windows 服務名稱大小寫不固定；比對必須不分大小寫，否則會靜默漏掉整個角色。
        var probe = new[] { Svc("lanmanserver", true), Svc("DHCPSERVER", false) };
        var f = Fact(ServerRoleDeepFactsService.Collect(At, servicesProbe: () => probe), "sv.roles.services");

        Assert.Equal(2, f.NumericValue);
        Assert.Contains("檔案伺服器", f.Value);
        Assert.Contains("DHCP 伺服器", f.Value);
    }

    // ── SV-012：授權與啟用 ───────────────────────────────────────────────

    [Theory]
    [InlineData(0, "未啟用")]
    [InlineData(1, "已啟用")]
    [InlineData(2, "初始寬限期")]
    [InlineData(3, "寬限期已過")]
    [InlineData(4, "非正版寬限期")]
    [InlineData(5, "通知模式")]
    [InlineData(6, "延長寬限期")]
    public void 授權_LicenseStatus逐值對照(int status, string expected)
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At,
            licenseProbe: () => new ServerRoleDeepFactsService.LicenseInfo(
                "Windows(R), ServerDatacenter edition", status, "OEM_DM channel")), "sv.licensing");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains(expected, f.Value);
        Assert.Equal(status, f.NumericValue);
    }

    [Fact]
    public void 授權_未收錄狀態如實標未收錄不冒充()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At,
            licenseProbe: () => new ServerRoleDeepFactsService.LicenseInfo("X", 99, "")), "sv.licensing");

        Assert.Contains("未收錄的授權狀態", f.Value);
        Assert.Contains("99", f.Value);
    }

    [Fact]
    public void 授權_無符合列標不適用不是錯誤()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At, licenseProbe: () => null), "sv.licensing");

        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不適用", f.UnavailableReason);
    }

    [Fact]
    public void 授權_查詢例外標ReadError()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At,
            licenseProbe: () => throw new UnauthorizedAccessException("拒絕存取")), "sv.licensing");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("拒絕存取", f.UnavailableReason);
    }

    [Fact]
    public void 授權_值不得含產品金鑰欄位()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At,
            licenseProbe: () => new ServerRoleDeepFactsService.LicenseInfo(
                "Windows(R), ServerStandard edition", 1, "RETAIL channel")), "sv.licensing");

        // 禁項紅線：授權事實只報狀態與描述，不得出現金鑰字樣。
        Assert.DoesNotContain("PartialProductKey", f.Value);
        Assert.DoesNotContain("金鑰：", f.Value);
    }

    // ── SV-006／SV-007：檔案與列印共用 ───────────────────────────────────

    [Fact]
    public void 共用_區分磁碟與印表並含系統預設()
    {
        var probe = new[]
        {
            new ServerRoleDeepFactsService.ShareEntry("C$", 0x8000_0000L),
            new ServerRoleDeepFactsService.ShareEntry("ADMIN$", 0x8000_0000L),
            new ServerRoleDeepFactsService.ShareEntry("IPC$", 3),
            new ServerRoleDeepFactsService.ShareEntry("Data", 0),
            new ServerRoleDeepFactsService.ShareEntry("HP LaserJet", 1),
        };
        var f = Fact(ServerRoleDeepFactsService.Collect(At, sharesProbe: () => probe), "sv.shares");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(3, f.NumericValue);                 // C$、ADMIN$、Data
        Assert.Contains("檔案共用 3 個", f.Value);
        Assert.Contains("列印共用 1 個", f.Value);
        Assert.Contains("C$", f.Value);
        Assert.Contains("Data", f.Value);
    }

    [Fact]
    public void 共用_系統預設共用的特殊位元不得溢位()
    {
        // 本機實測：WMI 回傳 uint 0x80000000（C$／ADMIN$）。以 Convert.ToInt32 承接會擲 OverflowException，
        // 使整個 sv.shares 退化成 ReadError——這條釘住轉換不得再走 int。
        long t = ServerRoleDeepFactsService.ShareTypeOf((uint)0x8000_0000);
        Assert.Equal(0x8000_0000L, t);
        Assert.Equal(0L, t & 0xFF);                       // 低 8 位元仍是磁碟(0)，特殊位元不影響分類
        Assert.Equal(0L, ServerRoleDeepFactsService.ShareTypeOf(null));
    }

    [Fact]
    public void 共用_不披露本機路徑()
    {
        var probe = new[] { new ServerRoleDeepFactsService.ShareEntry("Home", 0) };
        var f = Fact(ServerRoleDeepFactsService.Collect(At, sharesProbe: () => probe), "sv.shares");

        Assert.DoesNotContain(@"C:\", f.Value);
        Assert.DoesNotContain("Users", f.Value);
    }

    [Fact]
    public void 共用_讀取失敗標ReadError()
    {
        var f = Fact(ServerRoleDeepFactsService.Collect(At,
            sharesProbe: () => throw new InvalidOperationException("查詢逾時")), "sv.shares");

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("查詢逾時", f.UnavailableReason);
    }

    // ── 契約：三把鍵都在、三個來源互不影響 ───────────────────────────────

    [Fact]
    public void 單一來源失敗不影響其他來源()
    {
        var facts = ServerRoleDeepFactsService.Collect(At,
            servicesProbe: () => throw new InvalidOperationException("壞了"),
            licenseProbe: () => new ServerRoleDeepFactsService.LicenseInfo("Win", 1, ""),
            sharesProbe: () => [new ServerRoleDeepFactsService.ShareEntry("Data", 0)]);

        Assert.Equal(FactAvailability.ReadError, Fact(facts, "sv.roles.services").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "sv.licensing").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "sv.shares").Availability);
    }

    [Fact]
    public void 契約_每個不可得都帶原因且值為空()
    {
        var facts = ServerRoleDeepFactsService.Collect(At,
            servicesProbe: () => throw new InvalidOperationException("a"),
            licenseProbe: () => throw new InvalidOperationException("b"),
            sharesProbe: () => throw new InvalidOperationException("c"));

        foreach (var f in facts)
        {
            Assert.NotEqual(FactAvailability.Present, f.Availability);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺不可得原因");
            Assert.Equal("", f.Value);
            Assert.Equal(FactTrustLevel.Unknown, f.Trust);
        }
        Assert.Equal(new[] { "sv.licensing", "sv.roles.services", "sv.shares" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
    }
}
