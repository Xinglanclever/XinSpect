using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// Credential Guard 狀態鏈的守門（U10 第三圈 SE-0005，第 319 輪）。
///
/// <para>
/// <b>這一支的價值全在「分層」與「不歸因」：</b>設定面（LsaCfgFlags／Scenario）、政策面（VBS 總開關）、
/// 執行面（SecurityServicesRunning）、硬體面（AvailableSecurityProperties）是四個不同的斷言。
/// 把「已設定但未執行」寫成「已啟用」，或把「未設定」寫成「已關閉」，是這一項明文禁止的兩種混淆。
/// </para>
/// <para>
/// 測試因此盯五件事：四層各自陳述、三態分開、值表未收錄不猜、兩個來源矛盾時並列不仲裁、
/// 以及「讀不到」永遠不等於「沒有」。
/// </para>
/// </summary>
public class CredentialGuardFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static VbsStateFactsService.VbsSnapshot Vbs(
        uint status = 2, uint[]? required = null, uint[]? available = null,
        uint[]? configured = null, uint[]? running = null)
        => new(status, required ?? [], available ?? [], configured ?? [], running ?? []);

    private static CredentialGuardFactsService.Config Cfg(uint? flags, uint? scenario, uint? vbs) => new(flags, scenario, vbs);

    private static HardwareFact One(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == CredentialGuardFactsService.ChainKey);

    [Fact]
    public void 設定讀取失敗要標讀取錯誤_不是未啟用()
    {
        var fact = One(CredentialGuardFactsService.Collect(null, Vbs(), At));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("不是「未啟用」", fact.UnavailableReason);
        Assert.Contains("也不是 0", fact.UnavailableReason);
    }

    [Fact]
    public void 讀取失敗的理由要照讀取端給的原話()
    {
        var fact = One(CredentialGuardFactsService.Collect(null, null, At, "存取被拒 (5)"));
        Assert.Contains("存取被拒 (5)", fact.UnavailableReason);
    }

    [Fact]
    public void 值表未收錄的LsaCfgFlags不猜_帶原值標未知()
    {
        var fact = One(CredentialGuardFactsService.Collect(Cfg(7, null, null), Vbs(), At));
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("LsaCfgFlags＝7 不在本版值表", fact.UnavailableReason);
        Assert.Contains("不猜它算不算啟用", fact.UnavailableReason);
    }

    [Fact]
    public void 已設定且執行中要標第三級並四層都寫出來()
    {
        var fact = One(CredentialGuardFactsService.Collect(
            Cfg(1, null, 1), Vbs(running: [1, 2], available: [1, 2]), At));

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(CredentialGuardFactsService.LevelRunning, fact.NumericValue);
        Assert.Contains("已設定且執行中", fact.Value);
        Assert.Contains("LsaCfgFlags＝1（啟用，含 UEFI 鎖）", fact.Value);
        Assert.Contains("VBS 總開關（EnableVirtualizationBasedSecurity）＝1（開）", fact.Value);
        Assert.Contains("SecurityServicesRunning 含代碼 1", fact.Value);
        Assert.Contains("hypervisor 支援（1）在", fact.Value);
        Assert.Contains("Secure Boot（2）在", fact.Value);
    }

    [Fact]
    public void 已設定但未執行是一個待查的矛盾_不是已啟用()
    {
        // 政策要它跑（LsaCfgFlags=1），執行面說沒在跑——這是本項最常見的真實情境
        // （設定後沒重開機、或 VBS 總開關沒開），不得寫成「已啟用」。
        var fact = One(CredentialGuardFactsService.Collect(
            Cfg(1, null, 0), Vbs(running: [2], available: [1]), At));

        Assert.Equal(CredentialGuardFactsService.LevelConfiguredNotRunning, fact.NumericValue);
        Assert.Contains("已設定但未執行", fact.Value);
        Assert.Contains("待查的矛盾", fact.Value);
        Assert.DoesNotContain("已設定且執行中", fact.Value);
        // 三個可能原因並列，不選一個
        Assert.Contains("VBS 總開關沒開", fact.Value);
        Assert.Contains("硬體韌體缺 hypervisor 支援", fact.Value);
        Assert.Contains("尚未重開機", fact.Value);
    }

    [Fact]
    public void 設定說已啟用而執行面讀不到要標未知()
    {
        // 執行面整份不可用（Win32_DeviceGuard 缺席）：不能說它在跑，也不能說它沒在跑。
        var fact = One(CredentialGuardFactsService.Collect(Cfg(1, null, 1), null, At));

        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("執行面讀不到", fact.UnavailableReason);
        Assert.Contains("不能說它在跑", fact.UnavailableReason);
    }

    [Fact]
    public void 執行面欄位缺席也標未知_不代填成沒在跑()
    {
        var vbs = new VbsStateFactsService.VbsSnapshot(2, [], [], [], null);
        var fact = One(CredentialGuardFactsService.Collect(Cfg(1, null, 1), vbs, At));
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("執行面讀不到", fact.UnavailableReason);
    }

    [Fact]
    public void 明確關閉是政策事實_不是不支援()
    {
        var fact = One(CredentialGuardFactsService.Collect(
            Cfg(0, null, 1), Vbs(running: [2], available: [1]), At));

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(CredentialGuardFactsService.LevelOff, fact.NumericValue);
        Assert.Contains("明確關閉", fact.Value);
        Assert.Contains("不是「不支援」", fact.Value);
    }

    [Fact]
    public void 未設定不等於已關閉()
    {
        // 三個值都不存在：那是「未設定」，與 LsaCfgFlags=0 的「明確關閉」是兩個斷言。
        var fact = One(CredentialGuardFactsService.Collect(Cfg(null, null, null), Vbs(running: [2]), At));

        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("未設定", fact.Value);
        Assert.Contains("不等於「已關閉」", fact.Value);
        // 邊界句會列出「明確關閉」這個詞（說明三種斷言的分界），所以只釘住結論那一段：
        // 「明確關閉（政策值為 0）」是關閉那條路徑的措辭，未設定這條不得出現。
        Assert.DoesNotContain("明確關閉（政策值為 0）", fact.Value);
        Assert.Contains("LsaCfgFlags＝未設定（值不存在）", fact.Value);
    }

    [Fact]
    public void 政策說關閉而執行面說在跑_並列不仲裁()
    {
        var fact = One(CredentialGuardFactsService.Collect(
            Cfg(0, null, 0), Vbs(running: [1]), At));

        Assert.Contains("兩個獨立來源互相矛盾，並列不仲裁", fact.Value);
        Assert.Contains("SecurityServicesRunning 含代碼 1", fact.Value);
    }

    [Fact]
    public void Scenarios開關也算設定面()
    {
        // LsaCfgFlags 不存在但 Scenarios\CredentialGuard\Enabled=1：政策同樣要求它跑。
        var fact = One(CredentialGuardFactsService.Collect(
            Cfg(null, 1, 1), Vbs(running: [1]), At));
        Assert.Equal(CredentialGuardFactsService.LevelRunning, fact.NumericValue);
        Assert.Contains("Scenarios\\CredentialGuard\\Enabled＝1（開）", fact.Value);
    }

    [Fact]
    public void 硬體面缺屬性要如實寫不在()
    {
        var fact = One(CredentialGuardFactsService.Collect(
            Cfg(1, null, 1), Vbs(running: [1], available: [2]), At));
        Assert.Contains("hypervisor 支援（1）不在", fact.Value);
        Assert.Contains("Secure Boot（2）在", fact.Value);
    }

    [Fact]
    public void 四層名稱都要在值裡出現_否則讀者分不出是哪一層說的()
    {
        var fact = One(CredentialGuardFactsService.Collect(Cfg(1, 0, 1), Vbs(running: [1]), At));
        Assert.Contains("設定面：", fact.Value);
        Assert.Contains("政策面：", fact.Value);
        Assert.Contains("執行面：", fact.Value);
        Assert.Contains("硬體面：", fact.Value);
    }

    [Fact]
    public void 狀態鏈的值表要對準MicrosoftLearn()
    {
        Assert.Equal("0（停用）", CredentialGuardFactsService.FlagsName(0));
        Assert.Equal("1（啟用，含 UEFI 鎖）", CredentialGuardFactsService.FlagsName(1));
        Assert.Equal("2（啟用，不含 UEFI 鎖）", CredentialGuardFactsService.FlagsName(2));
        Assert.Equal("3（值表未收錄）", CredentialGuardFactsService.FlagsName(3));
        Assert.Equal("未設定（值不存在）", CredentialGuardFactsService.FlagsName(null));
    }

    [Fact]
    public void 同一份Vbs快照要能同時餵兩個消費者()
    {
        // 第 319 輪的接線：VbsStateFactsService 把快照交出來，狀態鏈直接用——
        // 兩支各自再查一次 WMI 會有兩個答案的可能。
        var facts = VbsStateFactsService.Collect(At, () => Vbs(running: [1]), out var snapshot);
        Assert.NotNull(snapshot);
        Assert.Equal([1u], snapshot!.Running);

        var chain = One(CredentialGuardFactsService.Collect(Cfg(1, null, 1), snapshot, At));
        Assert.Equal(CredentialGuardFactsService.LevelRunning, chain.NumericValue);
        Assert.Contains(facts, f => f.Key == VbsStateFactsService.StateKey);
    }
}
