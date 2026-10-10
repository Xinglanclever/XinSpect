using System.IO;

namespace XinSpect;

/// <summary>Prefetch 目錄的一條列舉結果：檔名、完整路徑、最後寫入時間（≈最後執行）、檔頭位元組。</summary>
public sealed record PrefetchListing(string FileName, string FullPath, DateTimeOffset LastWriteUtc, byte[]? Head);

/// <summary>
/// Prefetch 檔名層事實服務（Vol 2 批次 B／FS-003）。
/// <para>
/// 列舉 <c>C:\Windows\Prefetch\*.pf</c>，讀每檔前 80 位元組交
/// <see cref="PrefetchHeaderDecoder"/> 判格式；MAM 壓縮容器<b>不解壓</b>（沒列引用檔案清單）。
/// </para>
/// <para>
/// <b>界線</b>：檔名存在＝曾被預取，不等於執行成功；最後執行時間取自檔案寫入時間（系統於關機或定期寫入，
/// 近似不是精確時刻）；讀不到的檔案（權限／損毀）計入略過數如實列；Prefetch 服務停用時目錄可能是空的——
/// 空目錄＝「沒有預取資料」，不是「沒有執行過程式」。目錄需管理員權限，讀不到整個目錄＝InsufficientPrivilege。
/// </para>
/// </summary>
public static class PrefetchFactsService
{
    public const string Category = "檔案鑑識";
    public const string CountKey = "fs.pf.count";
    public const string LatestKey = "fs.pf.latest";
    public const string SampleKey = "fs.pf.sample";
    public const string FormatsKey = "fs.pf.formats";

    /// <summary>預取目錄（本機固定；跨機器比較要另開快照）。</summary>
    public const string PrefetchDir = @"C:\Windows\Prefetch";

    /// <summary>樣本最多列幾條。</summary>
    public const int ListedMax = 10;

    private const string Source = "Prefetch 目錄（唯讀）＋金標檔頭解碼器";

    /// <summary>本機收集。listing 注入式：測試餵假列舉，不碰真目錄。</summary>
    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at,
        Func<IReadOnlyList<PrefetchListing>?>? listing = null) =>
        Collect(at, listing ?? ListRealDirectory);

    /// <summary>列真目錄：目錄不存在回 null（＝沒有預取資料）；枚舉失敗丟例外（＝權限）。</summary>
    public static IReadOnlyList<PrefetchListing>? ListRealDirectory()
    {
        var dir = new DirectoryInfo(PrefetchDir);
        if (!dir.Exists) return null;
        var result = new List<PrefetchListing>();
        foreach (FileInfo f in dir.EnumerateFiles("*.pf"))
        {
            byte[]? head = null;
            try
            {
                using var fs = f.OpenRead();
                var buf = new byte[PrefetchHeaderDecoder.UncompressedHeaderBytes];
                int read = fs.Read(buf, 0, buf.Length);
                head = read == buf.Length ? buf : buf[..read];
            }
            catch (Exception)
            {
                head = null; // 讀不到的檔案由 Collect 計入略過數
            }
            result.Add(new PrefetchListing(f.Name, f.FullName, f.LastWriteTimeUtc, head));
        }
        return result;
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<IReadOnlyList<PrefetchListing>?> listing)
    {
        IReadOnlyList<PrefetchListing>? items;
        string? error = null;
        try
        {
            items = listing();
        }
        catch (Exception ex)
        {
            items = null;
            error = ex.GetType().Name;
        }

        if (error is not null)
            return
            [
                new HardwareFact(CountKey, Category, "Prefetch 預取記錄", "", "檔", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                    "目錄列舉失敗（" + error + "）——預取目錄需要管理員權限；讀不到不等於沒有資料"),
            ];

        if (items is null)
            return
            [
                new HardwareFact(CountKey, Category, "Prefetch 預取記錄", "", "檔", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "預取目錄不存在（" + PrefetchDir + "）——沒有預取資料，不是「沒有執行過程式」"),
            ];

        if (error is not null)
            return
            [
                new HardwareFact(CountKey, Category, "Prefetch 預取記錄", "", "檔", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                    "目錄列舉失敗（" + error + "）——預取目錄需要管理員權限；讀不到不等於沒有資料"),
            ];

        if (items.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "Prefetch 預取記錄", "0 檔", "檔", Source,
                    FactTrustLevel.Derived, false, at, 0.0, FactAvailability.NotApplicable,
                    "目錄在但沒有 .pf 檔——Prefetch 服務可能停用或剛清過；不是「沒有執行過程式」"),
            ];

        var decoded = items.Select(it =>
        {
            var h = it.Head is null ? null : PrefetchHeaderDecoder.Parse(it.Head);
            return (Item: it, Head: h);
        }).ToList();

        int unreadable = decoded.Count(d => d.Head is null);
        var recognizable = decoded.Where(d => d.Head is not null && d.Head.Kind != "Unrecognized").ToList();
        int unrecognized = decoded.Count(d => d.Head is not null && d.Head.Kind == "Unrecognized");

        var byTime = recognizable.OrderByDescending(d => d.Item.LastWriteUtc).ToList();
        string formatTally = string.Join("｜",
            recognizable.GroupBy(d => d.Head!.VersionLabel ?? d.Head.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key + " " + g.Count()))
            + (unrecognized > 0 ? "｜無法辨識 " + unrecognized : "")
            + (unreadable > 0 ? "｜檔頭讀不到 " + unreadable + "（略過如實計數）" : "");

        string latestText = byTime.Count == 0
            ? "沒有可辨識的預取檔"
            : byTime[0].Item.LastWriteUtc.ToString("yyyy-MM-dd HH:mm") + " UTC｜" + byTime[0].Item.FileName;
        string sample = string.Join("；", byTime.Take(ListedMax).Select(d =>
                d.Item.LastWriteUtc.ToString("yyyy-MM-dd HH:mm") + "｜" + d.Item.FileName
                + "｜" + (d.Head!.ExecutableName is { } n ? n : d.Head.Kind)))
            + (byTime.Count > ListedMax ? "；等 " + (byTime.Count - ListedMax) + " 檔" : "");
        string boundary = "檔名存在＝曾被預取，不等於執行成功；時間是檔案寫入時間（系統於關機/定期寫入，近似不是精確時刻）；"
            + "MAM 壓縮內容未解壓（沒列引用檔案與卷冊）；空目錄＝沒有預取資料不是沒有執行過程式";

        return
        [
            new HardwareFact(CountKey, Category, "Prefetch 預取記錄檔數",
                items.Count + " 檔" + (unreadable > 0 ? "（" + unreadable + " 檔頭讀不到，略過如實計數）" : "") + "・" + boundary,
                "檔", Source, FactTrustLevel.Derived, false, at, items.Count),
            new HardwareFact(FormatsKey, Category, "Prefetch 版本分布", formatTally, "", Source,
                FactTrustLevel.Derived, false, at),
            new HardwareFact(LatestKey, Category, "Prefetch 最新一筆", latestText, "", Source,
                FactTrustLevel.Derived, false, at),
            new HardwareFact(SampleKey, Category, "Prefetch 樣本（最新 " + ListedMax + "）", sample + "・" + boundary, "", Source,
                FactTrustLevel.Derived, false, at),
        ];
    }
}
