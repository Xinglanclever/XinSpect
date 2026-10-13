using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

public class InventoryGovernanceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 23, 0, 0, TimeSpan.Zero);

    [Fact] public void APO_只報處理鏈計數() { var f = InventoryGovernanceFactsService.ApoFact(At, () => new(2, 3, 4, 5)); Assert.Contains("裝置 2", f.Value); Assert.Contains("不列音訊內容", f.Value); }
    [Fact] public void APO_不存在_NotApplicable() { var f = InventoryGovernanceFactsService.ApoFact(At, () => null); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 隱私開關_未設定不代填() { var f = InventoryGovernanceFactsService.PrivacySwitchFact(At, () => new(null, 1, 0)); Assert.Contains("未設定", f.Value); Assert.Contains("不讀使用紀錄或生物特徵", f.Value); }
    [Fact] public void 隱私開關_失敗_ReadError() { var f = InventoryGovernanceFactsService.PrivacySwitchFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void ICC_只報計數不讀檔() { var f = InventoryGovernanceFactsService.IccFact(At, () => new(4, 6, 2)); Assert.Contains("關聯 4", f.Value); Assert.Contains("不讀檔內容", f.Value); }
    [Fact] public void ICC_失敗_ReadError() { var f = InventoryGovernanceFactsService.IccFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 無線電量_未回報不猜() { var f = InventoryGovernanceFactsService.HidBatteryFact(At, () => new(5, 0, null)); Assert.Contains("未回報", f.Value); Assert.Contains("不列名稱或識別碼", f.Value); }
    [Fact] public void 無線電量_失敗_ReadError() { var f = InventoryGovernanceFactsService.HidBatteryFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void USB埠映射_只報計數() { var f = InventoryGovernanceFactsService.UsbPortMapFact(At, () => new(9, 2, 3)); Assert.Contains("USB 裝置實例 9", f.Value); Assert.Contains("不列序號", f.Value); }
    [Fact] public void USB埠映射_零個是值() { var f = InventoryGovernanceFactsService.UsbPortMapFact(At, () => new(0, 0, 0)); Assert.Contains("USB 裝置實例 0", f.Value); }
    [Fact] public void USB埠映射_失敗_ReadError() { var f = InventoryGovernanceFactsService.UsbPortMapFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 自啟動_只報來源計數() { var f = InventoryGovernanceFactsService.AutostartFact(At, () => new(3, 1, 4)); Assert.Contains("Run 登錄 3", f.Value); Assert.Contains("不列命令、路徑或任務名稱", f.Value); }
    [Fact] public void 自啟動_失敗_ReadError() { var f = InventoryGovernanceFactsService.AutostartFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void Minifilter_只讀拓撲() { var f = InventoryGovernanceFactsService.MinifilterFact(At, () => new(12, 30)); Assert.Contains("Minifilter 12", f.Value); Assert.Contains("不列驅動名稱", f.Value); }
    [Fact] public void Minifilter_不存在_NotApplicable() { var f = InventoryGovernanceFactsService.MinifilterFact(At, () => null); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 開機驅動_只報Start分組() { var f = InventoryGovernanceFactsService.BootDriverFact(At, () => new(50, 5, 10, 35)); Assert.Contains("Boot 5", f.Value); Assert.Contains("不列驅動名稱", f.Value); }
    [Fact] public void 開機驅動_失敗_ReadError() { var f = InventoryGovernanceFactsService.BootDriverFact(At, () => null); Assert.Equal(FactAvailability.ReadError, f.Availability); }
    [Fact] public void 匿名化選擇器_預設遮蔽敏感欄位() { var f = InventoryGovernanceFactsService.AnonSelectorFact(At); Assert.Contains("預設遮蔽 8 類", f.Value); Assert.Contains("不讀任何敏感欄位", f.Value); }
    [Fact] public void 結果哈希鏈_固定順序可重現() { var f = InventoryGovernanceFactsService.BenchChainFact(At, () => [new("b", 2), new("a", 1)]); Assert.Contains("2 列", f.Value); Assert.Contains("重算一致 True", f.Value); Assert.Contains("不寫檔", f.Value); }
    [Fact] public void 結果哈希鏈_無結果_NotApplicable() { var f = InventoryGovernanceFactsService.BenchChainFact(At, () => null); Assert.Equal(FactAvailability.NotApplicable, f.Availability); }
    [Fact] public void 本批十把鍵齊() { var facts = InventoryGovernanceFactsService.Collect(At); string[] keys = [InventoryGovernanceFactsService.ApoKey, InventoryGovernanceFactsService.PrivacySwitchKey, InventoryGovernanceFactsService.IccKey, InventoryGovernanceFactsService.HidBatteryKey, InventoryGovernanceFactsService.UsbPortMapKey, InventoryGovernanceFactsService.AutostartKey, InventoryGovernanceFactsService.MinifilterKey, InventoryGovernanceFactsService.BootDriverKey, InventoryGovernanceFactsService.AnonSelectorKey, InventoryGovernanceFactsService.BenchChainKey]; foreach (string k in keys) Assert.Contains(facts, f => f.Key == k); Assert.Equal(10, facts.Count); }
}