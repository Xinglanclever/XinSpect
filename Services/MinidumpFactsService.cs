using System.IO;

namespace XinSpect;

/// <summary>傾印目錄的一條列舉結果：檔名、最後寫入時間、檔頭位元組。</summary>
public sealed record DumpListing(string FileName, DateTimeOffset LastWriteUtc, byte[]? Head);

/// <summary>
/// Minidump 藍屏歷史事實服務（P0-04／RC-001）。
/// <para>
/// 列舉 <c>C:\Windows\Minidump\*.dmp</c>，讀每檔前 0x60 位元組交
/// <see cref="KernelDumpDecoder"/> 解出 bugcheck 代碼與四參數，代碼對知識表出符號名。
/// </para>
/// <para>
/// <b>界線</b>：<b>解碼不是診斷</b>——不宣稱根因；目錄是好消息時如實列（沒有傾印＝這台機器沒有藍屏紀錄）；
/// 檔案損毀/讀不到計入略過如實列；代碼未收錄標「未收錄」＋原碼；日期取自檔案名（MMDDYY-NNNNN-NN 慣例）與
/// 檔案寫入時間，兩者並列；參數可能是核心位址——樣本標敏感，匯出預設遮蔽。
/// </para>
/// </summary>
public static class MinidumpFactsService
{
    public const string Category = "韌體安全";
    public const string CountKey = "rc.dump.count";
    public const string LatestKey = "rc.dump.latest";
    public const string SampleKey = "rc.dump.sample";

    /// <summary>傾印目錄。</summary>
    public const string DumpDir = @"C:\Windows\Minidump";

    private const string Source = "Minidump 目錄（唯讀）＋金標解碼器";

    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at,
        Func<IReadOnlyList<DumpListing>?>? listing = null) =>
        Collect(at, listing ?? ListRealDirectory);

    /// <summary>列真目錄：不存在回 null（＝沒有藍屏紀錄，好消息如實）；枚舉失敗丟例外（＝權限）。</summary>
    public static IReadOnlyList<DumpListing>? ListRealDirectory()
    {
        var dir = new DirectoryInfo(DumpDir);
        if (!dir.Exists) return null;
        var result = new List<DumpListing>();
        foreach (FileInfo f in dir.EnumerateFiles("*.dmp"))
        {
            byte[]? head = null;
            try
            {
                using var fs = f.OpenRead();
                var buf = new byte[KernelDumpDecoder.HeaderBytes];
                int read = fs.Read(buf, 0, buf.Length);
                head = read == buf.Length ? buf : buf[..read];
            }
            catch (Exception)
            {
                head = null;
            }
            result.Add(new DumpListing(f.Name, f.LastWriteTimeUtc, head));
        }
        return result;
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<IReadOnlyList<DumpListing>?> listing)
    {
        IReadOnlyList<DumpListing>? items;
        string? error = null;
        try { items = listing(); }
        catch (Exception ex) { items = null; error = ex.GetType().Name; }

        if (error is not null)
            return
            [
                new HardwareFact(CountKey, Category, "藍屏傾印歷史", "", "份", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                    "傾印目錄列舉失敗（" + error + "）——需要管理員權限；讀不到不等於沒有藍屏"),
            ];

        if (items is null)
            return
            [
                new HardwareFact(CountKey, Category, "藍屏傾印歷史", "沒有傾印目錄", "份", Source,
                    FactTrustLevel.Derived, false, at, 0.0, FactAvailability.NotApplicable,
                    "目錄不存在（" + DumpDir + "）——是好消息：這台機器沒有核心傾印紀錄（或被清過）"),
            ];

        if (items.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "藍屏傾印歷史", "0 份", "份", Source,
                    FactTrustLevel.Derived, false, at, 0.0, FactAvailability.NotApplicable,
                    "目錄在、沒有 .dmp——是好消息（沒有藍屏紀錄或已被清理）；兩者從這裡分不清，不猜"),
            ];

        var decoded = items.Select(it => (Item: it, Head: it.Head is null ? null : KernelDumpDecoder.Parse(it.Head))).ToList();
        int unreadable = decoded.Count(d => d.Head is null);
        var byTime = decoded.Where(d => d.Head is not null).OrderByDescending(d => d.Item.LastWriteUtc).ToList();

        string Describe((DumpListing Item, KernelDumpHeader? Head) d)
        {
            string date = FileNameDate(d.Item.FileName) is { } dt ? dt.ToString("yyyy-MM-dd") : "日期未辨識";
            string code = d.Head is null ? "檔頭讀不到" :
                "0x" + d.Head.BugCheckCode.ToString("X") + " " + (KernelDumpDecoder.CodeName(d.Head.BugCheckCode) ?? "未收錄（不猜名稱）");
            string build = d.Head is null ? "" : $"（Win {d.Head.MajorVersion}.{d.Head.BuildNumber}）";
            return d.Item.FileName + "｜" + date + "｜" + code + build;
        }

        string latestText = byTime.Count == 0 ? "沒有可解析的傾印" : Describe(byTime[0]);
        string sample = string.Join("；", byTime.Take(10).Select(Describe))
            + (byTime.Count > 10 ? "；等 " + (byTime.Count - 10) + " 份" : "");
        string boundary = "解碼不是診斷——只回答「藍過幾次、代碼是什麼」，不宣稱根因；日期取自檔名慣例（MMDDYY）與檔案寫入時間；"
            + "參數可能是核心位址（匯出預設遮蔽）；代碼未收錄＝不猜名稱"
            + (unreadable > 0 ? "；" + unreadable + " 份檔頭讀不到（略過如實計數）" : "");

        return
        [
            new HardwareFact(CountKey, Category, "藍屏傾印歷史",
                items.Count + " 份" + (unreadable > 0 ? "（" + unreadable + " 份檔頭讀不到）" : "") + "・" + boundary,
                "份", Source, FactTrustLevel.Derived, false, at, items.Count),
            new HardwareFact(LatestKey, Category, "藍屏傾印最新一筆", latestText, "", Source,
                FactTrustLevel.Derived, false, at),
            new HardwareFact(SampleKey, Category, "藍屏傾印逐份", sample, "", Source,
                FactTrustLevel.Derived, true, at),
        ];
    }

    /// <summary>檔名慣例日期（MMDDYY-…）→ 日期；辨識不出回 null。與檔案寫入時間並列互相印證。</summary>
    public static DateTimeOffset? FileNameDate(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dash = stem.IndexOf('-', StringComparison.Ordinal);
        if (dash != 6) return null;
        string d = stem[..6];
        if (!int.TryParse(d, out int n)) return null;
        int mm = n / 10000, dd = n / 100 % 100, yy = n % 100;
        try { return new DateTimeOffset(2000 + yy, mm, dd, 0, 0, 0, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
