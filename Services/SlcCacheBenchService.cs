using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>SLC 快取耗盡曲線的一個取樣點。</summary>
public sealed class SlcSample
{
    public SlcSample(double seconds, double mbps) { Seconds = seconds; Mbps = mbps; }
    public double Seconds { get; }
    public double Mbps { get; }
}

/// <summary>
/// SLC 快取耗盡曲線：對選定磁碟區持續寫入（每秒強制 FlushToDisk 確保資料真的落到裝置），
/// 記錄寫入速度曲線，偵測「速度斷崖」——SLC 快取耗盡後掉到直寫速度的那個點。
/// 廠商規格表絕對不會寫這個數字，只能實測。
/// </summary>
/// <remarks>
/// 誠實界線：量的是「這台機器、這個當下」的持續寫入曲線——原廠快取演算法、溫度、背景活動、
/// 主機狀態都會影響它；斷崖點是曲線推導的，不是裝置回報的。測試會在選定磁碟區寫入大量資料，
/// 上限受使用者選擇與「剩餘空間 − 8 GB」雙重約束，結束後刪除測試檔。
/// </remarks>
public sealed class SlcCacheBenchService : ObservableObject
{
    private static readonly (long Bytes, string Text)[] Targets =
    {
        (16L * 1024 * 1024 * 1024, "16 GB"),
        (32L * 1024 * 1024 * 1024, "32 GB"),
        (64L * 1024 * 1024 * 1024, "64 GB"),
        (128L * 1024 * 1024 * 1024, "128 GB"),
    };

    private CancellationTokenSource? _cts;

    public string[] TargetTexts { get; } = Targets.Select(t => t.Text).ToArray();

    private int _targetIndex = 1;
    /// <summary>目標寫入量（索引對 <see cref="TargetTexts"/>）。</summary>
    public int TargetIndex { get => _targetIndex; set => SetProperty(ref _targetIndex, value); }

    private string _driveLetter = "C:";
    /// <summary>測試目標磁碟區（如「C:」）。由 UI 從磁碟區清單指派。</summary>
    public string DriveLetter { get => _driveLetter; set => SetProperty(ref _driveLetter, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未測試";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始測試」持續寫入並記錄速度曲線（期間該磁碟會滿載寫入）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private double[] _times = [];
    /// <summary>取樣時刻（秒）。</summary>
    public double[] Times { get => _times; private set => SetProperty(ref _times, value); }

    private double[] _speeds = [];
    /// <summary>各時刻的寫入速度（MB/s）。</summary>
    public double[] Speeds { get => _speeds; private set => SetProperty(ref _speeds, value); }

    private double[] _cliffMarks = [];
    /// <summary>斷崖位置（秒，供圖表標線）。</summary>
    public double[] CliffMarks { get => _cliffMarks; private set => SetProperty(ref _cliffMarks, value); }

    private string _peakText = "—", _cliffText = "尚未偵測到斷崖（可能快取很大或目標量太小）", _postText = "—";
    public string PeakText { get => _peakText; private set => SetProperty(ref _peakText, value); }
    public string CliffText { get => _cliffText; private set => SetProperty(ref _cliffText, value); }
    public string PostText { get => _postText; private set => SetProperty(ref _postText, value); }

    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        Phase = "測試中";
        ProgressFraction = 0;
        Times = []; Speeds = []; CliffMarks = [];
        PeakText = "—"; CliffText = "尚未偵測到斷崖"; PostText = "—";

        long target = Targets[Math.Clamp(_targetIndex, 0, Targets.Length - 1)].Bytes;
        var root = Path.GetPathRoot(Path.GetFullPath(DriveLetter)) ?? "C:\\";
        var file = Path.Combine(root, "XinSpect_slctest.tmp");
        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLineUpdate(t.Status); });
        var report = (IProgress<(double, string)>)prog;

        try
        {
            var samples = await Task.Run(() => WriteSustained(file, target, ct, report), ct);

            Times = samples.Select(s => s.Seconds).ToArray();
            Speeds = samples.Select(s => s.Mbps).ToArray();
            var (peakMbps, peakSec, cliffSec, postMed) = SlcMath.Analyze(Times, Speeds);
            PeakText = peakMbps > 0 ? $"{peakMbps:N0} MB/s（@ {peakSec:0.0} s）" : "—";
            CliffText = cliffSec >= 0 ? $"斷崖 @ {cliffSec:0.0} s" : "未偵測到斷崖（快取很大、或寫入量未超過快取）";
            CliffMarks = cliffSec >= 0 ? [cliffSec] : [];
            PostText = postMed > 0 ? $"斷崖後中位 {postMed:N0} MB/s" : "—";

            Phase = "完成";
            ProgressFraction = 1;
            Status = $"完成 ・ 共寫入 {samples.Count:N0} 秒曲線。斷崖點是曲線推導的（量到的），會受溫度與背景活動影響。";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            Status = "測試已停止。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            Status = "測試失敗：" + ex.Message;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* 測試檔刪除失敗留給使用者 */ }
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private string _statusLine = "";
    private void StatusLineUpdate(string s) => _statusLine = s;

    private List<SlcSample> WriteSustained(string file, long target, CancellationToken ct, IProgress<(double, string)> report)
    {
        const int chunk = 4 * 1024 * 1024;
        var payload = new byte[chunk];
        new Random(20260829).NextBytes(payload);

        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(file))!).AvailableFreeSpace;
        long reserve = 8L * 1024 * 1024 * 1024;
        long cap = Math.Min(target, free - reserve);
        if (cap < 4L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("該磁碟區可用空間不足（需至少 4 GB，且保留 8 GB 給系統）。");

        var samples = new List<SlcSample>();
        var sw = Stopwatch.StartNew();
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, chunk, FileOptions.None);
        long written = 0;
        long windowBytes = 0;
        var windowSw = Stopwatch.StartNew();
        double lastSampleSec = -1;

        while (written < cap && !ct.IsCancellationRequested)
        {
            fs.Write(payload, 0, chunk);
            written += chunk;
            windowBytes += chunk;

            // 每秒 FlushToDisk 一次並取樣：確保量到的是「真的落到裝置」的速度，不是作業系統快取的假象
            if (sw.Elapsed.TotalSeconds - lastSampleSec >= 1.0)
            {
                fs.Flush(flushToDisk: true);
                double sec = sw.Elapsed.TotalSeconds;
                double mbps = windowBytes / (1024.0 * 1024.0) / Math.Max(windowSw.Elapsed.TotalSeconds, 1e-9);
                samples.Add(new SlcSample(sec, mbps));
                report.Report((written / (double)cap, $"已寫入 {written / (1024.0 * 1024 * 1024):0.#} GB ・ 目前 {mbps:N0} MB/s"));
                windowBytes = 0;
                windowSw.Restart();
                lastSampleSec = sec;
            }
        }
        fs.Flush(flushToDisk: true);
        return samples;
    }
}

/// <summary>SLC 曲線的純函式數學（尖峰、斷崖偵測、後段中位）。</summary>
public static class SlcMath
{
    /// <summary>
    /// 斷崖定義：某點速度 &lt; 尖峰的 35%，且其後至少 5 個取樣的中位數 &lt; 尖峰的 45%（排除單點雜訊）。
    /// 回傳（尖峰 MB/s、尖峰時刻、斷崖時刻或 -1、斷崖後中位或 0）。
    /// </summary>
    public static (double PeakMbps, double PeakSec, double CliffSec, double PostMedian)
        Analyze(double[] times, double[] mbps)
    {
        if (times.Length == 0 || mbps.Length == 0) return (0, 0, -1, 0);
        int peak = 0;
        for (int i = 1; i < mbps.Length; i++) if (mbps[i] > mbps[peak]) peak = i;
        double peakMbps = mbps[peak];

        for (int j = peak + 1; j + 5 <= mbps.Length; j++)
        {
            if (mbps[j] >= peakMbps * 0.35) continue;
            var window = mbps.Skip(j).Take(6).OrderBy(x => x).ToArray();
            double med = window[3];
            if (med < peakMbps * 0.45)
            {
                var post = mbps.Skip(j).OrderBy(x => x).ToArray();
                double postMed = post.Length % 2 == 1 ? post[post.Length / 2] : (post[post.Length / 2 - 1] + post[post.Length / 2]) / 2.0;
                return (peakMbps, times[peak], times[j], postMed);
            }
        }
        return (peakMbps, times[peak], -1, 0);
    }
}
