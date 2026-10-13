using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class PlatformDeepDiveFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void 熱節流_log缺是時間窗聲明() { var f=PlatformDeepDiveFactsService.ThermalFact(At,()=>new(2,30,null)); Assert.Contains("時間窗聲明",f.Value); }
    [Fact] public void 熱節流_失敗_ReadError() { var f=PlatformDeepDiveFactsService.ThermalFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void WHEA_未施測不等於沒問題() { var f=PlatformDeepDiveFactsService.WheaFact(At,()=>new(2,1,3,At)); Assert.Contains("未施測≠沒問題",f.Value); }
    [Fact] public void WHEA_失敗_ReadError() { var f=PlatformDeepDiveFactsService.WheaFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void Uncore_只讀PMU() { var f=PlatformDeepDiveFactsService.UncoreFact(At,()=>new(5,1000,2000,1500)); Assert.Contains("只讀 PMU 讀數",f.Value); }
    [Fact] public void Uncore_失敗_ReadError() { var f=PlatformDeepDiveFactsService.UncoreFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void SSD韌體_快照日期必印() { var f=PlatformDeepDiveFactsService.SsdFirmwareFact(At,()=>new(2,1,1)); Assert.Contains("快照日期",f.Value); }
    [Fact] public void SSD韌體_失敗_ReadError() { var f=PlatformDeepDiveFactsService.SsdFirmwareFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 四K對齊_未對齊不是故障() { var f=PlatformDeepDiveFactsService.AlignFact(At,()=>new(5,4,1,0)); Assert.Contains("不是故障",f.Value); }
    [Fact] public void 四K對齊_失敗_ReadError() { var f=PlatformDeepDiveFactsService.AlignFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void DWPD_明標估計() { var f=PlatformDeepDiveFactsService.DwpdFact(At,()=>new(2,100000,0.5,5)); Assert.Contains("明標估計",f.Value); }
    [Fact] public void DWPD_失敗_ReadError() { var f=PlatformDeepDiveFactsService.DwpdFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void KB_查無不代填() { var f=PlatformDeepDiveFactsService.KbKnownFact(At,()=>new(10,2,true)); Assert.Contains("查無不代填",f.Value); }
    [Fact] public void KB_失敗_ReadError() { var f=PlatformDeepDiveFactsService.KbKnownFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void QPC_非長期保證() { var f=PlatformDeepDiveFactsService.QpcFact(At,()=>new(1e6,100,0.1)); Assert.Contains("非長期保證",f.Value); }
    [Fact] public void QPC_失敗_ReadError() { var f=PlatformDeepDiveFactsService.QpcFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void HAGS_不切換() { var f=PlatformDeepDiveFactsService.HagsFact(At,()=>new(2,1,1)); Assert.Contains("不切換",f.Value); }
    [Fact] public void HAGS_失敗_ReadError() { var f=PlatformDeepDiveFactsService.HagsFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void DVR_不列遊戲或檔案() { var f=PlatformDeepDiveFactsService.DvrFact(At,()=>new(1,1,3)); Assert.Contains("不列遊戲名稱或檔案",f.Value); }
    [Fact] public void DVR_失敗_ReadError() { var f=PlatformDeepDiveFactsService.DvrFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts=PlatformDeepDiveFactsService.Collect(At); string[] keys=[PlatformDeepDiveFactsService.ThermalKey,PlatformDeepDiveFactsService.WheaKey,PlatformDeepDiveFactsService.UncoreKey,PlatformDeepDiveFactsService.SsdFwKey,PlatformDeepDiveFactsService.AlignKey,PlatformDeepDiveFactsService.DwpdKey,PlatformDeepDiveFactsService.KbKnownKey,PlatformDeepDiveFactsService.QpcKey,PlatformDeepDiveFactsService.HagsKey,PlatformDeepDiveFactsService.DvrKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}