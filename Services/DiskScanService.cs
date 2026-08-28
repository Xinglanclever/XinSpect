using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;

namespace XinSpect;

/// <summary>
/// 大檔／重複檔掃描：遞迴列舉指定資料夾，找出最占空間的大檔，並（可選）以檔案大小分組後
/// 逐位元組雜湊（SHA-256）比對，真實判定內容完全相同的重複檔，統計可回收空間。
/// 掃描於背景執行緒進行、可取消，存取被拒的目錄自動略過。純本機、無第三方相依。
/// </summary>
public sealed record ScanFile(string Name, string FullPath, string Dir, long Size, string SizeText);

public sealed class DupGroup
{
    public long Size { get; init; }
    public string SizeText { get; init; } = "";
    public int Count { get; init; }
    public long Wasted { get; init; }               // 冗餘＝Size×(Count-1)
    public string WastedText { get; init; } = "";
    public List<ScanFile> Files { get; init; } = new();
    public string Header => $"{Count} 份相同 ・ 每份 {SizeText} ・ 可回收 {WastedText}";
}

public sealed record ScanResult(
    List<ScanFile> LargeFiles, List<DupGroup> Duplicates,
    long TotalSize, int TotalCount, long WastedTotal);

public sealed class DiskScanService : INotifyPropertyChanged
{
    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private const int TopCount = 100;               // 保留的最大檔數量

    public async Task<ScanResult> ScanAsync(string root, bool findDup,
        IProgress<int> progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var all = new List<ScanFile>();
            long total = 0;
            int count = 0;

            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,          // 存取被拒的目錄／檔案自動略過
                AttributesToSkip = FileAttributes.ReparsePoint,   // 略過連結點避免迴圈
            };

            foreach (var path in Directory.EnumerateFiles(root, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                long size;
                try { size = new FileInfo(path).Length; }
                catch { continue; }                 // 讀不到大小就略過
                total += size;
                count++;
                all.Add(new ScanFile(
                    Path.GetFileName(path), path, Path.GetDirectoryName(path) ?? "",
                    size, Human(size)));
                if ((count & 0x3FF) == 0) progress.Report(count);   // 每 1024 檔回報一次
            }
            progress.Report(count);

            var large = all.OrderByDescending(f => f.Size).Take(TopCount).ToList();

            var dups = new List<DupGroup>();
            long wastedTotal = 0;
            if (findDup)
                dups = FindDuplicates(all, progress, ct, out wastedTotal);

            return new ScanResult(large, dups, total, count, wastedTotal);
        }, ct);
    }

    private List<DupGroup> FindDuplicates(List<ScanFile> all, IProgress<int> progress,
        CancellationToken ct, out long wastedTotal)
    {
        wastedTotal = 0;
        var groups = new List<DupGroup>();

        // 先以大小分組，只有同大小且不只一份的才需要雜湊（大幅剪枝）
        var bySize = all.Where(f => f.Size > 0)
                        .GroupBy(f => f.Size)
                        .Where(g => g.Count() > 1);

        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();
            var byHash = new Dictionary<string, List<ScanFile>>();
            foreach (var f in sizeGroup)
            {
                ct.ThrowIfCancellationRequested();
                var hash = TryHash(f.FullPath);
                if (hash == null) continue;         // 檔案被鎖或讀取失敗就略過
                if (!byHash.TryGetValue(hash, out var list))
                    byHash[hash] = list = new List<ScanFile>();
                list.Add(f);
            }
            foreach (var kv in byHash)
            {
                if (kv.Value.Count < 2) continue;
                long size = kv.Value[0].Size;
                long wasted = size * (kv.Value.Count - 1);
                wastedTotal += wasted;
                groups.Add(new DupGroup
                {
                    Size = size,
                    SizeText = Human(size),
                    Count = kv.Value.Count,
                    Wasted = wasted,
                    WastedText = Human(wasted),
                    Files = kv.Value.OrderBy(x => x.FullPath).ToList(),
                });
            }
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch { return null; }
    }

    public static string Human(long bytes)
    {
        double b = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{b:0.##} {u[i]}";
    }

    public void SetStatus(string s) => Status = s;
}
