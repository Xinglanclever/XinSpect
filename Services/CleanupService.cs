using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 垃圾清理：掃描常見暫存／快取位置的占用大小，勾選後刪除。純本機、無第三方相依。
/// 僅清理公認安全可再生的位置（各種 Temp、縮圖快取、Prefetch、錯誤報告、資源回收筒）；
/// 刪除時逐檔容錯，遇使用中或無權限的檔案自動略過，不影響其他項目。
/// </summary>
public sealed class CleanCategory : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Paths { get; init; } = Array.Empty<string>();
    public bool IsRecycleBin { get; init; }

    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; On(nameof(Selected)); } }

    private long _size;
    public long Size { get => _size; set { _size = value; On(nameof(Size)); On(nameof(SizeText)); } }

    public string SizeText => Size < 0 ? "掃描中…"
        : Size >= 1L << 30 ? $"{Size / 1024.0 / 1024 / 1024:0.00} GB"
        : Size >= 1L << 20 ? $"{Size / 1024.0 / 1024:0.0} MB"
        : Size >= 1L << 10 ? $"{Size / 1024.0:0} KB" : $"{Size} B";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class CleanupService
{
    public ObservableCollection<CleanCategory> Categories { get; } = new();

    public CleanupService() => Build();

    private void Build()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Categories.Add(new CleanCategory
        {
            Name = "使用者暫存檔", Description = "%TEMP% ・ 應用程式與安裝程式產生的臨時檔",
            Paths = new[] { Path.GetTempPath() }
        });
        Categories.Add(new CleanCategory
        {
            Name = "Windows 暫存檔", Description = @"C:\Windows\Temp ・ 系統層級臨時檔",
            Paths = new[] { Path.Combine(win, "Temp") }
        });
        Categories.Add(new CleanCategory
        {
            Name = "縮圖與圖示快取", Description = "檔案總管縮圖／圖示快取（會自動重建）",
            Paths = new[] { Path.Combine(local, @"Microsoft\Windows\Explorer") }
        });
        Categories.Add(new CleanCategory
        {
            Name = "Prefetch 預先擷取", Description = @"C:\Windows\Prefetch ・ 啟動預取資料（會自動重建）",
            Paths = new[] { Path.Combine(win, "Prefetch") }
        });
        Categories.Add(new CleanCategory
        {
            Name = "應用程式錯誤報告", Description = "當機傾印與 Windows 錯誤報告（WER）",
            Paths = new[]
            {
                Path.Combine(local, "CrashDumps"),
                Path.Combine(progData, @"Microsoft\Windows\WER"),
            }
        });
        Categories.Add(new CleanCategory
        {
            Name = "資源回收筒", Description = "清空所有磁碟的資源回收筒", IsRecycleBin = true
        });
    }

    // 背景計算各分類占用大小。
    public void Scan()
    {
        foreach (var c in Categories)
        {
            c.Size = -1;
            long total = c.IsRecycleBin ? RecycleBinSize() : c.Paths.Sum(DirSize);
            c.Size = total;
        }
    }

    // 刪除勾選分類的內容；回傳 (釋放位元組, 逐項報告)。
    public (long freed, string report) Clean()
    {
        long freed = 0;
        var lines = new List<string>();
        foreach (var c in Categories.Where(c => c.Selected))
        {
            long before = c.Size < 0 ? 0 : c.Size;
            if (c.IsRecycleBin) EmptyRecycleBin();
            else foreach (var p in c.Paths) PurgeDir(p);

            long after = c.IsRecycleBin ? RecycleBinSize() : c.Paths.Sum(DirSize);
            long got = Math.Max(0, before - after);
            freed += got;
            c.Size = after;
            lines.Add($"・{c.Name}：釋放約 {Human(got)}");
        }
        if (lines.Count == 0) return (0, "未勾選任何項目。");
        return (freed, $"清理完成，共釋放約 {Human(freed)}：\n" + string.Join("\n", lines));
    }

    private static string Human(long b) =>
        b >= 1L << 30 ? $"{b / 1024.0 / 1024 / 1024:0.00} GB"
        : b >= 1L << 20 ? $"{b / 1024.0 / 1024:0.0} MB"
        : b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";

    private static long DirSize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { /* 略過無權限／使用中 */ }
            }
            return sum;
        }
        catch { return 0; }
    }

    private static void PurgeDir(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path))
                try { File.Delete(f); } catch { /* 使用中／無權限：略過 */ }
            foreach (var d in Directory.EnumerateDirectories(path))
                try { Directory.Delete(d, recursive: true); } catch { /* 略過 */ }
        }
        catch { /* 略過整個目錄的錯誤 */ }
    }

    // ── 資源回收筒（shell32）────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint flags);

    private static long RecycleBinSize()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch { return 0; }
    }

    private static void EmptyRecycleBin()
    {
        try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); } catch { /* 略過 */ }
    }
}
