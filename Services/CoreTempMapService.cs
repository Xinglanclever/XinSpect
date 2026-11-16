using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>
/// 每實體核心的溫度摘要（供熱力圖卡片繫結）。同時是核心熱區圖的一格（<see cref="ICoreHeatCell"/>）。
/// </summary>
/// <remarks>
/// <see cref="Name"/>／<see cref="TempC"/>／<see cref="LoadPercent"/> 是熱區圖共用的契約名，
/// 這裡以欄位別名接上本類別慣用的 <see cref="Label"/>／<see cref="Temperature"/>／<see cref="Load"/>：
/// 同一張圖只有一套畫格子的方式，不會因為來源不同而各自漂移。
/// </remarks>
public sealed class CoreTempEntry : ObservableObject, ICoreHeatCell
{
    public CoreTempEntry(int physicalId, string label) { PhysicalCoreId = physicalId; Label = label; }

    public int PhysicalCoreId { get; }
    public string Label { get; }

    /// <summary>熱區圖契約名（＝ <see cref="Label"/>）。</summary>
    public string Name => Label;

    private double? _temp;
    public double? Temperature { get => _temp; set { if (SetProperty(ref _temp, value)) { OnPropertyChanged(nameof(TempText)); OnPropertyChanged(nameof(TempC)); } } }

    /// <summary>熱區圖契約名（＝ <see cref="Temperature"/>）。</summary>
    public double? TempC => _temp;

    private double _load;
    public double Load { get => _load; set { if (SetProperty(ref _load, value)) OnPropertyChanged(nameof(LoadPercent)); } }

    /// <summary>熱區圖契約名（＝ <see cref="Load"/>）。</summary>
    public double LoadPercent => _load;

    private bool _isPerformance = true;
    /// <summary>混合架構下是否為 P-core（無法區分時預設 true）。</summary>
    public bool IsPerformance { get => _isPerformance; set => SetProperty(ref _isPerformance, value); }

    public string TempText => _temp.HasValue ? $"{_temp:0} °C" : "—";
}

/// <summary>
/// 彙整每實體核心的溫度與負載，供 CPU 分頁的熱力圖卡片繫結。
/// 主要資料來源為 <see cref="SensorService.CpuCores"/>，當 LHM 逐核溫度全為 null 時，
/// 嘗試退回 Core Temp 共享記憶體作為備援。
/// </summary>
public sealed class CoreTempMapService : ObservableObject
{
    private readonly SensorService _sensor;
    private readonly CpuTopology _topology;
    private readonly int _physicalCores;
    private readonly int _threadsPerCore;

    public ObservableCollection<CoreTempEntry> Cores { get; } = new();

    private double? _tjMax;
    /// <summary>TjMax（°C）：由 Core Temp 共享記憶體或 CpuDetail 取得。</summary>
    public double? TjMax { get => _tjMax; set => SetProperty(ref _tjMax, value); }

    public CoreTempMapService(SensorService sensor, CpuTopology topo)
    {
        _sensor = sensor;
        _topology = topo;
        _physicalCores = Math.Max(topo.PhysicalCores, 1);
        _threadsPerCore = topo.Smt ? 2 : 1;

        // 建立每實體核心一列
        for (int i = 0; i < _physicalCores; i++)
            // 標籤 1 起算：這是給人看的編號，而全站其他地方的核心編號（感測器列、每核心明細）都是 1 起算。
            Cores.Add(new CoreTempEntry(i, $"核心 #{i + 1}"));
    }

    /// <summary>
    /// 每秒由感測器迴圈呼叫（UI 執行緒）。把 <see cref="SensorService.CpuCores"/> 的逐核列對到這裡的實體核心格。
    /// </summary>
    public void Tick()
    {
        bool anyTemp = Apply(Cores, _sensor.CpuCores, _threadsPerCore);

        // 若 LHM 沒有逐核溫度，嘗試 Core Temp 共享記憶體作為備援
        if (!anyTemp)
            FallbackCoreTemp();
    }

    /// <summary>
    /// 對位與摘要（純函式，測試可直接餵假的逐核列）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>為什麼不再乘 threadsPerCore：</b>這條路以前假設 <see cref="SensorService.CpuCores"/> 是
    /// 「逐邏輯執行緒一列」，所以第 p 格要吃第 2p、2p+1 列。但那張表其實<b>已經是每實體核心一列</b>——
    /// <see cref="SensorService.CoreIndex"/> 取的 <c>#(\d+)</c> 只命中名稱裡第一個編號，
    /// 「CPU Core #5 Thread #2」會被歸到核心 5，兩條執行緒合成同一列。
    /// 於是 18 核的機器上：前 9 格各吃掉<b>兩顆不同核心</b>的最高溫（該格溫度不是它標的那顆核心的），
    /// 後 9 格起點 18 已超出筆數，整片空白——畫面上就是「只有 9 個核心在跑」。
    /// </para>
    /// <para>
    /// 現在優先用標籤對位（兩邊的標籤同源：都是「核心 #N」）；標籤對不上才退回比率折疊，
    /// 比率也由實際筆數推導（筆數／格數），而不是寫死 2。混合架構的 TODO 也因此一併解掉。
    /// </para>
    /// </remarks>
    /// <returns>是否至少量到一個溫度（沒有就交給 Core Temp 備援）。</returns>
    internal static bool Apply(IReadOnlyList<CoreTempEntry> entries, IReadOnlyList<CoreRow> src, int threadsPerCore)
    {
        var byLabel = new Dictionary<string, CoreRow>(StringComparer.Ordinal);
        foreach (var row in src) byLabel[row.Name] = row;

        bool anyLabelMatch = entries.Any(e => byLabel.ContainsKey(e.Label));
        int ratio = src.Count >= entries.Count && entries.Count > 0 && src.Count % entries.Count == 0
            ? Math.Max(1, src.Count / entries.Count)
            : Math.Max(1, threadsPerCore);

        bool anyTemp = false;
        for (int p = 0; p < entries.Count; p++)
        {
            var entry = entries[p];
            double? maxTemp = null;
            double totalLoad = 0;
            int loadCount = 0;

            if (byLabel.TryGetValue(entry.Label, out var one))
            {
                // 標籤對得上就是這顆核心自己的列，不做任何折疊
                if (one.TempC.HasValue) { maxTemp = one.TempC; anyTemp = true; }
                totalLoad += one.LoadPercent;
                loadCount++;
            }
            else if (!anyLabelMatch)
            {
                int loStart = p * ratio;
                int loEnd = Math.Min(loStart + ratio, src.Count);
                for (int li = loStart; li < loEnd; li++)
                {
                    var row = src[li];
                    if (row.TempC.HasValue)
                    {
                        maxTemp = maxTemp.HasValue ? Math.Max(maxTemp.Value, row.TempC.Value) : row.TempC.Value;
                        anyTemp = true;
                    }
                    totalLoad += row.LoadPercent;
                    loadCount++;
                }
            }

            entry.Temperature = maxTemp;
            entry.Load = loadCount > 0 ? totalLoad / loadCount : 0;
        }
        return anyTemp;
    }

    private void FallbackCoreTemp()
    {
        TjMax = null;
        if (!_topology.Loaded || _topology.PhysicalPackages != 1 || !_sensor.HasComparableIntelCoreOrder
            || !CoreTempSharedMem.TryRead(out var readings, out _)) return;
        ApplyExternalReadings(Cores, readings, _sensor.CpuName, _topology.PhysicalPackages,
            _sensor.HasComparableIntelCoreOrder, out double? tjMax);
        TjMax = tjMax;
    }

    internal static bool ApplyExternalReadings(IReadOnlyList<CoreTempEntry> entries,
        IReadOnlyList<ExternalReading> readings, string cpuName, int packages, bool knownCoreOrder,
        out double? tjMax)
    {
        tjMax = null;
        foreach (var entry in entries) entry.Temperature = null;
        if (packages != 1 || !knownCoreOrder || entries.Count == 0
            || string.IsNullOrWhiteSpace(cpuName) || cpuName == "—") return false;

        var temperatures = readings.Where(r => r.Source == "Core Temp"
            && r.ChannelIndex.HasValue && r.SensorType is "Temperature" or "TemperatureDelta").ToArray();
        if (temperatures.Length != entries.Count
            || temperatures.Any(r => !string.Equals(r.DeviceIdentity?.Trim(), cpuName.Trim(), StringComparison.Ordinal))
            || temperatures.Select(r => r.ChannelIndex).Distinct().Count() != entries.Count
            || entries.Select(e => e.PhysicalCoreId).Distinct().Count() != entries.Count
            || entries.Any(e => !temperatures.Any(r => r.ChannelIndex == e.PhysicalCoreId))) return false;

        // TjMax appears after the core rows in the reader; collect it before converting deltas.
        var limits = readings.Where(r => r.Source == "Core Temp" && r.SensorType == "Temperature"
            && !r.ChannelIndex.HasValue && r.DeviceIdentity?.Trim() == cpuName.Trim()
            && r.Label.EndsWith("TjMax", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (limits.Length == 1)
        {
            double limit = Celsius(limits[0], delta: false);
            if (double.IsFinite(limit) && limit is > 0 and < 200) tjMax = limit;
        }

        bool any = false;
        foreach (var entry in entries)
        {
            var reading = temperatures.Single(r => r.ChannelIndex == entry.PhysicalCoreId);
            bool delta = reading.SensorType == "TemperatureDelta";
            double value = Celsius(reading, delta);
            if (!double.IsFinite(value) || (delta && (!tjMax.HasValue || value < 0))) continue;
            double absolute = delta ? tjMax!.Value - value : value;
            if (!double.IsFinite(absolute) || absolute is <= -273.15 or >= 200) continue;
            entry.Temperature = absolute;
            any = true;
        }
        return any;
    }

    private static double Celsius(ExternalReading reading, bool delta) => reading.Unit switch
    {
        "°C" => reading.Value,
        "°F" => (reading.Value - (delta ? 0 : 32)) * 5 / 9,
        _ => double.NaN,
    };
}
