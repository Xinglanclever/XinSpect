using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>風扇曲線上的一個控制點（溫度 → 輸出百分比）。</summary>
public sealed class FanCurvePoint : ObservableObject
{
    private double _temp;
    /// <summary>觸發溫度（°C），夾在 <see cref="FanCurve.TempMin"/>–<see cref="FanCurve.TempMax"/>。</summary>
    public double TempC
    {
        get => _temp;
        set
        {
            if (SetProperty(ref _temp, Math.Clamp(Math.Round(value), FanCurve.TempMin, FanCurve.TempMax)))
                OnPropertyChanged(nameof(Label));
        }
    }

    private double _pct;
    /// <summary>該溫度下的風扇輸出（%）。</summary>
    public double Percent
    {
        get => _pct;
        set
        {
            if (SetProperty(ref _pct, Math.Clamp(Math.Round(value), 0, 100)))
                OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => $"{_temp:0} °C → {_pct:0} %";

    public FanCurvePoint() { }
    public FanCurvePoint(double tempC, double percent) { TempC = tempC; Percent = percent; }
}

/// <summary>曲線的溫度來源。</summary>
public enum FanCurveSource
{
    /// <summary>處理器封裝溫度。</summary>
    Cpu,
    /// <summary>主要顯示卡溫度。</summary>
    Gpu,
    /// <summary>兩者取較高（機殼風扇最實用）。</summary>
    Hotter,
}

/// <summary>
/// 一顆風扇的轉速曲線：溫度來源、控制點與遲滯。點與點之間線性內插，
/// 兩端則維持端點值（低於首點取首點、高於末點取末點）。
/// </summary>
public sealed class FanCurve : ObservableObject
{
    /// <summary>曲線橫軸下限（°C）。</summary>
    public const double TempMin = 20;
    /// <summary>曲線橫軸上限（°C）。</summary>
    public const double TempMax = 100;

    /// <summary>對應風扇的穩定識別（位置 + 名稱），落地時作為鍵。</summary>
    public string Key { get; init; } = "";
    /// <summary>顯示名稱（取自感測器）。</summary>
    public string Name { get; init; } = "";

    private bool _on;
    /// <summary>啟用後由曲線接管此風扇；停用則交還主機板自動控制。</summary>
    public bool Enabled
    {
        get => _on;
        set { if (SetProperty(ref _on, value)) OnPropertyChanged(nameof(StateText)); }
    }

    private FanCurveSource _src = FanCurveSource.Hotter;
    /// <summary>溫度來源。</summary>
    public FanCurveSource Source
    {
        get => _src;
        set
        {
            if (SetProperty(ref _src, value))
            {
                OnPropertyChanged(nameof(SourceIndex));
                OnPropertyChanged(nameof(SourceText));
            }
        }
    }

    /// <summary>供下拉選單繫結的來源索引。</summary>
    public int SourceIndex
    {
        get => (int)_src;
        set { if (value >= 0 && value <= 2) Source = (FanCurveSource)value; }
    }

    /// <summary>下拉選單的來源名稱（靜態，需以 x:Static 繫結）。</summary>
    public static string[] SourceNames { get; } = ["處理器溫度", "顯示卡溫度", "兩者取較高"];

    public string SourceText => SourceNames[(int)_src];
    public string StateText => _on ? "曲線控制中" : "未啟用";

    /// <summary>對應的實體風扇（由服務指派；供 UI 直接顯示轉速與手動滑桿）。</summary>
    public FanControlRow? Fan { get; internal set; }

    private double _liveTemp = double.NaN;
    /// <summary>目前來源溫度（每拍由服務寫入）；<c>NaN</c> 表示暫無讀值。</summary>
    public double LiveTempC
    {
        get => _liveTemp;
        internal set { if (SetProperty(ref _liveTemp, value)) OnPropertyChanged(nameof(TargetText)); }
    }

    /// <summary>「來源溫度 → 曲線輸出」的一行摘要。</summary>
    public string TargetText =>
        double.IsNaN(_liveTemp) ? "等待溫度讀值…" : $"{_liveTemp:0} °C → {Evaluate(_liveTemp):0} %";

    private double _hys = 3;
    /// <summary>
    /// 遲滯（°C）：溫度回落幅度未達此值前不降速，避免在門檻附近來回抽風。
    /// </summary>
    public double Hysteresis
    {
        get => _hys;
        set => SetProperty(ref _hys, Math.Clamp(Math.Round(value), 0, 15));
    }

    /// <summary>控制點；以溫度遞增排列，至少兩點。</summary>
    public ObservableCollection<FanCurvePoint> Points { get; } = [];

    /// <summary>任一點被拖動、新增或移除時觸發（曲線服務據此存檔）。</summary>
    public event Action? Changed;

    public FanCurve()
    {
        Points.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (FanCurvePoint p in e.OldItems) p.PropertyChanged -= Point_Changed;
            if (e.NewItems is not null)
                foreach (FanCurvePoint p in e.NewItems) p.PropertyChanged += Point_Changed;
            Raise();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Enabled) or nameof(Source) or nameof(Hysteresis)) Raise();
        };
    }

    // Label 是 TempC／Percent 的衍生屬性，若一併轉發會讓每次拖動觸發兩次存檔。
    private void Point_Changed(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FanCurvePoint.Label)) return;
        Raise();
    }
    private void Raise() => Changed?.Invoke();

    /// <summary>把控制點依溫度遞增重排（拖動越過鄰點後呼叫）。</summary>
    public void Sort()
    {
        var ordered = Points.OrderBy(p => p.TempC).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int at = Points.IndexOf(ordered[i]);
            if (at != i) Points.Move(at, i);
        }
    }

    /// <summary>
    /// 以線性內插求出該溫度對應的輸出（%）。點數不足時回傳 0；
    /// 溫度低於首點或高於末點時分別取端點值；同溫度的重複點取較高輸出（寧可多轉一點）。
    /// </summary>
    public double Evaluate(double tempC)
    {
        if (Points.Count == 0) return 0;
        if (Points.Count == 1) return Points[0].Percent;

        // 同溫合併為單點（取較高輸出），如此相鄰兩點的溫差必為正，內插不會除以零
        var pts = Points.GroupBy(p => p.TempC)
                        .Select(g => (T: g.Key, P: g.Max(x => x.Percent)))
                        .OrderBy(x => x.T)
                        .ToList();
        if (pts.Count == 1) return pts[0].P;
        if (tempC <= pts[0].T) return pts[0].P;
        if (tempC >= pts[^1].T) return pts[^1].P;

        for (int i = 1; i < pts.Count; i++)
        {
            if (tempC > pts[i].T) continue;
            var (t0, p0) = pts[i - 1];
            var (t1, p1) = pts[i];
            return p0 + (p1 - p0) * (tempC - t0) / (t1 - t0);
        }
        return pts[^1].P;
    }

    /// <summary>以一組 (溫度, 輸出) 取代全部控制點。</summary>
    public void SetPoints(IEnumerable<(double T, double P)> pts)
    {
        Points.Clear();
        foreach (var (t, p) in pts.OrderBy(x => x.T)) Points.Add(new FanCurvePoint(t, p));
        if (Points.Count < 2) Points.Add(new FanCurvePoint(TempMax, 100));
    }

    /// <summary>套用內建曲線樣板（0 靜音、1 均衡、2 效能）。</summary>
    public void LoadPreset(int preset) => SetPoints(FanCurveService.Preset(preset));
}

/// <summary>
/// 風扇曲線的持有者與控制迴圈。每拍讀取感測溫度、算出各風扇目標輸出，
/// 僅在目標明顯變動時才真正寫入硬體（<see cref="FanControlRow.ApplyManual"/>），
/// 並在停用時交還主機板自動控制。曲線落地於 %APPDATA%\XinSpect\fancurves.json。
/// </summary>
public sealed class FanCurveService : ObservableObject
{
    /// <summary>內建樣板名稱。</summary>
    public static string[] PresetNames { get; } = ["靜音", "均衡", "效能"];

    /// <summary>取內建樣板的控制點。</summary>
    public static (double T, double P)[] Preset(int i) => i switch
    {
        0 => [(30, 0), (45, 25), (60, 40), (75, 65), (90, 100)],
        2 => [(30, 40), (45, 60), (60, 80), (75, 95), (90, 100)],
        _ => [(30, 20), (45, 35), (60, 55), (75, 80), (90, 100)],
    };

    private readonly string _file;
    private readonly List<FanControlRow> _fans = [];
    private readonly Dictionary<string, double> _applied = [];   // 上次寫入的輸出
    private readonly Dictionary<string, double> _atTemp = [];    // 寫入當時的溫度（遲滯基準）
    private bool _loading;

    /// <summary>事件時間軸；設定後每次接管／交還都會留下一筆調校紀錄。</summary>
    public EventsService? Events { get; set; }

    /// <summary>所有已知風扇的曲線，順序與感測器回報一致。</summary>
    public ObservableCollection<FanCurve> Curves { get; } = [];

    public FanCurveService(string? folder = null)
    {
        string dir = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        try { Directory.CreateDirectory(dir); } catch { /* 無法建目錄則僅記憶體運作 */ }
        _file = Path.Combine(dir, "fancurves.json");
    }

    /// <summary>是否有可繪製的曲線。</summary>
    public bool HasCurves => Curves.Count > 0;

    /// <summary>是否有任一風扇正由曲線接管。</summary>
    public bool AnyEnabled => Curves.Any(c => c.Enabled);

    private string _status = "尚未啟用曲線控制";
    /// <summary>頁面上的一行狀態摘要。</summary>
    public string StatusText
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// 把感測器回報的可控風扇對進曲線清單：既有的沿用（含落地設定），
    /// 新出現的建立均衡樣板。可重複呼叫（一鍵初始化會重建感測器）。
    /// </summary>
    public void Attach(IReadOnlyList<FanControlRow> fans)
    {
        _fans.Clear();
        _fans.AddRange(fans);
        _applied.Clear();
        _atTemp.Clear();

        var saved = Load();
        var keep = new List<FanCurve>();
        foreach (var f in fans)
        {
            string key = KeyOf(f);
            var curve = Curves.FirstOrDefault(c => c.Key == key);
            if (curve is null)
            {
                curve = new FanCurve { Key = key, Name = f.Name };
                _loading = true;
                if (saved.TryGetValue(key, out var row)) Restore(curve, row);
                else curve.LoadPreset(1);
                _loading = false;
                curve.Changed += OnCurveChanged;
            }
            curve.Fan = f;
            keep.Add(curve);
        }

        Curves.Clear();
        foreach (var c in keep) Curves.Add(c);
        OnPropertyChanged(nameof(HasCurves));
        OnPropertyChanged(nameof(AnyEnabled));
        Describe();
    }

    /// <summary>風扇的穩定識別：位置 + 名稱（同名風扇以位置區分）。</summary>
    private static string KeyOf(FanControlRow f) => $"{f.Location}|{f.Name}";

    private bool _allowStop;
    /// <summary>
    /// 允許曲線把輸出壓到 20 % 以下（含停轉）。預設關閉：多數塔散與機殼風扇
    /// 低於兩成即失速，寧可吵一點也不要在無風下升溫。
    /// </summary>
    public bool AllowStop
    {
        get => _allowStop;
        set { if (SetProperty(ref _allowStop, value)) _applied.Clear(); }
    }

    /// <summary>
    /// 控制迴圈；由 <c>MetricsPump</c> 每拍呼叫。停用的曲線會自動把風扇交還主機板。
    /// </summary>
    public void Tick(SensorService? live)
    {
        if (Curves.Count == 0) return;

        double? cpu = live?.CpuTemp;
        double? gpu = live?.PrimaryGpu?.TempC;
        int active = 0;

        foreach (var c in Curves)
        {
            var fan = c.Fan;
            if (fan is null) continue;

            double? src = c.Source switch
            {
                FanCurveSource.Cpu => cpu,
                FanCurveSource.Gpu => gpu,
                _ => cpu is null ? gpu : gpu is null ? cpu : Math.Max(cpu.Value, gpu.Value),
            };
            c.LiveTempC = src ?? double.NaN;

            // 剛被停用（或風扇消失）：交還自動控制，狀態歸零
            if (!c.Enabled)
            {
                if (_applied.Remove(c.Key))
                {
                    _atTemp.Remove(c.Key);
                    try { fan.RestoreAuto(); } catch { /* 還原失敗不影響其餘風扇 */ }
                    Events?.Add(EventKind.Tune, "風扇曲線已交還自動", c.Name);
                }
                continue;
            }

            if (src is not double temp) continue;
            active++;

            double target = c.Evaluate(temp);
            if (!_allowStop) target = Math.Max(target, 20);

            bool first = !_applied.TryGetValue(c.Key, out double prev);
            if (!first)
            {
                // 遲滯：溫度回落不足時不降速
                if (target < prev && _atTemp.TryGetValue(c.Key, out double t0) && t0 - temp < c.Hysteresis)
                    continue;
                if (Math.Abs(target - prev) < 2) continue;   // 變動太小不打擾硬體
            }

            try { fan.ApplyManual(Math.Clamp(target, fan.MinValue, fan.MaxValue)); }
            catch { continue; }

            _applied[c.Key] = target;
            _atTemp[c.Key] = temp;
            if (first) Events?.Add(EventKind.Tune, "風扇曲線已接管", $"{c.Name}（{temp:0} °C → {target:0} %）");
        }

        Describe(active);
    }

    private bool _lastAny;

    /// <summary>更新狀態摘要；<paramref name="active"/> 為本拍實際取得溫度的風扇數（−1 表示未經迴圈）。</summary>
    private void Describe(int active = -1)
    {
        bool any = AnyEnabled;
        if (any != _lastAny)
        {
            _lastAny = any;
            OnPropertyChanged(nameof(AnyEnabled));
        }

        if (_fans.Count == 0) { StatusText = "未偵測到可控風扇"; return; }
        int on = Curves.Count(c => c.Enabled);
        StatusText = on == 0
            ? $"已載入 {Curves.Count} 條曲線，目前全部交由主機板自動控制"
            : active == 0
                ? $"{on} 顆風扇已啟用曲線，但暫時取不到溫度讀值"
                : $"{on} 顆風扇由曲線接管中" + (_allowStop ? "（允許低速／停轉）" : "（最低 20 %）");
    }

    /// <summary>把同一組樣板套用到全部曲線（不改變啟用狀態）。</summary>
    public void ApplyPresetToAll(int preset)
    {
        _loading = true;
        foreach (var c in Curves) c.LoadPreset(preset);
        _loading = false;
        _applied.Clear();
        _atTemp.Clear();
        Save();
        Events?.Add(EventKind.Tune, "風扇曲線樣板已套用",
            $"{PresetNames[Math.Clamp(preset, 0, PresetNames.Length - 1)]}・{Curves.Count} 顆風扇");
        Describe();
    }

    /// <summary>
    /// 使用者改為手動接管某顆風扇：停用其曲線並忘掉接管狀態，但不交還自動控制
    /// （呼叫端緊接著就會寫入手動值，若在此還原會被 BIOS 立刻蓋掉）。
    /// </summary>
    public void ReleaseFor(FanControlRow fan)
    {
        var c = Curves.FirstOrDefault(x => ReferenceEquals(x.Fan, fan));
        if (c is null) return;
        c.Enabled = false;              // 觸發存檔（Changed）
        _applied.Remove(c.Key);
        _atTemp.Remove(c.Key);
        Describe();
    }

    /// <summary>全部停用並立刻交還主機板自動控制（離開程式與一鍵還原都走這裡）。</summary>
    public void DisableAll()
    {
        foreach (var c in Curves) c.Enabled = false;
        foreach (var f in _fans)
        {
            if (!_applied.ContainsKey(KeyOf(f))) continue;
            try { f.RestoreAuto(); } catch { /* 逐顆嘗試，失敗略過 */ }
        }
        _applied.Clear();
        _atTemp.Clear();
        Describe();
    }

    // ── 落地 ────────────────────────────────────────────────────────────────

    private void OnCurveChanged()
    {
        Save();
        Describe();
    }

    private sealed class Row
    {
        public string Key { get; set; } = "";
        public bool On { get; set; }
        public int Src { get; set; }
        public double Hys { get; set; } = 3;
        public double[]? T { get; set; }
        public double[]? P { get; set; }
    }

    private sealed class Persist
    {
        public bool AllowStop { get; set; }
        public List<Row> Curves { get; set; } = [];
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            var p = new Persist { AllowStop = _allowStop };
            foreach (var c in Curves)
                p.Curves.Add(new Row
                {
                    Key = c.Key, On = c.Enabled, Src = (int)c.Source, Hys = c.Hysteresis,
                    T = c.Points.Select(x => x.TempC).ToArray(),
                    P = c.Points.Select(x => x.Percent).ToArray(),
                });
            File.WriteAllText(_file, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存檔失敗僅影響下次啟動的預設值 */ }
    }

    private Dictionary<string, Row> Load()
    {
        var map = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_file)) return map;
            var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(_file));
            if (p is null) return map;
            if (p.AllowStop != _allowStop) { _allowStop = p.AllowStop; OnPropertyChanged(nameof(AllowStop)); }
            foreach (var r in p.Curves)
                if (!string.IsNullOrEmpty(r.Key)) map[r.Key] = r;
        }
        catch { /* 壞檔視為沒有設定 */ }
        return map;
    }

    private static void Restore(FanCurve c, Row r)
    {
        c.Enabled = r.On;
        c.SourceIndex = Math.Clamp(r.Src, 0, 2);
        c.Hysteresis = r.Hys;
        if (r.T is { Length: >= 2 } t && r.P is { Length: >= 2 } p)
            c.SetPoints(t.Zip(p).Select(z => (z.First, z.Second)));
        else
            c.LoadPreset(1);
    }
}

