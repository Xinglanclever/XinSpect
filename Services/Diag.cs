using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>被吞掉的非致命例外的一筆紀錄。</summary>
public sealed class DiagEntry
{
    public DiagEntry(DateTime time, string site, string type, string message, string consequence)
    { Time = time; Site = site; Type = type; Message = message; Consequence = consequence; }

    public DateTime Time { get; }
    /// <summary>發生位置的短標籤（如「感測讀取」、「WMI 查詢」）。</summary>
    public string Site { get; }
    public string Type { get; }
    public string Message { get; }
    /// <summary>對使用者的實際後果（如「此欄顯示 —」）；空字串代表未標註。</summary>
    public string Consequence { get; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public string OneLine => $"{Site}：{Type}　{Message}" + (Consequence.Length > 0 ? $"　→ {Consequence}" : "");
}

/// <summary>
/// 「吞掉但不隱瞞」——非致命例外的診斷落點。
/// </summary>
/// <remarks>
/// 一個硬體診斷工具最不該做的事，就是把「量不到」和「程式壞了」混成同一個空欄位。
/// WMI 查詢失敗、MSR 被拒、感測器不存在，這些都必須繼續跑（不能讓整頁掛掉），
/// 但也必須留下痕跡：否則使用者回報「這裡沒有數字」時，沒有任何資訊能判斷是
/// 硬體沒有這個項目、驅動沒開放，還是程式本身有缺陷。
///
/// 與 <see cref="CrashLog"/> 的分工：CrashLog 記的是「已經炸了」的未處理例外，
/// Diag 記的是「接住了、功能降級、程式繼續」的例外。兩者寫在同一個資料夾，
/// 使用者回報問題時一併附上即可。
///
/// 本類別自身絕不拋例外：寫檔失敗就只留記憶體那一份。
/// </remarks>
public static class Diag
{
    /// <summary>記憶體中保留的最近筆數上限（供畫面即時檢視）。</summary>
    public const int MaxEntries = 200;

    /// <summary>紀錄檔大小上限；超過時只留後半。</summary>
    public const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static readonly List<DiagEntry> Ring = new();

    /// <summary>累計筆數（不因環形緩衝丟棄而減少）。</summary>
    public static int Count { get; private set; }

    /// <summary>是否把每一筆也寫進 diag.log（單元測試可關閉以免碰檔案系統）。</summary>
    public static bool FileSinkEnabled { get; set; } = true;

    /// <summary>與 <see cref="CrashLog"/> 同一個資料夾，回報問題時一起附上。</summary>
    public static string FilePath => Path.Combine(CrashLog.Folder, "diag.log");

    /// <summary>最近的紀錄（新的在後）。回傳快照，呼叫端可安全列舉。</summary>
    public static IReadOnlyList<DiagEntry> Entries
    {
        get { lock (Gate) return Ring.ToArray(); }
    }

    /// <summary>清空記憶體中的紀錄（不刪除檔案）。</summary>
    public static void Reset()
    {
        lock (Gate) { Ring.Clear(); Count = 0; }
    }

    /// <summary>單行文字格式。與寫檔分離，才能在不碰檔案系統的前提下驗證格式。</summary>
    public static string Format(DiagEntry e)
        => $"{e.Time:yyyy-MM-dd HH:mm:ss}　{e.Site}　{e.Type}：{e.Message}"
         + (e.Consequence.Length > 0 ? $"　後果：{e.Consequence}" : "");

    /// <summary>
    /// 記下一筆「已接住、功能降級」的例外。
    /// </summary>
    /// <param name="site">發生位置的短標籤，會出現在使用者看到的清單裡。</param>
    /// <param name="ex">例外本體；null 時仍留一筆（表示只知道失敗了）。</param>
    /// <param name="consequence">對使用者的實際後果，例如「此欄顯示 —」。</param>
    public static void Swallow(string site, Exception? ex, string? consequence = null)
    {
        var entry = new DiagEntry(
            DateTime.Now,
            string.IsNullOrWhiteSpace(site) ? "未標示位置" : site,
            ex?.GetType().Name ?? "（無例外物件）",
            ex?.Message ?? "未提供訊息",
            consequence ?? "");

        lock (Gate)
        {
            Count++;
            Ring.Add(entry);
            if (Ring.Count > MaxEntries) Ring.RemoveRange(0, Ring.Count - MaxEntries);
        }

        if (!FileSinkEnabled) return;
        try
        {
            Directory.CreateDirectory(CrashLog.Folder);
            Trim();
            File.AppendAllText(FilePath, Format(entry) + Environment.NewLine, new UTF8Encoding(true));
        }
        catch { /* 診斷紀錄本身不得成為新的失敗來源；記憶體那一份仍在 */ }
    }

    private static void Trim()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= MaxBytes) return;
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2), new UTF8Encoding(true));
        }
        catch { }
    }
}
