using System.IO;

namespace XinSpect;

/// <summary>
/// 長期追蹤的七項指標：索引常數與顯示中介資料。
/// 順序即磁碟紀錄的欄位順序，一經發佈不得調換（否則舊檔會被錯讀）。
/// </summary>
public static class HistoryMetrics
{
    public const int Count = 7;
    public const int CpuLoad = 0, CpuTemp = 1, CpuClock = 2, MemLoad = 3, GpuLoad = 4, GpuTemp = 5, GpuVram = 6;

    public static readonly string[] Titles =
        ["處理器負載", "處理器溫度", "處理器頻率", "記憶體使用", "顯示卡負載", "顯示卡溫度", "顯示記憶體"];

    public static readonly string[] Units = ["%", "°C", "MHz", "%", "%", "°C", "MB"];

    /// <summary>各指標的曲線顏色（與感測頁、走勢圖同一套色系）。</summary>
    public static readonly string[] Colors =
        ["#3987e5", "#ec835a", "#7db4ff", "#0ca30c", "#fab219", "#d03b3b", "#9d7bd8"];

    /// <summary>百分比類指標固定 0–100；頻率／容量類為 null（依區間自動縮放）。</summary>
    public static readonly double?[] FixedMax = [100, null, null, 100, 100, null, null];

    /// <summary>自感測引擎讀出一組即時值，順序與上列一致。</summary>
    public static void Read(SensorService live, float[] dst)
    {
        var g = live.PrimaryGpu;
        dst[CpuLoad] = (float)live.CpuLoad;
        dst[CpuTemp] = (float)(live.CpuTemp ?? 0);
        dst[CpuClock] = (float)live.CpuClock;
        dst[MemLoad] = (float)live.MemLoad;
        dst[GpuLoad] = (float)(g?.LoadPercent ?? 0);
        dst[GpuTemp] = (float)(g?.TempC ?? 0);
        dst[GpuVram] = (float)(g?.VramUsedMB ?? 0);
    }
}
/// <summary>
/// 一次歷史查詢的結果：時間點陣列 + 每點每指標的最小／平均／最大（三份平坦陣列，索引為 <c>i * 7 + m</c>）。
/// 秒級查詢時三者相同（原始取樣沒有區間可言）；分鐘級查詢時 min/max 為該分鐘的極值。
/// </summary>
public sealed class HistorySeries
{
    /// <summary>各點時間（UTC）。遞增排列。</summary>
    public DateTime[] Times { get; init; } = [];
    public float[] Avg { get; init; } = [];
    public float[] Min { get; init; } = [];
    public float[] Max { get; init; } = [];
    /// <summary>true 表示秒級原始取樣；false 表示分鐘級彙整。</summary>
    public bool SecondLevel { get; init; }

    public int Count => Times.Length;
    public static readonly HistorySeries Empty = new();

    public float A(int i, int m) => Avg[i * HistoryMetrics.Count + m];
    public float L(int i, int m) => Min[i * HistoryMetrics.Count + m];
    public float H(int i, int m) => Max[i * HistoryMetrics.Count + m];

    /// <summary>
    /// 桶化到最多 <paramref name="columns"/> 欄。30 天的分鐘級資料有 4 萬多點，
    /// 逐點成線會拖垮繪圖；每桶取平均的平均、極值的極值，形狀與統計皆不失真。
    /// </summary>
    public HistorySeries Downsample(int columns)
    {
        const int M = HistoryMetrics.Count;
        int n = Count;
        if (columns < 2 || n <= columns) return this;

        var t = new DateTime[columns];
        var a = new float[columns * M];
        var lo = new float[columns * M];
        var hi = new float[columns * M];
        for (int c = 0; c < columns; c++)
        {
            int s = (int)((long)c * n / columns);
            int e = (int)((long)(c + 1) * n / columns);
            if (e <= s) e = s + 1;
            if (e > n) e = n;
            t[c] = Times[s + (e - s) / 2];
            for (int m = 0; m < M; m++)
            {
                double sum = 0;
                float mn = float.MaxValue, mx = float.MinValue;
                for (int i = s; i < e; i++)
                {
                    sum += A(i, m);
                    if (L(i, m) < mn) mn = L(i, m);
                    if (H(i, m) > mx) mx = H(i, m);
                }
                int k = c * M + m;
                a[k] = (float)(sum / (e - s));
                lo[k] = mn;
                hi[k] = mx;
            }
        }
        return new HistorySeries { Times = t, Avg = a, Min = lo, Max = hi, SecondLevel = SecondLevel };
    }

    /// <summary>
    /// 該指標在本段是否真的有讀值。
    /// </summary>
    /// <remarks>
    /// 磁碟紀錄的欄位是固定寬度的，取樣當時「沒讀到」（無獨立顯示卡、無溫度感測器…）一律落為 0。
    /// 因此整段皆 0 的指標代表<b>從未量到</b>，而不是「量到了 0」——統計與圖層都必須據此顯示「—」，
    /// 否則畫面上會出現一條看似量測結果的 0 值直線（例如「顯示卡溫度 平均 0 °C」）。
    /// 反之只要出現過任一正值，就以真實資料看待，中間的 0 也照實納入統計。
    /// </remarks>
    public bool HasData(int m)
    {
        for (int i = 0; i < Count; i++)
            if (A(i, m) > 0 || H(i, m) > 0) return true;
        return false;
    }

    /// <summary>該指標於本段的最小／平均／最大／P95。</summary>
    /// <remarks>
    /// P95 取「各點代表值」的分布：秒級查詢即原始取樣，分鐘級查詢則為每分鐘平均，
    /// 故長區間的 P95 是彙整後的近似值（畫面上會標明資料粒度）。
    /// </remarks>
    public (double Min, double Avg, double Max, double P95) Summarize(int m)
    {
        int n = Count;
        if (n == 0) return (0, 0, 0, 0);
        double mn = double.MaxValue, mx = double.MinValue, sum = 0;
        var vals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double a = A(i, m);
            vals[i] = a;
            sum += a;
            if (L(i, m) < mn) mn = L(i, m);
            if (H(i, m) > mx) mx = H(i, m);
        }
        // vals 是本方法自己配置的，可就地排序；30 天區間有四萬多點，這裡每多複製一份都是數百 KB
        Array.Sort(vals);
        return (mn, sum / n, mx, PercentileOfSorted(vals, 95));
    }

    /// <summary>線性插值百分位數（會就地排序傳入陣列的複本，不動原始資料）。</summary>
    internal static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        var v = (double[])values.Clone();
        Array.Sort(v);
        return PercentileOfSorted(v, p);
    }

    // 已排序陣列上的線性插值百分位數。
    private static double PercentileOfSorted(double[] v, double p)
    {
        if (v.Length == 0) return 0;
        if (v.Length == 1) return v[0];
        double pos = Math.Clamp(p, 0, 100) / 100.0 * (v.Length - 1);
        int i = (int)Math.Floor(pos);
        if (i + 1 >= v.Length) return v[^1];
        return v[i] + (v[i + 1] - v[i]) * (pos - i);
    }
}
/// <summary>
/// 長期歷史倉：秒級近況（僅記憶體，約一小時）＋分鐘級彙整（磁碟，可保留數週）。
/// </summary>
/// <remarks>
/// 設計要點：
/// <list type="bullet">
/// <item>每拍 <see cref="Sample(SensorService)"/> 推入秒級環形緩衝，並累加當前分鐘的
/// 最小／平均／最大；跨分鐘時結算一筆固定長度紀錄並直接 append 到磁碟。</item>
/// <item>磁碟檔為「16 位元組表頭 + 等長紀錄」，記憶體保有一份時序鏡射，
/// 查詢完全在記憶體完成；逾期紀錄剔除後才回寫瘦身（30 天約 4 MB）。</item>
/// <item>所有磁碟操作皆以 try/catch 降級：歷史是附加功能，任何 I/O 失敗都不得影響心跳。</item>
/// </list>
/// </remarks>
public sealed class HistoryStore : IDisposable
{
    private const int Magic = 0x53485358;                 // "XSHS"
    private const int Version = 1;
    private const int HeaderBytes = 16;                   // magic + version + count + reserved
    private const int RecordBytes = 8 + HistoryMetrics.Count * 3 * 4;   // 時間 + 每指標 min/avg/max = 92
    private const int SecondCapacity = 3600;              // 秒級近況（1 秒間隔約一小時）
    private const int HardRecordCap = 200_000;            // 約 139 天，防呆上限

    // 秒級環形緩衝（重啟後由分鐘級彙整接手，不落地）
    private readonly long[] _secTicks = new long[SecondCapacity];
    private readonly float[] _secVals = new float[SecondCapacity * HistoryMetrics.Count];
    private int _secCount, _secHead;

    // 分鐘級彙整的記憶體鏡射（時間遞增；每筆 7 個浮點）
    private readonly List<long> _minTicks = new();
    private readonly List<float> _minMin = new(), _minAvg = new(), _minMax = new();

    // 當前分鐘的累加器
    private long _curMinute = -1;
    private int _curN;
    private readonly double[] _accSum = new double[HistoryMetrics.Count];
    private readonly float[] _accMin = new float[HistoryMetrics.Count];
    private readonly float[] _accMax = new float[HistoryMetrics.Count];
    private readonly float[] _scratch = new float[HistoryMetrics.Count];

    /// <summary>資料夾（預設 %APPDATA%\XinSpect）。</summary>
    public string Folder { get; }
    /// <summary>分鐘級彙整檔的完整路徑。</summary>
    public string FilePath { get; }

    /// <summary>關閉後不再取樣（既有資料保留）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>結算完一筆分鐘紀錄時觸發（歷史頁據此在必要時重新查詢）。</summary>
    public event Action? Updated;

    private int _retentionDays = 30;
    /// <summary>保留天數（1–120）。調小時立即剔除逾期紀錄並回寫瘦身。</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set
        {
            int v = Math.Clamp(value, 1, 120);
            if (v == _retentionDays) return;
            _retentionDays = v;
            try { Trim(); } catch { /* 瘦身失敗不影響取樣 */ }
        }
    }

    public HistoryStore(string? folder = null)
    {
        Folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
        FilePath = Path.Combine(Folder, "history.bin");
        try { Load(); } catch { /* 檔案損毀則視為無歷史，重新開始累積 */ }
    }

    // ── 對外資訊 ──────────────────────────────────────────────────────────

    /// <summary>已落地的分鐘紀錄筆數。</summary>
    public int MinuteCount => _minTicks.Count;
    /// <summary>記憶體中的秒級樣本數。</summary>
    public int SecondCount => _secCount;

    /// <summary>可查詢的最早時間（先看分鐘級，退回秒級）。</summary>
    public DateTime? OldestUtc
    {
        get
        {
            if (_minTicks.Count > 0) return new DateTime(_minTicks[0], DateTimeKind.Utc);
            if (_secCount > 0) return new DateTime(OldestSecTicks(), DateTimeKind.Utc);
            return null;
        }
    }

    /// <summary>磁碟佔用（位元組）；讀不到時為 0。</summary>
    public long DiskBytes
    {
        get { try { return File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0; } catch { return 0; } }
    }

    /// <summary>人類可讀的容量摘要，供設定頁顯示。</summary>
    public string SizeText
    {
        get
        {
            long b = DiskBytes;
            return b >= 1 << 20 ? $"{b / 1024.0 / 1024.0:0.0} MB" : $"{b / 1024.0:0} KB";
        }
    }

    private long OldestSecTicks() => _secTicks[(_secHead - _secCount + SecondCapacity) % SecondCapacity];

    // ── 取樣 ──────────────────────────────────────────────────────────────

    /// <summary>每一拍呼叫一次：推入秒級環並累加當前分鐘。</summary>
    public void Sample(SensorService live)
    {
        if (!Enabled) return;
        HistoryMetrics.Read(live, _scratch);
        Sample(_scratch, DateTime.UtcNow);
    }

    /// <summary>以明確的值與時間取樣（供單元測試與匯入使用）。</summary>
    internal void Sample(float[] values, DateTime utc)
    {
        const int M = HistoryMetrics.Count;
        long ticks = utc.Ticks;

        _secTicks[_secHead] = ticks;
        for (int m = 0; m < M; m++)
        {
            float v = values[m];
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
            _secVals[_secHead * M + m] = v;
        }
        _secHead = (_secHead + 1) % SecondCapacity;
        if (_secCount < SecondCapacity) _secCount++;

        long minute = ticks / TimeSpan.TicksPerMinute;
        if (_curMinute < 0) StartMinute(minute);
        else if (minute != _curMinute) { CloseMinute(); StartMinute(minute); }

        for (int m = 0; m < M; m++)
        {
            float v = _secVals[((_secHead - 1 + SecondCapacity) % SecondCapacity) * M + m];
            _accSum[m] += v;
            if (v < _accMin[m]) _accMin[m] = v;
            if (v > _accMax[m]) _accMax[m] = v;
        }
        _curN++;
    }

    private void StartMinute(long minute)
    {
        _curMinute = minute;
        _curN = 0;
        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            _accSum[m] = 0;
            _accMin[m] = float.MaxValue;
            _accMax[m] = float.MinValue;
        }
    }

    // 結算當前分鐘：推入記憶體鏡射 + append 一筆固定長度紀錄到磁碟。
    private void CloseMinute()
    {
        if (_curN <= 0 || _curMinute < 0) return;
        const int M = HistoryMetrics.Count;
        long ticks = _curMinute * TimeSpan.TicksPerMinute;

        _minTicks.Add(ticks);
        for (int m = 0; m < M; m++)
        {
            _minMin.Add(_accMin[m]);
            _minAvg.Add((float)(_accSum[m] / _curN));
            _minMax.Add(_accMax[m]);
        }
        _curN = 0;

        try { Append(_minTicks.Count - 1); } catch { /* 落地失敗僅損失持久性，記憶體仍有資料 */ }
        try { Trim(); } catch { }
        Updated?.Invoke();
    }

    /// <summary>結算未滿一分鐘的累加（結束前呼叫，避免最後一段資料遺失）。</summary>
    public void Flush()
    {
        try { CloseMinute(); } catch { }
    }

    /// <summary>清空全部歷史（設定頁的「清除歷史資料」）。</summary>
    public void Clear()
    {
        _minTicks.Clear(); _minMin.Clear(); _minAvg.Clear(); _minMax.Clear();
        _secCount = 0; _secHead = 0;
        _curMinute = -1; _curN = 0;
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        Updated?.Invoke();
    }

    public void Dispose() => Flush();

    // ── 查詢 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 取出區間資料。若整段都落在記憶體中的秒級近況內，回傳原始取樣（<see cref="HistorySeries.SecondLevel"/>
    /// 為 true）；否則回傳分鐘級彙整，並補上尚未結算的當前分鐘。
    /// </summary>
    public HistorySeries Query(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc) return HistorySeries.Empty;
        long f = fromUtc.Ticks, t = toUtc.Ticks;
        if (_secCount > 0 && f >= OldestSecTicks()) return SecondSeries(f, t);
        return MinuteSeries(f, t);
    }

    private HistorySeries SecondSeries(long f, long t)
    {
        const int M = HistoryMetrics.Count;
        var times = new List<DateTime>(_secCount);
        var vals = new List<float>(_secCount * M);
        for (int i = 0; i < _secCount; i++)
        {
            int slot = (_secHead - _secCount + i + SecondCapacity) % SecondCapacity;
            long ticks = _secTicks[slot];
            if (ticks < f || ticks > t) continue;
            times.Add(new DateTime(ticks, DateTimeKind.Utc));
            for (int m = 0; m < M; m++) vals.Add(_secVals[slot * M + m]);
        }
        var arr = vals.ToArray();
        return new HistorySeries
        {
            Times = times.ToArray(), Avg = arr, Min = arr, Max = arr, SecondLevel = true,
        };
    }

    /// <summary>
    /// 分鐘級彙整的區間查詢。
    /// </summary>
    /// <remarks>
    /// 歷史頁在自動跟隨時每兩秒查一次，30 天區間有四萬多筆——因此這裡以二分搜尋定出範圍、
    /// 一次配置好剛好的陣列，而不是逐筆比對再讓四個 List 反覆成長（那會在每次查詢丟出數 MB 垃圾）。
    /// </remarks>
    private HistorySeries MinuteSeries(long f, long t)
    {
        const int M = HistoryMetrics.Count;
        int start = LowerBound(f), end = LowerBound(t + 1);      // 落在區間內者為 [start, end)
        int n = Math.Max(0, end - start);

        // 尚未結算的當前分鐘：補一個即時點，長區間的右緣才不會落後一分鐘
        long pendTicks = _curMinute * TimeSpan.TicksPerMinute;
        bool pending = _curN > 0 && _curMinute >= 0 && pendTicks >= f && pendTicks <= t
                       && (n == 0 || _minTicks[end - 1] < pendTicks);

        int total = n + (pending ? 1 : 0);
        if (total == 0) return HistorySeries.Empty;

        var times = new DateTime[total];
        var min = new float[total * M];
        var avg = new float[total * M];
        var max = new float[total * M];

        for (int i = 0; i < n; i++)
        {
            times[i] = new DateTime(_minTicks[start + i], DateTimeKind.Utc);
            int src = (start + i) * M, dst = i * M;
            for (int m = 0; m < M; m++)
            {
                min[dst + m] = _minMin[src + m];
                avg[dst + m] = _minAvg[src + m];
                max[dst + m] = _minMax[src + m];
            }
        }
        if (pending)
        {
            times[n] = new DateTime(pendTicks, DateTimeKind.Utc);
            for (int m = 0; m < M; m++)
            {
                int k = n * M + m;
                min[k] = _accMin[m];
                avg[k] = (float)(_accSum[m] / _curN);
                max[k] = _accMax[m];
            }
        }
        return new HistorySeries { Times = times, Avg = avg, Min = min, Max = max };
    }

    // _minTicks 嚴格遞增：回傳第一個 >= ticks 的索引（都比它小時回傳筆數）。
    private int LowerBound(long ticks)
    {
        int lo = 0, hi = _minTicks.Count;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_minTicks[mid] < ticks) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ── 磁碟 ──────────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        var raw = File.ReadAllBytes(FilePath);
        if (raw.Length < HeaderBytes) return;
        if (BitConverter.ToInt32(raw, 0) != Magic || BitConverter.ToInt32(raw, 4) != Version) return;

        const int M = HistoryMetrics.Count;
        int n = (raw.Length - HeaderBytes) / RecordBytes;
        // 一次要進四個 List、每筆 21 個浮點：先備妥容量，免去反覆倍增（30 天檔約 4 MB，倍增會多丟一倍垃圾）
        _minTicks.EnsureCapacity(n);
        _minMin.EnsureCapacity(n * M);
        _minAvg.EnsureCapacity(n * M);
        _minMax.EnsureCapacity(n * M);
        long cutoff = DateTime.UtcNow.AddDays(-_retentionDays).Ticks;
        long future = DateTime.UtcNow.AddDays(1).Ticks;      // 時鐘曾被調動的荒謬紀錄一併丟棄
        for (int i = 0; i < n; i++)
        {
            int o = HeaderBytes + i * RecordBytes;
            long ticks = BitConverter.ToInt64(raw, o);
            if (ticks < cutoff || ticks > future) continue;
            if (_minTicks.Count > 0 && ticks <= _minTicks[^1]) continue;   // 保持嚴格遞增
            _minTicks.Add(ticks);
            for (int m = 0; m < M; m++)
            {
                int b = o + 8 + m * 12;
                _minMin.Add(BitConverter.ToSingle(raw, b));
                _minAvg.Add(BitConverter.ToSingle(raw, b + 4));
                _minMax.Add(BitConverter.ToSingle(raw, b + 8));
            }
        }
        if (_minTicks.Count != n) Compact();     // 有紀錄被剔除 → 回寫瘦身
    }

    // append 記憶體鏡射中第 index 筆到檔尾（檔案不存在時先寫表頭）。
    private void Append(int index)
    {
        const int M = HistoryMetrics.Count;
        Directory.CreateDirectory(Folder);
        bool needHeader;
        try { needHeader = !File.Exists(FilePath) || new FileInfo(FilePath).Length < HeaderBytes; }
        catch { needHeader = true; }
        if (needHeader) { Compact(); return; }    // 一併把記憶體鏡射整份寫出，含表頭

        var buf = new byte[RecordBytes];
        BitConverter.TryWriteBytes(buf.AsSpan(0), _minTicks[index]);
        for (int m = 0; m < M; m++)
        {
            int k = index * M + m, b = 8 + m * 12;
            BitConverter.TryWriteBytes(buf.AsSpan(b), _minMin[k]);
            BitConverter.TryWriteBytes(buf.AsSpan(b + 4), _minAvg[k]);
            BitConverter.TryWriteBytes(buf.AsSpan(b + 8), _minMax[k]);
        }
        using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.Write(buf);
    }

    // 剔除逾期／超量紀錄；有刪除才回寫（append 為常態，重寫是例外）。
    private void Trim()
    {
        const int M = HistoryMetrics.Count;
        long cutoff = DateTime.UtcNow.AddDays(-_retentionDays).Ticks;
        int cap = Math.Min(HardRecordCap, _retentionDays * 24 * 60);
        int drop = 0;
        while (drop < _minTicks.Count && _minTicks[drop] < cutoff) drop++;
        if (_minTicks.Count - drop > cap) drop = _minTicks.Count - cap;
        if (drop <= 0) return;

        _minTicks.RemoveRange(0, drop);
        _minMin.RemoveRange(0, drop * M);
        _minAvg.RemoveRange(0, drop * M);
        _minMax.RemoveRange(0, drop * M);
        Compact();
    }

    // 整份回寫（表頭 + 全部紀錄）。先寫暫存檔再覆蓋，避免中途失敗留下半截檔案。
    private void Compact()
    {
        const int M = HistoryMetrics.Count;
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(_minTicks.Count);
            w.Write(0);
            for (int i = 0; i < _minTicks.Count; i++)
            {
                w.Write(_minTicks[i]);
                for (int m = 0; m < M; m++)
                {
                    int k = i * M + m;
                    w.Write(_minMin[k]); w.Write(_minAvg[k]); w.Write(_minMax[k]);
                }
            }
        }
        // File.Move（同磁碟）比 File.Copy 原子：中途斷電不會在 FilePath 留下半截內容
        File.Move(tmp, FilePath, overwrite: true);
        try { File.Delete(tmp); } catch { }
    }
}









