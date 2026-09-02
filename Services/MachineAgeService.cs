using System.Collections.ObjectModel;
using System.Globalization;

namespace XinSpect;

/// <summary>
/// 「這台機器多老了」：由三個各有偏誤的線索推估，不假裝知道出廠日。
/// </summary>
/// <remarks>
/// 兩個日期（Windows 安裝、韌體建置）零特權、進頁即讀；磁碟通電時數要發 S.M.A.R.T. 查詢，
/// 需要系統管理員身分，所以留給使用者按下按鈕才做——一般人只想看年齡的話，前兩個就夠。
/// </remarks>
public sealed class MachineAgeService : ObservableObject
{
    /// <summary>掃描的實體磁碟上限（與 S.M.A.R.T. 頁一致）。</summary>
    private const int MaxDrives = 16;

    public ObservableCollection<DiskAge> Disks { get; } = [];

    private MachineAgeVerdict _verdict = new()
    {
        Headline = "尚未推估", Severity = Severity.Neutral,
        Detail = "第一次進入本頁時會讀 Windows 安裝日期與韌體建置日期（零特權）。",
    };
    public MachineAgeVerdict Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    private string _installText = "—";
    public string InstallText { get => _installText; private set => SetProperty(ref _installText, value); }

    private string _biosText = "—";
    public string BiosText { get => _biosText; private set => SetProperty(ref _biosText, value); }

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanReadDisks)); }
    }

    public bool CanReadDisks => !_busy;

    private string _status = "通電時數要讀 S.M.A.R.T.，按下按鈕才會去問磁碟。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private DateTime? _install, _bios;

    /// <summary>用系統摘要裡已經讀好的兩個日期做一次推估（零特權，不碰磁碟）。</summary>
    public void Update(SystemSummary system)
    {
        _install = Parse(system.InstallDate);
        _bios = Parse(system.BiosDate);
        InstallText = _install is { } i ? i.ToString("yyyy-MM-dd") : "讀不到";
        BiosText = _bios is { } b ? b.ToString("yyyy-MM-dd") : "讀不到";
        Recompute();
    }

    /// <summary>讀各磁碟的通電時數（S.M.A.R.T.，需要系統管理員身分）。</summary>
    public void ReadDisks()
    {
        if (_busy) return;
        IsBusy = true;
        Status = "正在讀取各磁碟的 S.M.A.R.T. 通電時數…";

        _ = Task.Run(CollectDisks).ContinueWith(t =>
        {
            Disks.Clear();
            foreach (var d in t.Result) Disks.Add(d);
            Status = Disks.Count > 0
                ? $"讀到 {Disks.Count} 顆磁碟的通電時數。這是那顆碟的年齡，換過碟就不代表整機。"
                : "沒有讀到任何通電時數：需要以系統管理員身分執行，且部分磁碟不回報這一項。讀不到就是讀不到。";
            IsBusy = false;
            Recompute();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Recompute() => Verdict = MachineAgeDecoder.Judge(new MachineAgeFacts
    {
        WindowsInstall = _install,
        BiosDate = _bios,
        Disks = [.. Disks],
        Now = DateTime.Now,
    });

    /// <summary>系統摘要裡的日期是 <c>yyyy-MM-dd</c> 字串；讀不到時是破折號。</summary>
    private static DateTime? Parse(string? s)
        => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var d) ? d : null;

    private static List<DiskAge> CollectDisks()
    {
        var list = new List<DiskAge>();
        for (int i = 0; i < MaxDrives; i++)
        {
            uint bus = StorageSmartService.TryGetBusType(i, out string name);
            if (bus == 0) continue;
            try
            {
                long hours = StorageSmartService.TryReadPowerOnHours(i);
                if (hours > 0) list.Add(new DiskAge($"PhysicalDrive{i}（{name}）", hours));
            }
            catch (Exception ex)
            {
                Diag.Swallow($"MachineAgeService.Disk{i}", ex, "該顆磁碟的通電時數讀不到，不列入推估。");
            }
        }
        return list;
    }
}
