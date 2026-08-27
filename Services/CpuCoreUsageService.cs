using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>格狀迷你走勢圖的一格：邏輯處理器標籤 + 其使用率走勢緩衝。</summary>
public sealed class CoreLoadCell
{
    public CoreLoadCell(string label, MetricHistory history)
    {
        Label = label;
        History = history;
    }
    public string Label { get; }
    public MetricHistory History { get; }
}

/// <summary>
/// 各「邏輯處理器」即時使用率（仿 Windows 工作管理員的「邏輯處理器」檢視）。
/// 以 NtQuerySystemInformation(SystemProcessorPerformanceInformation) 取得每顆邏輯處理器的
/// 閒置 / 核心 / 使用者時間，前後兩次取樣差分得佔用率——與工作管理員同源，故顆數與數值相符。
/// 每顆邏輯處理器各持一條走勢緩衝，供格狀曲線圖繪製；不需系統管理權限、無在地化字串依賴。
/// </summary>
public sealed class CpuCoreUsageService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;   // 含 IdleTime
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    private const int SystemProcessorPerformanceInformation = 8;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int infoClass,
        [Out] SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] info,
        int length,
        out int returnLength);

    private readonly int _structSize;
    private readonly SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _buf;
    private readonly long[] _prevIdle;
    private readonly long[] _prevTotal;

    /// <summary>實際邏輯處理器數（以系統回報為準；單一處理器群組上限 64）。</summary>
    public int Count { get; }

    /// <summary>每顆邏輯處理器一格（標籤 + 走勢緩衝），供 ItemsControl 繫結。</summary>
    public IReadOnlyList<CoreLoadCell> Cells { get; }

    public CpuCoreUsageService(int historyCapacity = 90)
    {
        _structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();

        int logical = Math.Max(1, Environment.ProcessorCount);
        var probe = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[logical];
        int n = logical;
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, probe, _structSize * probe.Length, out int ret) == 0 && ret > 0)
            n = Math.Clamp(ret / _structSize, 1, logical);
        Count = n;

        _buf = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[Count];
        _prevIdle = new long[Count];
        _prevTotal = new long[Count];

        var cells = new List<CoreLoadCell>(Count);
        for (int i = 0; i < Count; i++)
            cells.Add(new CoreLoadCell($"#{i}", new MetricHistory(historyCapacity, "%", 100)));
        Cells = cells;

        Sample();   // 建立差分基準
    }

    /// <summary>取樣一次，將每顆邏輯處理器佔用率推入其走勢緩衝（於 UI 執行緒每秒呼叫）。</summary>
    public void Refresh()
    {
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, _buf, _structSize * Count, out _) != 0)
            return;

        for (int i = 0; i < Count; i++)
        {
            long idle = _buf[i].IdleTime;
            long total = _buf[i].KernelTime + _buf[i].UserTime;   // KernelTime 已含閒置
            long dIdle = idle - _prevIdle[i];
            long dTotal = total - _prevTotal[i];
            _prevIdle[i] = idle;
            _prevTotal[i] = total;

            double load = dTotal > 0 ? 100.0 * (dTotal - dIdle) / dTotal : 0;
            Cells[i].History.Push(Math.Clamp(load, 0, 100));
        }
    }

    private void Sample()
    {
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, _buf, _structSize * Count, out _) != 0)
            return;
        for (int i = 0; i < Count; i++)
        {
            _prevIdle[i] = _buf[i].IdleTime;
            _prevTotal[i] = _buf[i].KernelTime + _buf[i].UserTime;
        }
    }
}
