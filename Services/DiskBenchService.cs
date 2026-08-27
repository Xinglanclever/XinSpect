using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XinSpect;

/// <summary>
/// 磁碟讀寫效能測試：於所選磁碟建立暫存檔，量測循序讀 / 寫（MB/s）與隨機 4K 讀 / 寫（IOPS）。
/// 全程採「未緩衝直接 I/O」（FILE_FLAG_NO_BUFFERING）並搭配 WriteThrough 直寫，繞過作業系統的
/// 讀寫快取，避免循序讀取讀到方才寫入而仍留存於記憶體的內容（此為一般以 FileStream 測試最常見的
/// 失真來源）；緩衝區以磁區對齊（4 KB）的原生記憶體配置，讀寫大小與檔案位移皆為磁區整數倍以符合
/// 未緩衝 I/O 的硬性要求。隨機 4K 測試以固定時間預算計時，佇列深度為 1（QD1，逐次同步 I/O）。
/// 測試結束後刪除暫存檔。
/// </summary>
public sealed class DiskBenchService : ObservableObject
{
    private const int Mib = 1024 * 1024;
    private const int Sector = 4096;             // 未緩衝 I/O 對齊粒度（涵蓋 512e 與 4Kn 磁碟及分頁大小）
    private const long TestBytes = 512L * Mib;   // 循序測試檔大小（512 MB，為磁區整數倍）
    private const int Chunk = Mib;               // 循序區塊（1 MB，為磁區整數倍）
    private const int RandBlock = Sector;        // 隨機區塊（4 KB）
    private const double RandSeconds = 3.0;      // 隨機讀 / 寫各自的計時預算
    private const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING

    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Drives { get; } = new();

    private int _driveIndex;
    public int SelectedDriveIndex { get => _driveIndex; set => SetProperty(ref _driveIndex, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running && Drives.Count > 0;

    private string _phase = "尚未測試";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "選擇磁碟後按「開始測試」。採未緩衝直接 I/O，將建立約 512 MB 暫存檔，完成後自動刪除。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private string _seqWrite = "—", _seqRead = "—", _randRead = "—", _randWrite = "—";
    public string SeqWriteText { get => _seqWrite; private set => SetProperty(ref _seqWrite, value); }
    public string SeqReadText { get => _seqRead; private set => SetProperty(ref _seqRead, value); }
    public string RandReadText { get => _randRead; private set => SetProperty(ref _randRead, value); }
    public string RandWriteText { get => _randWrite; private set => SetProperty(ref _randWrite, value); }

    /// <summary>列出可測試的固定磁碟機（就緒且為本機磁碟）。</summary>
    public void PopulateDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                Drives.Add($"{d.Name.TrimEnd('\\')}（可用 {d.AvailableFreeSpace / 1073741824.0:0.0} GB）");
            }
            catch { /* 部分磁碟查詢屬性可能拋出，略過 */ }
        }
        if (_driveIndex >= Drives.Count) SelectedDriveIndex = 0;
        OnPropertyChanged(nameof(CanStart));
    }

    public void Start()
    {
        if (IsRunning || Drives.Count == 0) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 由選取項還原磁碟根目錄（"C:\（可用 …）" → "C:\"）
        int idx = Math.Clamp(_driveIndex, 0, Drives.Count - 1);
        string label = Drives[idx];
        string root = label.Split('（')[0];
        string path = Path.Combine(root, "XinSpectDiskBench.tmp");

        IsRunning = true;
        Phase = "測試中";
        ProgressFraction = 0;
        SeqWriteText = SeqReadText = RandReadText = RandWriteText = "測試中…";
        StatusLine = $"正在測試 {root} …";

        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLine = t.Status; });
        var report = (IProgress<(double, string)>)prog;

        try
        {
            // 可用空間檢查（需測試檔大小 + 256 MB 餘裕）
            var di = new DriveInfo(root);
            if (di.AvailableFreeSpace < TestBytes + 256L * Mib)
                throw new IOException($"可用空間不足（需約 {(TestBytes + 256L * Mib) / (double)Mib / 1024:0.0} GB）。");

            var (sw, sr, rr, rw) = await Task.Run(() => RunAll(path, ct, report), ct);

            SeqWriteText = $"{sw:0} MB/s";
            SeqReadText = $"{sr:0} MB/s";
            RandReadText = $"{rr:#,0} IOPS";
            RandWriteText = $"{rw:#,0} IOPS";

            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"完成 ・ 循序寫 {sw:0} / 讀 {sr:0} MB/s ・ 隨機4K 讀 {rr:#,0} / 寫 {rw:#,0} IOPS（未緩衝直接 I/O ・ QD1）";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            StatusLine = "測試已停止。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "測試失敗：" + ex.Message;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 暫存檔清理失敗不影響結果 */ }
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 實際量測。全程使用未緩衝直接 I/O（NO_BUFFERING + WriteThrough）與磁區對齊的原生緩衝區，
    /// 以 <see cref="RandomAccess"/> 對 <see cref="SafeFileHandle"/> 在指定位移讀寫。
    /// </summary>
    private static unsafe (double SeqW, double SeqR, double RandR, double RandW) RunAll(
        string path, CancellationToken ct, IProgress<(double, string)> report)
    {
        // 磁區對齊的原生緩衝區（1 MB）：未緩衝 I/O 要求緩衝區位址、讀寫長度、檔案位移皆為磁區整數倍。
        void* p = NativeMemory.AlignedAlloc((nuint)Chunk, (nuint)Sector);
        try
        {
            var buf = new Span<byte>(p, Chunk);
            new Random(0x51A5).NextBytes(buf);

            double seqW, seqR, randR, randW;

            // 1) 循序寫入（未緩衝直寫）
            report.Report((0.02, "循序寫入（未緩衝直寫）…"));
            {
                using var h = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                              NoBuffering | FileOptions.WriteThrough, TestBytes);
                var sw = Stopwatch.StartNew();
                long off = 0;
                while (off < TestBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, buf, off);
                    off += Chunk;
                    if ((off & (32 * Mib - 1)) == 0)
                        report.Report((0.02 + 0.40 * off / TestBytes, $"循序寫入… {off / Mib} MB"));
                }
                sw.Stop();
                seqW = TestBytes / (double)Mib / sw.Elapsed.TotalSeconds;
            }

            // 2) 循序讀取（未緩衝，確保讀自實體磁碟而非系統快取）
            report.Report((0.42, "循序讀取（未緩衝）…"));
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering);
                var sw = Stopwatch.StartNew();
                long off = 0;
                while (off < TestBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = RandomAccess.Read(h, buf, off);
                    if (n <= 0) break;
                    off += n;
                    if ((off & (32 * Mib - 1)) == 0)
                        report.Report((0.42 + 0.30 * off / TestBytes, $"循序讀取… {off / Mib} MB"));
                }
                sw.Stop();
                seqR = off / (double)Mib / sw.Elapsed.TotalSeconds;
            }

            long blocks = TestBytes / RandBlock;
            var rnd = new Random(0x2C9E);
            var rblk = new Span<byte>(p, RandBlock);   // 沿用前段緩衝區前 4 KB

            // 3) 隨機 4K 讀取（未緩衝 ・ QD1 ・ 定時預算）
            report.Report((0.72, "隨機 4K 讀取…"));
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering);
                var sw = Stopwatch.StartNew();
                long ops = 0;
                while (sw.Elapsed.TotalSeconds < RandSeconds)
                {
                    long pos = (long)(rnd.NextDouble() * (blocks - 1)) * RandBlock;   // 4K 對齊
                    RandomAccess.Read(h, rblk, pos);
                    ops++;
                    if ((ops & 255) == 0) ct.ThrowIfCancellationRequested();
                }
                sw.Stop();
                randR = ops / sw.Elapsed.TotalSeconds;
            }

            // 4) 隨機 4K 寫入（未緩衝直寫 ・ QD1 ・ 定時預算）
            report.Report((0.86, "隨機 4K 寫入…"));
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.None,
                                              NoBuffering | FileOptions.WriteThrough);
                var sw = Stopwatch.StartNew();
                long ops = 0;
                while (sw.Elapsed.TotalSeconds < RandSeconds)
                {
                    long pos = (long)(rnd.NextDouble() * (blocks - 1)) * RandBlock;   // 4K 對齊
                    RandomAccess.Write(h, rblk, pos);
                    ops++;
                    if ((ops & 255) == 0) ct.ThrowIfCancellationRequested();
                }
                sw.Stop();
                randW = ops / sw.Elapsed.TotalSeconds;
            }

            report.Report((0.99, "整理結果…"));
            return (seqW, seqR, randR, randW);
        }
        finally
        {
            NativeMemory.AlignedFree(p);
        }
    }
}
