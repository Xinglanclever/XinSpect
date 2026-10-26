using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 內建知識表的清冊（第 283 輪，鍵 <c>kp.builtin.*</c>）。
///
/// <para>
/// <b>這一支要修的問題是一個說死的字串。</b><see cref="KnowledgePackService.BuiltinSnapshot"/>
/// 從 v2.66 起就寫著「OuiKnowledge／bugcheck 子集；打包於 2026-10-11」——但內建表早就不是那樣了：
/// 現在有 PCI 類別碼與廠商、Super I/O 晶片、CTA VIC 時序、SMART 屬性、bugcheck、
/// 以及 2,519 廠商／21,619 裝置的 PCI 登錄檔快照。<b>那句話在畫面上是「內建表有哪些東西」的唯一說明</b>，
/// 而它停在六個版本前。
/// </para>
///
/// <para>
/// <b>修法不是改字串，是讓它算出來。</b>清冊逐表列出條數、來源、快照日期與「誰擔保」，
/// 那個字串由清冊推導；表變了字串自己會變，不可能再落後。
/// </para>
///
/// <para>
/// <b>「誰擔保」分成三級，因為它們的錯誤代價不同：</b>
/// </para>
/// <list type="bullet">
/// <item><b>規格</b>（PCI 類別碼、CTA VIC）：規格定義的封閉集合，錯了是實作錯，不是資料舊。</item>
/// <item><b>本專案核對</b>（手工子集）：每一條都說得出出處，但覆蓋率有限。</item>
/// <item><b>上游登錄檔</b>（PCI 廠商／裝置、SMART 屬性、Super I/O 晶片）：社群維護，
///   接受投稿，<b>錯誤率不是零</b>，而且會過期。</item>
/// </list>
///
/// <para>
/// <b>沒有抓取日期的表如實留 null</b>，不編一個——「這張表沒有日期」與「這張表是今天的」
/// 在畫面上長得一樣，而後者是編的。
/// </para>
/// </summary>
public static class BuiltinKnowledgeInventory
{
    private const string Category = "知識與更新";

    /// <summary>表的擔保等級。</summary>
    public enum Guarantee
    {
        /// <summary>規格定義的封閉集合。</summary>
        Specification,

        /// <summary>本專案逐條核對過的手工子集。</summary>
        Curated,

        /// <summary>上游登錄檔／社群維護的快照。</summary>
        Upstream,
    }

    /// <summary>一張內建知識表。</summary>
    /// <param name="Slug">鍵的末段（ASCII）——鍵一路都是 ASCII，表名可以是中文。</param>
    /// <param name="Name">表名（給人看的）。</param>
    /// <param name="Entries">條數（執行期由表本身數出來）。</param>
    /// <param name="Guarantee">誰擔保。</param>
    /// <param name="Snapshot">快照日期；<c>null</c>＝這張表沒有抓取日期（不編）。</param>
    /// <param name="Source">出處。</param>
    public readonly record struct Entry(
        string Slug, string Name, int Entries, Guarantee Guarantee, DateOnly? Snapshot, string Source)
    {
        /// <summary>擔保等級的中文說明。</summary>
        public string GuaranteeText => Guarantee switch
        {
            Guarantee.Specification => "規格定義（封閉集合）",
            Guarantee.Curated => "本專案核對（手工子集）",
            _ => "上游登錄檔（社群維護，錯誤率不是零）",
        };

        /// <summary>快照欄的文字。</summary>
        public string SnapshotText => Snapshot is { } d
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "（無抓取日期）";
    }

    /// <summary>全部內建知識表（條數在執行期由各表本身數出來，不手抄）。</summary>
    public static IReadOnlyList<Entry> Entries { get; } = Build();

    /// <summary>
    /// pci.ids 快照日的<strong>日期形態</strong>——由 <see cref="PciIdDatabase.SnapshotVersion"/> 解析而來，
    /// 不另外手抄一份（兩份日期一定會分岔）。上游改了標籤格式就在這裡爆，不會靜默變成「沒有日期」。
    ///
    /// <para>
    /// <b>刻意寫成方法而不是唯讀屬性。</b>靜態成員的初始化照宣告順序跑，而 <see cref="Entries"/>
    /// 宣告在這一行之前——若這裡是屬性，<see cref="Build"/> 會讀到尚未初始化的 <c>default</c>，
    /// 也就是 <c>0001-01-01</c>。<b>那不會爆，只會安靜地把 1 年 1 月 1 日當成抓取日</b>
    /// （本輪就真的這樣紅了一次測試）。方法呼叫保證取值時已經初始化。
    /// </para>
    /// </summary>
    private static DateOnly PciSnapshotDate() =>
        DateOnly.ParseExact(PciIdDatabase.SnapshotVersion, "yyyy.MM.dd", CultureInfo.InvariantCulture);

    /// <summary>知識表自報的 ISO 日期字串轉日期——格式不合就爆，不吞成 null。</summary>
    private static DateOnly Iso(string text) =>
        DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IReadOnlyList<Entry> Build()
    {
        var pci = PciKnowledge.Counts();
        return
        [
        new("pci-class", "PCI 基底／子類別碼", pci.BaseClasses + pci.SubClasses, Guarantee.Specification, null,
            $"PCI-SIG, PCI Code and Vendor ID Assignments Spec（基底 {pci.BaseClasses}＋子類 {pci.SubClasses}）"),
        new("pci-vendor-curated", "PCI 廠商 ID（手工）", pci.Vendors, Guarantee.Curated, null,
            "PCI-SIG 廠商登錄的知名子集——每條都能說出出處"),
        new("pci-registry", "PCI 廠商／裝置 ID（登錄檔）", PciIdDatabase.VendorCount + PciIdDatabase.DeviceCount,
            Guarantee.Upstream, PciSnapshotDate(),
            $"pci.ids 快照標籤 {PciIdDatabase.SnapshotVersion}（{PciIdDatabase.VendorCount:N0} 廠商＋"
            + $"{PciIdDatabase.DeviceCount:N0} 裝置）——標籤是上游發佈日，不是本專案抓取日"),
        new("superio", "Super I/O 晶片名稱", SuperIoKnowledge.Count, Guarantee.Upstream, new DateOnly(2026, 10, 7),
            "coreboot util/superiotool ite.c／nuvoton.c／fintek.c"),
        new("oui", "MAC OUI 廠商", OuiKnowledge.Count, Guarantee.Upstream, null,
            "IEEE OUI 登記的知名前綴子集"),
        new("cta-vic", "CTA VIC 時序", VicKnowledge.Count, Guarantee.Specification, new DateOnly(2026, 10, 27),
            "CTA-861 的 VIC 定義（交叉 Linux drm_edid.c 兩張模式表）"),
        new("smart", "SMART 屬性（六家）", SmartAttributeKnowledge.TotalCount, Guarantee.Upstream,
            Iso(SmartAttributeKnowledge.FetchedDate),
            "六家硬碟廠的 SMART 屬性表（raw／normalized 分欄，逐家出處）"),
        new("bugcheck-code", "BugCheck 停止代碼", BugCheck.Count, Guarantee.Upstream, null,
            "常見藍屏停止代碼的白話原因提示"),
        new("bugcheck-symbol", "BugCheck 符號名（傾印解碼）", KernelDumpDecoder.TableCount(), Guarantee.Upstream,
            new DateOnly(2026, 10, 11), "Microsoft Learn「Bug Check Code Reference」"),
        ];
    }

    /// <summary>總條數。</summary>
    public static int TotalEntries => Entries.Sum(e => e.Entries);

    /// <summary>有快照日期的表數。</summary>
    public static int DatedTables => Entries.Count(e => e.Snapshot is not null);

    /// <summary>
    /// 最舊的快照日期（只算有日期的表）。<b>回 <c>null</c> 表示一張有日期的都沒有</b>——
    /// 那與「最舊的是今天」是兩件事。
    /// </summary>
    public static DateOnly? OldestSnapshot() => OldestSnapshot(Entries);

    /// <summary>指定清冊的最舊快照日（測試用：讓「一張有日期的都沒有」那條路徑真的跑得到）。</summary>
    internal static DateOnly? OldestSnapshot(IReadOnlyList<Entry> entries)
    {
        DateOnly? oldest = null;
        foreach (var e in entries)
            if (e.Snapshot is { } d && (oldest is null || d < oldest))
                oldest = d;
        return oldest;
    }

    /// <summary>一句話的內建表聲明——由清冊推導，不可能落後於表本身。</summary>
    public static string SnapshotStatement() => SnapshotStatement(Entries);

    /// <summary>指定清冊的一句話聲明。</summary>
    internal static string SnapshotStatement(IReadOnlyList<Entry> entries)
    {
        var oldest = OldestSnapshot(entries);
        string age = oldest is { } d
            ? $"最舊的快照是 {d:yyyy-MM-dd}"
            : "這些表都沒有抓取日期";
        return $"內建 {entries.Count} 張知識表、共 {entries.Sum(e => e.Entries):N0} 條"
             + $"（{entries.Count(e => e.Snapshot is not null)} 張有快照日期，{age}）";
    }

    // ── 事實輸出 ──────────────────────────────────────────────────────────

    /// <summary>內建知識表的張數。</summary>
    public const string TablesKey = "kp.builtin.tables";

    /// <summary>內建知識表的總條數。</summary>
    public const string EntriesKey = "kp.builtin.entries";

    /// <summary>最舊的快照日期（離線知識會過期，這一項讓它可見）。</summary>
    public const string OldestKey = "kp.builtin.oldest";

    /// <summary>逐表明細的<strong>家族前綴</strong>——表有幾張是本版的事實，成員由清冊決定。</summary>
    private const string TableItemPrefix = "kp.builtin.item.";

    /// <summary>收集 <c>kp.builtin.*</c>（三把固定鍵＋逐表明細家族）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at) => Collect(at, Entries);

    /// <summary>指定清冊的事實收集（測試用）。</summary>
    internal static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IReadOnlyList<Entry> entries)
    {
        var facts = new List<HardwareFact>(entries.Count + 3);
        var oldest = OldestSnapshot(entries);
        int totalEntries = entries.Sum(e => e.Entries);

        facts.Add(new HardwareFact(TablesKey, Category, "內建知識表張數",
            $"{entries.Count} 張（規格 {entries.Count(e => e.Guarantee == Guarantee.Specification)}、"
            + $"本專案核對 {entries.Count(e => e.Guarantee == Guarantee.Curated)}、"
            + $"上游登錄檔 {entries.Count(e => e.Guarantee == Guarantee.Upstream)}）。"
            + "界線：張數不是品質——每一張的擔保等級不同，混著數會讓社群維護的表看起來像規格。",
            "張", "BuiltinKnowledgeInventory（清冊）", FactTrustLevel.Derived, false, at, entries.Count));

        facts.Add(new HardwareFact(EntriesKey, Category, "內建知識表總條數",
            $"{totalEntries:N0} 條。其中 {entries.Where(e => e.Guarantee == Guarantee.Upstream).Sum(e => e.Entries):N0} 條"
            + "來自上游登錄檔（社群維護、會過期）；查不到名字代表表沒有那一格，不代表裝置有問題。",
            "條", "BuiltinKnowledgeInventory（清冊）", FactTrustLevel.Derived, false, at, totalEntries));

        if (oldest is { } d)
        {
            int days = DateOnly.FromDateTime(at.UtcDateTime).DayNumber - d.DayNumber;
            facts.Add(new HardwareFact(OldestKey, Category, "最舊的知識表快照",
                $"{d:yyyy-MM-dd}（距今 {days} 天）。沒有更新通道：知識表只在使用者手動換檔時更新。"
                + "界線：這是最舊的一張，不是每一張——逐表日期見 kp.builtin.item.*。",
                "日", "BuiltinKnowledgeInventory（清冊）", FactTrustLevel.Derived, false, at, days));
        }
        else
        {
            facts.Add(new HardwareFact(OldestKey, Category, "最舊的知識表快照", "", "日",
                "BuiltinKnowledgeInventory（清冊）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable,
                "**沒有任何一張表有抓取日期**——那不是「最舊的是今天」，是「這份清冊無從回答這個問題」"));
        }

        var today = DateOnly.FromDateTime(at.UtcDateTime);
        foreach (var e in entries)
        {
            string key = TableItemPrefix + e.Slug;
            // 宣告日期晚於量測日＝其中一個是錯的。兩邊都不改，照實並列——
            // 「代為修正成看起來合理的日期」會把一個查得到的矛盾變成一筆查不到的假資料。
            string future = e.Snapshot is { } sd && sd > today
                ? $"；**宣告的快照日 {sd:yyyy-MM-dd} 晚於量測日 {today:yyyy-MM-dd}——"
                  + "上游標籤或本專案的記錄有誤，這裡如實照登，不代為修正**"
                : "";
            facts.Add(new HardwareFact(key, Category, e.Name,
                $"{e.Entries:N0} 條；擔保：{e.GuaranteeText}；快照：{e.SnapshotText}；出處：{e.Source}{future}",
                "條", "BuiltinKnowledgeInventory（清冊）", FactTrustLevel.Derived, false, at, e.Entries));
        }
        return facts;
    }
}
