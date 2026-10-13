using System;
using Xunit;
using XinSpect;
namespace XinSpect.Tests;
public class FinalInventoryFactsTests
{
    private static readonly DateTimeOffset At = new(2026,10,13,23,0,0,TimeSpan.Zero);
    [Fact] public void 藍牙配對_快取殘留是Unknown() { var f=FinalInventoryFactsService.BluetoothPairFact(At,()=>new(3,2,5)); Assert.Contains("快取殘留 5",f.Value); Assert.Contains("Unknown",f.Value); }
    [Fact] public void 藍牙配對_失敗_ReadError() { var f=FinalInventoryFactsService.BluetoothPairFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void USB描述符_協商速度未施測() { var f=FinalInventoryFactsService.UsbDescFact(At,()=>new(10,2,4)); Assert.Contains("USB 裝置 10",f.Value); Assert.Contains("協商速度未施測",f.Value); }
    [Fact] public void USB描述符_失敗_ReadError() { var f=FinalInventoryFactsService.UsbDescFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void EDID_不列序號() { var f=FinalInventoryFactsService.EdidFact(At,()=>new(2,2,1,2)); Assert.Contains("顯示器 2",f.Value); Assert.Contains("不列序號",f.Value); }
    [Fact] public void EDID_失敗_ReadError() { var f=FinalInventoryFactsService.EdidFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 魔術封包_沒有計數器不等於沒收到() { var f=FinalInventoryFactsService.WolPacketFact(At,()=>new(0,0)); Assert.Contains("沒有計數器不等於沒收到",f.Value); Assert.Equal(FactAvailability.Present,f.Availability); }
    [Fact] public void 魔術封包_失敗_NotApplicable() { var f=FinalInventoryFactsService.WolPacketFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void LSA_缺源如實列缺源() { var f=FinalInventoryFactsService.LsaFact(At,()=>new(null,null,0)); Assert.Contains("未設定",f.Value); Assert.Contains("缺源如實列缺源",f.Value); }
    [Fact] public void LSA_失敗_ReadError() { var f=FinalInventoryFactsService.LsaFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void AppX_誤報率聲明() { var f=FinalInventoryFactsService.AppxFact(At,()=>new(10,1,2)); Assert.Contains("誤報率",f.Value); Assert.Contains("不列套件名稱或憑證",f.Value); }
    [Fact] public void AppX_失敗_ReadError() { var f=FinalInventoryFactsService.AppxFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 虛擬化矛盾_是待查不是錯誤() { var f=FinalInventoryFactsService.VirtContraFact(At,()=>new(true,"Off",true)); Assert.Contains("矛盾 是",f.Value); Assert.Contains("不是錯誤",f.Value); }
    [Fact] public void 虛擬化矛盾_失敗_ReadError() { var f=FinalInventoryFactsService.VirtContraFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 排程任務_孤兒是風險面() { var f=FinalInventoryFactsService.TaskFact(At,()=>new(5,3,1,2)); Assert.Contains("登入觸發 5",f.Value); Assert.Contains("風險面不是判決",f.Value); }
    [Fact] public void 排程任務_失敗_ReadError() { var f=FinalInventoryFactsService.TaskFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void 時鐘源_不顯示伺服器() { var f=FinalInventoryFactsService.TimeSourceFact(At,()=>new(true,"NTP","time.example")); Assert.Contains("已設定",f.Value); Assert.Contains("不顯示伺服器名稱",f.Value); Assert.DoesNotContain("time.example",f.Value); }
    [Fact] public void 時鐘源_失敗_ReadError() { var f=FinalInventoryFactsService.TimeSourceFact(At,()=>null); Assert.Equal(FactAvailability.ReadError,f.Availability); }
    [Fact] public void RTC漂移_限制已標註() { var f=FinalInventoryFactsService.RtcDriftFact(At,()=>new(100,102,2,7)); Assert.Contains("需關機／重啟配合",f.Value); }
    [Fact] public void RTC漂移_無樣本_NotApplicable() { var f=FinalInventoryFactsService.RtcDriftFact(At,()=>null); Assert.Equal(FactAvailability.NotApplicable,f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts=FinalInventoryFactsService.Collect(At); string[] keys=[FinalInventoryFactsService.BluetoothPairKey,FinalInventoryFactsService.UsbDescKey,FinalInventoryFactsService.EdidKey,FinalInventoryFactsService.WolPacketKey,FinalInventoryFactsService.LsaKey,FinalInventoryFactsService.AppxKey,FinalInventoryFactsService.VirtContraKey,FinalInventoryFactsService.TaskKey,FinalInventoryFactsService.TimeSourceKey,FinalInventoryFactsService.RtcDriftKey]; foreach(string k in keys) Assert.Contains(facts,f=>f.Key==k); Assert.Equal(10,facts.Count); }
}