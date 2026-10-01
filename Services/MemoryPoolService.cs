using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// RAMMap 式核心記憶體池細目：Paged Pool／Nonpaged Pool／各優先級 Standby／Modified／Free／Zeroed。
/// 全部走 NtQuerySystemInformation 純 API 直讀，零特權（讀取不需要 SeProfileSingleProcessPrivilege）。
///
/// 池大小取 SystemPerformanceInformation（info class 2）的 PagedPoolPages／NonPagedPoolPages；
/// 各清單取 SystemMemoryListInformation（info class 0x50）——與 MemoryService 同一來源，
/// 但 MemoryService 只彙總待命總量，這裡拆開每一優先級（0=最低…7=最高）誠實展開。
/// </summary>
public static class MemoryPoolService
{
    private const int SystemPerformanceInformation = 0x02;
    private const int SystemMemoryListInformation = 0x50;

    private static readonly long PageSize = Environment.SystemPageSize;
    private const double MB = 1024.0 * 1024.0;

    /// <summary>單次讀取的完整池細目快照（所有數值以 MB 呈現，誠實不四捨五入到 GB）。</summary>
    public sealed record PoolSnapshot(
        double PagedPoolMB, double NonPagedPoolMB,
        double ZeroedMB, double FreeMB,
        double ModifiedMB, double ModifiedNoWriteMB,
        double BadMB,
        double[] StandbyByPriorityMB,
        double CommitUsedMB, double CommitLimitMB)
    {
        /// <summary>待命清單總計（各優先級加總）。</summary>
        public double StandbyTotalMB => StandbyByPriorityMB.Sum();
    }

    /// <summary>讀取核心記憶體池細目。任何 API 失敗都會讓對應欄位為 0——呼叫端可判斷是否值得顯示。</summary>
    public static PoolSnapshot? Read()
    {
        try
        {
            // 池大小（SystemPerformanceInformation）
            var perf = new SYSTEM_PERFORMANCE_INFORMATION();
            // NtQuerySystemInformation 需要「夠大」的緩衝區；實際 SYSTEM_PERFORMANCE_INFORMATION 有數百位元組，
            // 我們只需要前幾個欄位，但緩衝區必須開到實際大小否則回 STATUS_INFO_LENGTH_MISMATCH。
            int perfSize = 4096;
            IntPtr perfBuf = Marshal.AllocHGlobal(perfSize);
            double paged = 0, nonPaged = 0;
            try
            {
                if (NtQuerySystemInformation(SystemPerformanceInformation, perfBuf, perfSize, out _) == 0)
                {
                    var raw = Marshal.PtrToStructure<SYSTEM_PERFORMANCE_INFORMATION>(perfBuf);
                    paged = raw.PagedPoolPages * PageSize / (double)MB;
                    nonPaged = raw.NonPagedPoolPages * PageSize / (double)MB;
                }
            }
            finally { Marshal.FreeHGlobal(perfBuf); }

            // 各清單（SystemMemoryListInformation，與 MemoryService 同源但展開）
            var memList = new SYSTEM_MEMORY_LIST_INFORMATION();
            int listSize = Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>();
            IntPtr listBuf = Marshal.AllocHGlobal(listSize);
            double[] standby = new double[8];
            double zeroed = 0, free = 0, modified = 0, modNoWrite = 0, bad = 0;
            double commitUsed = 0, commitLimit = 0;
            try
            {
                if (NtQuerySystemInformation(SystemMemoryListInformation, listBuf, listSize, out _) == 0)
                {
                    memList = Marshal.PtrToStructure<SYSTEM_MEMORY_LIST_INFORMATION>(listBuf);
                    zeroed = memList.ZeroPageCount * PageSize / (double)MB;
                    free = memList.FreePageCount * PageSize / (double)MB;
                    modified = memList.ModifiedPageCount * PageSize / (double)MB;
                    modNoWrite = memList.ModifiedNoWritePageCount * PageSize / (double)MB;
                    bad = memList.BadPageCount * PageSize / (double)MB;
                    for (int i = 0; i < 8; i++)
                        standby[i] = memList.PageCountByPriority[i] * PageSize / (double)MB;
                }
            }
            finally { Marshal.FreeHGlobal(listBuf); }

            // Commit（從 GlobalMemoryStatusEx 補）
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref ms))
            {
                commitLimit = ms.ullTotalPageFile / (double)MB;
                commitUsed = (ms.ullTotalPageFile - ms.ullAvailPageFile) / (double)MB;
            }

            return new PoolSnapshot(paged, nonPaged, zeroed, free, modified, modNoWrite, bad, standby, commitUsed, commitLimit);
        }
        catch { return null; }
    }

    /// <summary>產生 UI 用的標籤文字（例：P0 128 MB）。</summary>
    public static string StandbyLabel(int priority, double mb) =>
        $"P{priority} {mb:0} MB";

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>
    /// SYSTEM_PERFORMANCE_INFORMATION 的前幾個欄位（Windows Internals 7th Ch.5）。
    /// 只取 PagedPoolPages／NonPagedPoolPages 夠用；整個 struct 很大，用部份讀法。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PERFORMANCE_INFORMATION
    {
        public long IdleProcessTime;
        public long IoReadTransferCount;
        public long IoWriteTransferCount;
        public long IoOtherTransferCount;
        public uint IoReadOperationCount;
        public uint IoWriteOperationCount;
        public uint IoOtherOperationCount;
        public uint AvailablePages;
        public uint CommittedPages;
        public uint CommitLimit;
        public uint PeakCommitment;
        public uint PageFaultCount;
        public uint CopyOnWriteCount;
        public uint TransitionCount;
        public uint CacheTransitionCount;
        public uint DemandZeroCount;
        public uint PageReadCount;
        public uint PageReadIoCount;
        public uint CacheReadCount;
        public uint CacheIoCount;
        public uint DirtyPagesWriteCount;
        public uint DirtyWriteIoCount;
        public uint MappedPagesWriteCount;
        public uint MappedWriteIoCount;
        public uint PagedPoolPages;
        public uint NonPagedPoolPages;
        // 後面還有很多欄位，但只需要到這裡
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public long ZeroPageCount;
        public long FreePageCount;
        public long ModifiedPageCount;
        public long ModifiedNoWritePageCount;
        public long BadPageCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public long[] PageCountByPriority;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public long[] RepurposedPagesByPriority;
        public long ModifiedPageCountPageFile;
    }
}
