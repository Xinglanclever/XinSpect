using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class PlatformGovernanceFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void SDDL_權限事實非漏洞判決() { var f=PlatformGovernanceFactsService.SddlFact(At,()=>new(100,2,0)); Assert.Contains("非漏洞判決",f.Value); Assert.Contains("不列服務名稱",f.Value); }
    [Fact] public void SDDL_失敗_ReadError() { var f=PlatformGovernanceFactsService.SddlFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 混插_不一致不等於故障() { var f=PlatformGovernanceFactsService.MixMatchFact(At,()=>new(4,2,1,200)); Assert.Contains("不一致≠故障",f.Value); }
    [Fact] public void 混插_失敗_ReadError() { var f=PlatformGovernanceFactsService.MixMatchFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void RAS_不一致並列不裁決() { var f=PlatformGovernanceFactsService.RasFact(At,()=>new(4,2,1,1)); Assert.Contains("不一致並列不自動裁決",f.Value); }
    [Fact] public void RAS_失敗_ReadError() { var f=PlatformGovernanceFactsService.RasFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 驅動清單_不列名稱() { var f=PlatformGovernanceFactsService.DriverListFact(At,()=>new(10,9,1,0)); Assert.Contains("不列驅動名稱",f.Value); }
    [Fact] public void 驅動清單_失敗_ReadError() { var f=PlatformGovernanceFactsService.DriverListFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 規則史_不偽造() { var f=PlatformGovernanceFactsService.RuleHistoryFact(At,()=>new(3,5,2)); Assert.Contains("規則結果≠事實",f.Value); }
    [Fact] public void 規則史_無快照_NotApplicable() { var f=PlatformGovernanceFactsService.RuleHistoryFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void UWP_不列套件名稱() { var f=PlatformGovernanceFactsService.UwpFact(At,()=>new(10,2,1,7)); Assert.Contains("不列套件名稱",f.Value); }
    [Fact] public void SDK_不代表專案使用() { var f=PlatformGovernanceFactsService.SdkFact(At,()=>new(1,2,3,4)); Assert.Contains("不代表專案使用",f.Value); }
    [Fact] public void VRR_不代為判定() { var f=PlatformGovernanceFactsService.VrrFact(At,()=>new(2,2,1,1)); Assert.Contains("不代為判定",f.Value); }
    [Fact] public void 電池_無電池_NotApplicable() { var f=PlatformGovernanceFactsService.BatteryFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 電池_健康不預測壽命() { var f=PlatformGovernanceFactsService.BatteryFact(At,()=>new(1,50000,45000,100,null)); Assert.Contains("不預測剩餘壽命",f.Value); }
    [Fact] public void NIC趨勢_不列MAC() { var f=PlatformGovernanceFactsService.NicTrendFact(At,()=>new(3,4,5,2)); Assert.Contains("不列 MAC 或名稱",f.Value); }
    [Fact] public void NIC趨勢_失敗_ReadError() { var f=PlatformGovernanceFactsService.NicTrendFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts=PlatformGovernanceFactsService.Collect(At); string[] keys=[PlatformGovernanceFactsService.SddlKey,PlatformGovernanceFactsService.MixMatchKey,PlatformGovernanceFactsService.RasKey,PlatformGovernanceFactsService.DriverListKey,PlatformGovernanceFactsService.RuleHistoryKey,PlatformGovernanceFactsService.UwpKey,PlatformGovernanceFactsService.SdkKey,PlatformGovernanceFactsService.VrrKey,PlatformGovernanceFactsService.BatteryKey,PlatformGovernanceFactsService.NicTrendKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }

    // ── 服務 SDDL 判定（SA-020）────────────────────────────────────────────
    // 這一組的存在理由是一個真實缺陷：原版的 ReadSddl 只數服務數，另兩欄固定 0 並標 Present——
    // 「World 可寫 0」會被讀成「沒有服務是 World 可寫」。下面釘住的是「那兩欄真的由 ACE 算出來」。

    /// <summary>組一份只含單一 ACE 的安全性描述元，用來逐條驗判定規則。</summary>
    private static System.Security.AccessControl.RawSecurityDescriptor SdWith(
        System.Security.Principal.SecurityIdentifier sid, uint mask)
    {
        var ace = new System.Security.AccessControl.CommonAce(
            System.Security.AccessControl.AceFlags.None,
            System.Security.AccessControl.AceQualifier.AccessAllowed,
            unchecked((int)mask), sid, false, null);
        var dacl = new System.Security.AccessControl.RawAcl(2, 2);
        dacl.InsertAce(0, ace);
        // ⚠ 這個建構子在 systemAcl 傳 null 時，DiscretionaryAcl 屬性會是 null（實測）；
        // 傳一個「空的」RawAcl(0,1) 也沒用——DACL 會變成 0 個 ACE。
        // 只有讓第 4／5 個參數指向<b>同一個</b> RawAcl 的實例，ACE 才真的進得去（實測 1 個 ACE）。
        // 這是測試夾具的坑，不是判定端的：判定端看到 DACL 為 null 或空的，只能什麼都不判。
        return new System.Security.AccessControl.RawSecurityDescriptor(
            System.Security.AccessControl.ControlFlags.DiscretionaryAclPresent, null, null, dacl, dacl);
    }

    /// <summary>測試輔助：把描述元裡的 ACE 逐條列出來，紅燈時才知道是「沒讀到」還是「讀到但判錯」。</summary>
    private static string DescribeAces(System.Security.AccessControl.RawSecurityDescriptor sd) =>
        sd.DiscretionaryAcl is null
            ? "DACL 為 null"
            : string.Join("、", sd.DiscretionaryAcl.Cast<System.Security.AccessControl.GenericAce>()
                .Select(a => a is System.Security.AccessControl.CommonAce c
                    ? $"{a.AceType}/{c.AccessMask:X8}/{c.SecurityIdentifier.Value}"
                    : a.AceType.ToString()));

    private static System.Security.Principal.SecurityIdentifier Sid(
        System.Security.Principal.WellKnownSidType t) =>
        new(t, new System.Security.Principal.SecurityIdentifier("S-1-5-18"));

    [Fact] public void SDDL_World可寫是由ACE算出來的不是固定零()
    {
        var sd = SdWith(Sid(System.Security.Principal.WellKnownSidType.WorldSid), 0x00020000);
        var (reconf, world) = PlatformGovernanceFactsService.CountWritableAces(sd);
        Assert.True(world, $"WorldSid 的 WRITE_DAC 應被判為 World 可寫（ACE：{DescribeAces(sd)}）");
        Assert.True(reconf, $"WorldSid 也屬非管理員身分，應一併計入可重配置（ACE：{DescribeAces(sd)}）");
    }

    [Fact] public void SDDL_只給讀取權不算可重配置()
    {
        // SERVICE_QUERY_STATUS（0x0004）＋ SERVICE_START（0x0010）：能啟停不等於能重配置。
        var sd = SdWith(Sid(System.Security.Principal.WellKnownSidType.WorldSid), 0x0014);
        var (reconf, world) = PlatformGovernanceFactsService.CountWritableAces(sd);
        Assert.False(reconf);
        Assert.False(world);
    }

    [Fact] public void SDDL_本機Users群組按SID認定為非管理員()
    {
        // 服務 SDDL 常把本機 Users 群組寫成 S-1-5-32-545；WellKnownSidType 沒有對應成員，
        // 所以判定必須另外按 SID 字串比對，否則「非 admin 可重配置」會系統性漏報。
        // 這條釘住的就是那個實際發生的漏報（實測：修正前 0、修正後 78）。
        var users = new System.Security.Principal.SecurityIdentifier("S-1-5-32-545");
        Assert.True(PlatformGovernanceFactsService.IsNonAdminPrincipal(users));
        Assert.True(PlatformGovernanceFactsService.IsNonAdminPrincipal(
            new System.Security.Principal.SecurityIdentifier("S-1-5-32-547")));
    }

    [Fact] public void SDDL_系統與管理員不算非管理員()
    {
        Assert.False(PlatformGovernanceFactsService.IsNonAdminPrincipal(
            Sid(System.Security.Principal.WellKnownSidType.LocalSystemSid)));
        Assert.False(PlatformGovernanceFactsService.IsNonAdminPrincipal(
            Sid(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)));
    }

    /// <summary>
    /// 生產路徑的守門：<c>Collect</c> 不傳任何探針時，SDDL 那筆要嘛是<b>真的讀到的權限計數</b>，
    /// 要嘛是<b>誠實的 ReadError／NotApplicable</b>——不可以是「Present 但兩欄都是 0」。
    /// <para>
    /// 這正是先前的缺陷形狀：ReadSddl 只數服務數、兩欄固定 0 並標 Present，
    /// 於是每一台機器都得到「非 admin 可重配置 0、World 可寫 0」，看起來像讀過了。
    /// 注意斷言的方向——不是斷言「一定有值」（那在沒權限的機器上會誤紅），
    /// 而是斷言「有值就必須真的讀過」。
    /// </para>
    /// </summary>
    [Fact] public void SDDL_生產路徑不得回Present卻兩欄皆零()
    {
        var f = Assert.Single(PlatformGovernanceFactsService.Collect(At), x => x.Key == PlatformGovernanceFactsService.SddlKey);
        if (f.Availability != FactAvailability.Present)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason),
                "不是 Present 就必須說明原因——讀不到要說讀不到");
            return;
        }

        // Present：必須來自真正的描述元讀取。本機（已實測）會有非 admin 可重配置的服務，
        // 所以「兩欄都是 0」代表讀取根本沒發生，或判定退化成了舊版的假 stub。
        var counts = System.Text.RegularExpressions.Regex.Matches(f.Value, @"\d+");
        Assert.True(counts.Count >= 3, $"值裡應同時有服務數與兩個計數：{f.Value}");
        Assert.DoesNotContain("非 admin 可重配置 0、World 可寫 0", f.Value);
    }
}