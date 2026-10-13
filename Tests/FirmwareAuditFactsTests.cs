using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class FirmwareAuditFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void BitLocker_金鑰永不輸出() { var f=FirmwareAuditFactsService.BitLockerFact(At,()=>new(2,1,3,1)); Assert.Contains("金鑰值永不輸出",f.Value); }
    [Fact] public void BitLocker_提供者不存在_NotApplicable() { var f=FirmwareAuditFactsService.BitLockerFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void Capsule_不上傳() { var f=FirmwareAuditFactsService.CapsuleFact(At,()=>new(2,1,1)); Assert.Contains("不上傳",f.Value); }
    [Fact] public void Capsule_無檔_NotApplicable() { var f=FirmwareAuditFactsService.CapsuleFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void SPI權限_不寫flash() { var f=FirmwareAuditFactsService.SpiPermFact(At,()=>new(3,2,1)); Assert.Contains("不寫 flash",f.Value); }
    [Fact] public void SMM_不判等級() { var f=FirmwareAuditFactsService.SmmFact(At,()=>new(1,8,2)); Assert.Contains("不判等級",f.Value); }
    [Fact] public void SMM_缺權限_InsufficientPrivilege() { var f=FirmwareAuditFactsService.SmmFact(At,()=>null); Assert.Equal(FactAvailability.InsufficientPrivilege,f.Availability); }
    [Fact] public void dbx_表過期明說() { var f=FirmwareAuditFactsService.DbxGenerationFact(At,()=>new(10,3,true)); Assert.Contains("表過期明說",f.Value); }
    [Fact] public void dbx_失敗_ReadError() { var f=FirmwareAuditFactsService.DbxGenerationFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void GPT_只審計不修復() { var f=FirmwareAuditFactsService.GptFact(At,()=>new(2,5,0,0)); Assert.Contains("只審計不修復",f.Value); }
    [Fact] public void BCD_只審計不修改() { var f=FirmwareAuditFactsService.BcdFact(At,()=>new(3,1,0,1)); Assert.Contains("只審計不修改",f.Value); }
    [Fact] public void ELAM_裝了不等於在跑() { var f=FirmwareAuditFactsService.ElamFact(At,()=>new(4,4)); Assert.Contains("裝了≠在跑",f.Value); }
    [Fact] public void WDAC_判讀不執行() { var f=FirmwareAuditFactsService.WdacFact(At,()=>new("enterprise",2,1)); Assert.Contains("判讀不執行",f.Value); Assert.DoesNotContain("enterprise",f.Value); }
    [Fact] public void 開機鏈_缺口列未收集() { var f=FirmwareAuditFactsService.BootChainFact(At); Assert.Contains("缺口列「未收集」",f.Value); }
    [Fact] public void 本批十把鍵齊() { var facts=FirmwareAuditFactsService.Collect(At); string[] keys=[FirmwareAuditFactsService.BitLockerKey,FirmwareAuditFactsService.CapsuleKey,FirmwareAuditFactsService.SpiPermKey,FirmwareAuditFactsService.SmmKey,FirmwareAuditFactsService.DbxGenerationKey,FirmwareAuditFactsService.GptKey,FirmwareAuditFactsService.BcdKey,FirmwareAuditFactsService.ElamKey,FirmwareAuditFactsService.WdacKey,FirmwareAuditFactsService.BootChainKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}