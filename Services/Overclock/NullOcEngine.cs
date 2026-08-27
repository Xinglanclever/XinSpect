namespace XinSpect;

/// <summary>
/// 找不到 Intel XTU SDK 或平台不支援時使用的空引擎。
/// 一切寫入皆誠實回報失敗（絕不假裝成功），讀取一律回不可用。
/// </summary>
public sealed class NullOcEngine : IOcEngine
{
    public NullOcEngine(OcEngineStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
    }

    public string Name => "無可用超頻引擎";
    public OcEngineStatus Status { get; }
    public string StatusMessage { get; }

    public bool CoreTunable => false;
    public bool BclkTunable => false;
    public bool MemoryTunable => false;
    public bool CacheTunable => false;
    public bool SpeedOptimizerSupported => false;
    public int ProcessorFamily => 0;

    public bool WatchdogPresent => false;
    public bool WatchdogRunning => false;
    public bool WatchdogFailed => false;

    private static readonly IReadOnlyList<OcKnob> Empty = new List<OcKnob>();
    public IReadOnlyList<OcKnob> Knobs => Empty;

    public bool Initialize() => false;
    public void RefreshActives() { }
    public double? ReadCoreVoltage() => null;
    public double? ReadMonitor(params string[] nameContains) => null;

    public OcApplyResult Apply(OcKnob knob, double value)
        => OcApplyResult.Fail("超頻引擎不可用：" + StatusMessage);

    public bool Discard() => false;

    public OcApplyResult RestoreDefaults()
        => OcApplyResult.Fail("超頻引擎不可用：" + StatusMessage);

    public void SetBootRestore(bool on) { }

    public int SpeedOptimizerState => 0;
    public OcApplyResult SetSpeedOptimizer(bool on, bool extreme)
        => OcApplyResult.Fail("超頻引擎不可用：" + StatusMessage);

    public void Dispose() { }
}
