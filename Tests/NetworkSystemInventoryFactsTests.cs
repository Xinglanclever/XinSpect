using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public class NetworkSystemInventoryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);

    [Fact] public void 列印佇列_只報計數不列名稱() { var f = NetworkSystemInventoryFactsService.PrintFact(At, () => new(3, 1, 1, 2)); Assert.Contains("列印佇列 3 個", f.Value); Assert.Contains("錯誤 1", f.Value); Assert.Contains("不列印表機名稱", f.Value); }
    [Fact] public void 列印佇列_零個_NotApplicable() { var f = NetworkSystemInventoryFactsService.PrintFact(At, () => new(0, 0, 0, 0)); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 列印佇列_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.PrintFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void WOL_設定關不等於硬體不支援() { var f = NetworkSystemInventoryFactsService.WolFact(At, () => new(4, 0)); Assert.Contains("電源管理列 4 筆", f.Value); Assert.Contains("設定關不等於硬體不支援", f.Value); }
    [Fact] public void WOL_零列_NotApplicable() { var f = NetworkSystemInventoryFactsService.WolFact(At, () => new(0, 0)); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void WOL_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.WolFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 路由_只報計數與metric不列端點() { var f = NetworkSystemInventoryFactsService.RouteFact(At, () => new(12, 1, 2, 25)); Assert.Contains("路由 12 條", f.Value); Assert.Contains("最低 metric 25", f.Value); Assert.Contains("不列網段、閘道或介面名稱", f.Value); }
    [Fact] public void 路由_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.RouteFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 感應器_桌上型零個是值() { var f = NetworkSystemInventoryFactsService.SensorFact(At, () => new(0, 0, 0)); Assert.Contains("感應器類裝置 0 個", f.Value); Assert.Contains("不是讀取失敗", f.Value); }
    [Fact] public void 感應器_分類計數() { var f = NetworkSystemInventoryFactsService.SensorFact(At, () => new(3, 1, 2)); Assert.Contains("環境光 1", f.Value); Assert.Contains("加速度計 2", f.Value); }
    [Fact] public void 感應器_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.SensorFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 事件通道_停用不等於清空() { var f = NetworkSystemInventoryFactsService.EventChannelFact(At, () => new(100, 80, 20)); Assert.Contains("事件通道 100 個", f.Value); Assert.Contains("停用不等於被清空", f.Value); }
    [Fact] public void 事件通道_零個_NotApplicable() { var f = NetworkSystemInventoryFactsService.EventChannelFact(At, () => new(0, 0, 0)); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 事件通道_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.EventChannelFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void StorageSpaces_只報拓撲計數() { var f = NetworkSystemInventoryFactsService.StorageSpacesFact(At, () => new(2, 5, 8)); Assert.Contains("集區 2、虛擬磁碟 5、實體磁碟 8", f.Value); Assert.Contains("不讀成員碟名稱或資料", f.Value); }
    [Fact] public void StorageSpaces_沒有集區_NotApplicable() { var f = NetworkSystemInventoryFactsService.StorageSpacesFact(At, () => new(0, 0, 4)); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void StorageSpaces_通路不可用_NotApplicable() { var f = NetworkSystemInventoryFactsService.StorageSpacesFact(At, () => null); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 磁碟電源轉換_不宣稱預測壽命() { var f = NetworkSystemInventoryFactsService.PhysicalDiskPowerFact(At, () => new(4, 120, 300)); Assert.Contains("實體磁碟 4 顆", f.Value); Assert.Contains("不預測壽命", f.Value); }
    [Fact] public void 磁碟電源轉換_零顆_NotApplicable() { var f = NetworkSystemInventoryFactsService.PhysicalDiskPowerFact(At, () => new(0, 0, 0)); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 磁碟電源轉換_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.PhysicalDiskPowerFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 廣告設定_未設定不代填() { var f = NetworkSystemInventoryFactsService.AdvertisingFact(At, () => new(null, 1, 0)); Assert.Contains("未設定", f.Value); Assert.Contains("不代填 Windows 預設", f.Value); }
    [Fact] public void 廣告設定_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.AdvertisingFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 語音輸入_不讀內容() { var f = NetworkSystemInventoryFactsService.SpeechInputFact(At, () => new(1, 0, null)); Assert.Contains("線上語音辨識 1", f.Value); Assert.Contains("不讀任何語音或輸入內容", f.Value); }
    [Fact] public void 語音輸入_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.SpeechInputFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void NTP_只讀本機不出網() { var f = NetworkSystemInventoryFactsService.NtpFact(At, () => new("Running", "time.windows.com", 1)); Assert.Contains("W32Time 服務 Running", f.Value); Assert.Contains("已設定", f.Value); Assert.Contains("不出網查詢", f.Value); Assert.DoesNotContain("time.windows.com", f.Value); }
    [Fact] public void NTP_讀取失敗_ReadError() { var f = NetworkSystemInventoryFactsService.NtpFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts = NetworkSystemInventoryFactsService.Collect(At); string[] expected = [NetworkSystemInventoryFactsService.PrintKey, NetworkSystemInventoryFactsService.WolKey, NetworkSystemInventoryFactsService.RouteKey, NetworkSystemInventoryFactsService.SensorKey, NetworkSystemInventoryFactsService.EventChannelKey, NetworkSystemInventoryFactsService.StorageSpacesKey, NetworkSystemInventoryFactsService.PowerTransitionKey, NetworkSystemInventoryFactsService.AdvertisingKey, NetworkSystemInventoryFactsService.SpeechInputKey, NetworkSystemInventoryFactsService.NtpKey]; foreach (string key in expected) Assert.Contains(facts, f => f.Key == key); Assert.Equal(10, facts.Count); }
}