using System.IO;

namespace XinSpect;

/// <summary>一條用量項目（目錄或檔案）。</summary>
public sealed record DiskUsageEntry(string Path, long Bytes, bool IsFile, int Files);

/// <summary>
/// 檔案系統探測（可注入）：目錄清單與檔案清單各一支。測試用假件，正式路徑走 <c>System.IO</c>。
/// </summary>
public sealed record DiskProbe(
    Func<string, IReadOnlyList<string>> Directories,
    Func<string, IReadOnlyList<(string Name, long Size)>> Files)
{
    /// <summary>真實的檔案系統探測：讀不到的目錄拋出，由呼叫端計入「略過」。</summary>
    public static DiskProbe Real { get; } = new(
        dir => Directory.GetDirectories(dir),
        dir => new DirectoryInfo(dir).EnumerateFiles()
            .Select(f => (f.Name, f.Length)).ToList());
}

/// <summary>
/// 「磁碟為什麼滿」唯讀排行（Vol 2 批次 D／SG-002）。
/// <para>
/// <b>做什麼：</b>在指定的根之下（深度上限內）加總每個子目錄的檔案大小，排出最大者；
/// 順便列出根目錄下的大檔。回答「空間被什麼吃掉」的第一個問題——<b>不是</b>回答「該刪什麼」。
/// </para>
/// <para>
/// <b>界線（三條都寫進值裡）：</b>①<b>只讀排行，不刪任何東西</b>——本模組沒有任何刪除／移動路徑，
/// 也不提供「一鍵清理」；②深度上限是為了不讓一次掃描拖垮機器，因此數字是<b>上限內</b>的加總，
/// 不是磁碟真實用量；③無權限的目錄如實計入「略過」——<b>略過多＝數字偏低＝可能低估</b>，
/// 不假裝掃得很完整。
/// </para>
/// </summary>
public static class DiskFullFactsService
{
    public const string Category = "情境包";
    public const string SummaryKey = "sg.diskfull";

    private const string Source = "檔案系統唯讀遍歷";

    /// <summary>掃描參數：根、深度上限、排行長度。</summary>
    public sealed record ScanRequest(
        IReadOnlyList<string> Roots, int MaxDepth = 2, int TopCount = 10, long BigFileBytes = 200L * 1024 * 1024);

    /// <summary>
    /// 自動收集用的根：暫存與傾印目錄（<b>不含使用者設定檔</b>）。
    /// <para>
    /// 使用者設定檔往往是真正吃掉空間的那一個，但整份遞迴列舉在使用者目錄下要數十秒——
    /// 那種成本不該掛在每次啟動的收集路徑上。要做整份掃描就把 profile 加進 <see cref="ScanRequest.Roots"/>，
    /// 由明示觸發的入口跑（值裡會如實寫「本次掃了哪些根」）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();
        if (Path.GetTempPath() is { Length: > 0 } temp) roots.Add(temp);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0)
        {
            roots.Add(Path.Combine(windows, "Temp"));
            roots.Add(Path.Combine(windows, "Minidump"));
            roots.Add(Path.Combine(windows, "LiveKernelReports"));
        }
        return roots;
    }

    /// <summary>自動收集用的預設請求（見 <see cref="DefaultRoots"/> 為什麼不含使用者設定檔）。</summary>
    public static ScanRequest DefaultRequest(int maxDepth = 2, int topCount = 8) =>
        new(DefaultRoots(), maxDepth, topCount);

    /// <summary>一次掃描結果。</summary>
    public sealed record ScanResult(
        IReadOnlyList<DiskUsageEntry> Top, long TotalBytes, long FileCount, int SkippedDirectories, int RootsScanned);

    /// <summary>唯讀遍歷：深度上限內的目錄用量排行＋根目錄下的大檔。任何讀不到的路徑計入略過。</summary>
    public static ScanResult Scan(ScanRequest request, DiskProbe? probe = null)
    {
        var fs = probe ?? DiskProbe.Real;
        var dirs = new List<DiskUsageEntry>();
        var bigFiles = new List<DiskUsageEntry>();
        long total = 0, fileCount = 0;
        int roots = 0;
        // 略過以「路徑」計數（同一個目錄的檔案與子目錄各失敗一次也只算一個路徑）——
        // 有些目錄列出檔案會檔，但列出子目錄不會，所以分別嘗試、如實計入。
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in request.Roots)
        {
            if (string.IsNullOrWhiteSpace(root)) { skipped.Add(root); continue; }
            IReadOnlyList<string> children = SubDirs(root);
            if (!DirsReadable(root)) continue;   // 根自己讀不到：計入略過，不當成 0 用量
            roots++;

            // 每個根只走一遍：根自己的檔案 ＋ 深度 1 各子目錄的（深度上限內）用量。
            long rootFiles = 0;
            foreach (var (name, size) in Files(root))
            {
                rootFiles += size;
                if (size >= request.BigFileBytes)
                    bigFiles.Add(new DiskUsageEntry(Path.Combine(root, name), size, true, 1));
            }
            total += rootFiles;
            foreach (string child in children)
            {
                long size = SizeOf(child, 1);
                total += size;
                dirs.Add(new DiskUsageEntry(child, size, false, 0));
            }
        }

        // 深度上限內的目錄用量；讀不到的子目錄計入略過（略過多＝數字偏低，值裡會說）
        long SizeOf(string dir, int depth)
        {
            long sum = 0;
            foreach (var (_, size) in Files(dir)) sum += size;
            if (depth >= request.MaxDepth) return sum;
            foreach (string sub in SubDirs(dir)) sum += SizeOf(sub, depth + 1);
            return sum;
        }

        IReadOnlyList<(string Name, long Size)> Files(string dir)
        {
            try
            {
                var list = fs.Files(dir);
                fileCount += list.Count;
                return list;
            }
            catch { skipped.Add(dir); return []; }
        }

        IReadOnlyList<string> SubDirs(string dir)
        {
            try { return fs.Directories(dir); }
            catch { skipped.Add(dir); return []; }
        }

        bool DirsReadable(string dir)
        {
            try { fs.Directories(dir); return true; }
            catch { skipped.Add(dir); return false; }
        }

        var top = dirs.Concat(bigFiles)
            .Where(e => e.Bytes > 0)
            .OrderByDescending(e => e.Bytes)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Take(request.TopCount)
            .ToList();
        return new ScanResult(top, total, fileCount, skipped.Count, roots);
    }

    /// <summary>產生摘要與逐項排行事實。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, ScanRequest request, DiskProbe? probe = null)
    {
        ScanResult result;
        try { result = Scan(request, probe); }
        catch (Exception ex)
        {
            return
            [
                Unavailable(at, "掃描失敗（" + ex.GetType().Name + "：" + ex.Message + "）——讀不到就是不猜"),
            ];
        }

        if (result.RootsScanned == 0)
            return [Unavailable(at, "所有候選根都讀不到（權限或不存在）——略過 " + result.SkippedDirectories + " 個路徑")];

        // 排行的逐項明細串在同一個值裡（不另立鍵）：這一版刻意不新增動態家族。
        string ranking = result.Top.Count == 0
            ? "排行是空的（掃描範圍內沒有可加總的內容）"
            : string.Join("；", result.Top.Select((e, i) => $"#{i + 1} {Format(e.Bytes)} {e.Path}"));

        return
        [
            new(SummaryKey, Category, "磁碟為什麼滿",
                $"掃了 {result.RootsScanned} 個根（深度上限 {request.MaxDepth} 層）：{string.Join("、", request.Roots)}・" +
                $"檔案 {result.FileCount} 個・加總 {Format(result.TotalBytes)}・略過 {result.SkippedDirectories} 個讀不到的路徑。" +
                "只讀排行、不刪任何東西；數字是深度上限內的加總，略過多就偏低（可能低估）。" +
                "使用者設定檔預設不掃（整份遞迴要數十秒，屬明示觸發的操作）。" +
                (result.SkippedDirectories > 0 ? "本次有略過路徑，數字請當下限看。" : "") +
                $"排行：{ranking}",
                "bytes", Source, FactTrustLevel.Measured, false, at, result.TotalBytes),
        ];
    }

    /// <summary>人類可讀的容量字串（GB／MB／KB）。</summary>
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / 1024.0 / 1024 / 1024 / 1024:0.00} TB",
        >= 1L << 30 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1L << 20 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} bytes",
    };

    private static HardwareFact Unavailable(DateTimeOffset at, string reason) =>
        new(SummaryKey, Category, "磁碟為什麼滿", "", "", Source, FactTrustLevel.Unknown, false, at, null,
            FactAvailability.ReadError, reason);
}
