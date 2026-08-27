using LibreHardwareMonitor.Hardware;

namespace XinSpect;

/// <summary>
/// 單一可控風扇：包住 LHM 的 <see cref="IControl"/>，提供目前輸出%、轉速（RPM）與手動／自動切換。
/// 所有寫入（SetSoftware / SetDefault）皆為對硬體的真實操作，數值一律如實回報，不模擬。
/// </summary>
public sealed class FanControlRow : ObservableObject
{
    private readonly IControl _control;
    private readonly ISensor _pct;     // SensorType.Control：目前輸出百分比
    private readonly ISensor? _rpm;    // 對應的 SensorType.Fan：轉速（可能不存在）

    internal FanControlRow(string location, IControl control, ISensor pct, ISensor? rpm, int index)
    {
        Location = location;
        _control = control;
        _pct = pct;
        _rpm = rpm;
        Name = index >= 0 ? $"風扇 #{index}" : pct.Name;
        _setPoint = pct.Value is float v && !float.IsNaN(v) ? System.Math.Clamp(v, 0, 100) : 50;
    }

    /// <summary>風扇顯示名（如「風扇 #2」）。</summary>
    public string Name { get; }
    /// <summary>來源硬體位置（如「主機板 / Nuvoton NCT6796D」）。</summary>
    public string Location { get; }

    /// <summary>IControl 允許的最小／最大手動值（多為 0 / 100）。</summary>
    public double MinValue => _control.MinSoftwareValue;
    public double MaxValue => _control.MaxSoftwareValue;

    private double _current = double.NaN;
    public double CurrentPercent
    {
        get => _current;
        private set { if (SetProperty(ref _current, value)) OnPropertyChanged(nameof(CurrentText)); }
    }
    public string CurrentText => double.IsNaN(_current) ? "—" : $"{_current:0} %";

    private double? _rpmVal;
    public string RpmText => _rpmVal is double r ? $"{r:0} RPM" : "—";

    private double _setPoint;
    /// <summary>滑桿目標值（%）。改變滑桿不會立即寫入硬體，須按「套用」。</summary>
    public double SetPoint
    {
        get => _setPoint;
        set => SetProperty(ref _setPoint, System.Math.Clamp(value, 0, 100));
    }

    private bool _manual;
    public bool IsManual
    {
        get => _manual;
        private set { if (SetProperty(ref _manual, value)) OnPropertyChanged(nameof(ModeText)); }
    }
    public string ModeText => _manual ? "手動" : "自動（BIOS）";

    /// <summary>每秒由 SensorService.Publish 呼叫，更新目前輸出%與轉速。</summary>
    internal void Tick()
    {
        CurrentPercent = _pct.Value is float p && !float.IsNaN(p) ? p : double.NaN;
        _rpmVal = _rpm?.Value is float r && !float.IsNaN(r) ? r : (double?)null;
        OnPropertyChanged(nameof(RpmText));
    }

    /// <summary>套用手動轉速（%）；value 由呼叫端夾在安全下限與 100 之間。真實寫入硬體。</summary>
    public void ApplyManual(double value)
    {
        _control.SetSoftware((float)System.Math.Clamp(value, 0, 100));
        IsManual = true;
    }

    /// <summary>還原為 BIOS／自動控制。</summary>
    public void RestoreAuto()
    {
        _control.SetDefault();
        IsManual = false;
    }
}
