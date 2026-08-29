using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 記憶體真實面貌（認可部分）：`GetPerformanceInfo` 的認可總量／上限／尖峰。
/// 「已使用 12 GB」這種說法掩蓋一半事實——認可（Commit）才是程式真正要求過、
/// 系統必須保證有地方放的量。認可上限＝實體記憶體＋分頁檔，所以認可尖峰超過實體記憶體時，
/// 當時必然有一部分認可只能由分頁檔支撐。
/// </summary>
/// <remarks>
/// 誠實界線：本卡片量的是**認可帳面**，不是「真的寫出了多少頁面到磁碟」——
/// 後者要看 Memory\Pages Output/sec 之類的計數器，本卡片不宣稱。
/// 尖峰是「開機至今的累積最大值」，不是現在的狀態。
/// </remarks>
public sealed class MemoryTruthService : ObservableObject
{
    // 對應 native PERFORMANCE_INFORMATION：cb 與最後三個欄位是 DWORD，中間九個是 SIZE_T。
    // 若把 HandleCount/ProcessCount/ThreadCount 誤宣告為 ulong，Marshal.SizeOf 會比 native 大 8 bytes，
    // cb 不符會讓 GetPerformanceInfo 直接回 false。
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", EntryPoint = "GetPerformanceInfo", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint cb);

    private string _commitText = "—";
    /// <summary>目前認可與認可上限。</summary>
    public string CommitText { get => _commitText; private set => SetProperty(ref _commitText, value); }

    private string _peakText = "—";
    /// <summary>認可尖峰與實體記憶體總量的對照。</summary>
    public string PeakText { get => _peakText; private set => SetProperty(ref _peakText, value); }

    private string _pageSizeText = "—";
    /// <summary>頁面大小（量到的，不是假設 4 KB）。</summary>
    public string PageSizeText { get => _pageSizeText; private set => SetProperty(ref _pageSizeText, value); }

    private string _verdict = "—";
    /// <summary>判定短語。</summary>
    public string Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    private string _verdictText = "尚未讀取。";
    /// <summary>判定說明（含量測界線）。</summary>
    public string VerdictText { get => _verdictText; private set => SetProperty(ref _verdictText, value); }

    /// <summary>重讀一次。失敗時如實顯示失敗原因，不留舊值假裝成功。</summary>
    public void Refresh()
    {
        try
        {
            if (!GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PerformanceInformation>()))
            {
                int err = Marshal.GetLastWin32Error();
                CommitText = PeakText = PageSizeText = Verdict = "—";
                VerdictText = $"讀取失敗（GetPerformanceInfo，Win32 錯誤 {err}）。";
                return;
            }

            var r = MemoryTruthMath.ToGigabytes(pi.CommitTotal, pi.CommitLimit, pi.CommitPeak, pi.PhysicalTotal, pi.PageSize);
            CommitText = $"{r.CommitGb:0.0} / 上限 {r.LimitGb:0.0} GB";
            PeakText = $"{r.PeakGb:0.0} GB（實體 {r.PhysicalGb:0.0} GB）";
            PageSizeText = $"{pi.PageSize / 1024.0:0} KB";

            var (exceeded, verdict) = MemoryTruthMath.Judge(r.PeakGb, r.PhysicalGb);
            Verdict = verdict;
            VerdictText = exceeded
                ? $"認可尖峰 {r.PeakGb:0.0} GB 超過實體 {r.PhysicalGb:0.0} GB——開機至今曾有一部分認可只能由分頁檔支撐。"
                  + "（這是認可帳面，不代表當時真的把那些頁面寫到磁碟；本卡片不宣稱後者。）"
                : $"認可尖峰 {r.PeakGb:0.0} GB 未超過實體 {r.PhysicalGb:0.0} GB——開機至今的認可量一直放得進實體記憶體。";
        }
        catch (Exception ex)
        {
            CommitText = PeakText = PageSizeText = Verdict = "—";
            VerdictText = "讀取失敗：" + ex.Message;
        }
    }
}

/// <summary>認可數值換算與判定（純函式，單元測試涵蓋）。</summary>
public static class MemoryTruthMath
{
    /// <summary>單位換算結果（GB）。</summary>
    public readonly record struct Reading(double CommitGb, double LimitGb, double PeakGb, double PhysicalGb);

    /// <summary>
    /// 把 `GetPerformanceInfo` 的「頁數」換算成 GB。pageSizeBytes 為 0 時全回 0
    /// （寧可顯示 0 也不要除以零炸掉整頁）。
    /// </summary>
    public static Reading ToGigabytes(nuint commitPages, nuint limitPages, nuint peakPages, nuint physicalPages, nuint pageSizeBytes)
    {
        if (pageSizeBytes == 0) return new Reading(0, 0, 0, 0);
        const double gib = 1024.0 * 1024.0 * 1024.0;
        double ps = pageSizeBytes;
        return new Reading(commitPages * ps / gib, limitPages * ps / gib, peakPages * ps / gib, physicalPages * ps / gib);
    }

    /// <summary>純函式：判定認可尖峰是否超過實體記憶體。</summary>
    public static (bool Exceeded, string Verdict) Judge(double peakGb, double physGb)
        => peakGb > physGb ? (true, "曾超過實體（動用分頁檔支撐）") : (false, "未超過實體");
}
