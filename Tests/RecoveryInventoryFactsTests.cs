using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class RecoveryInventoryFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void LiveKernel_只列計數不讀dump() { var f=RecoveryInventoryFactsService.LiveKernelFact(At,()=>new(3,2,1)); Assert.Contains("報告 3",f.Value); Assert.Contains("不讀 dump 內容",f.Value); }
    [Fact] public void LiveKernel_失敗_ReadError() { var f=RecoveryInventoryFactsService.LiveKernelFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void WER_不列程式名稱() { var f=RecoveryInventoryFactsService.WerFact(At,()=>new(5,4,1)); Assert.Contains("不列程式名稱",f.Value); }
    [Fact] public void WER_失敗_ReadError() { var f=RecoveryInventoryFactsService.WerFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 懸掛_痕跡不是根因() { var f=RecoveryInventoryFactsService.HangFact(At,()=>new(2,30)); Assert.Contains("痕跡不是根因",f.Value); }
    [Fact] public void 懸掛_失敗_ReadError() { var f=RecoveryInventoryFactsService.HangFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void Pending_不列檔名() { var f=RecoveryInventoryFactsService.PendingFact(At,()=>new(2,1)); Assert.Contains("不列檔名或路徑",f.Value); }
    [Fact] public void Pending_失敗_ReadError() { var f=RecoveryInventoryFactsService.PendingFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void WinSxS_不清理不修復() { var f=RecoveryInventoryFactsService.WinSxsFact(At,()=>new(1,0,2)); Assert.Contains("不清理、不修復",f.Value); }
    [Fact] public void WinSxS_失敗_ReadError() { var f=RecoveryInventoryFactsService.WinSxsFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 還原點_不建立或還原() { var f=RecoveryInventoryFactsService.RestorePointFact(At,()=>new(3,At)); Assert.Contains("不建立或還原",f.Value); }
    [Fact] public void 還原點_未啟用_NotApplicable() { var f=RecoveryInventoryFactsService.RestorePointFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 備份_不執行備份() { var f=RecoveryInventoryFactsService.BackupFact(At,()=>new(1,null,4)); Assert.Contains("不執行備份或還原",f.Value); }
    [Fact] public void 備份_失敗_ReadError() { var f=RecoveryInventoryFactsService.BackupFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 資源耗盡_痕跡不是根因() { var f=RecoveryInventoryFactsService.ExhaustFact(At,()=>new(4,3,1)); Assert.Contains("痕跡不是根因",f.Value); }
    [Fact] public void 資源耗盡_失敗_ReadError() { var f=RecoveryInventoryFactsService.ExhaustFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 行程IO_不列程序名稱() { var f=RecoveryInventoryFactsService.ProcessIoFact(At,()=>new(100,100,12345)); Assert.Contains("不列程序名稱",f.Value); }
    [Fact] public void 行程IO_失敗_ReadError() { var f=RecoveryInventoryFactsService.ProcessIoFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 連結審計_不列目標路徑() { var f=RecoveryInventoryFactsService.LinkFact(At,()=>new(4,2,1,1)); Assert.Contains("不列目標路徑",f.Value); Assert.Contains("風險面不是判決",f.Value); }
    [Fact] public void 連結審計_失敗_ReadError() { var f=RecoveryInventoryFactsService.LinkFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts=RecoveryInventoryFactsService.Collect(At); string[] keys=[RecoveryInventoryFactsService.LiveKernelKey,RecoveryInventoryFactsService.WerKey,RecoveryInventoryFactsService.HangKey,RecoveryInventoryFactsService.PendingKey,RecoveryInventoryFactsService.WinSxsKey,RecoveryInventoryFactsService.RestorePointKey,RecoveryInventoryFactsService.BackupKey,RecoveryInventoryFactsService.ExhaustKey,RecoveryInventoryFactsService.ProcessIoKey,RecoveryInventoryFactsService.LinkKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}