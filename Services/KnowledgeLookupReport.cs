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

    /// <summary>
    /// PCI 名稱段的出處統計（第 284 輪）。<b>一次＝一個名稱段</b>——畫面上的每張卡有
    /// 廠商與裝置兩段名字，兩段可以各自落在不同的擔保層。
    ///
    /// <para>
    /// <b>為什麼不合成一個「命中」計數：</b>手工表的 15 家是本專案逐條核對過的，
    /// 登錄檔的兩萬多筆是社群維護、接受投稿、<b>錯誤率不是零</b>的快照。
    /// 合成之後「命中 45」看起來像單一品質，而讀者無從知道其中有多少只是
    /// 「某個不認識的人投稿過這個 ID」。分開數不是為了好看，是不讓後者借前者的信用。
    /// </para>
    /// </summary>
    public sealed record PciNameStats(int Curated, int Registry, int Unlisted, int Unparsable)
    {
        public static PciNameStats Empty { get; } = new(0, 0, 0, 0);

        /// <summary>名稱段總數。</summary>
        public int Total => Curated + Registry + Unlisted + Unparsable;

        /// <summary>說得出名字的段數（手工表＋登錄檔）。</summary>
        public int Named => Curated + Registry;
    }

    /// <summary>
    /// 查一次 OUI。<b>分類看 <see cref="OuiKnowledge.OriginOf"/>，不看拿回來的字串</b>——
    /// 第 285 輪之前這裡是靠「結果裡有沒有出現某句中文」判斷，換一個字就會安靜地改口。
    /// </summary>
    public static (LookupOutcome Outcome, string Result) LookupOui(string mac)
    {
        string result = OuiKnowledge.VendorOf(mac);
        return OuiKnowledge.OriginOf(mac) switch
        {
            OuiKnowledge.OuiOrigin.Hit => (LookupOutcome.Hit, result),
            OuiKnowledge.OuiOrigin.Unlisted => (LookupOutcome.NotListed, result),
            _ => (LookupOutcome.Unparsable, result),
        };
    }

    /// <summary>
    /// 查一次 PCI 廠商。<b>第二個回傳值是這一格名字的出處</b>——
    /// 只回名字會讓呼叫端把「本專案核對過」與「上游登錄檔這麼寫」當成同一件事。
    ///
    /// <para>
    /// <b>第 284 輪修掉的缺陷：</b>這裡原本只問 <see cref="PciKnowledge"/>（15 家手工表），
    /// 於是登錄檔認得的 2,519 家在這裡全部顯示「未收錄」——同一個問題在專案裡有兩條路徑，
    /// 一條知道登錄檔、一條不知道。現在一律走 <see cref="PciIdLookup"/>。
    /// </para>
    /// </summary>
    public static (LookupOutcome Outcome, PciIdLookup.NameOrigin Origin, string Result)
        LookupPciVendor(ushort vendorId)
    {
        var hit = PciIdLookup.Vendor(vendorId);
        return hit.Found
            ? (LookupOutcome.Hit, hit.Origin, hit.Name!)
            : (LookupOutcome.NotListed, hit.Origin, $"Vendor 0x{vendorId:X4}（未收錄）");
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

    /// <summary>
    /// 產生 <c>pci.id</c>——<b>逐名稱段</b>申報三種出處（第 284 輪）。
    ///
    /// <para>
    /// 舊版把「值裡沒有出現『未收錄』」算成命中，所以一張卡只要廠商認得就算命中，
    /// 而登錄檔給的名字與手工表給的名字在統計上完全一樣。現在改成看值裡那段
    /// <see cref="PciIdLookup.OriginClausePrefix"/> 說明，兩段各記一次。
    /// </para>
    /// </summary>
    public static HardwareFact ToPciFact(DateTimeOffset at, PciNameStats stats)
    {
        if (stats.Total == 0)
            return new HardwareFact(PciKey, Category, "PCI 名稱對照",
                "本輪沒有查詢 PCI 名稱", "段", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有任何 PCI 裝置需要對照");

        string detail = $"{stats.Total} 個名稱段（每張卡＝廠商＋裝置兩段）："
            + $"{PciIdLookup.CuratedToken} {stats.Curated}、{PciIdLookup.RegistryToken} {stats.Registry}、"
            + $"{PciIdLookup.UnlistedToken} {stats.Unlisted}"
            + (stats.Unparsable > 0 ? $"、無法分類 {stats.Unparsable}" : "")
            + $" ・ 界線：{PciIdLookup.CuratedToken}是本專案核對過的 15 家，"
            + $"{PciIdLookup.RegistryToken}是社群維護的快照（錯誤率不是零）——"
            + "兩者分開數，不讓後者借前者的信用；查不到只代表這份快照沒有那一格。";

        // 三種狀態各有各的處置：
        //   全部說得出名字 → Present（仍然不是「驗證過」，只是「表裡有」）。
        //   有未收錄 → Unknown：「這個表沒有這一筆」，不是錯誤，但要被看見。
        //   有無法分類 → ReadError：那是輸入或生產端的問題，補表沒有用。
        var availability =
            stats.Unparsable > 0 ? FactAvailability.ReadError
            : stats.Unlisted > 0 ? FactAvailability.Unknown
            : FactAvailability.Present;

        string? reason = availability switch
        {
            FactAvailability.ReadError =>
                $"{stats.Unparsable} 個名稱段沒有帶出處說明（值裡沒有「{PciIdLookup.OriginClausePrefix}」）——"
                + "那是生產端的問題，不是知識表的缺口",
            FactAvailability.Unknown =>
                $"{stats.Unlisted} 個名稱段未收錄——如實顯示原始碼而不猜名稱；要補的是知識表",
            _ => null,
        };

        return new HardwareFact(PciKey, Category, "PCI 名稱對照", detail, "段", Source,
            FactTrustLevel.Derived, false, at, stats.Named, availability, reason);
    }

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
