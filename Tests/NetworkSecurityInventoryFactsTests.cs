using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public class NetworkSecurityInventoryFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);

    [Fact] public void DNS_只讀本機不出網() { var f = NetworkSecurityInventoryFactsService.DnsFact(At, () => new(5, 1, null, 3)); Assert.Contains("介面解析器 5", f.Value); Assert.Contains("不出網", f.Value); }
    [Fact] public void DNS_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.DnsFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void SMB_未設定不代填() { var f = NetworkSecurityInventoryFactsService.SmbFact(At, () => new(null, 0, 1, null)); Assert.Contains("未設定", f.Value); Assert.Contains("設定關不等於攻擊面關", f.Value); }
    [Fact] public void SMB_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.SmbFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 防火牆_只讀不建議() { var f = NetworkSecurityInventoryFactsService.FirewallFact(At, () => new(3, 0, 120)); Assert.Contains("啟用 3", f.Value); Assert.Contains("不建議變更", f.Value); }
    [Fact] public void 防火牆_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.FirewallFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 憑證_存在性非信任判決() { var f = NetworkSecurityInventoryFactsService.CertFact(At, () => new(40, 5, 2, 1)); Assert.Contains("非 Microsoft 根 5", f.Value); Assert.Contains("不是信任判決", f.Value); Assert.Contains("不列主體或指紋", f.Value); }
    [Fact] public void 憑證_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.CertFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void WiFi_不含密碼不掃描() { var f = NetworkSecurityInventoryFactsService.WifiFact(At, () => new(4, 1, 2, 1)); Assert.Contains("設定檔 4", f.Value); Assert.Contains("不含密碼、不掃描周圍網路", f.Value); }
    [Fact] public void WiFi_零設定檔是值() { var f = NetworkSecurityInventoryFactsService.WifiFact(At, () => new(0, 0, 0, 0)); Assert.Contains("設定檔 0", f.Value); }
    [Fact] public void WiFi_登錄不存在_NotApplicable() { var f = NetworkSecurityInventoryFactsService.WifiFact(At, () => null); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 卸載_未提供不等於停用() { var f = NetworkSecurityInventoryFactsService.NetOffloadFact(At, () => new(1, -1, 2, 0)); Assert.Contains("未回報", f.Value); Assert.Contains("未提供不等於停用", f.Value); }
    [Fact] public void 卸載_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.NetOffloadFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 藍牙韌體_查無等於未收錄() { var f = NetworkSecurityInventoryFactsService.BluetoothFirmwareFact(At, () => new(2, 1)); Assert.Contains("藍牙無線電 2", f.Value); Assert.Contains("查無＝未收錄", f.Value); }
    [Fact] public void 藍牙韌體_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.BluetoothFirmwareFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 印表機韌體_缺源如實列缺源() { var f = NetworkSecurityInventoryFactsService.PrinterFirmwareFact(At, () => new(3, 0)); Assert.Contains("缺源如實列缺源", f.Value); }
    [Fact] public void 印表機韌體_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.PrinterFirmwareFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 音訊漂移_標註取樣非保證() { var f = NetworkSecurityInventoryFactsService.AudioDriftFact(At, () => new(60, 60000, 60006, 100)); Assert.Contains("100", f.Value); Assert.Contains("取樣結果", f.Value); Assert.Contains("不是長期保證", f.Value); }
    [Fact] public void 音訊漂移_無樣本_NotApplicable() { var f = NetworkSecurityInventoryFactsService.AudioDriftFact(At, () => null); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 供電事件_沒事件不等於沒問題() { var f = NetworkSecurityInventoryFactsService.PowerEventFact(At, () => new(1, 2, 3)); Assert.Contains("Kernel-Power 41 1", f.Value); Assert.Contains("沒事件不等於沒問題", f.Value); }
    [Fact] public void 供電事件_失敗_ReadError() { var f = NetworkSecurityInventoryFactsService.PowerEventFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts = NetworkSecurityInventoryFactsService.Collect(At); string[] keys = [NetworkSecurityInventoryFactsService.DnsKey, NetworkSecurityInventoryFactsService.SmbKey, NetworkSecurityInventoryFactsService.FirewallKey, NetworkSecurityInventoryFactsService.CertKey, NetworkSecurityInventoryFactsService.WifiKey, NetworkSecurityInventoryFactsService.NetOffloadKey, NetworkSecurityInventoryFactsService.BluetoothFwKey, NetworkSecurityInventoryFactsService.PrinterFwKey, NetworkSecurityInventoryFactsService.AudioDriftKey, NetworkSecurityInventoryFactsService.PowerEventsKey]; foreach (string k in keys) Assert.Contains(facts, f => f.Key == k); Assert.Equal(10, facts.Count); }
}