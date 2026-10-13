using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class SecurityAuditSupplementFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void COM劫持_覆蓋不等於惡意() { var f=SecurityAuditSupplementFactsService.ComHijackFact(At,()=>new(5,100,3)); Assert.Contains("覆蓋不等於惡意",f.Value); Assert.DoesNotContain("CLSID",f.Value.Replace("不列 CLSID","")); }
    [Fact] public void COM劫持_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.ComHijackFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 關聯_無基線聲明() { var f=SecurityAuditSupplementFactsService.AssocFact(At,()=>new(1,1,4)); Assert.Contains("無基線聲明",f.Value); }
    [Fact] public void 關聯_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.AssocFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void ADS_掃描範圍聲明() { var f=SecurityAuditSupplementFactsService.AdsFact(At,()=>new(100,2,2)); Assert.Contains("掃描範圍限系統可執行檔",f.Value); }
    [Fact] public void ADS_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.AdsFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 簽章_不列程式名稱() { var f=SecurityAuditSupplementFactsService.UninstFact(At,()=>new(10,8,2,1)); Assert.Contains("不列程式名稱",f.Value); }
    [Fact] public void 簽章_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.UninstFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 帳戶_存在性非判定() { var f=SecurityAuditSupplementFactsService.AccountFact(At,()=>new(2,2,1,0)); Assert.Contains("存在性非判定",f.Value); Assert.Contains("不列使用者名稱",f.Value); }
    [Fact] public void 帳戶_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.AccountFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 自動登入_值永不輸出() { var f=SecurityAuditSupplementFactsService.AutoLogonFact(At,()=>new(true,true,true)); Assert.Contains("值永不輸出",f.Value); Assert.Contains("L3 禁令",f.Value); }
    [Fact] public void 自動登入_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.AutoLogonFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 登入歷史_聚合不列使用者() { var f=SecurityAuditSupplementFactsService.LogonFact(At,()=>new(10,2,1,30)); Assert.Contains("聚合層級聲明",f.Value); Assert.Contains("不列使用者名稱",f.Value); }
    [Fact] public void 登入歷史_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.LogonFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 服務ACL_可寫是劫持面() { var f=SecurityAuditSupplementFactsService.ServiceBinaryFact(At,()=>new(10,1,0)); Assert.Contains("劫持面非已劫持",f.Value); }
    [Fact] public void 服務ACL_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.ServiceBinaryFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void PATH_不列路徑() { var f=SecurityAuditSupplementFactsService.PathFact(At,()=>new(5,1,0)); Assert.Contains("不列目錄路徑",f.Value); }
    [Fact] public void PATH_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.PathFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void WMI訂閱_存在不等於惡意() { var f=SecurityAuditSupplementFactsService.WmiSubFact(At,()=>new(1,1,1)); Assert.Contains("存在≠惡意",f.Value); Assert.Contains("不列查詢或命令",f.Value); }
    [Fact] public void WMI訂閱_失敗_ReadError() { var f=SecurityAuditSupplementFactsService.WmiSubFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts=SecurityAuditSupplementFactsService.Collect(At); string[] keys=[SecurityAuditSupplementFactsService.ComHijackKey,SecurityAuditSupplementFactsService.AssocKey,SecurityAuditSupplementFactsService.AdsKey,SecurityAuditSupplementFactsService.UninstKey,SecurityAuditSupplementFactsService.AccountKey,SecurityAuditSupplementFactsService.AutoLogonKey,SecurityAuditSupplementFactsService.LogonKey,SecurityAuditSupplementFactsService.ServiceBinaryKey,SecurityAuditSupplementFactsService.PathKey,SecurityAuditSupplementFactsService.WmiSubKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}