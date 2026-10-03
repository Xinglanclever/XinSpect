using Microsoft.Win32;

namespace XinSpect;

/// <summary>一筆 Windows Update 歷史（WUA COM 彙整前形態）。InstalledOn 為 null＝WUA 沒給日期，不猜。</summary>
/// <param name="ResultCode">WUA OperationResultCode：2 成功、3 部分成功、4 失敗、5 中止。</param>
public sealed record WuHistoryEntry(string Title, uint ResultCode, DateTimeOffset? InstalledOn, string Category);

/// <summary>
/// WP15 系統與軟體層：Windows Update 歷史（WUA COM「Microsoft.Update.Session」，usermode 零特權）。
/// 通路層極薄（Fetch 經 dynamic COM、失敗回 null），彙整邏輯（最新一筆／30 天內數量／失敗計數）
/// 是純函式、由注入探測釘值測試。歷史是 Windows 自己記的安裝記錄——可信度標 Reported。
/// </summary>
public static class WindowsUpdateHistoryService
{
    private const string Category = "系統與軟體";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<WuHistoryEntry>?>? probe = null)
    {
        var entries = (probe ?? FetchCom)();
        if (entries is null)
            return Unavailable(at, "Windows Update Agent COM 不可用（服務未啟動或 WUA 未安裝）——歷史讀不到就是不猜");

        var facts = new List<HardwareFact>
        {
            Fact("wu.history.total", "更新歷史總筆數", entries.Count.ToString(),
                "筆", entries.Count > 0 ? "" : "沒有任何記錄：這台機器的 WUA 歷史是空的", at, entries.Count),
        };
        var withDate = entries.Where(e => e.InstalledOn is not null).ToList();
        var latest = withDate.OrderByDescending(e => e.InstalledOn).FirstOrDefault();
        if (latest is not null)
        {
            string dateText = $"（安裝於 {latest.InstalledOn:yyyy-MM-dd}）";
            facts.Add(Fact("wu.history.latest", "最近一次更新",
                $"{latest.Title}{dateText}", "", $"類別：{latest.Category}；結果碼 {latest.ResultCode}", at, null));
        }
        else if (entries.FirstOrDefault() is { } noDate)
        {
            facts.Add(Fact("wu.history.latest", "最近一次更新",
                noDate.Title, "", "WUA 未提供安裝日期；類別：" + noDate.Category, at, null));
        }
        else
        {
            facts.Add(Fact("wu.history.latest", "最近一次更新", "—（歷史為空）", "", "", at, null));
        }

        uint within30 = (uint)withDate.Count(e => e.InstalledOn!.Value >= at.AddDays(-30));
        facts.Add(Fact("wu.history.30d", "近 30 天安裝數", within30.ToString(), "筆", "", at, within30));

        var failed = entries.Where(e => e.ResultCode is 4 or 5).ToList();
        string failedValue = failed.Count == 0
            ? "0 筆"
            : $"{failed.Count} 筆；最近一次：{failed.OrderByDescending(f => f.InstalledOn).First().Title}";
        facts.Add(Fact("wu.history.failed", "失敗／中止的更新", failedValue, "", "結果碼 4（失敗）或 5（中止）", at, (uint)failed.Count));
        return facts;
    }

    /// <summary>WUA COM 通路（極薄）：任何失敗回 null，由 Collect 標三態。</summary>
    public static IReadOnlyList<WuHistoryEntry>? FetchCom()
    {
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null) return null;
            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total <= 0) return [];
            dynamic history = searcher.QueryHistory(0, total);
            var entries = new List<WuHistoryEntry>();
            for (int i = 0; i < history.Count; i++)
            {
                dynamic h = history[i];
                DateTimeOffset? date = null;
                try
                {
                    dynamic d = h.Date;
                    if (d is not null && (DateTime)d != DateTime.MinValue)
                        date = new DateTimeOffset((DateTime)d, TimeSpan.Zero);
                }
                catch { /* 無日期就無日期，不猜 */ }
                string category = "";
                try
                {
                    foreach (var c in h.Categories) { category = c.Name; break; }
                }
                catch { /* 類別取不到留空 */ }
                entries.Add(new WuHistoryEntry((string)h.Title, (uint)h.ResultCode, date, category));
            }
            return entries;
        }
        catch
        {
            return null;
        }
    }

    private static HardwareFact Fact(string key, string name, string value, string unit, string note,
        DateTimeOffset at, double? numeric)
    {
        string v = string.IsNullOrEmpty(note) ? value : $"{value}（{note}）";
        return new HardwareFact(key, Category, name, v, unit, "Windows Update Agent COM（Microsoft.Update.Session 歷史查詢）",
            FactTrustLevel.Reported, false, at, numeric);
    }

    private static IReadOnlyList<HardwareFact> Unavailable(DateTimeOffset at, string reason) =>
        new[] { "wu.history.total", "wu.history.latest", "wu.history.30d", "wu.history.failed" }.Select(key =>
            new HardwareFact(key, Category, key switch
            {
                "wu.history.total" => "更新歷史總筆數",
                "wu.history.latest" => "最近一次更新",
                "wu.history.30d" => "近 30 天安裝數",
                _ => "失敗／中止的更新",
            }, "", "", "Windows Update Agent COM（Microsoft.Update.Session 歷史查詢）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason)).ToList();
}
