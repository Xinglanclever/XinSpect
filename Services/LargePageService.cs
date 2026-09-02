using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 大頁與位址轉換成本：先看這台機器現在到底配不配得出大頁，再用同一個存取樣式量出走表的代價。
/// </summary>
/// <remarks>
/// 為什麼值得單獨一張卡：4 KB 頁的 TLB 覆蓋範圍很小（第二層 TLB 通常一千多個項目，
/// 也就是幾 MB），資料集一大就每次存取都要走一次分頁表。那個成本不會出現在任何一張
/// 「記憶體使用量」的圖上，只會表現為「明明頻寬夠、延遲卻很高」。
/// <para>
/// 量法刻意做成 A／B：<b>同一份亂序指標鏈、同樣大小的工作集</b>，只有頁面大小不同。
/// 唯一的變數是頁面大小，兩者的差就是位址轉換的成本——不必動用任何效能計數器，
/// 也不必相信廠商公布的 TLB 規格。
/// </para>
/// <para>
/// 界線：本程式只在<b>自己的行程權杖</b>裡啟用已經授與的 SeLockMemoryPrivilege（行程內、可逆），
/// 絕不去改本機安全性政策——授不授與這項權限是使用者自己的決定。
/// </para>
/// </remarks>
public sealed class LargePageService : ObservableObject
{
    [DllImport("kernel32.dll")] private static extern nuint GetLargePageMinimum();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr addr, nuint size, uint type);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr prevState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool PrivilegeCheck(IntPtr token, ref PrivilegeSet set, out bool result);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrivilegeSet { public uint Count; public uint Control; public long Luid; public uint Attributes; }

    private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemLargePages = 0x20000000, MemRelease = 0x4000;
    private const uint PageReadWrite = 0x04;

    /// <summary>工作集大小：要遠超過 4 KB 頁的 TLB 覆蓋範圍，也要超過 L3，兩邊才都落在 DRAM 上。</summary>
    private const long WorkingSet = 256L * 1024 * 1024;

    /// <summary>指標鏈的節點間距（位元組）：一個快取行，避免同一行裡連續命中。</summary>
    private const int Stride = 64;

    /// <summary>追逐步數。</summary>
    private const int Steps = 4_000_000;

    // ── 對外狀態 ──────────────────────────────────────────────────────────

    private LargePageFacts _facts = new() { PrivilegeHeld = false, LargePageMinimum = 0, AllocationOk = false };
    public LargePageFacts Facts { get => _facts; private set => SetProperty(ref _facts, value); }

    private LargePageVerdict _verdict = new()
    {
        Headline = "尚未檢查", Severity = Severity.Neutral,
        Detail = "第一次進入本頁時會檢查權限與配置能力（不做任何量測）。",
    };
    public LargePageVerdict Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    public string PrivilegeText => _facts.PrivilegeHeld ? "已握有（行程內啟用）" : "未握有";
    public string SizeText => LargePageDecoder.SizeText(_facts.LargePageMinimum);
    public string AllocText => _facts.AllocationOk
        ? $"可以配置（試配 {LargePageDecoder.SizeText(WorkingSet)} 成功）"
        : LargePageDecoder.ErrorText(_facts.AllocationError);
    public string SmallText => _facts.SmallPageNs is { } v ? $"{v:0.0} ns" : "—";
    public string LargeText => _facts.LargePageNs is { } v ? $"{v:0.0} ns" : "—";

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanMeasure)); }
    }

    public bool CanMeasure => !_busy && _facts.AllocationOk;

    private string _progress = "";
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    private bool _checked;

    /// <summary>第一次進頁時檢查環境（權限、大頁單位、試配一塊）。不做量測。</summary>
    public void EnsureChecked()
    {
        if (_checked) return;
        _checked = true;
        _ = Task.Run(Probe).ContinueWith(t => Publish(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>使用者按下才跑：4 KB 頁與大頁各一次指標追逐。</summary>
    public void Measure()
    {
        if (_busy || !_facts.AllocationOk) return;
        IsBusy = true;
        _ = Task.Run(() => Chase(_facts, p => Progress = p))
            .ContinueWith(t => { Publish(t.Result); Progress = ""; IsBusy = false; },
                          TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Publish(LargePageFacts f)
    {
        Facts = f;
        Verdict = LargePageDecoder.Judge(f);
        OnPropertyChanged(nameof(PrivilegeText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(AllocText));
        OnPropertyChanged(nameof(SmallText));
        OnPropertyChanged(nameof(LargeText));
        OnPropertyChanged(nameof(CanMeasure));
    }

    // ── 環境檢查 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 檢查權限與配置能力。權限只在<b>本行程的權杖</b>裡啟用（可逆、不動系統政策）；
    /// 試配之後立刻釋放，不留住任何實體記憶體。
    /// </summary>
    private static LargePageFacts Probe()
    {
        long min = 0;
        try { min = (long)GetLargePageMinimum(); }
        catch (Exception ex) { Diag.Swallow("LargePageService.GetLargePageMinimum", ex, "大頁單位讀不到。"); }

        bool held = TryEnablePrivilege();
        if (!held || min <= 0)
            return new LargePageFacts
            {
                PrivilegeHeld = held, LargePageMinimum = min,
                AllocationOk = false, AllocationError = held ? 0 : 1314,
            };

        nuint size = RoundUp(WorkingSet, min);
        IntPtr p = VirtualAlloc(IntPtr.Zero, size, MemCommit | MemReserve | MemLargePages, PageReadWrite);
        int err = p == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (p != IntPtr.Zero) VirtualFree(p, 0, MemRelease);

        return new LargePageFacts
        {
            PrivilegeHeld = true, LargePageMinimum = min,
            AllocationOk = p != IntPtr.Zero, AllocationError = err,
        };
    }

    /// <summary>在本行程權杖裡啟用 SeLockMemoryPrivilege，並回報最後是否真的握著它。</summary>
    private static bool TryEnablePrivilege()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out token))
                return false;
            if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out long luid)) return false;

            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            // AdjustTokenPrivileges 即使沒有授與也會回 true，所以一定要再問一次實際狀態
            var set = new PrivilegeSet { Count = 1, Control = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return PrivilegeCheck(token, ref set, out bool ok) && ok;
        }
        catch (Exception ex)
        {
            Diag.Swallow("LargePageService.TryEnablePrivilege", ex, "大頁權限檢查失敗，本卡片視為未握有。");
            return false;
        }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    private static nuint RoundUp(long bytes, long unit) => (nuint)((bytes + unit - 1) / unit * unit);

    // ── 量測 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 兩次指標追逐：4 KB 頁與大頁各一次。<b>同一份亂序鏈</b>（同一個種子），同樣的工作集大小與步數，
    /// 唯一的差別是頁面大小。
    /// </summary>
    private static unsafe LargePageFacts Chase(LargePageFacts f, Action<string> progress)
    {
        nuint size = RoundUp(WorkingSet, Math.Max(f.LargePageMinimum, 4096));
        int nodes = (int)(size / Stride);

        // 亂序鏈的順序先算好，兩邊共用，才不會變成在比兩份不同的存取樣式
        progress("建立亂序指標鏈…");
        var order = new int[nodes];
        for (int i = 0; i < nodes; i++) order[i] = i;
        var rnd = new Random(20260902);
        for (int i = nodes - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        double? small = null, large = null;
        try
        {
            progress("量 4 KB 頁…");
            small = RunOne(size, order, useLargePages: false);
            progress("量大頁…");
            large = RunOne(size, order, useLargePages: true);
        }
        catch (Exception ex)
        {
            Diag.Swallow("LargePageService.Chase", ex, "指標追逐量不到；上方的環境檢查結果仍然有效。");
        }

        return new LargePageFacts
        {
            PrivilegeHeld = f.PrivilegeHeld,
            LargePageMinimum = f.LargePageMinimum,
            AllocationOk = f.AllocationOk,
            AllocationError = f.AllocationError,
            SmallPageNs = small,
            LargePageNs = large,
        };
    }

    /// <summary>配置一塊、串好鏈、追逐 <see cref="Steps"/> 步，回傳每次存取的平均奈秒；失敗回 null。</summary>
    private static unsafe double? RunOne(nuint size, int[] order, bool useLargePages)
    {
        uint type = MemCommit | MemReserve | (useLargePages ? MemLargePages : 0);
        IntPtr mem = VirtualAlloc(IntPtr.Zero, size, type, PageReadWrite);
        if (mem == IntPtr.Zero) return null;
        try
        {
            byte* b = (byte*)mem;
            int nodes = order.Length;

            // order[k] → order[k+1]：每個節點存下一個節點的位元組位移
            for (int k = 0; k < nodes; k++)
            {
                long from = (long)order[k] * Stride;
                long to = (long)order[(k + 1) % nodes] * Stride;
                *(ulong*)(b + from) = (ulong)to;
            }

            // 預熱：把整塊走一遍，讓分頁真的落地（大頁本來就落地，小頁要逐頁觸碰）
            ulong off = 0;
            for (int i = 0; i < nodes; i++) off = *(ulong*)(b + off);

            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < Steps; i++) off = *(ulong*)(b + off);
            long t1 = Stopwatch.GetTimestamp();

            // 讓編譯器不能把整個迴圈丟掉
            if (off == ulong.MaxValue) Diag.Swallow("LargePageService.RunOne", null, "不可能發生的位移值。");

            return (t1 - t0) * 1_000_000_000.0 / Stopwatch.Frequency / Steps;
        }
        finally { VirtualFree(mem, 0, MemRelease); }
    }
}

