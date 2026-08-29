using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace XinSpect;

/// <summary>歷史回放頁的單一指標開關與統計欄。</summary>
public sealed class MetricToggle : ObservableObject
{
    private readonly Action _changed;

    public MetricToggle(int index, bool on, Action changed)
    {
        Index = index;
        _on = on;
        _changed = changed;
        var c = (Color)ColorConverter.ConvertFromString(HistoryMetrics.Colors[index])!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        Color = b;
    }

    public int Index { get; }
    public string Title => HistoryMetrics.Titles[Index];
    public string Unit => HistoryMetrics.Units[Index];
    public Brush Color { get; }

    private bool _on;
    public bool IsOn { get => _on; set { if (SetProperty(ref _on, value)) _changed(); } }

    private string _min = "—", _avg = "—", _max = "—", _p95 = "—";
    public string MinText { get => _min; private set => SetProperty(ref _min, value); }
    public string AvgText { get => _avg; private set => SetProperty(ref _avg, value); }
    public string MaxText { get => _max; private set => SetProperty(ref _max, value); }
    public string P95Text { get => _p95; private set => SetProperty(ref _p95, value); }

    /// <summary>滿刻度提示（多指標疊圖時，縱軸為百分比，實際上限列在此）。</summary>
    private string _scale = "";
    public string ScaleText { get => _scale; set => SetProperty(ref _scale, value); }

    public void SetStats(double min, double avg, double max, double p95)
    {
        string F(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + Unit;
        MinText = F(min); AvgText = F(avg); MaxText = F(max); P95Text = F(p95);
    }

    public void ClearStats() { MinText = MaxText = AvgText = P95Text = "—"; }
}
/// <summary>
/// 歷史回放的檢視模型：管理時間窗（區間預設、縮放、平移、自動跟隨）、查詢歷史倉、
/// 計算每項指標的最小／平均／最大／P95，並把結果交給時間軸圖繪製。
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    /// <summary>區間預設值（與 <see cref="RangeNames"/> 同序）。</summary>
    private static readonly TimeSpan[] Spans =
    [
        TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6),
        TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30),
    ];

    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxSpan = TimeSpan.FromDays(120);

    private readonly HistoryStore _store;
    private readonly EventsService _events;
    private readonly SettingsService _settings;

    private DateTime _from, _to;      // 皆為 UTC

    /// <summary>時間窗或資料變動，檢視據此重畫圖。</summary>
    public event Action? Changed;

    public HistoryViewModel(HistoryStore store, EventsService events, SettingsService settings)
    {
        _store = store;
        _events = events;
        _settings = settings;

        // 預設顯示處理器負載與溫度：最常一起看的兩條
        Metrics = new ObservableCollection<MetricToggle>(
            Enumerable.Range(0, HistoryMetrics.Count).Select(
                i => new MetricToggle(i, i is HistoryMetrics.CpuLoad or HistoryMetrics.CpuTemp, OnToggle)));

        _to = DateTime.UtcNow;
        _from = _to - Spans[1];
        _rangeIndex = 1;
    }

    public ObservableCollection<MetricToggle> Metrics { get; }
    public static string[] RangeNames => ["10 分鐘", "1 小時", "6 小時", "24 小時", "7 天", "30 天"];

    public HistorySeries Series { get; private set; } = HistorySeries.Empty;
    public IReadOnlyList<TimelineEvent> Markers { get; private set; } = [];

    /// <summary>各指標是否顯示（供時間軸圖直接讀取）。</summary>
    public bool[] Active { get; } = new bool[HistoryMetrics.Count];

    public DateTime FromUtc => _from;
    public DateTime ToUtc => _to;

    private int _rangeIndex;
    /// <summary>區間預設下拉：選定後回到「最近 N」並恢復自動跟隨。</summary>
    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            int v = Math.Clamp(value, 0, Spans.Length - 1);
            if (!SetProperty(ref _rangeIndex, v)) return;
            Follow = true;
            _to = DateTime.UtcNow;
            _from = _to - Spans[v];
            Reload();
        }
    }

    private bool _follow = true;
    /// <summary>自動跟隨：時間窗右緣固定在現在，隨心跳持續右移。</summary>
    public bool Follow { get => _follow; set { if (SetProperty(ref _follow, value) && value) Reset(); } }

    // ── 顯示文字 ──────────────────────────────────────────────────────────

    public string RangeText
    {
        get
        {
            var f = _from.ToLocalTime();
            var t = _to.ToLocalTime();
            string fmt = (_to - _from).TotalDays >= 1 ? "MM-dd HH:mm" : "HH:mm:ss";
            return $"{f:yyyy-MM-dd HH:mm:ss} → {t.ToString(fmt)}　（{SpanText(_to - _from)}）";
        }
    }

    public string TierText => Series.Count == 0 ? "無資料"
        : Series.SecondLevel ? "秒級原始取樣" : "分鐘級彙整（帶狀為每分鐘極值範圍）";

    public string CountText => $"{Series.Count} 個資料點";

    public string StoreText
    {
        get
        {
            string oldest = _store.OldestUtc is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
            return $"已保留 {_store.MinuteCount} 筆分鐘紀錄（{_store.SizeText}），最早 {oldest}；"
                 + $"秒級近況 {_store.SecondCount} 點，保留 {_store.RetentionDays} 天";
        }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private static string SpanText(TimeSpan s)
        => s.TotalMinutes < 90 ? $"{s.TotalMinutes:0.#} 分鐘"
         : s.TotalHours < 48 ? $"{s.TotalHours:0.#} 小時"
         : $"{s.TotalDays:0.#} 天";

    // ── 時間窗操作 ────────────────────────────────────────────────────────

    private void OnToggle()
    {
        foreach (var m in Metrics) Active[m.Index] = m.IsOn;
        Changed?.Invoke();
    }

    /// <summary>重新查詢並更新統計。</summary>
    public void Reload()
    {
        foreach (var m in Metrics) Active[m.Index] = m.IsOn;
        Series = _store.Query(_from, _to);
        Markers = _events.InRange(_from, _to);

        foreach (var m in Metrics)
        {
            // 沒有資料點、或該指標整段皆無讀值（本機沒有這顆感測器）時一律留白，
            // 不把「沒讀到」的 0 當成量測結果寫進統計欄。
            if (Series.Count == 0 || !Series.HasData(m.Index)) { m.ClearStats(); continue; }
            var (mn, avg, mx, p95) = Series.Summarize(m.Index);
            m.SetStats(mn, avg, mx, p95);
        }

        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(TierText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(StoreText));
        Changed?.Invoke();
    }

    /// <summary>每一拍由檢視呼叫：自動跟隨時把時間窗右緣推到現在。</summary>
    public void Tick()
    {
        if (!_follow) return;
        var span = _to - _from;
        _to = DateTime.UtcNow;
        _from = _to - span;
        Reload();
    }

    /// <summary>回到「最近 N」並恢復自動跟隨。</summary>
    public void Reset()
    {
        _follow = true;
        OnPropertyChanged(nameof(Follow));
        _to = DateTime.UtcNow;
        _from = _to - Spans[_rangeIndex];
        Reload();
    }

    /// <summary>以錨點為中心縮放時間窗（滾輪）。</summary>
    public void Zoom(double factor, double anchor)
    {
        var span = _to - _from;
        var anchorTime = _from + TimeSpan.FromTicks((long)(span.Ticks * Math.Clamp(anchor, 0, 1)));
        var next = TimeSpan.FromTicks((long)(span.Ticks * factor));
        if (next < MinSpan) next = MinSpan;
        if (next > MaxSpan) next = MaxSpan;

        _from = anchorTime - TimeSpan.FromTicks((long)(next.Ticks * Math.Clamp(anchor, 0, 1)));
        _to = _from + next;
        ClampToNow();
        Reload();
    }

    /// <summary>以時間窗寬度為單位平移（拖曳）。</summary>
    public void Pan(double fraction)
    {
        var span = _to - _from;
        var d = TimeSpan.FromTicks((long)(span.Ticks * fraction));
        _from += d;
        _to += d;
        ClampToNow();
        Reload();
    }

    // 不允許看到未來；右緣貼齊現在時視為仍在跟隨。
    private void ClampToNow()
    {
        var now = DateTime.UtcNow;
        if (_to > now)
        {
            var span = _to - _from;
            _to = now;
            _from = _to - span;
        }
        bool atEdge = (now - _to).TotalSeconds <= 3;
        if (_follow != atEdge)
        {
            _follow = atEdge;
            OnPropertyChanged(nameof(Follow));
        }
    }

    /// <summary>跳到某個時刻（事件時間軸點擊事件時使用），以該時刻為中心開一段視窗。</summary>
    public void JumpTo(DateTime utc, TimeSpan? span = null)
    {
        var s = span ?? TimeSpan.FromMinutes(30);
        _follow = false;
        OnPropertyChanged(nameof(Follow));
        _from = utc - TimeSpan.FromTicks(s.Ticks / 2);
        _to = _from + s;
        ClampToNow();
        Reload();
    }

    // ── 匯出 ──────────────────────────────────────────────────────────────

    /// <summary>把目前時間窗的資料匯出為 CSV（欄位隨資料粒度而異），並開啟所在資料夾。</summary>
    public void ExportCsv()
    {
        if (Series.Count == 0) { StatusText = "此區間沒有資料可匯出"; return; }
        try
        {
            Directory.CreateDirectory(_settings.LogFolder);
            string name = $"XinSpect_歷史_{_from.ToLocalTime():yyyyMMdd_HHmm}-{_to.ToLocalTime():yyyyMMdd_HHmm}.csv";
            string path = Path.Combine(_settings.LogFolder, name);

            var sb = new StringBuilder();
            sb.Append("時間");
            for (int m = 0; m < HistoryMetrics.Count; m++)
            {
                string head = $"{HistoryMetrics.Titles[m]}({HistoryMetrics.Units[m]})";
                if (Series.SecondLevel) sb.Append(',').Append(head);
                else sb.Append(",平均 ").Append(head).Append(",最小 ").Append(head).Append(",最大 ").Append(head);
            }
            sb.AppendLine();

            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < Series.Count; i++)
            {
                sb.Append(Series.Times[i].ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                for (int m = 0; m < HistoryMetrics.Count; m++)
                {
                    sb.Append(',').Append(Series.A(i, m).ToString("0.##", inv));
                    if (Series.SecondLevel) continue;
                    sb.Append(',').Append(Series.L(i, m).ToString("0.##", inv));
                    sb.Append(',').Append(Series.H(i, m).ToString("0.##", inv));
                }
                sb.AppendLine();
            }

            // UTF-8 BOM：Excel 才能正確辨識中文表頭
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            StatusText = $"已匯出 {Series.Count} 列：{path}";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }
        catch (Exception ex) { StatusText = "匯出失敗：" + ex.Message; }
    }

    /// <summary>全選／全不選指標。</summary>
    public void SetAllMetrics(bool on)
    {
        foreach (var m in Metrics) m.IsOn = on;
    }
}





