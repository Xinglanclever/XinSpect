using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>Amcache 的一筆軟體安裝紀錄。<b>只帶版本欄位，不帶完整路徑</b>（見服務說明）。</summary>
/// <param name="Name">產品名（可能是檔案描述、可能是資料夾名，來源不一）。</param>
/// <param name="Version">版本字串（讀不到時是空字串）。</param>
/// <param name="Publisher">發行者（讀不到時是空字串）。</param>
/// <param name="LinkDate">連結時間（PE 標頭的時間戳）——<b>不等於安裝時間</b>。</param>
public sealed record AmcacheEntry(string Name, string Version, string Publisher, DateTimeOffset? LinkDate);

/// <summary>
/// Amcache 解碼（Vol 2 FS-002）：<c>Amcache.hve</c> 裡的<b>軟體安裝紀錄</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是「安裝」而不是「執行」。</b>本專案已經有一項在答「執行痕跡」——Shimcache（FS-001）。
/// Amcache 的主角是 <c>Root\InventoryApplication</c> 這個機碼：系統在掃描時把<b>看得到的
/// 已安裝程式</b>寫進去，附帶版本與發行者。它與 Shimcache 的差別是<b>問題不同</b>：
/// Shimcache 答「哪些執行檔被執行過」，本項答「這台機器上裝了哪些版本的什麼軟體」。
/// 兩者都只是痕跡，都不是判決。
/// </para>
/// <para>
/// <b>誠實界線一：連結時間不是安裝時間。</b>PE 標頭裡的連結時間戳是<b>編譯器</b>寫的，
/// 反映的是這個執行檔什麼時候被建置出來，與「什麼時候被裝到這台機器上」可以差好幾年
/// （微軟的產品常見這種落差）。所以值裡把它叫「連結時間」而不是「安裝時間」——
/// 名字錯了，後面的推論就全錯。
/// </para>
/// <para>
/// <b>誠實界線二：不列完整路徑。</b>Amcache 的每一筆都帶執行檔路徑，而那些路徑常含
/// 使用者名稱、專案代號或客戶名（例如放在 <c>%USERPROFILE%</c> 下的自製工具）。
/// 本項只交付<b>產品名、版本、發行者</b>三欄——這三欄就足以回答「裝了哪些東西」，
/// 而路徑只是把隱私多送一份。檔名層級已經由既有的 Shimcache 樣本項負責（那項標敏感）。
/// </para>
/// <para>
/// <b>誠實界線三：解析 Amcache.hve 需要 hive 解析器，未對準出處就不出值。</b>
/// hive 的格式（regf 標頭、hbin、巢狀 cell、big data 值）本身是有公開文件的老格式，
/// 但 Amcache 的<b>欄位語意</b>是微軟未正式公告的——社群版本對「哪個欄位叫什麼」彼此有出入。
/// 本專案的紀律是：<b>沒有對準出處的欄位一律不顯示</b>（見 NVMe 健康紀錄位移那份教訓）。
/// 因此列舉由探針提供，未注入＝不適用，不是「裝了 0 個程式」。
/// </para>
/// </remarks>
public static class AmcacheFactsService
{
    public const string Category = "檔案鑑識";

    /// <summary>條目數（含申報數與解析數的落差）。</summary>
    public const string CountKey = "fs.amcache.count";
    /// <summary>版本分布。</summary>
    public const string VersionsKey = "fs.amcache.versions";
    /// <summary>發行者分布。</summary>
    public const string PublishersKey = "fs.amcache.publishers";

    /// <summary>條目數很少時不宣稱分布有代表性。</summary>
    public const int MinEntriesForDistribution = 20;

    /// <summary>值裡列出的發行者數上限。</summary>
    public const int MaxPublishersInText = 6;

    private const string Source = "Amcache.hve Root\\InventoryApplication（唯讀；欄位語意未對準出處者不顯示）";

    /// <summary>
    /// 由列舉結果算出事實。<paramref name="entries"/> 為 <c>null</c>＝未注入探針（沒讀）。
    /// <paramref name="declared"/> 是來源自報的條目數（讀不到時 <c>null</c>）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<AmcacheEntry>? entries = null, long? declared = null)
    {
        if (entries is null)
            return
            [
                Unavailable(at, CountKey, "Amcache 條目數",
                    "未注入 Amcache 列舉——未讀不等於沒裝任何程式；hive 欄位語意未對準出處前不出值"),
                Unavailable(at, VersionsKey, "Amcache 版本分布", "未注入 Amcache 列舉——未讀不等於沒有"),
                Unavailable(at, PublishersKey, "Amcache 發行者分布", "未注入 Amcache 列舉——未讀不等於沒有"),
            ];

        string gap = declared is { } d && d != entries.Count
            ? $"（來源自報 {d} 筆，本項解析出 {entries.Count} 筆——<b>兩個數字不同就照實並列</b>，不挑一個好看的）"
            : "";

        if (entries.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "Amcache 條目數",
                    "Amcache 裡沒有可解析的軟體安裝紀錄"
                    + (declared is { } d0 ? $"（來源自報 {d0} 筆）" : "")
                    + "。<b>空的 Amcache 不是「沒裝過程式」</b>：這個快取由系統維護、"
                    + "在乾淨安裝後可能還沒建立，也可能被清理工具刪掉。"
                    + "<b>沒有這份資料＝沒有這份資料</b>，不從缺檔案推論安裝狀況。",
                    "筆", Source, FactTrustLevel.Derived, false, at, 0),
                Unavailable(at, VersionsKey, "Amcache 版本分布", "沒有條目——沒有分布可言，不以「0 種版本」冒充"),
                Unavailable(at, PublishersKey, "Amcache 發行者分布", "沒有條目——沒有分布可言"),
            ];

        var versions = entries.Where(e => e.Version.Length > 0)
            .GroupBy(e => e.Version, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var publishers = entries.Where(e => e.Publisher.Length > 0)
            .GroupBy(e => e.Publisher, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        int unknownPublisher = entries.Count(e => e.Publisher.Length == 0);

        var facts = new List<HardwareFact>
        {
            new(CountKey, Category, "Amcache 條目數",
                $"{entries.Count} 筆軟體安裝紀錄{gap}。"
                + "<b>這一項答的是「裝了什麼」不是「跑了什麼」</b>——執行痕跡由 Shimcache（fs.shim.*）負責，"
                + "兩者是不同的問題，條目對不起來不是矛盾。"
                + "<b>痕跡不是判決</b>：Amcache 由系統掃描維護，殘留與缺漏都是常態。"
                + "<b>不列完整路徑</b>：路徑常含使用者名稱、專案代號或客戶名，"
                + "而產品名／版本／發行者這三欄就足以回答「裝了哪些東西」；"
                + "檔名層級已由 fs.shim.sample 負責（那項標敏感）。",
                "筆", Source, FactTrustLevel.Derived, false, at, entries.Count),
        };

        string distCaveat = entries.Count < MinEntriesForDistribution
            ? $"<b>樣本只有 {entries.Count} 筆，分布只作方向參考</b>，不足以代表這台機器的軟體組成"
            : "分布本身不是判決：同一支程式的不同版本可能各佔一筆，發行者空白也是常見情形";

        facts.Add(new HardwareFact(VersionsKey, Category, "Amcache 版本分布",
            versions.Count == 0
                ? "所有條目都沒有版本欄位——不如實列個「版本未知」的假分類"
                : "版本出現次數：" + string.Join("、",
                    versions.Take(MaxPublishersInText).Select(g => $"{g.Key}×{g.Count()}"))
                  + (versions.Count > MaxPublishersInText ? $"、其餘 {versions.Count - MaxPublishersInText} 種" : "")
                  + $"（共 {versions.Count} 種版本）。" + distCaveat + "。",
            "種", Source, FactTrustLevel.Derived, false, at, versions.Count));

        facts.Add(new HardwareFact(PublishersKey, Category, "Amcache 發行者分布",
            publishers.Count == 0
                ? $"所有條目的發行者都是空白（或全部缺少該欄位）——不補一個「未知」把它算成一類"
                : "發行者出現次數：" + string.Join("、",
                    publishers.Take(MaxPublishersInText).Select(g => $"{g.Key}×{g.Count()}"))
                  + (publishers.Count > MaxPublishersInText ? $"、其餘 {publishers.Count - MaxPublishersInText} 個" : "")
                  + $"（共 {publishers.Count} 個）"
                  + (unknownPublisher > 0 ? $"，另有 {unknownPublisher} 筆沒有發行者欄位" : "")
                  + "。<b>空白不併進「未知」那一類</b>：沒有欄位與有欄位但寫著 unknown 是兩件事。"
                  + distCaveat + "。",
            "個", Source, FactTrustLevel.Derived, false, at, publishers.Count));

        return facts;
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name, string reason)
        => new(key, Category, name, "", "", "Amcache（未讀取）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, reason);
}
