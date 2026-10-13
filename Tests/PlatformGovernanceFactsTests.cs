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
}