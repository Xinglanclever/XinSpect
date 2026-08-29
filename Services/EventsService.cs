using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;

namespace XinSpect;

/// <summary>事件類別（決定時間軸上的圖示、顏色與篩選分組）。</summary>
public enum EventKind
{
    /// <summary>應用程式啟動／關閉，作為歷史圖上的工作階段標記。</summary>
    App,
    /// <summary>溫度／負載警示（鏡射自警示服務）。</summary>
    Alert,
    /// <summary>偵測到熱降頻。</summary>
    Throttle,
    /// <summary>磁碟壽命或健康狀態變化。</summary>
    Smart,
    /// <summary>藍屏傾印檔。</summary>
    Bsod,
    /// <summary>跑分／烤機。</summary>
    Bench,
    /// <summary>超頻、風扇或場景設定的變更。</summary>
    Tune,
}

/// <summary>時間軸上的一筆事件。時間為本機時間（顯示用），歷史圖標記則取 <see cref="TimeUtc"/>。</summary>
public sealed class TimelineEvent
{
    public required DateTime Time { get; init; }
    public required EventKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Neutral;

    public DateTime TimeUtc => Time.ToUniversalTime();
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string DayText => Time.ToString("MM-dd");
    public string ClockText => Time.ToString("HH:mm:ss");

    public string KindText => Kind switch
    {
        EventKind.App => "工作階段",
        EventKind.Alert => "警示",
        EventKind.Throttle => "降頻",
        EventKind.Smart => "磁碟",
        EventKind.Bsod => "藍屏",
        EventKind.Bench => "測試",
        EventKind.Tune => "調校",
        _ => "其他",
    };

    /// <summary>相對於現在的口語時間（「3 分鐘前」）。</summary>
    public string AgoText
    {
        get
        {
            var d = DateTime.Now - Time;
            if (d.TotalSeconds < 0) return "剛剛";
            if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0} 秒前";
            if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0} 分鐘前";
            if (d.TotalHours < 24) return $"{d.TotalHours:0} 小時前";
            return $"{d.TotalDays:0} 天前";
        }
    }
}
/// <summary>
/// 事件時間軸：把散落各處的「值得記一筆」彙整成單一時序，並落地到
/// %APPDATA%\XinSpect\events.json，重開機後仍看得到。
/// </summary>
/// <remarks>
/// 來源有四：警示服務（鏡射新增的溫度／負載警示）、熱降頻偵測（本服務自行判斷）、
/// 磁碟壽命變化（與上次觀測值比對）、藍屏傾印（自傾印檔匯入，以檔名去重）。
/// 其餘模組可直接呼叫 <see cref="Add"/> 記錄調校與測試事件。
/// </remarks>
public sealed class EventsService : ObservableObject
{
    private const int MaxEvents = 800;
    private const double ThrottleTempC = 95;      // 熱降頻判定：封裝溫度門檻
    private const double ThrottleRatio = 0.85;    // 且頻率低於本機觀測上限的比例
    private static readonly TimeSpan ThrottleCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);

    private readonly string _file;
    private readonly Dictionary<string, double> _lifeSeen = new();   // 磁碟 → 上次觀測壽命 %
    private readonly HashSet<string> _bsodSeen = new();              // 已匯入的傾印檔名
    private bool _loading;

    private double _refClock;            // 本機觀測到的頻率上限（低溫時取樣）
    private DateTime _lastThrottle = DateTime.MinValue;

    /// <summary>全部事件，最新在前。</summary>
    public ObservableCollection<TimelineEvent> All { get; } = new();

    /// <summary>套用類別與關鍵字篩選後的檢視（畫面繫結此項）。</summary>
    public ICollectionView View { get; }

    public EventsService(string? folder = null)
    {
        string dir = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        _file = Path.Combine(dir, "events.json");
        try { Load(); } catch { /* 事件為附加功能，讀取失敗則從空白開始 */ }
        View = new ListCollectionView(All) { Filter = Pass };
    }

    // ── 篩選 ──────────────────────────────────────────────────────────────

    private int _kindFilter;
    /// <summary>0 = 全部；其餘為 <see cref="EventKind"/> + 1。</summary>
    public int KindFilter { get => _kindFilter; set { if (SetProperty(ref _kindFilter, value)) View.Refresh(); } }

    private string _search = "";
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) View.Refresh(); } }

    private bool Pass(object o)
    {
        if (o is not TimelineEvent e) return false;
        if (_kindFilter > 0 && (int)e.Kind != _kindFilter - 1) return false;
        if (_search.Length == 0) return true;
        return e.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || e.Detail.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    // ── 記錄 ──────────────────────────────────────────────────────────────

    public bool HasEvents => All.Count > 0;
    /// <summary>最近一筆事件的摘要，供總覽頁一行式呈現。</summary>
    public string LatestText => All.Count > 0 ? $"{All[0].ClockText}　{All[0].Title}" : "尚無事件";

    /// <summary>加入一筆事件（同類別同標題在 30 秒內視為重複，直接忽略）。</summary>
    public TimelineEvent? Add(EventKind kind, string title, string detail = "", Severity sev = Severity.Neutral)
        => Add(kind, title, detail, sev, DateTime.Now);

    private TimelineEvent? Add(EventKind kind, string title, string detail, Severity sev, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        foreach (var e in All)
        {
            if (when - e.Time > DedupeWindow) break;                 // 已排到更早的事件
            if (e.Kind == kind && e.Title == title) return null;
        }

        var ev = new TimelineEvent { Time = when, Kind = kind, Title = title, Detail = detail, Severity = sev };
        int i = 0;
        while (i < All.Count && All[i].Time > when) i++;              // 維持時間遞減
        All.Insert(i, ev);
        while (All.Count > MaxEvents) All.RemoveAt(All.Count - 1);

        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(LatestText));
        if (!_loading) Save();
        return ev;
    }

    /// <summary>取出區間內的事件（供歷史圖在時間軸上畫標記）。</summary>
    public List<TimelineEvent> InRange(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<TimelineEvent>();
        foreach (var e in All)
        {
            var t = e.TimeUtc;
            if (t >= fromUtc && t <= toUtc) list.Add(e);
        }
        list.Reverse();      // 時間遞增，方便繪圖
        return list;
    }

    public void NoteAppStart() => Add(EventKind.App, "曦覽啟動", AppInfo.VersionText, Severity.Good);
    public void NoteAppStop() => Add(EventKind.App, "曦覽結束", "", Severity.Neutral);

    public void Clear()
    {
        All.Clear();
        _lifeSeen.Clear();
        _bsodSeen.Clear();
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(LatestText));
        Save();
    }

    // ── 來源一：鏡射警示服務 ──────────────────────────────────────────────

    /// <summary>接上警示服務：其後新增的每筆警示都會同步成一筆時間軸事件。</summary>
    public void AttachAlerts(AlertService alerts)
    {
        ((INotifyCollectionChanged)alerts.Events).CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (var o in e.NewItems)
                if (o is AlertEvent a)
                    Add(EventKind.Alert, a.Message, "溫度／負載警示", a.Severity);
        };
    }

    // ── 來源二：熱降頻偵測 ────────────────────────────────────────────────

    /// <summary>
    /// 依溫度與頻率判定熱降頻：溫度達門檻，且頻率低於本機觀測上限的一定比例。
    /// 純函式，供單元測試驗證門檻。
    /// </summary>
    internal static bool IsThermalThrottling(double? tempC, double clockMHz, double refClockMHz)
        => tempC is double t && t >= ThrottleTempC
           && refClockMHz > 0 && clockMHz > 0
           && clockMHz < refClockMHz * ThrottleRatio;

    // ── 來源三：磁碟壽命變化 ──────────────────────────────────────────────

    /// <summary>由主計時器每拍呼叫：偵測熱降頻與磁碟壽命變化。</summary>
    public void Check(SensorService live)
    {
        try
        {
            // 低溫時的最高頻率視為本機的頻率上限（含超頻後的實際能力）
            double clk = live.CpuClock;
            if (clk > _refClock && (live.CpuTemp ?? 0) < 80) _refClock = clk;

            if (IsThermalThrottling(live.CpuTemp, clk, _refClock)
                && DateTime.Now - _lastThrottle > ThrottleCooldown)
            {
                _lastThrottle = DateTime.Now;
                Add(EventKind.Throttle, "偵測到處理器熱降頻",
                    $"{live.CpuTemp:0} °C 下頻率僅 {clk:0} MHz（本機觀測上限 {_refClock:0} MHz）",
                    Severity.Serious);
            }

            foreach (var d in live.Drives) CheckDriveLife(d);
        }
        catch { /* 偵測失敗不影響心跳 */ }
    }

    private void CheckDriveLife(StorageRow d)
    {
        if (d.RemainingLife is not double life) return;
        double now = Math.Round(life);
        if (!_lifeSeen.TryGetValue(d.Name, out double was))
        {
            _lifeSeen[d.Name] = now;                     // 首次觀測只建立基準，不記事件
            if (!_loading) Save();
            return;
        }
        if (Math.Abs(now - was) < 1) return;

        _lifeSeen[d.Name] = now;
        var sev = now < was ? (now <= 10 ? Severity.Critical : now <= 30 ? Severity.Serious : Severity.Warning)
                            : Severity.Neutral;
        Add(EventKind.Smart, $"{d.Name} 剩餘壽命 {was:0} % → {now:0} %",
            now < was ? "S.M.A.R.T. 回報的剩餘壽命下降" : "S.M.A.R.T. 回報值變動", sev);
    }

    // ── 來源四：藍屏傾印 ──────────────────────────────────────────────────

    /// <summary>把傾印檔清單併入時間軸（以檔名去重，可重複呼叫）。</summary>
    public void ImportBsod(BsodService bsod)
    {
        try
        {
            foreach (var r in bsod.Rows)
            {
                if (!_bsodSeen.Add(r.FileName)) continue;
                Add(EventKind.Bsod, $"藍屏 {r.CodeHex} {r.Name}",
                    $"{r.FileName}　{r.Hint}", Severity.Critical, r.Time);
            }
        }
        catch { /* 傾印匯入失敗不影響其他來源 */ }
    }

    // ── 落地 ──────────────────────────────────────────────────────────────

    private sealed class Row
    {
        public long T { get; set; }
        public int K { get; set; }
        public string? A { get; set; }
        public string? D { get; set; }
        public int S { get; set; }
    }

    private sealed class Persist
    {
        public List<Row> Events { get; set; } = new();
        public Dictionary<string, double> Life { get; set; } = new();
        public List<string> Bsod { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(_file));
        if (p is null) return;

        _loading = true;
        foreach (var r in p.Events)
        {
            if (string.IsNullOrWhiteSpace(r.A)) continue;
            All.Add(new TimelineEvent
            {
                Time = new DateTime(r.T, DateTimeKind.Local),
                Kind = Enum.IsDefined((EventKind)r.K) ? (EventKind)r.K : EventKind.App,
                Title = r.A!,
                Detail = r.D ?? "",
                Severity = Enum.IsDefined((Severity)r.S) ? (Severity)r.S : Severity.Neutral,
            });
        }
        foreach (var kv in p.Life) _lifeSeen[kv.Key] = kv.Value;
        foreach (var f in p.Bsod) _bsodSeen.Add(f);
        _loading = false;
    }

    private void Save()
    {
        try
        {
            var p = new Persist { Life = new(_lifeSeen), Bsod = new(_bsodSeen) };
            foreach (var e in All)
                p.Events.Add(new Row
                {
                    T = e.Time.Ticks, K = (int)e.Kind, A = e.Title, D = e.Detail, S = (int)e.Severity,
                });
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(p, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch { /* 落地失敗僅損失持久性 */ }
    }
}





