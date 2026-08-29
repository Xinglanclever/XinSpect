using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 一次跑分期間的實機條件：溫度與頻率區間。
/// </summary>
/// <remarks>
/// 由每秒脈動於跑分進行中餵入（<see cref="Sample"/>），跑分結束時併入紀錄。
/// 一個分數若不附帶量測當時的溫度與頻率，就無從判斷它是在冷機還是在溫度牆下量到的；
/// 這也是「同一台機器兩次跑分不一樣」最常見的原因。
/// 「沒有溫度感測器」與「溫度 0 °C」是兩件事：沒讀到就完全不提，不以 0 充數。
/// </remarks>
public sealed class BenchConditions
{
    // 頻率上下差達此比例即提醒「期間頻率變動大」；與烤機的降頻門檻同一量級（一成）
    private const double ClockSagRatio = 0.88;

    private double? _minTemp, _maxTemp;
    private double _minClock, _maxClock;
    private int _samples;

    public int SampleCount => _samples;
    public double? MinTempC => _minTemp;
    public double? MaxTempC => _maxTemp;
    public double MinClockMHz => _minClock;
    public double MaxClockMHz => _maxClock;

    /// <summary>頻率上下差超過一成：可能受降頻、電源計劃或背景負載影響。</summary>
    public bool ClockVaried => _samples >= 4 && _minClock > 0 && _minClock < _maxClock * ClockSagRatio;

    public void Reset()
    {
        _minTemp = _maxTemp = null;
        _minClock = _maxClock = 0;
        _samples = 0;
    }

    /// <summary>餵入一拍即時值（沒讀到的項目傳 <c>null</c> 或 0，不會被計入區間）。</summary>
    public void Sample(double? tempC, double clockMHz)
    {
        _samples++;
        if (tempC is double t && double.IsFinite(t))
        {
            if (_minTemp is null || t < _minTemp) _minTemp = t;
            if (_maxTemp is null || t > _maxTemp) _maxTemp = t;
        }
        if (clockMHz > 0 && double.IsFinite(clockMHz))
        {
            if (_minClock <= 0 || clockMHz < _minClock) _minClock = clockMHz;
            if (clockMHz > _maxClock) _maxClock = clockMHz;
        }
    }

    /// <summary>可讀的條件描述；完全沒取到感測值時回傳空字串（呼叫端據此整行略過，而非印出空區間）。</summary>
    public string Text()
    {
        var parts = new List<string>(3);
        if (_maxTemp is double hi)
            parts.Add(_minTemp is double lo && hi - lo >= 1
                ? $"期間溫度 {lo:0}–{hi:0} °C" : $"期間溫度 {hi:0} °C");
        if (_maxClock > 0)
            parts.Add(_maxClock - _minClock >= 50
                ? $"頻率 {_minClock:0}–{_maxClock:0} MHz" : $"頻率 {_maxClock:0} MHz");
        if (parts.Count == 0) return "";
        if (ClockVaried) parts.Add("期間頻率變動逾一成，分數可能受降頻或電源計劃影響");
        return string.Join(" ・ ", parts);
    }
}
/// <summary>
/// 一筆已完成的跑分紀錄。
/// </summary>
/// <remarks>屬性名稱即磁碟 JSON 的欄位名，一經發佈不得改名（否則舊紀錄會讀不回來）。</remarks>
public sealed class BenchRun
{
    /// <summary>項目代號，例如 <c>chess.multi</c>；同代號才是同一項測試。</summary>
    public string Kind { get; set; } = "";
    /// <summary>畫面上的項目名稱，例如「象棋 多執行緒」。</summary>
    public string Title { get; set; } = "";
    /// <summary>設定簽章（執行緒數、秒數…）。設定不同的成績本就不該直接相比，故一併記下。</summary>
    public string Config { get; set; } = "";
    public double Score { get; set; }
    public string Unit { get; set; } = "";
    /// <summary>true 為數值越大越好（吞吐），false 為越小越好（耗時）。</summary>
    public bool HigherIsBetter { get; set; } = true;
    /// <summary>顯示格式（例：<c>#,0</c>、<c>0.0</c>）。</summary>
    public string Format { get; set; } = "#,0";
    public DateTime UtcTime { get; set; }
    /// <summary>量測期間的實機條件（溫度／頻率區間）；沒取到時為空字串。</summary>
    public string Conditions { get; set; } = "";

    public string TimeText => UtcTime.ToLocalTime().ToString("MM-dd HH:mm");
    public string ScoreText => BenchFormat.Value(Score, Format, Unit);
    public bool HasConditions => Conditions.Length > 0;
}
/// <summary>分數的統一格式化（紀錄、統計與畫面共用同一套小數位與單位寫法）。</summary>
internal static class BenchFormat
{
    public static string Value(double v, string? format, string? unit)
    {
        string s = v.ToString(string.IsNullOrEmpty(format) ? "#,0" : format, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(unit) ? s : s + " " + unit;
    }
}
/// <summary>
/// 同項目、同設定的重複量測統計。
/// </summary>
/// <remarks>
/// 跑分的可信度就寫在離散度上：同一台機器連跑三次，數字本來就不會一樣。
/// 只報一個分數會讓人以為它精確到個位數，所以這裡把範圍與離散度一起說明白，
/// 只量過一次時就直說「一次還看不出重複性」，不假裝那個數字有代表性。
/// </remarks>
public readonly record struct BenchStats(int Count, double Min, double Max, double Mean, string Format, string Unit)
{
    public static readonly BenchStats None = new(0, 0, 0, 0, "#,0", "");

    /// <summary>離散度 =（最高 − 最低）／平均，百分比。</summary>
    public double SpreadPercent => Mean > 0 ? (Max - Min) / Mean * 100 : 0;

    /// <summary>重複性評語；只量過一次時為「—」（無從判斷）。</summary>
    public string Repeatability => Count < 2 ? "—"
        : SpreadPercent < 3 ? "重複性良好"
        : SpreadPercent < 8 ? "重複性尚可"
        : "離散偏大，建議關閉背景程式後重測";

    public string Text => Count switch
    {
        0 => "本機尚無同設定的量測紀錄",
        1 => $"本機同設定僅 1 次量測（{F(Mean)}）；跑分本有波動，建議至少測 3 次再下結論",
        _ => $"本機同設定 {Count} 次：平均 {F(Mean)} ・ 範圍 {F(Min)}–{F(Max)}"
             + $" ・ 離散 {SpreadPercent:0.0}%（{Repeatability}）",
    };

    private string F(double v) => BenchFormat.Value(v, Format, Unit);
}
/// <summary>
/// 跑分紀錄簿：本機歷次成績的落地紀錄。
/// </summary>
/// <remarks>
/// 這裡是曦覽唯一承認的「跑分基準」——不內建任何別台機器的參考分數。
/// 內建參考表沒有辦法誠實：同型號的機器換一支散熱膏、換一種電源計劃就差一成，
/// 標成「約略」也只是把猜測寫得像量測。可信的對照對象只有這台機器自己量到的數字，
/// 因此本類別記下每一次成績、當時的設定與溫度／頻率條件，讓比較建立在真實紀錄上。
/// 所有磁碟操作皆降級處理：寫檔失敗只損失持久性，不影響跑分本身。
/// </remarks>
public sealed class BenchLog : ObservableObject
{
    /// <summary>保留筆數上限（超過則丟最舊的）。</summary>
    public const int MaxRuns = 300;
    /// <summary>畫面對照表顯示的筆數。</summary>
    public const int RecentShown = 12;

    private readonly List<BenchRun> _runs = new();
    private readonly string _folder;

    /// <summary>新增或清除紀錄時觸發（畫面據此刷新對照列表）。</summary>
    public event Action? Updated;

    /// <summary>最近的紀錄（新到舊），直接供畫面繫結；集合實例固定，內容就地更新。</summary>
    public ObservableCollection<BenchRun> Recent { get; } = new();

    public BenchLog(string? folder = null)
    {
        _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        try { Load(); } catch { /* 檔案損毀視為無紀錄，重新開始累積 */ }
        RefreshRecent();
    }

    public string Folder => _folder;
    public string FilePath => Path.Combine(_folder, "bench-history.json");
    public int Count => _runs.Count;

    /// <summary>新增一筆成績並落地。NaN／無限大一律拒收（那不是量測結果）。</summary>
    public void Add(BenchRun run)
    {
        if (run is null || string.IsNullOrEmpty(run.Kind) || !double.IsFinite(run.Score)) return;
        _runs.Add(run);
        if (_runs.Count > MaxRuns) _runs.RemoveRange(0, _runs.Count - MaxRuns);
        try { Save(); } catch { /* 落地失敗僅損失持久性 */ }
        RefreshRecent();
        Updated?.Invoke();
    }

    private void RefreshRecent()
    {
        Recent.Clear();
        for (int i = _runs.Count - 1; i >= 0 && Recent.Count < RecentShown; i--) Recent.Add(_runs[i]);
        OnPropertyChanged(nameof(Count));
    }

    /// <summary>同項目、同設定的歷次成績統計。</summary>
    public BenchStats Stats(string kind, string config)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        int n = 0;
        string fmt = "#,0", unit = "";
        foreach (var r in _runs)
        {
            if (r.Kind != kind || r.Config != config) continue;
            n++;
            sum += r.Score;
            if (r.Score < min) min = r.Score;
            if (r.Score > max) max = r.Score;
            fmt = r.Format; unit = r.Unit;
        }
        return n == 0 ? BenchStats.None : new BenchStats(n, min, max, sum / n, fmt, unit);
    }

    /// <summary>
    /// 與同設定上一次成績相比的變化描述。只有一筆時直說是首次量測，不硬湊出比較。
    /// </summary>
    public string DeltaText(string kind, string config)
    {
        BenchRun? last = null, prev = null;
        foreach (var r in _runs)
            if (r.Kind == kind && r.Config == config) { prev = last; last = r; }
        if (last is null) return "";
        if (prev is null || prev.Score <= 0) return "本機首次同設定量測";
        double pct = (last.Score - prev.Score) / prev.Score * 100;
        // 1% 以內視為同一水準：跑分的波動本就有這個量級，硬要分高下是過度解讀
        if (Math.Abs(pct) < 1) return $"與上次同設定相當（差 {Math.Abs(pct):0.0}%）";
        bool better = last.HigherIsBetter ? pct > 0 : pct < 0;
        return $"較上次同設定{(better ? "快" : "慢")} {Math.Abs(pct):0.0}%";
    }

    /// <summary>最近的紀錄（新到舊），依項目代號前綴篩選。</summary>
    public IReadOnlyList<BenchRun> RecentOf(string kindPrefix, int max = 8)
    {
        var list = new List<BenchRun>(Math.Min(max, _runs.Count));
        for (int i = _runs.Count - 1; i >= 0 && list.Count < max; i--)
            if (_runs[i].Kind.StartsWith(kindPrefix, StringComparison.Ordinal)) list.Add(_runs[i]);
        return list;
    }

    /// <summary>清除全部紀錄（設定頁的「清空跑分紀錄」）。</summary>
    public void Clear()
    {
        _runs.Clear();
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        RefreshRecent();
        Updated?.Invoke();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        var list = JsonSerializer.Deserialize<List<BenchRun>>(File.ReadAllText(FilePath));
        if (list is null) return;
        foreach (var r in list)
        {
            if (r is null || string.IsNullOrEmpty(r.Kind) || !double.IsFinite(r.Score)) continue;
            _runs.Add(r);
        }
        if (_runs.Count > MaxRuns) _runs.RemoveRange(0, _runs.Count - MaxRuns);
    }

    private void Save()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_runs));
    }
}
