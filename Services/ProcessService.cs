using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;

namespace XinSpect;

/// <summary>行程排序依據。</summary>
public enum ProcSort { Cpu, Ram, Name, Pid, Threads }

/// <summary>
/// 列舉全部行程並計算即時 CPU%（依 TotalProcessorTime 差值 / 經過時間 / 邏輯處理器數）、
/// 工作集記憶體與執行緒數。維護單一可排序/可篩選的完整清單（仿工作管理員），
/// 並提供「結束工作」（終止行程）。行程存取受限者略過。
/// </summary>
public sealed class ProcessService : ObservableObject
{
    private sealed class Prev { public TimeSpan Cpu; public long Ticks; }

    private readonly Dictionary<int, Prev> _prev = new();
    private readonly Dictionary<int, ProcRow> _pool = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _cpuCount = Math.Max(1, Environment.ProcessorCount);

    /// <summary>完整行程清單（就地維護：新增/移除/更新，供 <see cref="View"/> 排序與篩選）。</summary>
    public ObservableCollection<ProcRow> All { get; } = new();

    /// <summary>供 UI 繫結的可排序/可篩選檢視（即時排序，數值變動自動重排）。</summary>
    public ICollectionView View { get; }

    public ProcessService()
    {
        View = new CollectionViewSource { Source = All }.View;
        if (View is ICollectionViewLiveShaping live)
        {
            live.IsLiveSorting = true;
            live.IsLiveFiltering = true;
            // 會隨時間變動的數值欄位加入即時排序屬性，使排序不需每拍手動重整
            live.LiveSortingProperties.Add(nameof(ProcRow.CpuPercent));
            live.LiveSortingProperties.Add(nameof(ProcRow.RamMB));
            live.LiveSortingProperties.Add(nameof(ProcRow.Threads));
        }
        ApplySort();
    }

    private int _count;
    public int Count { get => _count; private set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(CountText)); } }
    public string CountText => $"{_count} 個行程";

    private double _totalRamMB;
    public string TotalRamText => _totalRamMB >= 1024 ? $"合計工作集 {_totalRamMB / 1024:0.0} GB" : $"合計工作集 {_totalRamMB:0} MB";

    private ProcSort _sort = ProcSort.Cpu;
    /// <summary>排序依據；變更即重建 SortDescriptions。</summary>
    public ProcSort Sort { get => _sort; set { if (SetProperty(ref _sort, value)) { ApplySort(); OnPropertyChanged(nameof(SortIndex)); } } }

    /// <summary>供 ComboBox 以索引雙向繫結排序依據（順序須與下拉項目一致）。</summary>
    public int SortIndex { get => (int)_sort; set { var s = (ProcSort)value; if (_sort != s) Sort = s; } }
    public IReadOnlyList<string> SortChoices { get; } = new[] { "CPU 使用率", "記憶體", "名稱", "PID", "執行緒數" };

    private string _filter = "";
    /// <summary>名稱/PID 篩選字串（不分大小寫）；變更即重整檢視。</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? "")) return;
            View.Filter = string.IsNullOrWhiteSpace(_filter) ? null : PassesFilter;
            View.Refresh();
        }
    }

    private bool PassesFilter(object o)
    {
        if (o is not ProcRow r) return false;
        string f = _filter.Trim();
        return r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
               || r.PidText.Contains(f, StringComparison.Ordinal);
    }

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            switch (_sort)
            {
                case ProcSort.Cpu:
                    View.SortDescriptions.Add(new SortDescription(nameof(ProcRow.CpuPercent), ListSortDirection.Descending));
                    View.SortDescriptions.Add(new SortDescription(nameof(ProcRow.RamMB), ListSortDirection.Descending));
                    break;
                case ProcSort.Ram:
                    View.SortDescriptions.Add(new SortDescription(nameof(ProcRow.RamMB), ListSortDirection.Descending));
                    break;
                case ProcSort.Name:
                    View.SortDescriptions.Add(new SortDescription(nameof(ProcRow.Name), ListSortDirection.Ascending));
                    break;
                case ProcSort.Pid:
                    View.SortDescriptions.Add(new SortDescription(nameof(ProcRow.Pid), ListSortDirection.Ascending));
                    break;
                case ProcSort.Threads:
                    View.SortDescriptions.Add(new SortDescription(nameof(ProcRow.Threads), ListSortDirection.Descending));
                    break;
            }
        }
    }

    // 列舉全部行程並讀取 CPU 時間 / 工作集 / 執行緒數屬於較重的系統呼叫，於背景執行緒進行；
    // 回到 UI 執行緒後才計算百分比並就地更新繫結列（繫結物件的屬性變更通知須在 UI 執行緒）。
    public async Task RefreshAsync()
    {
        var raw = await Task.Run(Enumerate);
        if (raw is not null) Apply(raw);
    }

    /// <summary>同步版本（保留給非 UI 情境）；UI 每秒更新請改用 <see cref="RefreshAsync"/>。</summary>
    public void Refresh()
    {
        var raw = Enumerate();
        if (raw is not null) Apply(raw);
    }

    /// <summary>取得行程主模組完整路徑；存取受限（系統／他人行程）或已結束則回傳 null。按需呼叫，不逐拍列舉以免拖慢更新。</summary>
    public string? PathOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.MainModule?.FileName; }
        catch { return null; }
    }

    /// <summary>
    /// 結束（終止）指定行程。成功回傳 null，失敗回傳錯誤訊息。
    /// 連同子行程一併結束（仿工作管理員「結束工作」）：多行程應用（瀏覽器 / WebView2 等）
    /// 若只終止單一 PID，其子行程仍在，會讓使用者誤以為「結束沒反應」。
    /// 成功後即刻自清單移除該列，提供即時回饋（不必等下一拍列舉）。
    /// </summary>
    public string? EndTask(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);   // 等待實際退出，確認終止確實生效
        }
        catch (ArgumentException) { /* 行程已不存在：視為已結束 */ }
        catch (Exception ex) { return ex.Message; }

        PruneRow(pid);
        return null;
    }

    // 自完整清單與內部狀態即刻移除指定 PID（於 UI 執行緒的「結束工作」後呼叫）。
    private void PruneRow(int pid)
    {
        _prev.Remove(pid);
        if (_pool.Remove(pid, out var row))
        {
            All.Remove(row);
            Count = All.Count;
        }
    }

    private readonly record struct Raw(int Pid, string Name, TimeSpan Cpu, long Ram, int Threads);

    // 背景執行緒：僅蒐集原始資料，不觸碰任何繫結物件
    private static List<Raw>? Enumerate()
    {
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return null; }

        var list = new List<Raw>(procs.Length);
        foreach (var p in procs)
        {
            try
            {
                int pid = p.Id;
                if (pid == 0) continue;
                int threads = 0;
                try { threads = p.Threads.Count; } catch { /* 存取受限：執行緒數留 0 */ }
                list.Add(new Raw(pid, p.ProcessName, p.TotalProcessorTime, p.WorkingSet64, threads));
            }
            catch { /* 存取受限 / 已結束：略過 */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    // UI 執行緒：依前後差值算 CPU%、就地更新/新增/移除列（保留物件實例，讓即時排序穩定）
    private void Apply(List<Raw> raw)
    {
        long now = _clock.ElapsedTicks;
        var seen = new HashSet<int>(raw.Count);
        double totalRam = 0;

        foreach (var r in raw)
        {
            double ramMB = r.Ram / (1024.0 * 1024.0);
            totalRam += ramMB;

            double pct = 0;
            // r.Cpu < pv.Cpu 代表 CPU 時間「倒退」：同一行程不可能發生，必為 PID 被回收給新行程，
            // 此拍不計算百分比（否則會出現虛假的 CPU 尖峰）。
            if (_prev.TryGetValue(r.Pid, out var pv) && pv.Ticks > 0 && r.Cpu >= pv.Cpu)
            {
                double secs = (now - pv.Ticks) / (double)Stopwatch.Frequency;
                if (secs > 0.05)
                    pct = Math.Clamp((r.Cpu - pv.Cpu).TotalSeconds / (secs * _cpuCount) * 100.0, 0, 100);
            }
            _prev[r.Pid] = new Prev { Cpu = r.Cpu, Ticks = now };

            // PID 已被回收給不同名稱的行程：捨棄舊列改建新列，避免沿用過期名稱
            if (_pool.TryGetValue(r.Pid, out var stale) && stale.Name != r.Name)
            {
                _pool.Remove(r.Pid);
                All.Remove(stale);
            }
            if (!_pool.TryGetValue(r.Pid, out var row))
            {
                row = new ProcRow(r.Pid, r.Name);
                _pool[r.Pid] = row;
                All.Add(row);
            }
            row.CpuPercent = pct;
            row.RamMB = ramMB;
            row.Threads = r.Threads;
            seen.Add(r.Pid);
        }

        // 清掉已結束的行程（同步自完整清單移除）
        foreach (var dead in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _prev.Remove(dead);
            if (_pool.Remove(dead, out var drow)) All.Remove(drow);
        }

        _totalRamMB = totalRam;
        OnPropertyChanged(nameof(TotalRamText));
        Count = All.Count;
    }
}
