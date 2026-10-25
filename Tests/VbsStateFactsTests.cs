using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// VBS／HVCI 狀態與未啟用原因面的守門（U10-SE-0003）。
///
/// <para>
/// <b>核心：</b>①命名空間不存在＝NotApplicable（環境事實），查詢拋例外＝ReadError——兩者都不得
/// 被寫成「VBS 已關閉」；②未啟用時必須把 Required／Available 兩張清單並列（原因面），
/// 且「沒啟用」不得寫成「不支援」；③代碼表對準 Microsoft Learn，未收錄的代碼如實帶原碼。
/// </para>
/// </summary>
public class VbsStateFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static VbsStateFactsService.VbsSnapshot Snap(uint status,
        uint[]? required = null, uint[]? available = null, uint[]? configured = null, uint[]? running = null)
        => new(status, required ?? [], available ?? [], configured ?? [], running ?? []);

    private static VbsStateFactsService.SnapshotProbe Probe(VbsStateFactsService.VbsSnapshot? s) => () => s;

    [Fact]
    public void 命名空間不存在要標不適用且是環境事實()
    {
        var facts = VbsStateFactsService.Collect(At, Probe(null));
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("環境事實", facts[0].UnavailableReason);
        Assert.Contains("不是「VBS 已關閉」", facts[0].UnavailableReason);
    }

    [Fact]
    public void 查詢拋例外要標讀取錯誤_與命名空間不存在分開()
    {
        var facts = VbsStateFactsService.Collect(At, () => throw new InvalidOperationException("WMI 掛了"));
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Contains("例外", facts[0].UnavailableReason);
    }

    [Fact]
    public void 未啟用時要並列原因面且不得寫成不支援()
    {
        // 狀態 0＝未啟用；可用屬性有 hypervisor 與 Secure Boot（硬體具備）——典型「可用而未啟用」。
        var facts = VbsStateFactsService.Collect(At, Probe(Snap(0,
            required: [1, 2], available: [1, 2, 7], configured: [], running: [0])));
        var state = facts.Single(f => f.Key == VbsStateFactsService.StateKey);

        Assert.Equal(FactAvailability.Present, state.Availability);
        Assert.Equal(0u, state.NumericValue);                    // 狀態碼本身就是數值
        Assert.Contains("0（未啟用）", state.Value);
        Assert.Contains("未啟用≠不支援", state.Value);
        Assert.Contains("可用而未啟用", state.Value);
        Assert.Contains("1（hypervisor 支援）", state.Value);     // Required 清單有解碼
        Assert.Contains("7（MBEC/GMET）", state.Value);           // Available 清單有解碼
        Assert.Contains("0（無服務）", state.Value);
        Assert.Null(state.UnavailableReason);

        var required = facts.Single(f => f.Key == VbsStateFactsService.RequiredKey);
        Assert.Equal(2, required.NumericValue);
        Assert.Contains("1（hypervisor 支援）、2（Secure Boot）", required.Value);
        Assert.Contains("不得互相代言", required.Value);
    }

    [Fact]
    public void 執行中時陳述運行面()
    {
        var facts = VbsStateFactsService.Collect(At, Probe(Snap(2,
            required: [1], available: [1, 2, 3], configured: [2], running: [2])));
        var state = facts.Single(f => f.Key == VbsStateFactsService.StateKey);
        Assert.Equal(2u, state.NumericValue);
        Assert.Contains("2（已啟用且執行中）", state.Value);
        Assert.Contains("2（記憶體完整性／HVCI）", state.Value);
        Assert.DoesNotContain("未啟用≠不支援", state.Value);      // 原因面只在狀態 0 時出現
    }

    [Fact]
    public void 未收錄的代碼如實帶原碼不解讀()
    {
        var facts = VbsStateFactsService.Collect(At, Probe(Snap(9, required: [42], running: [42])));
        var state = facts.Single(f => f.Key == VbsStateFactsService.StateKey);
        Assert.Contains("0x9（值表未收錄）", state.Value);
        Assert.Contains("0x2A（值表未收錄）", state.Value);
    }

    [Fact]
    public void 狀態欄位讀不到時不代填且清單空時如實講()
    {
        var facts = VbsStateFactsService.Collect(At, Probe(
            new VbsStateFactsService.VbsSnapshot(null, [], [], [], [])));
        var state = facts.Single(f => f.Key == VbsStateFactsService.StateKey);
        Assert.Null(state.NumericValue);
        Assert.Contains("未回報", state.Value);
        Assert.Contains("沒有服務在跑", state.Value);            // 0 在 Running 清單裡的講法
        var required = facts.Single(f => f.Key == VbsStateFactsService.RequiredKey);
        Assert.Contains("沒有任何屬性被要求", required.Value);
        Assert.Contains("≠「硬體不支援」", required.Value);
    }
}
