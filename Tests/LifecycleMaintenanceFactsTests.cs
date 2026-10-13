using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class LifecycleMaintenanceFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void OS_EOL_快照日期必印() { var f=LifecycleMaintenanceFactsService.OsEolFact(At,()=>new("Windows","10.0", "2034-10-10", 2940, true)); Assert.Contains("快照日期",f.Value); }
    [Fact] public void OS_EOL_查無不代填() { var f=LifecycleMaintenanceFactsService.OsEolFact(At,()=>new("Windows","x",null,0,false)); Assert.Contains("查無≠永久支援",f.Value); }
    [Fact] public void 驅動EOL_覆蓋有限聲明() { var f=LifecycleMaintenanceFactsService.DriverEolFact(At,()=>new(10,2,1)); Assert.Contains("覆蓋有限聲明",f.Value); }
    [Fact] public void BIOS_EOL_查無不代填() { var f=LifecycleMaintenanceFactsService.BiosEolFact(At,()=>new("BIOS","1.0",null,0,false)); Assert.Contains("查無不代填",f.Value); }
    [Fact] public void 公告對照_查無不等於沒有() { var f=LifecycleMaintenanceFactsService.AdvisoryFact(At,()=>new(5,1)); Assert.Contains("查無≠沒有公告",f.Value); }
    [Fact] public void EOL儀表_不推測EOL() { var f=LifecycleMaintenanceFactsService.EolDashFact(At); Assert.Contains("不自行推測 EOL",f.Value); }
    [Fact] public void 依賴完整性_未簽章不是惡意() { var f=LifecycleMaintenanceFactsService.DepsFact(At,()=>new(10,8,1,1)); Assert.Contains("未簽章不是惡意判決",f.Value); }
    [Fact] public void 依賴完整性_失敗_ReadError() { var f=LifecycleMaintenanceFactsService.DepsFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 安裝時長_不列標題() { var f=LifecycleMaintenanceFactsService.InstallDurationFact(At,()=>[("x",At,5.0),("y",At,10.0)]); Assert.Contains("平均 7.5",f.Value); Assert.Contains("不列更新標題",f.Value); }
    [Fact] public void 驅動更新_分類不等於品質() { var f=LifecycleMaintenanceFactsService.DriverUpdateFact(At,()=>[("driver a",At,0),("firmware b",At,0),("other",At,0)]); Assert.Contains("驅動 1",f.Value); Assert.Contains("分類≠品質判定",f.Value); }
    [Fact] public void 更新大小_不代填() { var f=LifecycleMaintenanceFactsService.UpdateSizeFact(At,()=>[("x",At,0)]); Assert.Equal(FactAvailability.Unknown,f.Availability); Assert.Contains("不代填",f.Value); }
    [Fact] public void 任務結果碼_不判故障() { var f=LifecycleMaintenanceFactsService.TaskResultFact(At,()=>new(10,6,2,2)); Assert.Contains("結果碼≠故障判決",f.Value); }
    [Fact] public void 隱藏任務_隱藏不等於惡意() { var f=LifecycleMaintenanceFactsService.HiddenTaskFact(At,()=>new(10,2,1)); Assert.Contains("隱藏≠惡意",f.Value); Assert.Contains("不列工作名稱或命令",f.Value); }
    [Fact] public void 本批十一把鍵齊() { var facts=LifecycleMaintenanceFactsService.Collect(At); string[] keys=[LifecycleMaintenanceFactsService.OsEolKey,LifecycleMaintenanceFactsService.DriverEolKey,LifecycleMaintenanceFactsService.BiosEolKey,LifecycleMaintenanceFactsService.AdvisoryKey,LifecycleMaintenanceFactsService.EolDashKey,LifecycleMaintenanceFactsService.DepsKey,LifecycleMaintenanceFactsService.InstallDurationKey,LifecycleMaintenanceFactsService.DriverUpdateKey,LifecycleMaintenanceFactsService.UpdateSizeKey,LifecycleMaintenanceFactsService.TaskResultKey,LifecycleMaintenanceFactsService.HiddenTaskKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(11,facts.Count); }
}