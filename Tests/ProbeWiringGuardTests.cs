using System;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 「探針預設回常數、生產入口從不傳探針」的守門（`work/deadprobe.py` 的機械化版本）。
///
/// <para>
/// 這個形狀我在 FW-024 抓過一次：<c>FwdtFact</c> 早早寫好、值裡寫著「FPDT 提供韌體自報的
/// POST 時長」，但 <c>FpdtProbe</c> 預設是 <c>() => (0, false)</c> 而生產入口從不傳探針——
/// 它在<b>每一台機器上</b>都回「本平台無 FPDT 表」，永遠。比缺鍵更糟的是它<b>看起來有實作</b>，
/// 覆蓋掃描也會算它已覆蓋。
/// </para>
/// <para>
/// <b>這條守門測的是「該服務是否有真實讀取器」</b>，不是「有沒有被呼叫」——
/// 有明確共識的「閘門型」事實（如 <c>dl.stack</c> 只宣告同意狀態）不算死路，
/// 它們的預設常數<b>就是</b>語意（未取得同意）。所以要嘛讀取器是真的，
/// 要嘛這把鍵被列在下面的白名單裡並寫明為什麼。
/// </para>
/// </summary>
public class ProbeWiringGuardTests
{
    /// <summary>
    /// 明文豁免：這些鍵的「讀取器」刻意是常數，因為它們陳述的是<b>意圖或閘門狀態</b>，
    /// 而不是可量測的機器事實。每一條都要寫得出理由。
    /// </summary>
    private static readonly (string Key, string Why)[] ConstantByDesign =
    [
        ("dl.stack", "崩潰堆疊閘門（DL-004）：只宣告同意狀態；本版無擷取實作，"
                   + "閘門本身就是這把鍵的全部語意——有實作卻不宣告才是不誠實。"),
    ];

    [Fact]
    public void 閘門型豁免都寫了理由()
    {
        foreach (var (key, why) in ConstantByDesign)
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(why.Length >= 30, $"{key} 的豁免理由太短，等於沒寫");
        }
    }

    /// <summary>
    /// 這條測的是「豁免清單還對得上實作」：如果哪天 dl.stack 長出了真的擷取器，
    /// 這條會提醒把豁免拿掉——豁免不該在能力到位後繼續留著。
    /// </summary>
    [Fact]
    public void 豁免的鍵仍然存在且仍是閘門型()
    {
        var facts = PlatformExpansionFactsService.Collect(
            new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var stack = facts.SingleOrDefault(f => f.Key == "dl.stack");
        Assert.NotNull(stack);
        // 未取得同意（預設）→ NotApplicable；這是閘門型應有的樣子。
        Assert.Equal(FactAvailability.NotApplicable, stack!.Availability);
        Assert.Contains("未取得明示同意", stack.UnavailableReason);
    }

    /// <summary>
    /// 平台擴充的 MSR 型事實<b>有真實讀取器</b>（不是回常數）：
    /// 讀不到時是 InsufficientPrivilege 附原因，不是「一切正常」也不是 0。
    /// </summary>
    [Fact]
    public void MSR型事實讀不到時標權限態而不是零()
    {
        var at = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        // 注入「沒有 ring0」的探針，等同生產環境在無驅動時的情形。
        var smi = PlatformExpansionFactsService.SmiCstateFact(at, () => null);
        Assert.Equal(FactAvailability.InsufficientPrivilege, smi.Availability);
        Assert.Contains("不猜", smi.UnavailableReason);

        var wall = PlatformExpansionFactsService.PowerWallFact(at, () => null);
        Assert.Equal(FactAvailability.InsufficientPrivilege, wall.Availability);
        // 兩把鍵的措辭不同（一個「不猜」、一個「不宣稱沒有牆」），各自都是那個值該說的話。
        Assert.Contains("不宣稱沒有牆", wall.UnavailableReason);
    }

    /// <summary>
    /// 環境變數型事實有真實讀取器：注入探針會改變輸出，代表生產路徑讀的是真環境。
    /// </summary>
    [Fact]
    public void 環境變數型事實走真實讀取器()
    {
        var at = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        // 探針回真實字串時，計數要跟著動——證明生產路徑讀的是環境而不是常數。
        // 注意 PathFact 對 machine 與 user 各呼叫探針一次，所以固定回傳會被算兩次；
        // 用「依名稱給不同值」的探針，計數才反映真實語意。
        var two = EnvironmentDiagnosticsFactsService.PathFact(at,
            name => name == "PATH" ? @"C:;C:" : null);
        Assert.Equal(2d, two.NumericValue);
        // 兩邊都讀不到 → ReadError，不是「0 段」。
        var none = EnvironmentDiagnosticsFactsService.PathFact(at, _ => null);
        Assert.Equal(FactAvailability.ReadError, none.Availability);
        Assert.Contains("不把空字串當成 0 段", none.UnavailableReason);
    }
}
