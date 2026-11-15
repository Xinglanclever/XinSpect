using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// BCD 開機元素現值（U10 第四圈批次三，第 333 輪）的守門：純解析、六態形狀、
/// 「沒有設定」與「讀不到」分開、韌體層獨立三態。
/// </summary>
public class BcdElementsTests
{
    private const string EnumOutput = """
        Windows 開機管理程式
        --------------------
        identifier              {bootmgr}
        device                  partition=\Device\HarddiskVolume1
        path                    \EFI\Microsoft\Boot\bootmgfw.efi
        description             Windows Boot Manager
        locale                  zh-TW
        default                 {DEFAULT-GUID}
        bootsequence            {LEGACY-GUID}
        displayorder            {DEFAULT-GUID} {OTHER-GUID}
        toolsdisplayorder       {MEMDIAG-GUID}
        timeout                 30

        Windows 開機載入器
        --------------------
        identifier              {DEFAULT-GUID}
        device                  partition=C:
        osdevice                partition=D:
        path                    \Windows\system32\winload.efi
        description             Windows 11
        nx                      OptIn
        hypervisorlaunchtype    Off

        RAM Defects
        --------------------
        identifier              {badmemory}
        badmemorylist           0x1234 0x5678
        badmemoryaccess         Yes
        """;

    private const string FwOutput = """
        韌體應用程式
        --------------------
        identifier              {FW1-GUID}
        description             Windows Boot Manager

        韌體開機管理程式
        --------------------
        identifier              {fwbootmgr}
        displayorder            {FW2-GUID} {FW1-GUID}
        default                 {FW1-GUID}
        """;

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    [Fact]
    public void 十把鍵一次到齊()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, FwOutput, null, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(10, facts.Count);
        var keys = facts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(BcdElementsFactsService.ToolsKey, keys);
        Assert.Contains(BcdElementsFactsService.BootSequenceKey, keys);
        Assert.Contains(BcdElementsFactsService.BootmgrDeviceKey, keys);
        Assert.Contains(BcdElementsFactsService.LoaderDeviceKey, keys);
        Assert.Contains(BcdElementsFactsService.NxKey, keys);
        Assert.Contains(BcdElementsFactsService.HvLaunchKey, keys);
        Assert.Contains(BcdElementsFactsService.BadMemoryKey, keys);
        Assert.Contains(BcdElementsFactsService.MemTrimKey, keys);
        Assert.Contains(BcdElementsFactsService.FwBootMgrKey, keys);
        Assert.Contains(BcdElementsFactsService.AccessStatementKey, keys);
    }

    [Fact]
    public void 工具選單與一次性覆寫逐值對照()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, FwOutput, null, null, DateTimeOffset.UnixEpoch);
        var tools = Fact(facts, BcdElementsFactsService.ToolsKey);
        Assert.True(tools.NumericValue == 1);
        Assert.Contains("{MEMDIAG-GUID}", tools.Value);
        var seq = Fact(facts, BcdElementsFactsService.BootSequenceKey);
        Assert.Contains("下一次開機生效", seq.Value);
        Assert.Contains("{LEGACY-GUID}", seq.Value);
    }

    [Fact]
    public void 載入器定位與nx與Hypervisor()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, FwOutput, null, null, DateTimeOffset.UnixEpoch);
        var dev = Fact(facts, BcdElementsFactsService.LoaderDeviceKey);
        Assert.Contains("partition=C:", dev.Value);
        Assert.Contains("partition=D:", dev.Value);
        Assert.DoesNotContain("BitLocker", dev.Value);
        Assert.Equal("OptIn", Fact(facts, BcdElementsFactsService.NxKey).Value);
        Assert.Equal("Off", Fact(facts, BcdElementsFactsService.HvLaunchKey).Value);
    }

    [Fact]
    public void 壞記憶體清單逐筆與遮蔽缺項()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, FwOutput, null, null, DateTimeOffset.UnixEpoch);
        var bad = Fact(facts, BcdElementsFactsService.BadMemoryKey);
        Assert.True(bad.NumericValue == 2);
        Assert.Contains("0x1234", bad.Value);
        Assert.Contains("0x5678", bad.Value);
        var trim = Fact(facts, BcdElementsFactsService.MemTrimKey);
        Assert.Contains("沒有 removememory／truncatememory 欄位", trim.Value);
    }

    [Fact]
    public void 韌體層順序獨立於os層()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, FwOutput, null, null, DateTimeOffset.UnixEpoch);
        var fw = Fact(facts, BcdElementsFactsService.FwBootMgrKey);
        Assert.True(fw.NumericValue == 2);
        Assert.Contains("韌體層", fw.Value);
        Assert.Contains("{FW1-GUID}", fw.Value);
    }

    [Fact]
    public void os層讀不到時_整組ReadError且理由如實()
    {
        var facts = BcdElementsFactsService.Collect(null, FwOutput, "bcdedit 沒回應", null, DateTimeOffset.UnixEpoch);
        // os 層相依的八把鍵整組 ReadError（按鍵名列，不按順序——收集後有排序）
        var osKeys = new[]
        {
            BcdElementsFactsService.ToolsKey, BcdElementsFactsService.BootSequenceKey,
            BcdElementsFactsService.BootmgrDeviceKey, BcdElementsFactsService.LoaderDeviceKey,
            BcdElementsFactsService.NxKey, BcdElementsFactsService.HvLaunchKey,
            BcdElementsFactsService.BadMemoryKey, BcdElementsFactsService.MemTrimKey,
        };
        foreach (var k in osKeys)
        {
            var f = Fact(facts, k);
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("bcdedit 沒回應", f.UnavailableReason);
            Assert.True(f.NumericValue is null, "非 Present 不得帶數字");
        }
        // 韌體層不受 os 層失敗拖累——兩次讀取各自三態
        Assert.Equal(FactAvailability.Present, Fact(facts, BcdElementsFactsService.FwBootMgrKey).Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, BcdElementsFactsService.AccessStatementKey).Availability);
    }

    [Fact]
    public void 韌體層讀不到時_只有韌體鍵ReadError()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, null, null, "bcdedit /enum firmware 沒回應", DateTimeOffset.UnixEpoch);
        var fw = Fact(facts, BcdElementsFactsService.FwBootMgrKey);
        Assert.Equal(FactAvailability.ReadError, fw.Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, BcdElementsFactsService.ToolsKey).Availability);
        var stmt = Fact(facts, BcdElementsFactsService.AccessStatementKey);
        Assert.Contains("韌體層失敗", stmt.Value);
    }

    [Fact]
    public void 缺韌體物件不足以判定非UEFI()
    {
        const string legacy = "舊版作業系統\n--------------------\nidentifier              {legacy}";
        var facts = BcdElementsFactsService.Collect(EnumOutput, legacy, null, null, DateTimeOffset.UnixEpoch);
        var fw = Fact(facts, BcdElementsFactsService.FwBootMgrKey);
        Assert.Equal(FactAvailability.Unknown, fw.Availability);
        Assert.Contains("不推斷平台類型", fw.UnavailableReason);
    }

    [Fact]
    public void 缺欄位是Present帶說明_不是ReadError()
    {
        const string minimal = """
            Windows 開機管理程式
            --------------------
            identifier              {bootmgr}
            timeout                 5
            """;
        var facts = BcdElementsFactsService.Collect(minimal, null, null, "沒有讀到", DateTimeOffset.UnixEpoch);
        var seq = Fact(facts, BcdElementsFactsService.BootSequenceKey);
        Assert.Equal(FactAvailability.Present, seq.Availability);
        Assert.Contains("沒有 bootsequence 欄位", seq.Value);
        // loader 找不到時的形狀：default 沒有 → loader 區塊找不到 → 如實說
        var dev = Fact(facts, BcdElementsFactsService.LoaderDeviceKey);
        Assert.Equal(FactAvailability.Unknown, dev.Availability);
        Assert.Contains("系統載入器", dev.UnavailableReason);
    }

    [Fact]
    public void 存取宣告說明唯讀與新鮮度()
    {
        var facts = BcdElementsFactsService.Collect(EnumOutput, FwOutput, null, null, DateTimeOffset.UnixEpoch);
        var stmt = Fact(facts, BcdElementsFactsService.AccessStatementKey);
        Assert.Contains("bcdedit /enum", stmt.Value);
        Assert.Contains("沒有快取", stmt.Value);
        Assert.Contains("不寫 BCD", stmt.Value);
        Assert.DoesNotContain("os 層失敗", stmt.Value);
    }
}
