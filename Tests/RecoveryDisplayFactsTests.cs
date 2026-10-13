using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class RecoveryDisplayFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void ACL_不列路徑() { var f=RecoveryDisplayFactsService.AclFact(At,()=>new(2,1,0)); Assert.Contains("抽查目錄 2",f.Value); Assert.Contains("不列路徑",f.Value); }
    [Fact] public void ACL_失敗_ReadError() { var f=RecoveryDisplayFactsService.AclFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void VSS_不建立或掛載() { var f=RecoveryDisplayFactsService.VssFact(At,()=>new(3,5,0)); Assert.Contains("不建立、刪除或掛載",f.Value); }
    [Fact] public void VSS_失敗_ReadError() { var f=RecoveryDisplayFactsService.VssFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void MSI孤兒_刪除留給人() { var f=RecoveryDisplayFactsService.MsiOrphanFact(At,()=>new(10,8,2)); Assert.Contains("刪除判斷留給人",f.Value); }
    [Fact] public void MSI孤兒_失敗_ReadError() { var f=RecoveryDisplayFactsService.MsiOrphanFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 喚醒來源_不列裝置名稱() { var f=RecoveryDisplayFactsService.WakeSourceFact(At,()=>new(3,2,"x")); Assert.Contains("不列裝置名稱",f.Value); }
    [Fact] public void 喚醒來源_失敗_ReadError() { var f=RecoveryDisplayFactsService.WakeSourceFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void SleepStudy_未施測不代填() { var f=RecoveryDisplayFactsService.SleepStudyFact(At,()=>new(2,1,3)); Assert.Contains("未施測不代填",f.Value); }
    [Fact] public void SleepStudy_無報告_NotApplicable() { var f=RecoveryDisplayFactsService.SleepStudyFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 計畫漂移_不切換不顯示GUID() { var f=RecoveryDisplayFactsService.PlanDriftFact(At,()=>new("guid",2,1)); Assert.Contains("不切換",f.Value); Assert.Contains("不顯示 GUID",f.Value); Assert.DoesNotContain("guid",f.Value); }
    [Fact] public void 計畫漂移_失敗_ReadError() { var f=RecoveryDisplayFactsService.PlanDriftFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void ModernStandby_未施測聲明() { var f=RecoveryDisplayFactsService.ModernStandbyFact(At,()=>new(3,2,1)); Assert.Contains("未施測明確聲明",f.Value); }
    [Fact] public void ModernStandby_失敗_ReadError() { var f=RecoveryDisplayFactsService.ModernStandbyFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void TDR_痕跡不是根因() { var f=RecoveryDisplayFactsService.TdrFact(At,()=>new(2,At)); Assert.Contains("痕跡不是根因",f.Value); }
    [Fact] public void TDR_失敗_ReadError() { var f=RecoveryDisplayFactsService.TdrFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void DirectX_不跑渲染測試() { var f=RecoveryDisplayFactsService.DirectXFact(At,()=>new(2,2,1)); Assert.Contains("不跑渲染測試",f.Value); }
    [Fact] public void DirectX_失敗_ReadError() { var f=RecoveryDisplayFactsService.DirectXFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 熱插拔_不讀事件內容() { var f=RecoveryDisplayFactsService.HotPlugFact(At,()=>new(3,2,30)); Assert.Contains("不讀事件內容",f.Value); }
    [Fact] public void 熱插拔_失敗_ReadError() { var f=RecoveryDisplayFactsService.HotPlugFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts=RecoveryDisplayFactsService.Collect(At); string[] keys=[RecoveryDisplayFactsService.AclKey,RecoveryDisplayFactsService.VssKey,RecoveryDisplayFactsService.MsiOrphanKey,RecoveryDisplayFactsService.WakeSourceKey,RecoveryDisplayFactsService.SleepStudyKey,RecoveryDisplayFactsService.PlanDriftKey,RecoveryDisplayFactsService.ModernStandbyKey,RecoveryDisplayFactsService.TdrKey,RecoveryDisplayFactsService.DirectXKey,RecoveryDisplayFactsService.HotPlugKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}