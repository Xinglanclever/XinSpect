using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 一個邏輯處理器的完整定址：處理器群組編號 ＋ 群組內位元索引。
/// </summary>
/// <remarks>
/// Windows 的親和性遮罩是 <c>ULONG_PTR</c>，64 位元行程最多只能表示 64 個邏輯處理器；
/// 超過 64 個的機器（雙路 Xeon、Threadripper 等）會被切成多個「處理器群組」，
/// 每組各自從位元 0 重新編號。因此「LP 編號」單獨存在是沒有意義的，必須連群組一起帶。
/// </remarks>
public readonly record struct ProcessorRef(ushort Group, int Index)
{
    /// <summary>此邏輯處理器在其群組內的單位元遮罩。</summary>
    public ulong Mask => Index is >= 0 and < 64 ? 1UL << Index : 0UL;

    /// <summary>顯示用短標籤：單一群組時只寫 LP 編號，多群組時標明群組，否則兩台機器的「LP3」意思不同。</summary>
    public string Label(bool multiGroup) => multiGroup ? $"G{Group}·LP{Index}" : $"LP{Index}";
}

/// <summary>
/// 跨處理器群組的執行緒釘選。
/// </summary>
/// <remarks>
/// 本專案有五處需要「把執行緒釘到指定核心上讀 MSR」（Top-down、頻率真相、MCA、核心延遲、RDT）。
/// 原本一律用 <c>SetThreadAffinityMask(1UL &lt;&lt; lp)</c>，這在超過 64 個邏輯處理器的機器上
/// <b>只能碰到群組 0</b>，其餘核心會被靜默跳過。改用 <c>SetThreadGroupAffinity</c> 之後，
/// 群組可以明確指定，遮罩也只需在群組內有效。
///
/// <b>誠實界線</b>：開發機是單一群組（36 個邏輯處理器），多群組路徑<b>沒有在真實硬體上驗證過</b>，
/// 只驗到「單一群組的行為與改寫前完全一致」以及純函式部分的單元測試。UI 必須照實說明這一點。
/// </remarks>
public static class CpuAffinity
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GroupAffinity
    {
        public ulong Mask;
        public ushort Group;
        public ushort Reserved0, Reserved1, Reserved2;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadGroupAffinity(IntPtr hThread, ref GroupAffinity affinity, out GroupAffinity previous);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);

    private const ushort AllProcessorGroups = 0xFFFF;

    /// <summary>作用中的處理器群組數。取不到時保守回 1。</summary>
    public static int GroupCount
    {
        get { try { ushort n = GetActiveProcessorGroupCount(); return n == 0 ? 1 : n; } catch { return 1; } }
    }

    /// <summary>全機邏輯處理器總數（跨所有群組）。</summary>
    public static int TotalLogicalProcessors
    {
        get
        {
            try { uint n = GetActiveProcessorCount(AllProcessorGroups); return n == 0 ? Environment.ProcessorCount : (int)n; }
            catch { return Environment.ProcessorCount; }
        }
    }

    /// <summary>是否為多群組機器（＞64 邏輯處理器）。多群組路徑未經真實硬體驗證，UI 需據實說明。</summary>
    public static bool IsMultiGroup => GroupCount > 1;

    /// <summary>各群組的邏輯處理器數量。</summary>
    public static int[] GroupSizes()
    {
        int n = GroupCount;
        var sizes = new int[n];
        for (ushort g = 0; g < n; g++)
        {
            try { sizes[g] = (int)GetActiveProcessorCount(g); } catch { sizes[g] = 0; }
        }
        return sizes;
    }

    /// <summary>
    /// 把遮罩展開成群組內的索引清單（低位在前）。純函式。
    /// </summary>
    public static List<int> IndicesFromMask(ulong mask)
    {
        var list = new List<int>();
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) list.Add(i);
        return list;
    }

    /// <summary>
    /// 把「全機第 n 個邏輯處理器」換成群組 ＋ 群組內索引。純函式，供單元測試釘住換算。
    /// </summary>
    /// <remarks>超出總數時回 <c>null</c>：與其回一個看似合法的 (0, n)，不如讓呼叫端明確處理。</remarks>
    public static ProcessorRef? Split(int globalIndex, IReadOnlyList<int> groupSizes)
    {
        if (globalIndex < 0) return null;
        int remaining = globalIndex;
        for (int g = 0; g < groupSizes.Count; g++)
        {
            if (groupSizes[g] <= 0) continue;
            if (remaining < groupSizes[g]) return new ProcessorRef((ushort)g, remaining);
            remaining -= groupSizes[g];
        }
        return null;
    }

    /// <summary>
    /// <see cref="Split"/> 的反向：群組 ＋ 群組內索引 → 全機序號。純函式。
    /// </summary>
    public static int? Global(ProcessorRef p, IReadOnlyList<int> groupSizes)
    {
        if (p.Group >= groupSizes.Count || p.Index < 0 || p.Index >= Math.Max(groupSizes[p.Group], 0)) return null;
        int acc = 0;
        for (int g = 0; g < p.Group; g++) acc += Math.Max(groupSizes[g], 0);
        return acc + p.Index;
    }

    /// <summary>
    /// 全機可用的邏輯處理器清單（含群組）。
    /// </summary>
    /// <remarks>
    /// 單一群組時沿用行程親和性遮罩過濾（尊重使用者用 start /affinity 之類手段限制過的範圍）；
    /// 多群組時 <c>Process.ProcessorAffinity</c> 只描述得了群組 0，故改用各群組的實際處理器數，
    /// 不再把群組 1 以後整批丟掉。
    /// </remarks>
    public static List<ProcessorRef> AllLogicalProcessors()
    {
        var list = new List<ProcessorRef>();
        var sizes = GroupSizes();
        if (sizes.Length <= 1)
        {
            ulong mask;
            try { mask = (ulong)System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity.ToInt64(); }
            catch (Exception ex) { Diag.Swallow("讀取行程親和性", ex, "改以全部邏輯處理器列舉"); mask = ulong.MaxValue; }
            int n = sizes.Length == 1 && sizes[0] > 0 ? sizes[0] : Environment.ProcessorCount;
            for (int i = 0; i < Math.Min(n, 64); i++)
                if ((mask & (1UL << i)) != 0) list.Add(new ProcessorRef(0, i));
            return list;
        }
        for (ushort g = 0; g < sizes.Length; g++)
            for (int i = 0; i < Math.Min(sizes[g], 64); i++)
                list.Add(new ProcessorRef(g, i));
        return list;
    }

    /// <summary>
    /// 把目前執行緒釘到指定的群組與遮罩上，離開 <c>using</c> 範圍時自動還原原本的親和性。
    /// </summary>
    /// <remarks>
    /// 用 <c>SetThreadGroupAffinity</c> 回傳的 <c>previous</c> 還原，而不是自己記一份行程遮罩：
    /// 後者在多群組機器上根本表達不出「原本可以跑在哪些群組」，還原等於把執行緒鎖死在群組 0。
    /// </remarks>
    public static Pin Pinned(ProcessorRef p) => Pinned(p.Group, p.Mask);

    /// <inheritdoc cref="Pinned(ProcessorRef)"/>
    public static Pin Pinned(ushort group, ulong mask)
    {
        if (mask == 0) return new Pin(false, default);
        var want = new GroupAffinity { Mask = mask, Group = group };
        bool ok;
        GroupAffinity prev;
        try { ok = SetThreadGroupAffinity(GetCurrentThread(), ref want, out prev); }
        catch { return new Pin(false, default); }
        return new Pin(ok, prev);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// 以 <c>GetLogicalProcessorInformationEx(RelationProcessorCore)</c> 取實體核心 → 群組 ＋ 邏輯處理器遮罩。
    /// </summary>
    /// <remarks>
    /// 「一核一次」的量測（Top-down、逐核歸因、效能天花板的溫度掃描）都需要這份清單：
    /// 直接列邏輯處理器會把 SMT 兄弟核當成兩顆核，溫度與計數器都會重複計算。
    /// <c>First</c> 是該實體核心的第一個邏輯處理器（釘選用），<c>LpText</c> 是整組 LP 的顯示字串。
    ///
    /// 全部處理器群組都會列出。<paramref name="group0Mask"/> 只在單一群組機器上用來過濾
    /// （<c>Process.ProcessorAffinity</c> 本身表達不了多群組），多群組時一律採用韌體回報的完整遮罩。
    /// 取不到拓撲時回空清單，呼叫端必須據實說明而不是退回猜一個核心數。
    /// </remarks>
    public static List<(int Core, ProcessorRef First, string LpText)> PhysicalCores(bool multiGroup, ulong group0Mask)
    {
        var list = new List<(int, ProcessorRef, string)>();
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, 0, ref len);
        if (len == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return list;

        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return list;
            int off = 0, core = 0;
            while (off + 8 <= (int)len)
            {
                nint rec = buf + off;
                int size = Marshal.ReadInt32(rec + 4);
                if (size <= 0) break;
                nint pl = rec + 8;                                   // PROCESSOR_RELATIONSHIP
                ushort groupCount = (ushort)Marshal.ReadInt16(pl + 22);
                ulong mask = groupCount == 0 ? 0 : (ulong)Marshal.ReadIntPtr(pl + 24).ToInt64();
                ushort group = groupCount == 0 ? (ushort)0 : (ushort)Marshal.ReadInt16(pl + 32);
                if (!multiGroup && group == 0) mask &= group0Mask;    // 只用行程真的能跑的邏輯處理器
                if (mask != 0)
                {
                    var idx = IndicesFromMask(mask);
                    list.Add((core, new ProcessorRef(group, idx[0]),
                              string.Join("／", idx.Select(i => new ProcessorRef(group, i).Label(multiGroup)))));
                }
                core++;
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    /// <summary>釘選的作用範圍。<see cref="Ok"/> 為 false 表示沒釘上，呼叫端必須跳過該核心而不是照讀。</summary>
    public readonly struct Pin : IDisposable
    {
        private readonly GroupAffinity _prev;
        internal Pin(bool ok, GroupAffinity prev) { Ok = ok; _prev = prev; }

        /// <summary>是否成功釘上。</summary>
        public bool Ok { get; }

        public void Dispose()
        {
            if (!Ok) return;
            var prev = _prev;
            try { SetThreadGroupAffinity(GetCurrentThread(), ref prev, out _); } catch { }
        }
    }
}
