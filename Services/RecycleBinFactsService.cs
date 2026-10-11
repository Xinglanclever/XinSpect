using System.IO;

namespace XinSpect;

/// <summary>
/// 回收桶審計（Vol2 FS-007）：逐磁碟的 <c>$Recycle.Bin</c> 條目計數與大小。
/// </summary>
/// <remarks>
/// <para><b>只讀、只計數</b>：<c>$I*</c> 檔（回收中繼資料，內含<b>原始檔名與路徑</b>）刻意<b>不解析</b>，
/// 只數它有幾個、以及對應 <c>$R*</c> 的位元組數。要讀原檔名就變成「把使用者刪掉的檔名攤開來」，
/// 那是隱私面而不是鑑識面——與 FS-013 的禁項精神一致。</para>
/// <para><b>誠實界線</b>：回收桶會被清空、<c>$I</c> 與 <c>$R</c> 可能不成對（部分刪除、權限不足），
/// 所以條目數是「現在看得到的」而不是「曾經刪過的」。讀不到的磁碟如實列出原因，不當成 0 條。</para>
/// </remarks>
public static class RecycleBinFactsService
{
    private const string Category = "檔案鑑識";

    /// <summary>單一磁碟的回收桶統計。</summary>
    public readonly record struct BinStat(string Root, int Items, long Bytes, string? UnreadableReason);

    /// <summary>
    /// 收集回收桶事實。<paramref name="rootsProbe"/> 回傳要看的磁碟根（預設＝邏輯磁碟），
    /// <paramref name="counterProbe"/> 對單一根目錄計數（預設走真實檔案系統）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<string>>? rootsProbe = null,
        Func<string, BinStat>? counterProbe = null)
    {
        IReadOnlyList<string> roots;
        try { roots = (rootsProbe ?? DefaultRoots)(); }
        catch (Exception ex)
        {
            return Na(at, "列舉磁碟失敗：" + ex.Message, FactAvailability.ReadError);
        }

        var counter = counterProbe ?? (root => CountOne(root));
        var stats = new List<BinStat>();
        foreach (string root in roots)
        {
            try { stats.Add(counter(root)); }
            catch (Exception ex) { stats.Add(new BinStat(root, 0, 0, ex.GetType().Name + "：" + ex.Message)); }
        }

        var readable = stats.Where(s => s.UnreadableReason is null).ToList();
        int items = readable.Sum(s => s.Items);
        long bytes = readable.Sum(s => s.Bytes);
        string unreadable = stats.Where(s => s.UnreadableReason is not null)
            .Select(s => $"{s.Root}（{s.UnreadableReason}）") is { } u && u.Any() ? string.Join("；", u) : "";

        return
        [
            new HardwareFact("fs.bin.count", Category, "回收桶條目數",
                items.ToString(System.Globalization.CultureInfo.InvariantCulture), "個",
                "各磁碟 $Recycle.Bin 的 $I* 檔計數（不解析原檔名）", FactTrustLevel.Measured, false, at, items),
            new HardwareFact("fs.bin.size_mb", Category, "回收桶佔用",
                (bytes / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture), "MiB",
                "各磁碟 $Recycle.Bin 下 $R* 檔的總位元組數", FactTrustLevel.Measured, false, at, bytes),
            new HardwareFact("fs.bin.roots", Category, "回收桶磁碟",
                $"{readable.Count}/{stats.Count} 個磁碟可讀："
                + (readable.Count > 0 ? string.Join("、", readable.Select(s => $"{s.Root}{s.Items} 條")) : "（無）")
                + (unreadable.Length > 0 ? "；讀不到：" + unreadable : ""),
                "", "逐邏輯磁碟 $Recycle.Bin", FactTrustLevel.Measured, false, at),
        ];
    }

    private static IReadOnlyList<string> DefaultRoots() =>
        Environment.GetLogicalDrives().Select(d => d.TrimEnd('\\')).ToList();

    /// <summary>計數一個磁碟的回收桶：只認 <c>$I</c>／<c>$R</c> 開頭的檔（各 SID 子目錄內），不讀內容。</summary>
    private static BinStat CountOne(string root)
    {
        string bin = Path.Combine(root + "\\", "$Recycle.Bin");
        if (!Directory.Exists(bin)) return new BinStat(root, 0, 0, null);

        int items = 0;
        long bytes = 0;
        foreach (string sidDir in Directory.EnumerateDirectories(bin))
        {
            foreach (string file in Directory.EnumerateFiles(sidDir))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("$I", StringComparison.Ordinal)) items++;
                else if (name.StartsWith("$R", StringComparison.Ordinal))
                {
                    try { bytes += new FileInfo(file).Length; } catch { /* 單檔讀不到不影響其他條目 */ }
                }
            }
        }
        return new BinStat(root, items, bytes, null);
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason, FactAvailability availability)
        =>
        [
            new("fs.bin.count", Category, "回收桶條目數", "", "", "各磁碟 $Recycle.Bin", FactTrustLevel.Unknown, false, at, null, availability, reason),
            new("fs.bin.size_mb", Category, "回收桶佔用", "", "", "各磁碟 $Recycle.Bin", FactTrustLevel.Unknown, false, at, null, availability, reason),
            new("fs.bin.roots", Category, "回收桶磁碟", "", "", "各磁碟 $Recycle.Bin", FactTrustLevel.Unknown, false, at, null, availability, reason),
        ];
}
