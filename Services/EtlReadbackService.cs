using System.IO;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace XinSpect;

/// <summary>一個 .etl 的內容摘要：事件數、未解事件數、時間範圍、逐提供者事件數（多者在前）。</summary>
public sealed record EtlFileSummary(
    string FileName, long SizeBytes, int EventCount, int UnparsedCount,
    string? FirstTimestamp, string? LastTimestamp,
    IReadOnlyList<(string Provider, int Events)> TopProviders);

/// <summary>
/// .etl 的內容讀回（缺口 #4）：本專案寫得出（EtwTraceService 檔案模式）、認得出（IsValidEtl 檔頭驗證）、
/// 這一版起也<b>讀得回來</b>——用 TraceEvent 的檔案模式把落地產物解析回事件統計。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是讀回而不是再寫一份解析器：</b>.etl 是 Windows 事件追蹤的原生格式，TraceEvent
/// 已在相依裡、且寫入路徑用的就是它——讀回用同一個引擎，schema 對得上；自己重寫解析器反而
/// 會造出第二份需要維護的真相。
/// </para>
/// <para>
/// <b>誠實界線：</b>「未解事件」＝TraceEvent 沒有對應 schema 的事件，如實計數、<b>不以 0 補</b>
/// 也不丟棄假裝沒看見；讀回失敗（檔案被佔用、非 .etl、解析中斷）如實回原因。讀回是唯讀的。
/// </para>
/// </remarks>
public static class EtlReadbackService
{
    private const string Category = "系統與軟體";
    public const string CountKey = "etl.traces.count";
    private const string Source = "TraceEvent 檔案模式讀回（唯讀）";

    /// <summary>讀回最近幾個軌跡的內容（全部讀會拖慢 reload；摘要明說只讀了前幾個）。</summary>
    public const int MaxFiles = 3;

    /// <summary>摘要文字裡列出的提供者數上限。</summary>
    private const int MaxProvidersInText = 4;

    /// <summary>讀回單一 .etl 的內容摘要。失敗回（null, 原因）——讀不到、非 .etl、解析中斷都如實帶原因。</summary>
    /// <remarks>
    /// <b>必須先過檔頭驗證：</b>把非 .etl 的垃圾位元組餵給 ETWTraceEventSource 會踩到 TraceEvent 的
    /// finalizer 缺陷——半建構的來源物件被 GC 收走時在 Dispose(Boolean) 擲 NullReferenceException、
    /// <b>炸掉整個行程</b>（實測：測試主機當機、回合中止）。所以垃圾永遠不進 TraceEvent：
    /// 先用 EtwTraceService.IsValidEtl 驗檔頭，不符就如實標「不是 .etl 檔頭」並略過。
    /// </remarks>
    public static (EtlFileSummary? Summary, string? Error) ReadFile(string path)
    {
        if (!EtwTraceService.IsValidEtl(path))
            return (null, "不是 .etl 檔頭——略過，不把非 ETW 內容餵給解析引擎");
        try
        {
            var info = new FileInfo(path);
            int total = 0, unparsed = 0;
            DateTime? first = null, last = null;
            var providers = new Dictionary<string, int>(StringComparer.Ordinal);
            using var source = new ETWTraceEventSource(path);
            source.AllEvents += e =>
            {
                total++;
                string name = e.ProviderName;
                if (string.IsNullOrEmpty(name)) name = "（無提供者名）";
                providers[name] = providers.TryGetValue(name, out var n) ? n + 1 : 1;
                first ??= e.TimeStamp;
                last = e.TimeStamp;
            };
            source.UnhandledEvents += _ => unparsed++;
            source.Process();

            var top = providers.OrderByDescending(kv => kv.Value).Take(6)
                .Select(kv => (kv.Key, kv.Value)).ToList();
            string Stamp(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "—";
            return (new EtlFileSummary(Path.GetFileName(path), info.Length, total, unparsed,
                Stamp(first), Stamp(last), top), null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// 聚合入口：列舉軌跡資料夾裡的 .etl（本服務的命名：類別_時間戳），讀回最近
    /// <see cref="MaxFiles"/> 個的內容摘要。<paramref name="folder"/> 供測試與探針指定，
    /// 生產路徑用 EtwTraceService 的預設資料夾。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string? folder = null, int maxFiles = MaxFiles)
    {
        folder ??= new EtwTraceService().Folder;
        var files = new List<(string Path, long Size)>();
        if (Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFiles(folder, "*.etl"))
                files.Add((f, new FileInfo(f).Length));
        if (files.Count == 0)
            return [Unavailable(at, FactAvailability.NotApplicable,
                $"軌跡資料夾裡還沒有 .etl（{folder}）——還沒產生過軌跡，不是錯誤，是環境狀態")];
        files.Sort((a, b) => string.CompareOrdinal(b.Path, a.Path)); // 本服務命名含時間戳，檔名降冪≈新到舊

        var facts = new List<HardwareFact>
        {
            new(CountKey, Category, "已落地的 ETW 軌跡",
                $"資料夾 {files.Count} 個 .etl（共 {files.Sum(f => f.Size) / 1024} KiB）；本版讀回最近 {Math.Min(files.Count, maxFiles)} 個的內容。讀回是唯讀的；未解事件＝TraceEvent 沒有對應 schema，如實計數不以 0 補", "",
                Source, FactTrustLevel.Measured, false, at, files.Count),
        };

        int read = 0, failed = 0;
        var content = new StringBuilder();
        foreach (var (path, size) in files.Take(maxFiles))
        {
            var (sum, err) = ReadFile(path);
            if (sum is null)
            {
                failed++;
                content.Append($"【{Path.GetFileName(path)}】讀回失敗：{err}；");
                continue;
            }
            read++;
            content.Append($"【{sum.FileName}】{sum.EventCount} 事件、未解 {sum.UnparsedCount}"
                + $"、提供者 {string.Join("、", sum.TopProviders.Take(MaxProvidersInText).Select(p => $"{p.Provider}×{p.Events}"))}"
                + $"、{sum.FirstTimestamp} – {sum.LastTimestamp}；");
        }
        facts.Add(new HardwareFact("etl.readback", Category, ".etl 內容讀回",
            read == 0
                ? $"最近 {Math.Min(files.Count, maxFiles)} 個軌跡全部讀回失敗：" + content + "如實回報，不猜內容"
                : $"讀回 {read}/{Math.Min(files.Count, maxFiles)}：" + content,
            "", Source, FactTrustLevel.Measured, false, at, null,
            read > 0 ? FactAvailability.Present : FactAvailability.ReadError,
            read > 0 ? null : $"{failed} 個軌跡讀回失敗——TraceEvent 解析不開，逐檔原因見事實內容"));
        return facts;
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason) =>
        new(CountKey, Category, "已落地的 ETW 軌跡", "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
