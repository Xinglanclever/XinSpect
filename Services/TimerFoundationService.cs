using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>一列計時器地基資訊。</summary>
public sealed class TimerFoundationRow
{
    public TimerFoundationRow(string key, string value, string note) { Key = key; Value = value; Note = note; }
    public string Key { get; }
    public string Value { get; }
    public string Note { get; }
}

/// <summary>
/// 計時器地基：所有量測的精度來源。QPC 到底走 TSC 還是被「優化教學」用 useplatformclock
/// 強制降級到 HPET；系統計時器解析度被誰拉高；Invariant TSC 支援與否。
/// 這些決定了本程式所有延遲／頻率量測的可信度上限——誠實的工具必須先交代地基。
/// </summary>
public sealed class TimerFoundationService : ObservableObject
{
    [DllImport("ntdll.dll")]
    private static extern uint NtQueryTimerResolution(out uint minRes, out uint maxRes, out uint currentRes);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long frequency);

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<TimerFoundationRow> Rows { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "讀取中…";
        Rows.Clear();
        try
        {
            var rows = await Task.Run(Collect);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Status = "讀取完成。";
        }
        catch (Exception ex)
        {
            Status = "讀取失敗：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<TimerFoundationRow> Collect()
    {
        var rows = new List<TimerFoundationRow>();

        // QPC 頻率與來源判定
        QueryPerformanceFrequency(out long freq);
        var (source, sourceNote) = QpcSource();
        rows.Add(new TimerFoundationRow("QPC 頻率", $"{freq:N0} Hz", ""));
        rows.Add(new TimerFoundationRow("QPC 時間源", source, sourceNote));

        // 計時器解析度（NtQueryTimerResolution，單位 100 ns）
        var (cur, min, max) = QueryTimerResolution();
        rows.Add(new TimerFoundationRow("系統計時器解析度（目前/最小/最大）",
            $"{cur / 10000.0:0.00} / {min / 10000.0:0.00} / {max / 10000.0:0.00} ms",
            cur <= 1000 ? "有程式把計時器拉到 1 ms 以下（增加耗電）" : ""));

        // Invariant TSC（CPUID 0x80000007 EDX bit 8）
        bool invariant = CpuIdDecoder2.InvariantTscSupported();
        rows.Add(new TimerFoundationRow("Invariant TSC", invariant ? "支援（頻率不隨 C-state 變動）" : "不支援（計時精度受限）", ""));

        return rows;
    }

    private (string Source, string Note) QpcSource()
    {
        // useplatformclock=1 強制 HPET、useplatformtick=1 強制 PM timer——「優化教學」常見的降級設定
        bool platformClock = ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "useplatformclock") == 1;
        bool platformTick = ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "useplatformtick") == 1;
        if (platformClock) return ("被強制降級（HPET）", "⚠ useplatformclock 已設為 1：QPC 走 HPET，延遲量測精度打折。這多半來自「優化教學」，建議移除。");
        if (platformTick) return ("被強制降級（PM timer）", "⚠ useplatformtick 已設為 1：QPC 走 ACPI PM timer，精度較差。");
        return ("預設（TSC 或系統最佳選擇）", "");
    }

    private static int ReadRegDword(string path, string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int v ? v : 0;
        }
        catch { return 0; }
    }

    private static (uint Current, uint Min, uint Max) QueryTimerResolution()
    {
        try
        {
            NtQueryTimerResolution(out uint min, out uint max, out uint cur);
            return (cur, min, max);
        }
        catch { return (0, 0, 0); }
    }
}

/// <summary>CPUID 相關純函式（單元測試涵蓋）。</summary>
public static class CpuIdDecoder2
{
    /// <summary>leaf 0x80000007 EDX bit 8 ＝ Invariant TSC 支援。</summary>
    public static bool InvariantTscSupported()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return false;
        var r = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000007), 0);
        return ((uint)r.Edx & 0x100) != 0;
    }
}
