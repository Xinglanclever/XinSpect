using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class PlatformExpansionFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void 驅動史_未比較不冒充零變更() { var f=PlatformExpansionFactsService.DriverHistoryFact(At,()=>[new("id","裝置","1.0",null)]); Assert.Contains("沒有變更可陳述",f.Value); Assert.Contains("不把「未比較」寫成 0 筆變更",f.Value); }
    [Fact] public void 驅動史_新增移除變更計數() { var prev=new[]{new PlatformExpansionFactsService.DriverVersionRow("a","A","1",null),new PlatformExpansionFactsService.DriverVersionRow("b","B","2",null)}; var now=new[]{new PlatformExpansionFactsService.DriverVersionRow("a","A","3",null),new PlatformExpansionFactsService.DriverVersionRow("c","C","1",null)}; var f=PlatformExpansionFactsService.DriverHistoryFact(At,()=>now,prev); Assert.Contains("版本變更 1",f.Value); Assert.Contains("新增 1",f.Value); Assert.Contains("移除 1",f.Value); }
    [Fact] public void EOL包_驗證狀態與快照必印() { var f=PlatformExpansionFactsService.EolPackFact(At,()=>new(3,"2026-10-13",4,true)); Assert.Contains("快照 2026-10-13",f.Value); Assert.Contains("內容雜湊驗證",f.Value); }
    [Fact] public void AML統計_不解碼本體() { var f=PlatformExpansionFactsService.AmlStatFact(At,()=>new(2,100,10,90)); Assert.Contains("不解碼 AML 本體",f.Value); Assert.Equal(100.0,f.NumericValue); }
    [Fact] public void SMI_只有次數沒有時間() { var f=PlatformExpansionFactsService.SmiCstateFact(At,()=>new(10,2,50UL,100UL,0.25)); Assert.Contains("沒有時間",f.Value); Assert.Contains("50.00%",f.Value); }
    [Fact] public void 功率牆_讀不到不宣稱沒有牆() { var f=PlatformExpansionFactsService.PowerWallFact(At,()=>null); Assert.Equal(FactAvailability.InsufficientPrivilege,f.Availability); Assert.Contains("不宣稱",f.UnavailableReason); }
    [Fact] public void 功率牆_正常列PL1PL2() { var f=PlatformExpansionFactsService.PowerWallFact(At,()=>new(0xA0E03UL,0x0043FFF800DEFFF8UL,0x00000B58U)); Assert.Contains("PL1",f.Value); Assert.Contains("PL2",f.Value); Assert.Equal(FactAvailability.Present,f.Availability); }
    [Fact] public void 記憶體訓練_無事件不寫零毫秒() { var f=PlatformExpansionFactsService.MemoryTrainingFact(At,()=>new(0,0,null)); Assert.Contains("不把無數字寫成 0 毫秒",f.Value); Assert.Null(f.NumericValue); }
    [Fact] public void 熱引導_兩源失敗不猜() { var f=PlatformExpansionFactsService.HotGuidanceFact(At,()=>null,()=>null); Assert.Equal(FactAvailability.InsufficientPrivilege,f.Availability); }
    [Fact] public void NIC慢_不列MAC或名稱() { var f=PlatformExpansionFactsService.NicSlowFact(At,()=>[new("p",100_000_000,false,false)]); Assert.Contains("不列 MAC／名稱",f.Value); Assert.Contains("低速鏈路 1",f.Value); }
    [Fact] public void 更新卡住_只讀不修復() { var f=PlatformExpansionFactsService.UpdateStuckFact(At,()=>new(2,1,1,1)); Assert.Contains("不修復",f.Value); Assert.Equal(1.0,f.NumericValue); }
    [Fact] public void 崩潰堆疊_未同意永不執行() { var f=PlatformExpansionFactsService.CrashStackFact(At,_=>false); Assert.Equal(FactAvailability.NotApplicable,f.Availability); Assert.Contains("不碰任何行程記憶體",f.UnavailableReason); }
    [Fact] public void 崩潰堆疊_同意但無實作不假裝做過() { var f=PlatformExpansionFactsService.CrashStackFact(At,_=>true); Assert.Equal(FactAvailability.NotSupported,f.Availability); Assert.Contains("不假裝做過",f.UnavailableReason); }
    [Fact] public void 本批十把鍵齊() { var facts=PlatformExpansionFactsService.Collect(At); string[] keys=[PlatformExpansionFactsService.DriverHistoryKey,PlatformExpansionFactsService.EolPackKey,PlatformExpansionFactsService.AmlStatKey,PlatformExpansionFactsService.SmiCstateKey,PlatformExpansionFactsService.PowerWallKey,PlatformExpansionFactsService.MemTrainKey,PlatformExpansionFactsService.HotGuidanceKey,PlatformExpansionFactsService.NicSlowKey,PlatformExpansionFactsService.UpdateStuckKey,PlatformExpansionFactsService.CrashStackKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}