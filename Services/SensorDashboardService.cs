using System.Globalization;

namespace XinSpect;

/// <summary>
/// 感測器儀表板頁（U10-VZ-0057，追蹤鍵 <c>ui.dashboard.sensors</c>）的彙整器。
/// </summary>
/// <remarks>
/// <para>
/// <b>本服務不新增任何讀取。</b>資料全部回收既有來源：即時感測值回收
/// <see cref="SensorService"/> 已發佈的列、逐核溫度回收 <see cref="CoreTempMapService"/>
/// 已經在每秒更新的格點、歷史序列回收 <see cref="HistoryStore"/>、逐日異常回收
/// <see cref="EventsService"/> 已收集的事件。本類只做「把既有資料搬進圖表形狀」的彙整，
/// 並把規格區間（ATX 電壓容差）與統計計算留成純函式，讓單元測試用合成資料直接驗證。
/// </para>
/// <para>
/// <b>誠實原則：</b>規格區間由呼叫端決定要不要傳，本服務只提供「名字像 ATX 電源軌」的
/// 配對建議與該軌的公開容差帶；量不到的軌不列入，Vcore 之類非 ATX 軌的電壓
/// <b>不</b>配 ATX 容差（那是 CPU 廠商的規格，不是 ATX 的）。
/// </para>
/// </remarks>
public static class SensorDashboardService
{
    /// <summary>本功能的追蹤鍵（U10-VZ-0057 感測器儀表板頁）。const 字面寫在 Services/ 原始碼裡供收口對帳；
    /// 常數名不含「Key」——事實鍵目錄本輪凍結，掃描器會把含「Key」的常數掃成事實鍵（見 <see cref="VizTrackingKeys"/>）。</summary>
    public const string DashboardFactKey = "ui.dashboard.sensors";

    // ── ATX 電源軌：子彈圖的規格區間帶 ──────────────────────────────────────

    /// <summary>一段規格區間帶：<c>[Lo, Hi]</c> 與它的標示（供圖例與工具提示）。</summary>
    public readonly record struct SpecBand(double Lo, double Hi, string Label);

    /// <summary>ATX 容差：±5% 為規格內（ATX12V 對 +12V／+5V 的要求；+3.3V 為 ±4%），
    /// ±10% 為經驗上的「還能開機但不應長期待」容忍帶——第二段是本程式的監看建議，不是 ATX 規格。</summary>
    public static SpecBand[] AtxBands(double nominal)
    {
        double specFrac = Math.Abs(nominal - 3.3) < 0.01 ? 0.04 : 0.05;
        return
        [
            new SpecBand(nominal * (1 - 0.10), nominal * (1 + 0.10), "±10%（監看容忍帶，非 ATX 規格）"),
            new SpecBand(nominal * (1 - specFrac), nominal * (1 + specFrac),
                specFrac < 0.05 ? "ATX 規格 ±4%" : "ATX 規格 ±5%"),
        ];
    }

    /// <summary>
    /// 感測器名稱 → ATX 電源軌的標稱電壓；不是電源軌（Vcore、SOH、其他）回 <c>null</c>。
    /// 只認以「+」開頭的軌名（LibreHardwareMonitor 對主機板電壓軌的慣用命名），
    /// 避免把 CPU 核心電壓誤配成 12V 軌的容差。
    /// </summary>
    public static double? AtxRailNominal(string? sensorName)
    {
        if (string.IsNullOrWhiteSpace(sensorName)) return null;
        string n = sensorName.Trim();
        if (n.StartsWith("+12V", StringComparison.OrdinalIgnoreCase)) return 12.0;
        if (n.StartsWith("+5V", StringComparison.OrdinalIgnoreCase)) return 5.0;
        if (n.StartsWith("+3.3V", StringComparison.OrdinalIgnoreCase)) return 3.3;
        return null;
    }

    /// <summary>一軌的量測與它的規格帶（子彈圖的一列）；量不到電壓（<see cref="Volts"/>＝null）照實留著，畫「沒有讀值」。</summary>
    public sealed record RailReading(string Name, double? Volts, double Nominal, SpecBand[] Bands);

    /// <summary>
    /// 從（名稱, 電壓）列裡挑出 ATX 電源軌。輸入由呼叫端從既有感測列搬過來，
    /// 本方法不做任何讀取；沒有任何軌時回空清單——畫面誠實說「沒有電壓軌讀值」。
    /// </summary>
    public static List<RailReading> CollectRails(IEnumerable<(string Name, double? Volts)> rows)
    {
        var rails = new List<RailReading>();
        foreach (var (name, volts) in rows)
        {
            if (AtxRailNominal(name) is not { } nominal) continue;
            rails.Add(new RailReading(name, volts, nominal, AtxBands(nominal)));
        }
        return rails;
    }

    /// <summary>
    /// 從既有感測列的<b>顯示文字</b>還原電壓數值（如 <c>"12.096 V"</c>）；
    /// 不是數字開頭、「—」、或解析不動時回 <c>null</c>——還原不了就照實說沒有讀值，
    /// 不回 0 冒充量到 0 V。接受「.」與「,」兩種小數點（發佈文字跟著執行緒文化走）。
    /// </summary>
    public static double? ParseVolts(string? valueText)
    {
        if (string.IsNullOrWhiteSpace(valueText)) return null;
        string trimmed = valueText.Trim();
        int end = 0;
        while (end < trimmed.Length && (char.IsDigit(trimmed[end]) || trimmed[end] is '.' or ',' or '-')) end++;
        if (end == 0) return null;
        string num = trimmed[..end].Replace(',', '.');
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && !double.IsNaN(v) && !double.IsInfinity(v)
            ? v
            : null;
    }

    // ── 逐日異常計數：日曆熱圖的資料 ────────────────────────────────────────

    /// <summary>一天一格：本地日期與當日異常（警告以上）事件數。</summary>
    public readonly record struct DailyCount(DateOnly Day, int Count);

    /// <summary>
    /// 把既有事件流按<b>本地日</b>折成逐日異常計數（警告及以上才計；中性／良好不算異常）。
    /// 只回「有事件的日」——沒有事件的日不在輸出裡，由日曆控制項留白：
    /// 程式沒在跑的日子我們不知道有沒有異常，留白是誠實，補 0 是編造。
    /// </summary>
    public static List<DailyCount> DailyAnomalies(
        IEnumerable<(DateTime TimeLocal, Severity Severity)> events, DateOnly fromDay, DateOnly toDay)
    {
        if (toDay < fromDay) (fromDay, toDay) = (toDay, fromDay);
        var counts = new SortedDictionary<DateOnly, int>();
        foreach (var (time, sev) in events)
        {
            if (sev is not (Severity.Warning or Severity.Serious or Severity.Critical)) continue;
            var day = DateOnly.FromDateTime(time);
            if (day < fromDay || day > toDay) continue;
            counts[day] = counts.TryGetValue(day, out int c) ? c + 1 : 1;
        }
        return [.. counts.Select(kv => new DailyCount(kv.Key, kv.Value))];
    }

    // ── 核×時間熱圖的滾動取樣：只搬 CoreTempMap 已算好的值 ───────────────────

    /// <summary>
    /// 逐核溫度的滾動緩衝：每次 <see cref="Push"/> 把當下的逐核讀值接成新的一欄，
    /// 超過 <see cref="Columns"/> 欄就丟掉最舊的一欄。列以核心標籤對齊——
    /// 某一欄某核心沒有讀值（感測晚到）就記 <c>null</c>，熱圖把那格留白，不冒充 0 °C。
    /// </summary>
    public sealed class CoreHeatBuffer
    {
        /// <summary>保留的時間欄數（近 N 拍）。</summary>
        public int Columns { get; }

        /// <summary>列的固定順序（第一次見到的核心排前面，之後不再重排，畫面才不會跳）。</summary>
        private readonly List<string> _labels = [];
        private readonly Dictionary<string, Queue<double?>> _rows = new(StringComparer.Ordinal);

        public CoreHeatBuffer(int columns = 60)
            => Columns = Math.Max(2, columns);

        /// <summary>核心標籤（列序）。</summary>
        public IReadOnlyList<string> Labels => _labels;

        /// <summary>接一拍：每呼叫一次記一欄，取樣節奏由呼叫端決定（本服務不自己起計時器）。
        /// 同一拍同名重複的列只記第一筆——不讓重複回報把一欄擠成兩格。</summary>
        public void Push(IEnumerable<(string Label, double? Temp)> cores)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (label, temp) in cores)
            {
                if (!seen.Add(label)) continue;
                if (!_rows.TryGetValue(label, out var q))
                {
                    q = new Queue<double?>(Enumerable.Repeat<double?>(null, _pushed));
                    _rows[label] = q;
                    _labels.Add(label);
                }
                q.Enqueue(temp);
                while (q.Count > Columns) q.Dequeue();
            }

            // 這一拍缺席的核心也補一個 null 格：它在這一拍沒有讀值是事實
            foreach (var (label, q) in _rows)
            {
                if (seen.Contains(label)) continue;
                q.Enqueue(null);
                while (q.Count > Columns) q.Dequeue();
            }

            _pushed++;
        }

        /// <summary>已接的拍數（新列以此補歷史缺格）。</summary>
        private int _pushed;

        /// <summary>熱圖列：標籤與（可能含缺格的）序列；呼叫端直接餵 <see cref="XinSpect.CoreTimeHeatmapRow"/>。</summary>
        public IReadOnlyList<(string Label, double?[] Values)> Rows
            => [.. _labels.Select(l => (Label: l, Values: (double?[])[.. _rows[l]]))];
    }

    // ── 歷史序列 → 控制圖樣本：回收 HistoryStore，不重讀 ─────────────────────

    /// <summary>
    /// 抽出歷史倉單一指標的平均值序列（控制圖的樣本）。指標整段沒有讀值
    /// （<see cref="HistorySeries.HasData"/> 為假——本機沒有那顆感測器）時回空陣列，
    /// 呼叫端據此顯示「本機無此感測器」，不把一串 0 當量測結果。
    /// </summary>
    public static double[] MetricPoints(HistorySeries series, int metric)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (!series.HasData(metric)) return [];
        var pts = new double[series.Count];
        for (int i = 0; i < series.Count; i++) pts[i] = series.A(i, metric);
        return pts;
    }
}
