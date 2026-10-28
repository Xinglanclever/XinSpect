using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 最便宜的兩個系統即時值（U10-MT-0001 的重複取樣來源，第 312 輪）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼不用感測器服務：</b>重複量測要跑好幾輪，每輪都碰 LibreHardwareMonitor／WMI
/// 不只慢，還可能在驅動層卡住（第 263、304 輪都實證過）。這裡選兩個零特權、零驅動、
/// 微秒級的來源：CPU 忙碌比例（<c>GetSystemTimes</c> 的差分）與可用實體記憶體
/// （<c>GlobalMemoryStatusEx</c>）。
/// </para>
/// <para>
/// <b>它們有真的雜訊</b>——這正是要被量到的東西：CPU 使用率的跨度通常遠大於可用記憶體，
/// 重複性事實會把這件事直接寫出來，而不是替它下「穩定／不穩定」的評語。
/// </para>
/// </remarks>
public static class SystemUtilSampler
{
    /// <summary>取樣鍵：CPU 使用率（0–100）；讀不到就是 null，不是 0。</summary>
    /// <remarks>
    /// 名稱刻意<b>不含</b> <c>Key</c>：事實鍵掃描器會把「名字含 Key 的字串常數」當成事實鍵，
    /// 而這兩把是重複性家族的<b>成員名</b>（會被併進 <c>mt.repeat.key.*</c>），不是獨立事實鍵。
    /// </remarks>
    public const string CpuUtilId = "cpu.util";

    /// <summary>取樣鍵：可用實體記憶體（MB）；讀不到就是 null，不是 0。</summary>
    public const string MemoryAvailableId = "mem.available";

    /// <summary>兩次 <c>GetSystemTimes</c> 之間的間隔：太短會讓差分被排程噪音淹掉。</summary>
    public const int DifferentialWindowMs = 120;

    /// <summary>取一輪樣本（會睡 <see cref="DifferentialWindowMs"/> 毫秒）。</summary>
    public static IReadOnlyList<MeasurementRepeatFactsService.RepeatSample> Sample()
    {
        ulong idle0 = 0, total0 = 0, idle1 = 0, total1 = 0;
        bool first = TryReadTimes(out idle0, out total0);
        if (first) Thread.Sleep(DifferentialWindowMs);
        bool second = first && TryReadTimes(out idle1, out total1);

        double? cpu = null;
        if (first && second)
        {
            ulong idle = idle1 - idle0;
            ulong total = total1 - total0;
            if (total > 0 && idle <= total)
                cpu = 100.0 * (total - idle) / total;
        }

        return
        [
            new MeasurementRepeatFactsService.RepeatSample(CpuUtilId, "CPU 使用率", cpu),
            new MeasurementRepeatFactsService.RepeatSample(MemoryAvailableId, "可用實體記憶體", TryReadAvailableMb()),
        ];
    }

    private static bool TryReadTimes(out ulong idle, out ulong total)
    {
        idle = 0;
        total = 0;
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return false;
        ulong i = ToUlong(idleFt), k = ToUlong(kernelFt), u = ToUlong(userFt);
        // 核心時間含閒置時間，所以「總量 = 核心 + 使用者」，忙碌 = 總量 − 閒置。
        idle = i;
        total = k + u;
        return total >= idle;
    }

    private static double? TryReadAvailableMb()
    {
        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref status)) return null;
        return status.ullAvailPhys / 1048576.0;
    }

    private static ulong ToUlong(FileTime ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
