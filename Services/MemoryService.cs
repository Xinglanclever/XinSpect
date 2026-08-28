using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 記憶體整理：採 RAMMap 等級的「系統記憶體清單」真實操作，而非表面的單一行程工作集數字。
/// 透過未公開但穩定的 NtSetSystemInformation(SystemMemoryListInformation, …) 執行：
/// 清空所有行程工作集、刷新已修改頁面、清除待命（快取）清單。其中「清除待命清單」會把
/// 系統快取的檔案資料真正釋回可用記憶體——這是唯一能大量釋放 RAM 的動作。
/// 讀取則以 NtQuerySystemInformation 取得待命／已修改／可用的真實分佈，誠實呈現可釋放量。
/// 需系統管理員權限並啟用 SeProfileSingleProcessPrivilege。純本機、無第三方相依。
/// </summary>
public enum MemOp { EmptyWorkingSets, FlushModified, PurgeStandby, PurgeLowPriorityStandby, DeepClean }

/// <summary>實體記憶體的真實分佈快照（GB）。Standby＝待命快取，是主要可釋放對象。</summary>
public sealed record MemStats(
    double TotalGB, double AvailGB, double UsedGB, int LoadPercent,
    double StandbyGB, double ModifiedGB, double FreeGB,
    double CommitUsedGB, double CommitLimitGB);

public sealed class MemoryService : INotifyPropertyChanged
{
    // ── SystemMemoryListInformation 命令碼（與 RAMMap／EmptyStandbyList 相同）──
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryEmptyWorkingSets = 2;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    private static readonly long PageSize = Environment.SystemPageSize;
    private const double GB = 1024.0 * 1024.0 * 1024.0;

    private bool _privileged;

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // 讀取實體記憶體真實分佈：總量／可用／負載取自 GlobalMemoryStatusEx；
    // 待命（各優先級加總）／已修改／可用頁數取自 SystemMemoryListInformation。
    public MemStats ReadStats()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double total = 0, avail = 0, commitLimit = 0, commitUsed = 0;
        int load = 0;
        if (GlobalMemoryStatusEx(ref m))
        {
            total = m.ullTotalPhys / GB;
            avail = m.ullAvailPhys / GB;
            load = (int)m.dwMemoryLoad;
            commitLimit = m.ullTotalPageFile / GB;
            commitUsed = (m.ullTotalPageFile - m.ullAvailPageFile) / GB;
        }

        double standby = 0, modified = 0, free = 0;
        if (TryQueryMemoryList(out var info))
        {
            long standbyPages = 0;
            for (int i = 0; i < 8; i++) standbyPages += info.PageCountByPriority[i];
            standby = standbyPages * PageSize / GB;
            modified = (info.ModifiedPageCount + info.ModifiedNoWritePageCount) * PageSize / GB;
            free = (info.FreePageCount + info.ZeroPageCount) * PageSize / GB;
        }

        double used = Math.Max(0, total - avail);
        return new MemStats(total, avail, used, load, standby, modified, free, commitUsed, commitLimit);
    }

    // 執行一項整理操作，量測前後可用／待命變化並回報真實釋放量。
    public (bool ok, string message) Run(MemOp op)
    {
        if (!EnsurePrivilege())
            return (false, "無法啟用 SeProfileSingleProcessPrivilege；請以系統管理員身分執行曦覽。");

        var before = ReadStats();
        try
        {
            switch (op)
            {
                case MemOp.EmptyWorkingSets:        Check(SetList(MemoryEmptyWorkingSets)); break;
                case MemOp.FlushModified:           Check(SetList(MemoryFlushModifiedList)); break;
                case MemOp.PurgeStandby:            Check(SetList(MemoryPurgeStandbyList)); break;
                case MemOp.PurgeLowPriorityStandby: Check(SetList(MemoryPurgeLowPriorityStandbyList)); break;
                case MemOp.DeepClean:
                    Check(SetList(MemoryEmptyWorkingSets));
                    Check(SetList(MemoryFlushModifiedList));
                    Check(SetList(MemoryPurgeStandbyList));
                    break;
            }
        }
        catch (Exception ex)
        {
            return (false, "操作失敗：" + ex.Message);
        }

        Thread.Sleep(350);   // 讓系統完成頁面搬移後再量測
        var after = ReadStats();
        double freedAvail = after.AvailGB - before.AvailGB;
        double droppedStandby = before.StandbyGB - after.StandbyGB;

        string name = op switch
        {
            MemOp.EmptyWorkingSets => "清空工作集",
            MemOp.FlushModified => "刷新已修改頁面",
            MemOp.PurgeStandby => "清除待命快取",
            MemOp.PurgeLowPriorityStandby => "清除低優先待命",
            _ => "深度整理",
        };
        string msg = $"{name}完成 ・ 可用 {(freedAvail >= 0 ? "+" : "")}{freedAvail:0.00} GB" +
                     $"（待命快取 {(droppedStandby >= 0 ? "-" : "+")}{Math.Abs(droppedStandby):0.00} GB）" +
                     $" ・ 目前可用 {after.AvailGB:0.00} / {after.TotalGB:0.00} GB";
        Status = msg;
        return (true, msg);
    }

    private static void Check(int ntstatus)
    {
        if (ntstatus == 0) return;
        if ((uint)ntstatus == 0xC0000061) throw new InvalidOperationException("權限不足（需系統管理員）。");
        throw new InvalidOperationException($"NTSTATUS 0x{(uint)ntstatus:X8}");
    }

    private static int SetList(int command)
    {
        int cmd = command;
        return NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
    }

    private static bool TryQueryMemoryList(out SYSTEM_MEMORY_LIST_INFORMATION info)
    {
        info = default;
        int size = Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQuerySystemInformation(SystemMemoryListInformation, buf, size, out _) != 0) return false;
            info = Marshal.PtrToStructure<SYSTEM_MEMORY_LIST_INFORMATION>(buf);
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // 啟用清單操作所需的 SeProfileSingleProcessPrivilege（僅需一次）。
    private bool EnsurePrivilege()
    {
        if (_privileged) return true;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out LUID luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            _privileged = Marshal.GetLastWin32Error() == 0;
            return _privileged;
        }
        finally { CloseHandle(token); }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufLen, IntPtr prevState, IntPtr retLen);

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x08;
    private const int SE_PRIVILEGE_ENABLED = 0x02;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public int Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID Luid; public int Attributes; }

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
