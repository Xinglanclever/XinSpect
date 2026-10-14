using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Tracing;

namespace XinSpect;

/// <summary>
/// 一次「篩選讀回」的結果：某個已落地軌跡套上一組提供者篩選後，留下與丟掉各多少事件。
/// </summary>
/// <param name="FileName">軌跡檔名（不含資料夾）。</param>
/// <param name="Matched">通過篩選的事件數。</param>
/// <param name="Dropped">被篩選排除的事件數（未解事件計入此處，因為它連提供者名都沒有）。</param>
/// <param name="Providers">留下的逐提供者事件數（多者在前）。</param>
public sealed record EtwFilterResult(
    string FileName, int Matched, int Dropped, IReadOnlyList<(string Provider, int Events)> Providers);

/// <summary>
/// ETW 篩選視圖（Vol 2 DL-001）：對<b>已落地</b>的 .etl 套一組提供者條件，只留下符合的事件。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項不是新的軌跡來源，是既有軌跡的第二種看法。</b>軌跡的產生（<see cref="EtwTraceService"/>）
/// 與整體內容讀回（<see cref="EtlReadbackService"/>）都已存在；缺的一直是「這麼多事件裡，跟我手上
/// 這個問題有關的是哪幾筆」。篩選視圖補的是那一層：<b>同一份檔案，換一組條件，得到不同的子集</b>。
/// </para>
/// <para>
/// <b>誠實界線一：篩選是事後過濾，不是事前擷取。</b>ETW 的提供者／關鍵字在<b>工作階段建立當時</b>就固定了；
/// 檔案裡沒有的提供者，篩選再怎麼放也篩不出來。所以條件寫錯不會得到「更乾淨的資料」，
/// 只會得到<b>空的子集</b>——而空子集與「這段期間真的沒發生」長得一模一樣。
/// 值裡因此一定同時報<b>留下幾個與丟掉幾個</b>：只講留下來的，讀者無從判斷是「本來就少」還是「篩歪了」。
/// </para>
/// <para>
/// <b>誠實界線二：未解事件歸「丟掉」不是歸「符合」。</b>TraceEvent 沒有對應 schema 的事件連提供者名都拿不到，
/// 無從比對條件。把它算成符合會虛報命中數；<b>計入丟掉並在值裡說明</b>，讓「未解」這件事仍然是看得見的。
/// </para>
/// <para>
/// <b>誠實界線三：量測期間要說得出來。</b>子集的時間範圍由檔案本身決定（<see cref="EtlReadbackService"/> 已報），
/// 本項不重複報時間，但也不宣稱涵蓋任何它沒讀到的期間——篩選視圖的名字裡沒有「量測」二字，
/// 是因為它量的是檔案，不是機器。
/// </para>
/// <para>
/// <b>唯讀：</b>本服務只開檔讀，不寫、不刪、不改名；<see cref="EtwTraceService.IsValidEtl"/> 的檔頭驗證
/// 先跑過才把位元組交給 TraceEvent——那條驗證不是防禦性程式設計，是實測踩過的當機（見讀回服務的說明）。
/// </para>
/// </remarks>
public static class EtwFilterViewService
{
    public const string Category = "系統與軟體";

    /// <summary>留下的事件數。</summary>
    public const string SummaryKey = "dl.etw.summary";
    /// <summary>逐提供者的留下事件數。</summary>
    public const string FilterKey = "dl.etw.filter";

    /// <summary>來源字串（測試會驗它標明了唯讀——讀者要看得出這項不動檔案）。</summary>
    public const string SourceText = "TraceEvent 檔案模式讀回（唯讀，事後篩選）";

    private const string Source = SourceText;

    /// <summary>逐提供者清單在事實值裡列出的上限（超過的以「其餘 N 個」收尾）。</summary>
    public const int MaxProvidersInText = 4;

    /// <summary>讀回幾個軌跡做篩選（全部讀會拖慢 reload；與讀回服務同一個量級）。</summary>
    public const int MaxFiles = 3;

    /// <summary>
    /// 對單一 .etl 套篩選。<paramref name="keep"/> 回 <c>true</c> 代表這個提供者的事件要留下；
    /// 傳 <c>null</c>＝不過濾（全部留下，仍報計數）。
    /// </summary>
    /// <remarks>
    /// <b>必須先過檔頭驗證：</b>與 <see cref="EtlReadbackService.ReadFile"/> 同一個理由——
    /// 非 .etl 的位元組餵給 TraceEvent 會在 finalizer 裡踩到半建構物件，炸掉整個行程。
    /// </remarks>
    public static (EtwFilterResult? Result, string? Error) Filter(string path, Func<string, bool>? keep)
    {
        if (!EtwTraceService.IsValidEtl(path))
            return (null, "不是 .etl 檔頭——略過，不把非 ETW 內容餵給解析引擎");
        try
        {
            int matched = 0, dropped = 0;
            var providers = new Dictionary<string, int>(StringComparer.Ordinal);
            using var source = new ETWTraceEventSource(path);
            source.AllEvents += e =>
            {
                string name = e.ProviderName;
                bool ok = !string.IsNullOrEmpty(name) && (keep is null || keep(name));
                if (!ok) { dropped++; return; }
                matched++;
                providers[name] = providers.TryGetValue(name, out var n) ? n + 1 : 1;
            };
            // 未解事件拿不到提供者名，無從比對條件——計入丟掉並由事實值說明（界線二）
            source.UnhandledEvents += _ => dropped++;
            source.Process();

            var kept = providers.OrderByDescending(kv => kv.Value)
                .Select(kv => (kv.Key, kv.Value)).ToList();
            return (new EtwFilterResult(Path.GetFileName(path), matched, dropped, kept), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// 聚合入口：列舉資料夾裡的 .etl，對最近 <see cref="MaxFiles"/> 個套同一組篩選。
    /// <paramref name="folder"/> 供測試與探針指定，生產路徑用 <see cref="EtwTraceService"/> 的預設資料夾。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string? folder = null,
        Func<string, bool>? keep = null, int maxFiles = MaxFiles)
    {
        folder ??= new EtwTraceService().Folder;
        var files = new List<(string Path, long Size)>();
        if (Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFiles(folder, "*.etl"))
                files.Add((f, new FileInfo(f).Length));
        if (files.Count == 0)
            return
            [
                Unavailable(at, SummaryKey, "ETW 篩選視圖", FactAvailability.NotApplicable,
                    $"軌跡資料夾裡還沒有 .etl（{folder}）——沒有可篩選的軌跡，不是錯誤，是環境狀態"),
                Unavailable(at, FilterKey, "ETW 逐提供者篩選結果", FactAvailability.NotApplicable,
                    $"軌跡資料夾裡還沒有 .etl（{folder}）——沒有可篩選的軌跡"),
            ];
        // 本服務命名含時間戳，檔名降冪 ≈ 新到舊（與讀回服務同一條規則）
        files.Sort((a, b) => string.CompareOrdinal(b.Path, a.Path));
        int take = Math.Min(files.Count, maxFiles);

        int totalMatched = 0, totalDropped = 0, read = 0, failed = 0;
        var detail = new List<string>();
        var byProvider = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, _) in files.Take(take))
        {
            var (res, err) = Filter(path, keep);
            if (res is null)
            {
                failed++;
                detail.Add($"【{Path.GetFileName(path)}】篩選失敗：{err}；");
                continue;
            }
            read++;
            totalMatched += res.Matched;
            totalDropped += res.Dropped;
            foreach (var (p, n) in res.Providers)
                byProvider[p] = byProvider.TryGetValue(p, out var m) ? m + n : n;
            detail.Add($"【{res.FileName}】留下 {res.Matched}、丟掉 {res.Dropped}；");
        }

        string cond = keep is null ? "無條件（全部留下）" : "提供者白名單";
        int totalSeen = totalMatched + totalDropped;
        string summary = read == 0
            ? $"最近 {take} 個軌跡全部篩選失敗：" + string.Join("", detail) + "如實回報，不猜內容"
            : $"篩選條件：{cond}；最近 {take} 個軌跡共讀到 {totalSeen} 個事件，"
              + $"留下 {totalMatched}、丟掉 {totalDropped}。" + string.Join("", detail)
              + ExplainKeepAndDrop();

        var top = byProvider.OrderByDescending(kv => kv.Value).Take(MaxProvidersInText).ToList();
        int rest = byProvider.Count - top.Count;
        string filterValue = read == 0
            ? $"最近 {take} 個軌跡全部篩選失敗——逐檔原因見彙總值；不猜內容"
            : byProvider.Count == 0
                ? $"篩選後沒有任何事件留下（讀到 {totalSeen} 個、全部被條件排除）。"
                  + ExplainEmptySubset()
                : "留下的逐提供者事件數：" +
                  string.Join("、", top.Select(kv => $"{kv.Key}×{kv.Value}"))
                  + (rest > 0 ? $"、其餘 {rest} 個提供者" : "")
                  + "。" + ExplainNoNewSession();

        var facts = new List<HardwareFact>
        {
            new(SummaryKey, Category, "ETW 篩選視圖", summary, "個", Source,
                FactTrustLevel.Measured, false, at, totalSeen,
                read > 0 ? FactAvailability.Present : FactAvailability.ReadError,
                read > 0 ? null : $"{failed} 個軌跡篩選失敗——TraceEvent 解析不開，逐檔原因見事實內容"),
        };
        facts.Add(new HardwareFact(FilterKey, Category, "ETW 逐提供者篩選結果", filterValue, "個",
            Source, FactTrustLevel.Measured, false, at, byProvider.Count,
            read > 0 ? FactAvailability.Present : FactAvailability.ReadError,
            read > 0 ? null : $"{failed} 個軌跡篩選失敗——無法產生逐提供者結果"));
        return facts;
    }

    /// <summary>
    /// 「留下與丟掉都要報」的說明。<b>這是本項的核心誠實契約</b>：只講留下來的，
    /// 讀者無從判斷是「本來就少」還是「條件篩歪了」，所以抽成具名方法並由測試釘住。
    /// </summary>
    public static string ExplainKeepAndDrop()
        => "<b>留下與丟掉都要報</b>：只講留下來的，讀者無從判斷是「本來就少」還是「條件篩歪了」；"
         + "未解事件（TraceEvent 沒有 schema、連提供者名都拿不到）計入丟掉，不冒充命中。";

    /// <summary>
    /// 「這是第二種看法不是新來源」的說明。<b>不講清楚，讀者會以為它抓了新資料</b>——
    /// 那牽涉管理員權限與系統負載，與本項「只讀既有檔案」的實際行為差很遠。
    /// </summary>
    public static string ExplainNoNewSession()
        => "<b>篩選是事後過濾不是事前擷取</b>——ETW 的提供者／關鍵字在工作階段建立當時就固定了；"
         + "本項只讀既有檔案，不建立新工作階段、不啟用任何提供者。";

    /// <summary>
    /// 空子集的說明文字。<b>這是本項最容易誤導讀者的地方</b>，所以抽成一個具名方法：
    /// 條件篩歪與「這段期間真的沒發生」產生一模一樣的輸出，文字必須自己把它講開，
    /// 而且要被測試釘住（改壞了會紅）。
    /// </summary>
    public static string ExplainEmptySubset()
        => "<b>空子集不是「沒發生過」</b>：篩選是事後過濾，檔案裡沒有的提供者篩不出來——"
         + "條件寫錯只會得到空的子集，而它與「這段期間真的沒有」長得一模一樣。";

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
