using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP15 第四組：稽核政策與機器原則的契約。稽核政策經 LSA（LsaQueryInformationPolicy，
/// usermode 唯讀）；LSA 通路以注入探測替代，等級陣列→繁中描述的純解碼逐類釘值；
/// 機器原則以 Registry.pol 檔的存在與寫入時間為可量測事實（無檔＝如實 NotSupported）。
/// </summary>
public class AuditPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 稽核政策_九類別等級描述逐項釘值()
    {
        // 0 無稽核、1 成功、2 失敗、3 成功＋失敗（LSA POLICY_AUDIT_EVENT_*）
        string full = XinSpect.AuditPolicyService.Describe(auditingMode: true,
            [1, 3, 0, 0, 2, 3, 1, 0, 1]);
        Assert.Contains("稽核模式：開啟", full);
        Assert.Contains("登入/登出＝成功＋失敗", full);
        Assert.Contains("程序追蹤＝失敗", full);
        Assert.Contains("帳戶登入＝成功", full);
        Assert.DoesNotContain("物件存取＝", full);   // 等級 0 的類別不列（0＝未設定不是值）

        string off = XinSpect.AuditPolicyService.Describe(auditingMode: false, [3, 3]);
        Assert.Contains("稽核模式：關閉", off);
        Assert.Contains("登入/登出＝成功＋失敗", off);  // 類別設定仍在，只是總開關關閉
    }

    [Fact]
    public void 稽核政策_非規範等級如實標未知不猜()
    {
        string text = XinSpect.AuditPolicyService.Describe(true, [7]);
        Assert.Contains("等級 7", text);
    }

    [Fact]
    public void 稽核政策_LSA不可用如實三態()
    {
        var facts = XinSpect.AuditPolicyService.Collect(At, probe: () => null, registryPolPath: @"C:\definitely\not\here.pol");
        Assert.Equal(FactAvailability.ReadError,
            Assert.Single(facts, f => f.Key == "audit.mode").Availability);
        Assert.Contains("LSA", Assert.Single(facts, f => f.Key == "audit.mode").UnavailableReason);
    }

    [Fact]
    public void 機器原則_RegistryPol存在帶寫入時間_不存在如實標()
    {
        string tempPol = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xinsp-{Guid.NewGuid():N}.pol");
        System.IO.File.WriteAllText(tempPol, "[GroupPolicy]");
        try
        {
            var factsOk = XinSpect.AuditPolicyService.Collect(At,
                probe: () => (true, new[] { 1, 3, 0, 0, 2, 3, 1, 0, 1 }),
                registryPolPath: tempPol);
            var gp = Assert.Single(factsOk, f => f.Key == "gp.registrypol");
            Assert.Equal(FactAvailability.Present, gp.Availability);
            Assert.Equal(FactAvailability.Present, Assert.Single(factsOk, f => f.Key == "audit.mode").Availability);
            Assert.Contains("開啟", Assert.Single(factsOk, f => f.Key == "audit.mode").Value);
            Assert.Contains("存在（最後寫入", gp.Value);

            var factsMissing = XinSpect.AuditPolicyService.Collect(At,
                probe: () => (true, new[] { 1 }),
                registryPolPath: @"C:\definitely\not\here.pol");
            var missing = Assert.Single(factsMissing, f => f.Key == "gp.registrypol");
            Assert.Equal(FactAvailability.NotSupported, missing.Availability);
            Assert.Contains("沒有機器原則檔", missing.UnavailableReason);
        }
        finally { System.IO.File.Delete(tempPol); }
    }
}
