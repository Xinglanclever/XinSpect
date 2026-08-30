using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 逐邏輯處理器的核心時間歸因：閒置／使用者／核心／DPC／硬體中斷，以及每秒中斷次數。
/// </summary>
/// <remarks>
/// <para>資料來源是 <c>NtQuerySystemInformation(SystemProcessorPerformanceInformation)</c>——
/// 這是工作管理員與 perfmon 自己用的同一份計數器，零特權、未正式文件化但二十年來結構未變。</para>
/// <para>誠實界線：</para>
/// <list type="bullet">
/// <item>本卡片<b>取兩次樣求差</b>，不報「自開機以來的累計平均」——累計平均會把三天前的一次編譯
/// 攤平成一個看不出問題的數字。取樣區間固定約一秒，並以實測經過時間換算中斷次數。</item>
/// <item>Windows 把閒置時間算在核心模式內，DPC 與中斷又是核心模式的子集。本卡片顯示前先扣除、
/// 並在說明列明講三者關係，<b>不把五個數字並排讓人誤以為可以相加</b>。</item>
/// <item>閒置週期數（<c>SystemProcessorIdleCycleTime</c>）另外一列，並明說它的單位是 TSC 週期
/// 而非時間刻，不與上表的百分比混算。</item>
/// <item>超過 64 顆邏輯處理器的機器需逐處理器群組查詢；該路徑本機（36 顆）無法驗證，
/// 故未實作，只在數量對不上時照實說明。</item>
/// </list>
/// </remarks>
public sealed class CoreTimeService : ObservableObject
{
    private bool _loading;
    public bool IsLoading
    {
        get => _loading;
        private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }
    public bool CanRefresh => !_loading;

    private string _status = "尚未取樣。按「取樣」擷取約一秒的逐核時間分佈。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private string _idleCycles = "—";
    /// <summary>逐核閒置週期數的說明（TSC 週期，不與百分比同尺）。</summary>
    public string IdleCycles { get => _idleCycles; private set => SetProperty(ref _idleCycles, value); }

    /// <summary>這張表的讀法說明（固定文字）。</summary>
    public string ReadingNotice => CoreTimeDecoder.ReadingNotice;

    /// <summary>計數器精度說明（固定文字）：百分比為何呈離散跳動。</summary>
    public string ResolutionNotice => CoreTimeDecoder.ResolutionNotice;

    private string _groupNotice = "";
    /// <summary>邏輯處理器數對不上時的說明；一致時為空字串。</summary>
    public string GroupNotice { get => _groupNotice; private set { if (SetProperty(ref _groupNotice, value)) OnPropertyChanged(nameof(HasGroupNotice)); } }
    public bool HasGroupNotice => _groupNotice.Length > 0;

    public ObservableCollection<CoreTimeRow> Rows { get; } = [];

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        Status = "取樣中（約一秒）…";
        try
        {
            var (rows, summary, idle, group, note) = await Task.Run(SampleAsync);
            Rows.Clear(); foreach (var r in rows) Rows.Add(r);
            Summary = summary;
            IdleCycles = idle;
            GroupNotice = group;
            Status = note;
        }
        catch (Exception ex)
        {
            Status = "取樣失敗：" + ex.Message;
        }
        finally { IsLoading = false; }
    }

    private async Task<(List<CoreTimeRow> Rows, string Summary, string Idle, string Group, string Note)> SampleAsync()
    {
        int reported = Environment.ProcessorCount;

        var first = ReadPerf(reported);
        if (first is null)
            return ([], "—", "—", "", "NtQuerySystemInformation(SystemProcessorPerformanceInformation) 查詢失敗，無法取樣。");
        ulong[]? idle1 = ReadIdleCycles(reported);

        // 以高解析度計時器量實際經過時間：Task.Delay 不保證剛好一秒，中斷次數的分母必須用實測值
        var sw = Stopwatch.StartNew();
        await Task.Delay(1000).ConfigureAwait(false);
        var second = ReadPerf(reported);
        sw.Stop();
        double seconds = sw.Elapsed.TotalSeconds;
        if (second is null)
            return ([], "—", "—", "", "第二次取樣失敗，無法求差（單次讀值是自開機以來的累計值，直接顯示會誤導）。");

        ulong[]? idle2 = ReadIdleCycles(reported);

        int n = Math.Min(first.Length, second.Length);
        var rows = new List<CoreTimeRow>(n);
        int skipped = 0;
        for (int i = 0; i < n; i++)
        {
            var row = CoreTimeDecoder.Diff(i, first[i], second[i], seconds);
            if (row is null) skipped++; else rows.Add(row);
        }

        string note = $"取樣區間 {seconds:0.00} 秒，共 {rows.Count} 顆邏輯處理器"
                    + (skipped > 0 ? $"（{skipped} 顆的差值無效已略過）" : "")
                    + "。零特權讀值，與工作管理員同一份計數器。";
        return (rows,
                CoreTimeDecoder.Summarize(rows),
                CoreTimeDecoder.DescribeIdleCycles(idle1, idle2, seconds),
                CoreTimeDecoder.GroupNotice(n, reported),
                note);
    }

    // ── NtQuerySystemInformation 直呼 ─────────────────────────────────────────

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, nint buffer,
                                                      uint length, out uint returnLength);

    private const int SystemProcessorPerformanceInformation = 8;
    private const int SystemProcessorIdleCycleTime = 83;

    /// <summary>x64 下 SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION 為 48 位元組（5×LARGE_INTEGER＋ULONG＋填補）。</summary>
    private const int PerfRecordSize = 48;

    private static CoreTimeSample[]? ReadPerf(int count)
    {
        uint len = (uint)(PerfRecordSize * count);
        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, len, out uint got) != 0)
                return null;
            int n = (int)(got / PerfRecordSize);
            if (n <= 0) return null;
            var result = new CoreTimeSample[Math.Min(n, count)];
            for (int i = 0; i < result.Length; i++)
            {
                nint p = buf + i * PerfRecordSize;
                result[i] = new CoreTimeSample(
                    Marshal.ReadInt64(p),
                    Marshal.ReadInt64(p + 8),
                    Marshal.ReadInt64(p + 16),
                    Marshal.ReadInt64(p + 24),
                    Marshal.ReadInt64(p + 32),
                    unchecked((uint)Marshal.ReadInt32(p + 40)));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>逐核閒置週期數（每顆 8 位元組）。查不到就回 null，由解碼器照實說。</summary>
    private static ulong[]? ReadIdleCycles(int count)
    {
        uint len = (uint)(8 * count);
        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorIdleCycleTime, buf, len, out uint got) != 0)
                return null;
            int n = (int)(got / 8);
            if (n <= 0) return null;
            var result = new ulong[Math.Min(n, count)];
            for (int i = 0; i < result.Length; i++)
                result[i] = unchecked((ulong)Marshal.ReadInt64(buf + i * 8));
            return result;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
