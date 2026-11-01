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

    /// <summary>下拉項目的自動化名稱取自 ToString（DisplayMemberPath 只影響畫面），沒有這行會唸成型別名稱。</summary>
    public override string ToString() => CaptionText;

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

    /// <summary>一輪逐碟探測的整體上界。健康的卷回容量是毫秒級；拖到秒級就代表那顆碟不對了。</summary>
    internal const int ProbeTimeoutMs = 2000;

    private readonly Func<DriveInfo, VolumeReading?> _probe;

    public VolumeService() : this(DefaultProbe) { }

    /// <summary><paramref name="probe"/> 是測試注入點：換掉探測就能重現「某一顆碟卡住」。</summary>
    internal VolumeService(Func<DriveInfo, VolumeReading?> probe)
    {
        _probe = probe;
        // 建構子在 MainViewModel 的欄位初始化式上（UI 執行緒、主視窗還沒顯示），而 DriveInfo 的
        // IsReady／TotalSize 是會等 IOCTL 的呼叫：壞碟、休眠中的機械碟、失聯的網路磁碟機都能讓它
        // 一直不回來。所以第一輪丢背景，讀到什麼再回 UI 套用；之後 Refresh() 自己也有上界。
        _ = Task.Run(() =>
        {
            var readings = Collect(_probe);
            Shell.BeginOnUi(() => Apply(readings));
        });
    }

    /// <summary>
    /// 一輪探測的讀數。收集（背景、有上界）與套用（必須在 UI 執行緒，動 ObservableCollection）分開。
    /// </summary>
    internal sealed record VolumeReading(
        string Name, string Root, string Label, string Format, double Total, double Free);

    /// <summary>對每一顆碟丢一個探測，整批只等到 <see cref="ProbeTimeoutMs"/>；逾時的那顆這輪不給讀數。</summary>
    internal static List<VolumeReading> Collect(Func<DriveInfo, VolumeReading?> probe, int timeoutMs = ProbeTimeoutMs)
    {
        var results = new List<VolumeReading>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return results; }

        var tasks = new List<Task<VolumeReading?>>(drives.Length);
        foreach (var d in drives) tasks.Add(Task.Run(() => probe(d)));

        try { Task.WaitAll([.. tasks], timeoutMs); }
        catch { /* 個別探測失敗不影響其他顆；逾時由下方狀態判斷 */ }

        foreach (var t in tasks)
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } r) results.Add(r);
        return results;
    }

    public void Refresh() => Apply(Collect(_probe));

    void Apply(List<VolumeReading> readings)
    {
        foreach (var r in readings)
        {
            if (!_byName.TryGetValue(r.Name, out var vi))
            {
                vi = new VolumeInfo(r.Name, r.Root) { Label = r.Label, TypeText = r.Format };
                _byName[r.Name] = vi;
                Volumes.Add(vi);
            }
            vi.TotalBytes = r.Total;
            vi.FreeBytes = r.Free;
        }

        // 總量取「目前已知的每一顆」而不是這一輪讀到的那幾顆：某顆碟這一輪逾時，不該讓整排數字
        // 縮水成半台機器（那會讓「已用／總量」看起來突然變好）。
        double total = 0, free = 0;
        foreach (var v in _byName.Values) { total += v.TotalBytes; free += v.FreeBytes; }

        TotalBytes = total;
        FreeBytes = free;
        OnPropertyChanged(nameof(UsedFraction));
        OnPropertyChanged(nameof(SummaryText));
    }

    static VolumeReading? DefaultProbe(DriveInfo d)
    {
        try
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) return null;
            return new VolumeReading(
                d.Name.TrimEnd('\\'), d.Name, SafeLabel(d), SafeFormat(d), d.TotalSize, d.TotalFreeSpace);
        }
        catch { return null; }   // 單一磁碟讀取失敗即略過
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
