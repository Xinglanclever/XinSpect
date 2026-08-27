using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace XinSpect;

/// <summary>走訪 LHM 硬體樹並更新每個節點的感測器數值。</summary>
internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

/// <summary>
/// 以 LibreHardwareMonitorLib 讀取即時感測器（溫度 / 負載 / 頻率 / 電壓 / 風扇 / 電力）。
/// 初始化時快取感測器參考，之後每次 Refresh() 就地更新繫結物件，避免重建集合造成閃爍。
/// </summary>
public sealed partial class SensorService : ObservableObject, IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly List<(ISensor s, SensorRow row)> _sensorRows = new();

    // Poll() 於背景執行緒走訪硬體樹，Dispose() 於 UI 執行緒關閉 LHM；關閉視窗時兩者可能並行，
    // 以此鎖與旗標序列化，避免關閉瞬間背景 Poll 存取已 Close 的原生控制代碼而崩潰。
    private readonly object _pollLock = new();
    private volatile bool _disposed;

    private sealed class CoreBind { public ISensor? Clk, Load, Temp; public CoreRow Row = null!; }
    private sealed class GpuBind { public ISensor? Temp, Load, CoreClk, MemClk, Fan, FanRpm, Power, VramUsed, VramTotal; public GpuRow Row = null!; }
    private sealed class StorageBind { public ISensor? Temp, Life, Used, Activity; public bool LifeConsumed; public StorageRow Row = null!; }

    private readonly List<CoreBind> _cores = new();
    private readonly List<GpuBind> _gpuBinds = new();
    private readonly List<StorageBind> _diskBinds = new();

    private ISensor? _cpuLoad, _cpuPkgTemp, _cpuPower, _cpuVolt, _cpuBus;
    private ISensor? _vrmTemp;   // 主機板 VRM / MOS 溫度（超頻模組用；LHM 於 SuperIO 子硬體曝露）
    private readonly List<ISensor> _coreTempFallback = new();
    private ISensor? _memLoad, _memUsed, _memAvail;

    public ObservableCollection<CoreRow> CpuCores { get; } = new();
    public ObservableCollection<GpuRow> Gpus { get; } = new();
    public ObservableCollection<StorageRow> Drives { get; } = new();
    public ObservableCollection<SensorRow> AllSensors { get; } = new();

    public string CpuName { get; private set; } = "—";

    public SensorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
        };
        _computer.Open();
        _computer.Accept(_visitor);
        BuildCache();
        Refresh();
    }

    // ---- 初始化快取 -------------------------------------------------------

    private void BuildCache()
    {
        foreach (var hw in _computer.Hardware)
        {
            EnumerateSensors(hw, hw.Name);

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    CpuName = hw.Name;
                    BuildCpu(hw);
                    break;
                case HardwareType.Memory:
                    // 需精確比對：LHM 的實體記憶體名為「Memory」「Memory Used/Available」，
                    // 另有「Virtual Memory…」（認可負載/交換）。用 Contains 會誤中虛擬記憶體，故改用完全比對。
                    _memLoad = Exact(hw, SensorType.Load, "Memory");
                    _memUsed = Exact(hw, SensorType.Data, "Memory Used");
                    _memAvail = Exact(hw, SensorType.Data, "Memory Available");
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    BuildGpu(hw);
                    break;
                case HardwareType.Storage:
                    BuildStorage(hw);
                    break;
                case HardwareType.Motherboard:
                    // VRM/MOS 溫度多半位於主機板底下的 SuperIO 子硬體，需向下搜尋
                    _vrmTemp = FirstDeep(hw, SensorType.Temperature, "VRM", "VR MOS", "MOS", "VCORE MOS", "CPU VRM");
                    break;
            }
        }

        // 感測器分頁：把列舉到的所有感測列一次性加入繫結集合（之後每秒就地更新其數值）
        foreach (var (_, row) in _sensorRows)
            AllSensors.Add(row);

        // 系統風扇控制：列舉主機板／SuperIO 上可由軟體寫入的風扇（顯示卡風扇由顯示卡超頻頁以 NVML 控制）
        CollectFanControls();
    }

    private void BuildCpu(IHardware hw)
    {
        _cpuLoad = First(hw, SensorType.Load, "CPU Total") ?? First(hw, SensorType.Load, "Total");
        _cpuPkgTemp = First(hw, SensorType.Temperature, "CPU Package", "Package", "Core (Tctl/Tdie)", "Core Max", "Core Average");
        _cpuPower = First(hw, SensorType.Power, "CPU Package", "Package");
        _cpuVolt = First(hw, SensorType.Voltage, "CPU Core", "Vcore", "Core");
        _cpuBus = First(hw, SensorType.Clock, "Bus Speed");

        var byIndex = new SortedDictionary<int, CoreBind>();
        CoreBind Bind(int i)
        {
            if (!byIndex.TryGetValue(i, out var b))
            {
                b = new CoreBind { Row = new CoreRow($"核心 #{i}") };
                byIndex[i] = b;
            }
            return b;
        }

        foreach (var s in hw.Sensors)
        {
            int idx = CoreIndex(s.Name);
            if (s.SensorType == SensorType.Temperature && idx < 0)
                _coreTempFallback.Add(s);
            if (idx < 0) continue;
            var b = Bind(idx);
            switch (s.SensorType)
            {
                case SensorType.Clock: b.Clk = s; break;
                case SensorType.Load: b.Load ??= s; break;
                case SensorType.Temperature: b.Temp = s; break;
            }
        }

        foreach (var b in byIndex.Values)
        {
            _cores.Add(b);
            CpuCores.Add(b.Row);
        }
    }

    private void BuildGpu(IHardware hw)
    {
        var b = new GpuBind
        {
            Row = new GpuRow(hw.Name) { VendorText = VendorZh(hw.HardwareType) },
            Temp = First(hw, SensorType.Temperature, "GPU Core", "GPU Hot Spot", "GPU"),
            Load = First(hw, SensorType.Load, "GPU Core", "GPU", "D3D 3D"),
            CoreClk = First(hw, SensorType.Clock, "GPU Core"),
            MemClk = First(hw, SensorType.Clock, "GPU Memory"),
            FanRpm = First(hw, SensorType.Fan, "GPU", "Fan"),
            Fan = First(hw, SensorType.Control, "GPU Fan", "Fan"),
            Power = First(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU"),
            VramUsed = First(hw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            VramTotal = First(hw, SensorType.SmallData, "GPU Memory Total"),
        };
        _gpuBinds.Add(b);
        Gpus.Add(b.Row);
    }

    private void BuildStorage(IHardware hw)
    {
        // NVMe 於 LHM 曝露「Percentage Used」（已耗損%，0＝全新）；SATA SSD 曝露「Remaining Life」（剩餘%）。
        // 兩者語意相反，先記錄本磁碟採用的是哪一種，Publish 時據此換算為統一的「剩餘壽命%」。
        var life = First(hw, SensorType.Level, "Remaining Life", "Percentage Used", "Life");
        var b = new StorageBind
        {
            Row = new StorageRow(hw.Name)
            {
                TypeText = "儲存裝置",
                CapacityText = "—",
            },
            Temp = First(hw, SensorType.Temperature, "Temperature", "Drive"),
            Life = life,
            LifeConsumed = life is not null && life.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase),
            Used = First(hw, SensorType.Load, "Used Space", "Used"),
            // 磁碟活動時間％（LHM 對多數 SSD/NVMe/HDD 曝露「Total Activity」）
            Activity = First(hw, SensorType.Load, "Total Activity", "Activity"),
        };
        _diskBinds.Add(b);
        Drives.Add(b.Row);
    }

    private void EnumerateSensors(IHardware hw, string group)
    {
        foreach (var s in hw.Sensors.OrderBy(x => x.SensorType).ThenBy(x => x.Name))
            _sensorRows.Add((s, new SensorRow(group, s.Name, TypeZh(s.SensorType), UnitOf(s.SensorType))));
        foreach (var sub in hw.SubHardware)
            EnumerateSensors(sub, $"{group} / {sub.Name}");
    }

    // ---- 每秒更新 ---------------------------------------------------------

    /// <summary>
    /// 是否已切換到「感測器」總表分頁。整表逐列 ToString 格式化是每秒最重的一段工作，
    /// 未顯示時略過（由 MainWindow 於切頁時設定）。
    /// </summary>
    public bool DetailedSensorsVisible { get; set; }

    /// <summary>
    /// 重工（可於背景執行緒呼叫）：走訪整棵 LHM 硬體樹，更新所有節點的原始感測值。
    /// 此為造成 UI 卡頓的主因，務必在背景執行緒執行。
    /// </summary>
    public void Poll()
    {
        lock (_pollLock)
        {
            if (_disposed) return;   // 已釋放後遲到的背景 Poll 直接略過
            _computer.Accept(_visitor);
        }
    }

    /// <summary>
    /// 輕工（須於 UI 執行緒呼叫）：讀取 Poll() 已更新的數值、格式化並就地寫入繫結物件，
    /// 最後觸發總覽用純量的變更通知。
    /// </summary>
    public void Publish()
    {
        // 感測器總表僅在該分頁可見時才格式化（整表逐列 ToString 為每秒最重的一段）
        if (DetailedSensorsVisible)
            foreach (var (s, row) in _sensorRows)
            {
                row.ValueText = Fmt(s.Value, s.SensorType);
                row.MinText = Fmt(s.Min, s.SensorType);
                row.MaxText = Fmt(s.Max, s.SensorType);
            }

        foreach (var b in _cores)
        {
            b.Row.ClockMHz = Val(b.Clk) ?? 0;
            b.Row.LoadPercent = Val(b.Load) ?? 0;
            b.Row.TempC = Val(b.Temp);
        }

        foreach (var b in _gpuBinds)
        {
            b.Row.TempC = Val(b.Temp);
            b.Row.LoadPercent = Val(b.Load) ?? 0;
            b.Row.CoreClockMHz = Val(b.CoreClk) ?? 0;
            b.Row.MemClockMHz = Val(b.MemClk) ?? 0;
            b.Row.FanPercent = Val(b.Fan) ?? 0;
            b.Row.PowerW = Val(b.Power) ?? 0;
            b.Row.VramUsedMB = Val(b.VramUsed) ?? 0;
            b.Row.VramTotalMB = Val(b.VramTotal) ?? 0;
        }

        // 系統風扇：即時輸出%與轉速（僅「系統風扇控制」分頁可見時才更新，省去無謂開銷）
        if (FanControlsVisible) TickFans();

        foreach (var b in _diskBinds)
        {
            b.Row.TempC = Val(b.Temp);
            var life = Val(b.Life);
            // 「已耗損%」換算為「剩餘壽命%」；「Remaining Life」則直接採用。
            b.Row.RemainingLife = life is double lv ? (b.LifeConsumed ? Math.Clamp(100 - lv, 0, 100) : lv) : null;
            b.Row.UsedPercent = Val(b.Used);
            double act = Val(b.Activity) ?? 0;
            b.Row.ActivityPercent = act;
            b.Row.ActivityHist.Push(act);          // 每秒累積活動時間走勢（未顯示時圖層不重繪）
        }

        OnPropertyChanged(nameof(CpuLoad));
        OnPropertyChanged(nameof(CpuLoadText));
        OnPropertyChanged(nameof(CpuLoadSeverity));
        OnPropertyChanged(nameof(CpuTemp));
        OnPropertyChanged(nameof(CpuTempText));
        OnPropertyChanged(nameof(CpuTempPercent));
        OnPropertyChanged(nameof(CpuTempSeverity));
        OnPropertyChanged(nameof(CpuClockText));
        OnPropertyChanged(nameof(CpuPowerText));
        OnPropertyChanged(nameof(CpuVoltText));
        OnPropertyChanged(nameof(CpuVoltage));
        OnPropertyChanged(nameof(CpuPowerW));
        OnPropertyChanged(nameof(VrmTempC));
        OnPropertyChanged(nameof(MemLoad));
        OnPropertyChanged(nameof(MemLoadText));
        OnPropertyChanged(nameof(MemLoadSeverity));
        OnPropertyChanged(nameof(MemUsageText));
        OnPropertyChanged(nameof(HasGpu));
        OnPropertyChanged(nameof(PrimaryGpu));
        OnPropertyChanged(nameof(GpuLoadText));
        OnPropertyChanged(nameof(GpuTempText));
    }

    /// <summary>同步全刷（Poll + Publish）；供建構時初始化與相容呼叫使用。</summary>
    public void Refresh() { Poll(); Publish(); }

    /// <summary>
    /// 以 WMI 實體磁碟資訊補上容量與類型（LHM 不提供），並填入 HDD 的 S.M.A.R.T. 磁區健康。
    /// 依型號比對 LHM 磁碟列與實體磁碟；重複型號依序配對（配對後即從候選移除）。
    /// 須於 UI 執行緒呼叫（會觸發繫結變更通知）。
    /// </summary>
    public void ApplyDiskInfo(IReadOnlyList<PhysicalDiskInfo> disks)
    {
        var pool = disks.ToList();
        foreach (var row in Drives)
        {
            int idx = FindDiskMatch(pool, row.Name);
            if (idx < 0) continue;
            var d = pool[idx];
            pool.RemoveAt(idx);

            if (d.SizeBytes > 0) row.CapacityText = d.CapacityText;
            if (!string.IsNullOrEmpty(d.TypeText)) row.TypeText = d.TypeText;
            // 僅 HDD 由 S.M.A.R.T. 磁區狀態決定健康；SSD/NVMe 的健康由剩餘壽命於健康總評衍生
            if (d.Kind == DiskKind.Hdd)
            {
                row.HealthSeverity = d.HealthSeverity;
                row.HealthDetail = d.HealthDetail;
            }
            CopyDeep(d, row);
        }

        // 未與 LHM 感測器配對的實體磁碟（例：LHM 未列舉者）仍以無即時值的列補入，
        // 避免「整合」後反而漏顯示——資料一律真實，取不到的即時欄位留白。
        foreach (var d in pool)
        {
            var row = new StorageRow(d.Model)
            {
                TypeText = string.IsNullOrEmpty(d.TypeText) ? "儲存裝置" : d.TypeText,
                CapacityText = d.CapacityText,
            };
            if (d.Kind == DiskKind.Hdd)
            {
                row.HealthSeverity = d.HealthSeverity;
                row.HealthDetail = d.HealthDetail;
            }
            CopyDeep(d, row);
            Drives.Add(row);
        }
    }

    // 將 WMI 深度靜態欄位併入即時列，讓儲存分頁單一區塊即涵蓋摘要與深度資訊。
    private static void CopyDeep(PhysicalDiskInfo d, StorageRow row)
    {
        row.Model = string.IsNullOrEmpty(d.Model) ? "—" : d.Model;
        row.SerialNumber = d.SerialNumber;
        row.Firmware = d.Firmware;
        row.InterfaceType = d.InterfaceType;
        row.BusText = d.BusText;
        row.PartitionsText = d.PartitionsText;
        row.SectorText = d.SectorText;
        row.MediaType = d.MediaType;
        row.CountText = d.CountText;
    }


    private static int FindDiskMatch(List<PhysicalDiskInfo> pool, string name)
    {
        string n = NormModel(name);
        for (int i = 0; i < pool.Count; i++)
            if (NormModel(pool[i].Model) == n) return i;                 // 完全相同（去空白/大小寫）
        for (int i = 0; i < pool.Count; i++)                            // 互相包含（LHM 名稱有時較長/較短）
        {
            string m = NormModel(pool[i].Model);
            if (m.Length > 3 && (n.Contains(m) || m.Contains(n))) return i;
        }
        return -1;
    }

    private static string NormModel(string s) =>
        new string((s ?? "").ToUpperInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());

    // ---- 總覽用純量 -------------------------------------------------------

    public double CpuLoad => Val(_cpuLoad) ?? 0;
    public string CpuLoadText => $"{CpuLoad:0} %";
    public Severity CpuLoadSeverity => Health.Load(CpuLoad);

    public double? CpuTemp => Val(_cpuPkgTemp) ?? _coreTempFallback.Select(Val).Where(v => v.HasValue).DefaultIfEmpty(null).Max();
    public string CpuTempText => CpuTemp is double t ? $"{t:0} °C" : "—";
    public double CpuTempPercent => CpuTemp is double t ? Math.Clamp(t, 0, 100) : 0;
    public Severity CpuTempSeverity => Health.Cpu(CpuTemp);

    public double CpuClock => _cores.Select(c => Val(c.Clk) ?? 0).DefaultIfEmpty(0).Max();
    public string CpuClockText => CpuClock > 0 ? $"{CpuClock:0} MHz" : "—";
    public string CpuPowerText => Val(_cpuPower) is double p and > 0 ? $"{p:0.#} W" : "—";
    public string CpuVoltText => Val(_cpuVolt) is double v and > 0 ? $"{v:0.###} V" : "—";
    public string CpuBusText => Val(_cpuBus) is double b and > 0 ? $"{b:0.#} MHz" : "—";

    // 超頻模組用的數值型讀值（非格式化字串）：取不到回 null，絕不填假值。
    public double? CpuVoltage => Val(_cpuVolt);
    public double? CpuPowerW => Val(_cpuPower);
    public double? VrmTempC => Val(_vrmTemp);

    public double MemLoad => Val(_memLoad) ?? 0;
    public string MemLoadText => $"{MemLoad:0} %";
    public Severity MemLoadSeverity => Health.Load(MemLoad);
    public double MemUsedGB => Val(_memUsed) ?? 0;
    public double MemAvailGB => Val(_memAvail) ?? 0;
    public double MemTotalGB => MemUsedGB + MemAvailGB;
    public string MemUsageText => MemTotalGB > 0 ? $"{MemUsedGB:0.0} / {MemTotalGB:0.0} GB" : "—";

    public bool HasGpu => _gpuBinds.Count > 0;
    public bool HasDrives => _diskBinds.Count > 0;
    public GpuRow? PrimaryGpu => Gpus.Count > 0 ? Gpus[0] : null;
    public string GpuLoadText => PrimaryGpu?.LoadText ?? "—";
    public string GpuTempText => PrimaryGpu?.TempText ?? "—";

    // ---- 靜態工具 ---------------------------------------------------------

    private static double? Val(ISensor? s) => s?.Value is float f && !float.IsNaN(f) ? f : (double?)null;

    private static int CoreIndex(string name)
    {
        var m = CoreRegex().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
    }

    private static ISensor? First(IHardware hw, SensorType type, params string[] nameContains)
    {
        var of = hw.Sensors.Where(s => s.SensorType == type).ToList();
        foreach (var want in nameContains)
        {
            var hit = of.FirstOrDefault(s => s.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return nameContains.Length == 0 ? of.FirstOrDefault() : null;
    }

    /// <summary>以感測器名稱完全比對取值（避免 Contains 誤中同前綴的相似感測器，如 Virtual Memory）。</summary>
    private static ISensor? Exact(IHardware hw, SensorType type, string name)
        => hw.Sensors.FirstOrDefault(s => s.SensorType == type &&
               s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>如 <see cref="First"/>，但會遞迴向下搜尋子硬體（VRM 溫度常在 SuperIO 子節點）。</summary>
    private static ISensor? FirstDeep(IHardware hw, SensorType type, params string[] nameContains)
    {
        var hit = First(hw, type, nameContains);
        if (hit != null) return hit;
        foreach (var sub in hw.SubHardware)
        {
            hit = FirstDeep(sub, type, nameContains);
            if (hit != null) return hit;
        }
        return null;
    }

    private static string Fmt(float? value, SensorType type)
    {
        if (value is not float v || float.IsNaN(v)) return "—";
        return type switch
        {
            SensorType.Temperature => $"{v:0.0} °C",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{v:0.0} %",
            SensorType.Clock => $"{v:0.0} MHz",
            SensorType.Voltage => $"{v:0.000} V",
            SensorType.Power => $"{v:0.0} W",
            SensorType.Fan => $"{v:0} RPM",
            SensorType.Current => $"{v:0.000} A",
            SensorType.Data => $"{v:0.00} GB",
            SensorType.SmallData => $"{v:0} MB",
            SensorType.Throughput => v > 1_048_576 ? $"{v / 1_048_576:0.0} MB/s" : $"{v / 1024:0.0} KB/s",
            SensorType.Frequency => $"{v:0.0} Hz",
            _ => v.ToString("0.##", CultureInfo.InvariantCulture),
        };
    }

    private static string UnitOf(SensorType t) => t switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Voltage => "V",
        SensorType.Power => "W",
        SensorType.Fan => "RPM",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        _ => "",
    };

    private static string TypeZh(SensorType t) => t switch
    {
        SensorType.Temperature => "溫度",
        SensorType.Load => "負載",
        SensorType.Clock => "頻率",
        SensorType.Voltage => "電壓",
        SensorType.Power => "功耗",
        SensorType.Fan => "風扇",
        SensorType.Control => "風扇控制",
        SensorType.Level => "水位",
        SensorType.Current => "電流",
        SensorType.Data => "資料",
        SensorType.SmallData => "資料",
        SensorType.Throughput => "傳輸率",
        SensorType.Frequency => "頻率",
        _ => t.ToString(),
    };

    private static string VendorZh(HardwareType t) => t switch
    {
        HardwareType.GpuNvidia => "NVIDIA",
        HardwareType.GpuAmd => "AMD",
        HardwareType.GpuIntel => "Intel",
        _ => "—",
    };

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex CoreRegex();

    public void Dispose()
    {
        lock (_pollLock)   // 等待任何在途的背景 Poll 結束後再關閉，杜絕關閉瞬間的競態
        {
            if (_disposed) return;
            _disposed = true;
            // 關閉硬體前，先把所有被手動接管的系統風扇交回 BIOS／自動控制，避免風扇卡在固定轉速
            try { RestoreAllFansToAuto(); } catch { }
            try { _computer.Close(); } catch { }
        }
    }
}
