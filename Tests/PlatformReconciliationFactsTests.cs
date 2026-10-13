using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class PlatformReconciliationFactsTests
{
    private static readonly DateTimeOffset At=new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void Turbo_讀不到不猜() { var f=PlatformReconciliationFactsService.TurboFact(At,()=>null); Assert.Equal(FactAvailability.InsufficientPrivilege,f.Availability); Assert.Contains("不猜",f.UnavailableReason); }
    [Fact] public void Turbo_有值列組數與解鎖() { var f=PlatformReconciliationFactsService.TurboFact(At,()=>new(3,42,true,true)); Assert.Contains("最高倍頻 42",f.Value); Assert.Contains("解鎖 是",f.Value); }
    [Fact] public void 能效_少於兩點不宣稱() { var f=PlatformReconciliationFactsService.EffCurveFact(At,()=>[new(3000,50)]); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 能效_兩點以上列範圍() { var f=PlatformReconciliationFactsService.EffCurveFact(At,()=>[new(3000,50),new(4000,90)]); Assert.Contains("樣本 2",f.Value); Assert.Contains("50",f.Value); }
    [Fact] public void MRC_只盤點不解碼() { var f=PlatformReconciliationFactsService.MrcFact(At,()=>new(2,1,4096)); Assert.Contains("不解碼",f.Value); Assert.Equal(2.0,f.NumericValue); }
    [Fact] public void 記憶體延遲_缺樣本不宣稱() { var f=PlatformReconciliationFactsService.MemLatFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 記憶體延遲_列階層與量測界線() { var f=PlatformReconciliationFactsService.MemLatFact(At,()=>[new("L1",1.2),new("RAM",80)]); Assert.Contains("L1 1.2 ns",f.Value); Assert.Contains("不是記憶體規格",f.Value); }
    [Fact] public void TLB_缺斷點不冒充硬體事實() { var f=PlatformReconciliationFactsService.TlbFact(At,()=>[]); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void TLB_列working_set與大頁() { var f=PlatformReconciliationFactsService.TlbFact(At,()=>[new(2*1024*1024,false,12.5),new(64*1024*1024,true,9.5)]); Assert.Contains("2 MiB 4K",f.Value); Assert.Contains("大頁",f.Value); }
    [Fact] public void 逐核對稱_缺樣本不裁決() { var f=PlatformReconciliationFactsService.CoreSymFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 逐核對稱_跨幅是觀察不是故障() { var f=PlatformReconciliationFactsService.CoreSymFact(At,()=>new(18,3500,4200,20)); Assert.Contains("不是故障",f.Value); Assert.Equal(20.0,f.NumericValue); }
    [Fact] public void 電壓_無可靠通路不猜() { var f=PlatformReconciliationFactsService.VoltFact(At,()=>new(null,null,null,false)); Assert.Equal(FactAvailability.NotSupported,f.Availability); Assert.Contains("不猜",f.UnavailableReason); }
    [Fact] public void 電壓_值以伏特呈現() { var f=PlatformReconciliationFactsService.VoltFact(At,()=>new(1.05,1.1,1.2,true)); Assert.Contains("VCCSA 1.05 V",f.Value); Assert.Contains("VDDQ 1.2 V",f.Value); }
    [Fact] public void 步進_讀不到不代填() { var f=PlatformReconciliationFactsService.SteppingFact(At,()=>null); Assert.Equal(FactAvailability.NotSupported,f.Availability); }
    [Fact] public void 步進_快照日期必印() { var f=PlatformReconciliationFactsService.SteppingFact(At,()=>(6,0x55,4)); Assert.Contains("快照日期 2026-10-13",f.Value); Assert.Contains("已知問題提示",f.Value); }
    [Fact] public void 親和性_失敗不猜() { var f=PlatformReconciliationFactsService.AffinityFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 親和性_不列程式名稱() { var f=PlatformReconciliationFactsService.AffinityFact(At,()=>new(10,2,3,5)); Assert.Contains("不列程式名稱",f.Value); Assert.Equal(2.0,f.NumericValue); }
    [Fact] public void NUMA_單節點明確不適用() { var f=PlatformReconciliationFactsService.NumaProcFact(At,()=>new(1,10,0,true)); Assert.Equal(FactAvailability.NotApplicable,f.Availability); Assert.Contains("未驗證不代表可用",f.UnavailableReason); }
    [Fact] public void NUMA_多節點列多節點行程計數() { var f=PlatformReconciliationFactsService.NumaProcFact(At,()=>new(2,10,3,false)); Assert.Contains("多節點行程 3",f.Value); Assert.Contains("不列程式名稱",f.Value); }
    [Fact] public void 本批十把鍵齊() { var facts=PlatformReconciliationFactsService.Collect(At); string[] keys=[PlatformReconciliationFactsService.TurboKey,PlatformReconciliationFactsService.EffCurveKey,PlatformReconciliationFactsService.MrcKey,PlatformReconciliationFactsService.MemLatKey,PlatformReconciliationFactsService.TlbKey,PlatformReconciliationFactsService.CoreSymKey,PlatformReconciliationFactsService.VoltKey,PlatformReconciliationFactsService.SteppingKey,PlatformReconciliationFactsService.AffinityKey,PlatformReconciliationFactsService.NumaProcKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }

}
