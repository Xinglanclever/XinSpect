using System.Collections.ObjectModel;
using System.IO;

namespace XinSpect;

/// <summary>單一邏輯磁碟區（磁碟機代號）的容量資訊。已用/可用即時更新，供甜甜圈圖繪製。</summary>
public sealed class VolumeInfo : ObservableObject
{
    public VolumeInfo(string name, string root) { Name = name; RootPath = root; }

    public string Name { get; }          // 例："C:"
    public string RootPath { get; }      // 例："C:\"
    public string Label { get; set; } = "";
    public string TypeText { get; set; } = "—";   // 檔案系統，例：NTFS

    private double _total, _free;
    public double TotalBytes { get => _total; set { if (SetProperty(ref _total, value)) RaiseCalc(); } }
    public double FreeBytes { get => _free; set { if (SetProperty(ref _free, value)) RaiseCalc(); } }

    public double UsedBytes => Math.Max(0, _total - _free);
    public double UsedFraction => _total > 0 ? UsedBytes / _total : 0;

    public string CenterPercentText => _total > 0 ? $"{UsedFraction * 100:0}%" : "—";
    public string SizeText => _total > 0 ? $"{Gb(UsedBytes):0} / {Gb(_total):0} GB" : "—";
    public string FreeText => _total > 0 ? $"{Gb(_free):0.0} GB 可用" : "—";
    public string CaptionText => string.IsNullOrWhiteSpace(Label) ? Name : $"{Name}　{Label}";
    public Severity Severity => Health.Space(UsedFraction * 100);

    private void RaiseCalc()
    {
        OnPropertyChanged(nameof(UsedBytes));
        OnPropertyChanged(nameof(UsedFraction));
        OnPropertyChanged(nameof(CenterPercentText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FreeText));
        OnPropertyChanged(nameof(Severity));
    }

    private static double Gb(double bytes) => bytes / 1_073_741_824.0;
}

/// <summary>
/// 以 System.IO.DriveInfo 讀取固定磁碟區的總量/可用空間（邏輯磁碟，數值可靠，
/// 不同於 LHM 儲存節點常缺的「已用空間」感測值）。就地更新列，避免重建集合造成甜甜圈重畫。
/// </summary>
public sealed class VolumeService : ObservableObject
{
    private readonly Dictionary<string, VolumeInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<VolumeInfo> Volumes { get; } = new();

    private double _totalBytes, _freeBytes;
    public double TotalBytes { get => _totalBytes; private set => SetProperty(ref _totalBytes, value); }
    public double FreeBytes { get => _freeBytes; private set => SetProperty(ref _freeBytes, value); }
    public double UsedFraction => _totalBytes > 0 ? (_totalBytes - _freeBytes) / _totalBytes : 0;
    public string SummaryText => _totalBytes > 0
        ? $"{Gb(_totalBytes - _freeBytes):0} / {Gb(_totalBytes):0} GB 已用（{Gb(_freeBytes):0} GB 可用）"
        : "—";

    public VolumeService()
    {
        try { Refresh(); } catch { /* best-effort */ }
    }

    public void Refresh()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return; }

        double total = 0, free = 0;
        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;

                string name = d.Name.TrimEnd('\\');   // "C:\" → "C:"
                if (!_byName.TryGetValue(name, out var vi))
                {
                    vi = new VolumeInfo(name, d.Name)
                    {
                        Label = SafeLabel(d),
                        TypeText = SafeFormat(d),
                    };
                    _byName[name] = vi;
                    Volumes.Add(vi);
                }
                vi.TotalBytes = d.TotalSize;
                vi.FreeBytes = d.TotalFreeSpace;

                total += d.TotalSize;
                free += d.TotalFreeSpace;
            }
            catch { /* 單一磁碟讀取失敗即略過 */ }
        }

        TotalBytes = total;
        FreeBytes = free;
        OnPropertyChanged(nameof(UsedFraction));
        OnPropertyChanged(nameof(SummaryText));
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel ?? ""; } catch { return ""; }
    }

    private static string SafeFormat(DriveInfo d)
    {
        try { return d.DriveFormat ?? "—"; } catch { return "—"; }
    }

    private static double Gb(double bytes) => bytes / 1_073_741_824.0;
}
