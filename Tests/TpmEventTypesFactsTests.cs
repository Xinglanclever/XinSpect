using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// TCG log 逐事件型別統計的守門（U10-FW-0075）。
///
/// <para>
/// <b>核心：</b>①只列型別編號、不命名（本專案沒有 EV_* 對照表的出處——
/// 與 tpm.log.histogram 同一紀律）；②家族成員＝log 裡實際出現的型別；
/// ③截斷時的計數是「走到哪算到哪」，必須在文字裡說出來。
/// </para>
/// </summary>
public class TpmEventTypesFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static Tpm2.TcgEvent E(uint pcr, uint type)
        => new(pcr, type, [((ushort)0x0B, new byte[32])]);

    private static Tpm2.TcgLogWalk W(bool truncated, params Tpm2.TcgEvent[] events)
        => new(events, truncated);

    [Fact]
    public void 沒有走訪結果時要標不支援並說出不重讀()
    {
        var fact = TpmEventTypesFactsService.Collect(At, null).Single();
        Assert.Equal(TpmEventTypesFactsService.TypesKey, fact.Key);
        Assert.Equal(FactAvailability.NotSupported, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("不重讀 log", fact.UnavailableReason);
    }

    [Fact]
    public void 沒有事件時不填零()
    {
        var fact = TpmEventTypesFactsService.Collect(At, W(false)).Single();
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("不填 0", fact.UnavailableReason);
    }

    [Fact]
    public void 彙總與逐型別鍵()
    {
        var walk = W(false, E(0, 0x03), E(0, 0x03), E(1, 0x08), E(2, 0x08), E(4, 0x08), E(7, 0x0D));
        var facts = TpmEventTypesFactsService.Collect(At, walk);

        var agg = facts.Single(f => f.Key == TpmEventTypesFactsService.TypesKey);
        Assert.Equal(FactAvailability.Present, agg.Availability);
        Assert.Equal(3, agg.NumericValue);                          // 3 種型別
        Assert.Contains("6 筆事件、型別 3 種", agg.Value);
        Assert.Contains("0x00000008×3", agg.Value);                 // 按事件數排序
        Assert.Contains("不命名", agg.Value);

        // 家族成員＝log 裡實際出現的型別；十進位鍵名、十六進位值
        var t8 = facts.Single(f => f.Key == "tpm.ev.type.8");
        Assert.Equal(3, t8.NumericValue);
        Assert.Contains("0x00000008：3 筆", t8.Value);
        Assert.Equal(2, facts.Single(f => f.Key == "tpm.ev.type.3").NumericValue);
        Assert.Equal(1, facts.Single(f => f.Key == "tpm.ev.type.13").NumericValue);
        Assert.Equal(3, facts.Count(f => f.Key.StartsWith("tpm.ev.type.")));
    }

    [Fact]
    public void 截斷的走訪要說走到哪算到哪()
    {
        var facts = TpmEventTypesFactsService.Collect(At, W(true, E(0, 0x03)));
        Assert.Contains("走到哪算到哪", facts.Single(f => f.Key == TpmEventTypesFactsService.TypesKey).Value);
        Assert.Contains("走到哪算到哪", facts.Single(f => f.Key == "tpm.ev.type.3").Value
            , StringComparison.Ordinal);
    }

    [Fact]
    public void 超過上限的型別只出有限把鍵且彙總說等()
    {
        var events = Enumerable.Range(0, 20)
            .Select(i => E((uint)(i % 24), (uint)(0x10 + i))).ToArray();
        var facts = TpmEventTypesFactsService.Collect(At, W(false, events));
        Assert.Equal(16, facts.Count(f => f.Key.StartsWith("tpm.ev.type.")));
        Assert.Contains("等 20 種", facts.Single(f => f.Key == TpmEventTypesFactsService.TypesKey).Value);
    }

    [Fact]
    public void 沒出現的型別不產鍵()
    {
        var facts = TpmEventTypesFactsService.Collect(At, W(false, E(0, 0x08)));
        Assert.DoesNotContain(facts, f => f.Key == "tpm.ev.type.1");
        Assert.DoesNotContain(facts, f => f.Key == "tpm.ev.type.4");
    }
}
