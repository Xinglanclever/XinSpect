using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>
/// 每實體核心的溫度摘要（供熱力圖卡片繫結）。
/// </summary>
public sealed class CoreTempEntry : ObservableObject
{
    public CoreTempEntry(int physicalId, string label) { PhysicalCoreId = physicalId; Label = label; }

    public int PhysicalCoreId { get; }
    public string Label { get; }

    private double? _temp;
    public double? Temperature { get => _temp; set { if (SetProperty(ref _temp, value)) OnPropertyChanged(nameof(TempText)); } }

    private double _load;
    public double Load { get => _load; set => SetProperty(ref _load, value); }

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
    private readonly int _physicalCores;
    private readonly int _threadsPerCore;

    public ObservableCollection<CoreTempEntry> Cores { get; } = new();

    private double? _tjMax;
    /// <summary>TjMax（°C）：由 Core Temp 共享記憶體或 CpuDetail 取得。</summary>
    public double? TjMax { get => _tjMax; set => SetProperty(ref _tjMax, value); }

    public CoreTempMapService(SensorService sensor, CpuTopology topo)
    {
        _sensor = sensor;
        _physicalCores = Math.Max(topo.PhysicalCores, 1);
        _threadsPerCore = topo.Smt ? 2 : 1;

        // 建立每實體核心一列
        for (int i = 0; i < _physicalCores; i++)
            Cores.Add(new CoreTempEntry(i, $"核心 #{i}"));
    }

    /// <summary>
    /// 每秒由感測器迴圈呼叫（UI 執行緒）。
    /// 從 SensorService.CpuCores 裡把每 N 條邏輯處理器折疊成一條實體核心（取最高溫、平均負載）。
    /// </summary>
    public void Tick()
    {
        var src = _sensor.CpuCores;
        bool anyTemp = false;

        for (int p = 0; p < Cores.Count; p++)
        {
            var entry = Cores[p];

            // 邏輯→實體映射：LHM 以 CoreRow index 對應邏輯核心，SMT 時每兩條對一個實體核心
            int loStart = p * _threadsPerCore;
            int loEnd = Math.Min(loStart + _threadsPerCore, src.Count);

            double? maxTemp = null;
            double totalLoad = 0;
            int loadCount = 0;

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

            entry.Temperature = maxTemp;
            entry.Load = loadCount > 0 ? totalLoad / loadCount : 0;
        }

        // 若 LHM 沒有逐核溫度，嘗試 Core Temp 共享記憶體作為備援
        if (!anyTemp)
            FallbackCoreTemp();
    }

    private void FallbackCoreTemp()
    {
        if (!CoreTempSharedMem.TryRead(out var readings, out _)) return;

        double? firstTjMax = null;
        int coreIdx = 0;
        foreach (var r in readings)
        {
            if (r.SensorType != "Temperature") continue;   // 「距 TjMax」的差值已另立 TemperatureDelta，不會進來
            // 用 EndsWith 而非 Contains：「核心 #0 TjMax」才是 TjMax 列；
            // 任何含這四個字母的標籤（例如舊版的「(ΔTjMax)」後綴）都會被 Contains 誤吃。
            if (r.Label.EndsWith("TjMax", StringComparison.OrdinalIgnoreCase))
            {
                firstTjMax ??= r.Value;
                continue;
            }
            // 逐核溫度：Core Temp 的核心索引即為實體核心
            if (coreIdx < Cores.Count)
            {
                Cores[coreIdx].Temperature = r.Value;
                coreIdx++;
            }
        }
        TjMax ??= firstTjMax;
    }
}
