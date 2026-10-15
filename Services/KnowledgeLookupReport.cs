using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 知識表查詢的覆蓋申報（ULTIMATE-1000 階段一 P1-039 OUI、P1-040 PCI）。
///
/// <para>
/// <b>這一支不重寫知識表。</b><see cref="OuiKnowledge"/> 與 <see cref="PciKnowledge"/>
/// 已經存在、有出處、也已註冊在 <see cref="SpecRefRegistry"/> 裡。缺的是同一件事：
/// 把「查了多少次、命中幾次、哪些查不到」變成事實輸出。
/// </para>
///
/// <para>
/// <b>為什麼「查不到」要單獨申報：</b>知識表的價值在於「說得出出處的名字」。
/// 查不到時若靜默回一個代碼，使用者看到的是「未知裝置」——他不知道那是
/// 「這台機器有一顆很罕見的晶片」還是「我們的表太舊」。把未收錄的鍵列出來，
/// 他就能自己判斷，也才有依據去補表。
/// </para>
/// </summary>
public static class KnowledgeLookupReport
{
    public const string OuiKey = "net.oui";
    public const string PciKey = "pci.id";

    private const string Category = "知識與更新";
    private const string Source = "內建知識表（離線）";

    /// <summary>一次知識表查詢的結果。三態：命中、未收錄、無法解析——第二與第三不同。</summary>
    public enum LookupOutcome
    {
        /// <summary>表裡有，而且給得出名字。</summary>
        Hit,
        /// <summary>格式對，但表裡沒有這一筆。</summary>
        NotListed,
        /// <summary>輸入本身無法解析（不是表的問題）。</summary>
        Unparsable,
    }

    /// <summary>
    /// 查詢統計。<see cref="NotListed"/> 與 <see cref="Unparsable"/> 刻意分開：
    /// 前者是「該補表」，後者是「輸入有問題」——處理方式完全不同。
    /// </summary>
    public sealed record LookupStats(int Total, int Hits, int NotListed, int Unparsable,
        IReadOnlyList<string> MissedKeys)
    {
        public static LookupStats Empty { get; } = new(0, 0, 0, 0, []);
    }

    /// <summary>查一次 OUI。</summary>
    public static (LookupOutcome Outcome, string Result) LookupOui(string mac)
    {
        string result = OuiKnowledge.VendorOf(mac);
        // OuiKnowledge 自己就把兩種失敗分開了（「無法解析的 MAC」／「（未收錄）」），
        // 這裡只是把它轉成可統計的列舉——不重複判斷邏輯。
        if (result.Contains("無法解析", StringComparison.Ordinal)) return (LookupOutcome.Unparsable, result);
        if (result.Contains("未收錄", StringComparison.Ordinal)) return (LookupOutcome.NotListed, result);
        return (LookupOutcome.Hit, result);
    }

    /// <summary>查一次 PCI 廠商。</summary>
    public static (LookupOutcome Outcome, string Result) LookupPciVendor(ushort vendorId)
    {
        string? name = PciKnowledge.VendorName(vendorId);
        return name is not null
            ? (LookupOutcome.Hit, name)
            : (LookupOutcome.NotListed, $"Vendor 0x{vendorId:X4}（未收錄）");
    }

    /// <summary>把一串查詢結果彙總。</summary>
    public static LookupStats Summarize(IEnumerable<(LookupOutcome Outcome, string Result)> lookups)
    {
        var list = lookups.ToList();
        if (list.Count == 0) return LookupStats.Empty;
        var missed = list.Where(l => l.Outcome == LookupOutcome.NotListed)
            .Select(l => l.Result).Distinct(StringComparer.Ordinal).ToList();
        return new LookupStats(list.Count,
            list.Count(l => l.Outcome == LookupOutcome.Hit),
            list.Count(l => l.Outcome == LookupOutcome.NotListed),
            list.Count(l => l.Outcome == LookupOutcome.Unparsable),
            missed);
    }

    /// <summary>產生 <c>net.oui</c>。</summary>
    public static HardwareFact ToOuiFact(DateTimeOffset at, LookupStats stats) =>
        ToFact(OuiKey, "OUI 廠商對照", at, stats, "網卡");

    /// <summary>產生 <c>pci.id</code>。</summary>
    public static HardwareFact ToPciFact(DateTimeOffset at, LookupStats stats) =>
        ToFact(PciKey, "PCI 廠商對照", at, stats, "PCI 裝置");

    private static HardwareFact ToFact(string key, string name, DateTimeOffset at,
        LookupStats stats, string subject)
    {
        if (stats.Total == 0)
            return new HardwareFact(key, Category, name,
                $"本輪沒有查詢{subject}", "次", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, $"沒有任何{subject}需要對照");

        string detail = stats.NotListed == 0 && stats.Unparsable == 0
            ? $"{stats.Total} 次查詢全部命中"
            : $"{stats.Total} 次查詢：命中 {stats.Hits}、未收錄 {stats.NotListed}"
              + (stats.Unparsable > 0 ? $"{stats.NotListed}、無法解析 {stats.Unparsable}" : "")
              + (stats.MissedKeys.Count > 0
                  ? "；未收錄的鍵：" + string.Join("、", stats.MissedKeys.Take(10))
                  : "");

        // 全部命中 → Present。
        // 有未收錄 → Unknown：**這不是錯誤**，是「這個表沒有這一筆」，
        //   而使用者需要知道他的機器上有東西沒被辨識出來。
        // 有無法解析 → ReadError：那是輸入本身的問題，需要修的是輸入。
        var availability =
            stats.Total == stats.Hits ? FactAvailability.Present
            : stats.Unparsable > 0 ? FactAvailability.ReadError
            : FactAvailability.Unknown;

        string? reason = availability switch
        {
            FactAvailability.Present => null,
            FactAvailability.ReadError =>
                $"{stats.Unparsable} 筆輸入無法解析——那是輸入的問題，不是知識表的缺口",
            _ => $"{stats.NotListed} 筆未收錄——如實顯示原始碼而不猜名稱；要補的是知識表",
        };

        return new HardwareFact(key, Category, name, detail, "次", Source,
            FactTrustLevel.Derived, false, at, stats.Hits, availability, reason);
    }
}
